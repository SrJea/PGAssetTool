using AssetsTools.NET;
using PGAssetTool.Core.Export.Meshes;

namespace PGAssetTool.Core.Import.Meshes;

public enum SkinStrategy
{
    /// The mesh was never skinned, so there is nothing to line up.
    None,

    /// The replacement carries the same bones, in the same order.
    Kept,

    /// The replacement carries the same bones in a different order, so its indices were remapped.
    Remapped,

    /// The replacement brought no usable skeleton, so every vertex is bound rigidly to one bone and
    /// the original bind poses stay. A whole model swapped in from elsewhere lands here.
    BoundRigidly,
}

public sealed record MeshChange(
    int OldVertexCount, int NewVertexCount, int OldTriangles, int NewTriangles,
    IReadOnlyList<string> Channels, SkinStrategy Skin)
{
    public override string ToString()
        => $"{OldVertexCount}->{NewVertexCount} vertices, {OldTriangles}->{NewTriangles} triangles"
           + $" [{string.Join(" ", Channels)}]"
           + (Skin == SkinStrategy.None ? "" : $" skin {Skin.ToString().ToLowerInvariant()}");
}

/// Writes a mesh back into a Unity Mesh asset.
///
/// The buffers are rebuilt rather than patched. An editor round trip does not preserve the vertex
/// count — Blender splits at UV and normal seams, turning 595 vertices into 624 with the same 324
/// triangles — so there is nothing to patch in place.
///
/// Everything goes into one interleaved stream. The original spread its attributes over three, but
/// that is a decision the build made, and the channel descriptors are what the runtime reads.
public static class MeshImporter
{
    private const int Float32 = (int)VertexFormat.Float32;
    private const int UInt32Format = (int)VertexFormat.UInt32;

    /// The order channels occupy in Unity's array, with the width and format each is written as.
    private static readonly (VertexAttribute Attribute, int Width, int Format)[] Layout =
    [
        (VertexAttribute.Position, 3, Float32),
        (VertexAttribute.Normal, 3, Float32),
        (VertexAttribute.Tangent, 4, Float32),
        (VertexAttribute.Color, 4, Float32),
        (VertexAttribute.TexCoord0, 2, Float32),
        (VertexAttribute.TexCoord1, 2, Float32),
        (VertexAttribute.BlendWeight, 4, Float32),
        (VertexAttribute.BlendIndices, 4, UInt32Format),
    ];

    /// <param name="resource">
    /// Reads the bundle's own stream entries, for a mesh that keeps its vertices there rather than
    /// in the object. Only the original is read through it, and only to say what changed and to
    /// line up the bones — but without it such a mesh could not be replaced at all: `glider_basic`,
    /// which several gliders are built on, failed every pack that touched it.
    /// </param>
    public static MeshChange Replace(
        AssetTypeValueField field, UnityMesh mesh, Func<string, long, long, byte[]?>? resource = null)
    {
        var before = UnityMesh.Read(field, resource);

        if (!mesh.Attributes.ContainsKey(VertexAttribute.Position))
            throw new InvalidDataException("The replacement mesh has no positions.");

        var (skin, reconciled) = ReconcileSkin(before, mesh);
        mesh = reconciled;

        var present = Layout.Where(l => mesh.Attributes.ContainsKey(l.Attribute)).ToList();

        WriteVertexData(field, mesh, present);
        WriteIndices(field, mesh);
        WriteSubMeshes(field, mesh);
        if (skin is SkinStrategy.Kept or SkinStrategy.Remapped) WriteSkin(field, mesh);
        WriteBounds(field, mesh);

        // The payload is inline now, so a pointer to the shared stream file would send the game back
        // to the original geometry.
        var stream = field["m_StreamData"];
        if (!stream.IsDummy)
        {
            stream["path"].AsString = "";
            stream["offset"].AsULong = 0;
            stream["size"].AsUInt = 0;
        }
        field["m_MeshCompression"].AsInt = 0;
        if (!field["m_IsReadable"].IsDummy) field["m_IsReadable"].AsBool = true;

        return new MeshChange(
            before.VertexCount, mesh.VertexCount,
            before.Indices.Length / 3, mesh.Indices.Length / 3,
            present.Select(p => p.Attribute.ToString()).ToList(), skin);
    }

    /// Decides what to do with the replacement's skeleton, returning the mesh that should be written.
    ///
    /// The mesh cannot choose its own bone list. Whatever SkinnedMeshRenderer draws it holds an array
    /// of Transforms in the prefab, its length always equal to the mesh's bind pose count, and the
    /// mesh's blend indices point into that array by position. Writing a different number of bones
    /// leaves indices pointing at Transforms that are not there.
    ///
    /// So the original bone list is the one that counts. A replacement carrying the same bones is
    /// used as is, or remapped when the exporter reordered them. Anything else — most obviously a
    /// model brought in from somewhere with a skeleton of its own — is bound rigidly to the first
    /// bone instead, which is what a weapon swap wants anyway: these meshes are almost all one bone
    /// per vertex to begin with.
    private static (SkinStrategy Strategy, UnityMesh Mesh) ReconcileSkin(UnityMesh before, UnityMesh replacement)
    {
        if (before.BindPoses.Count == 0) return (SkinStrategy.None, replacement);

        var wanted = before.BoneNameHashes;
        var offered = replacement.BoneNameHashes;
        var indices = replacement.Get(VertexAttribute.BlendIndices);

        if (indices is not null && offered.Count == wanted.Count && offered.SequenceEqual(wanted))
            return (SkinStrategy.Kept, replacement);

        if (indices is not null && offered.Count > 0)
        {
            var positions = new Dictionary<uint, int>();
            for (int i = wanted.Count - 1; i >= 0; i--) positions[wanted[i]] = i;

            var mapping = new int[offered.Count];
            var mappable = true;
            for (int i = 0; i < offered.Count; i++)
                if (!positions.TryGetValue(offered[i], out mapping[i])) { mappable = false; break; }

            if (mappable)
            {
                for (int i = 0; i < indices.Length; i++)
                {
                    var joint = (int)indices[i];
                    indices[i] = joint >= 0 && joint < mapping.Length ? mapping[joint] : 0;
                }
                return (SkinStrategy.Remapped, replacement);
            }
        }

        return (SkinStrategy.BoundRigidly, BindRigidly(replacement));
    }

    /// Puts every vertex on the first bone at full weight, keeping the bind poses already in the asset.
    ///
    /// The channels are written even when the replacement has none. A model exported without a
    /// skeleton — the usual case for something brought in from elsewhere — would otherwise leave a
    /// mesh that still declares bind poses but offers the renderer no weights to skin it with.
    private static UnityMesh BindRigidly(UnityMesh mesh)
    {
        var attributes = new Dictionary<VertexAttribute, float[]>(mesh.Attributes);
        var dimensions = new Dictionary<VertexAttribute, int>(mesh.Dimensions);

        var weights = new float[mesh.VertexCount * 4];
        for (int v = 0; v < mesh.VertexCount; v++) weights[v * 4] = 1f;

        attributes[VertexAttribute.BlendIndices] = new float[mesh.VertexCount * 4];
        attributes[VertexAttribute.BlendWeight] = weights;
        dimensions[VertexAttribute.BlendIndices] = 4;
        dimensions[VertexAttribute.BlendWeight] = 4;

        return mesh with { Attributes = attributes, Dimensions = dimensions };
    }

    private static void WriteVertexData(
        AssetTypeValueField field, UnityMesh mesh, List<(VertexAttribute Attribute, int Width, int Format)> present)
    {
        var stride = present.Sum(p => p.Width * 4);
        var buffer = new byte[stride * mesh.VertexCount];

        var offset = 0;
        var offsets = new Dictionary<VertexAttribute, int>();
        foreach (var (attribute, width, _) in present)
        {
            offsets[attribute] = offset;
            offset += width * 4;
        }

        foreach (var (attribute, width, format) in present)
        {
            var source = mesh.Attributes[attribute];
            var sourceWidth = mesh.Dimensions.GetValueOrDefault(attribute, width);
            for (int v = 0; v < mesh.VertexCount; v++)
                for (int c = 0; c < width; c++)
                {
                    var value = c < sourceWidth && v * sourceWidth + c < source.Length
                        ? source[v * sourceWidth + c]
                        : 0f;
                    var at = v * stride + offsets[attribute] + c * 4;
                    var bytes = format == UInt32Format
                        ? BitConverter.GetBytes((uint)Math.Max(value, 0))
                        : BitConverter.GetBytes(value);
                    bytes.CopyTo(buffer, at);
                }
        }

        var vertexData = field["m_VertexData"];
        vertexData["m_VertexCount"].AsUInt = (uint)mesh.VertexCount;
        SetBytes(vertexData["m_DataSize"], buffer);

        var channels = vertexData["m_Channels"]["Array"];
        for (int i = 0; i < channels.Children.Count; i++)
        {
            var channel = channels.Children[i];
            var match = present.FirstOrDefault(p => (int)p.Attribute == i);
            var used = match.Width > 0;
            channel["stream"].AsInt = 0;
            channel["offset"].AsInt = used ? offsets[match.Attribute] : 0;
            channel["format"].AsInt = used ? match.Format : 0;
            channel["dimension"].AsInt = used ? match.Width : 0;
        }
    }

    private static void WriteIndices(AssetTypeValueField field, UnityMesh mesh)
    {
        var wide = mesh.VertexCount > ushort.MaxValue;
        field["m_IndexFormat"].AsInt = wide ? 1 : 0;

        var buffer = new byte[mesh.Indices.Length * (wide ? 4 : 2)];
        for (int i = 0; i < mesh.Indices.Length; i++)
        {
            var bytes = wide
                ? BitConverter.GetBytes((uint)mesh.Indices[i])
                : BitConverter.GetBytes((ushort)mesh.Indices[i]);
            bytes.CopyTo(buffer, i * (wide ? 4 : 2));
        }
        SetBytes(field["m_IndexBuffer"], buffer);
    }

    private static void WriteSubMeshes(AssetTypeValueField field, UnityMesh mesh)
    {
        var indexSize = mesh.VertexCount > ushort.MaxValue ? 4 : 2;
        var array = field["m_SubMeshes"]["Array"];
        var template = array.Children.FirstOrDefault()
            ?? throw new InvalidDataException("The mesh has no submesh to model the replacement on.");

        var replacements = new List<AssetTypeValueField>();
        foreach (var submesh in mesh.SubMeshes)
        {
            var copy = template.Clone();
            copy["firstByte"].AsUInt = (uint)(submesh.IndexStart * indexSize);
            copy["indexCount"].AsUInt = (uint)submesh.IndexCount;
            copy["topology"].AsInt = 0;
            copy["baseVertex"].AsUInt = 0;
            copy["firstVertex"].AsUInt = 0;
            copy["vertexCount"].AsUInt = (uint)mesh.VertexCount;
            WriteBoundsInto(copy["localAABB"], mesh);
            replacements.Add(copy);
        }

        array.Children.Clear();
        array.Children.AddRange(replacements);
    }

    private static void WriteSkin(AssetTypeValueField field, UnityMesh mesh)
    {
        var poses = field["m_BindPose"]["Array"];
        if (!poses.IsDummy && mesh.BindPoses.Count > 0 && poses.Children.Count > 0)
        {
            var template = poses.Children[0];
            var replacements = new List<AssetTypeValueField>();
            foreach (var pose in mesh.BindPoses)
            {
                var copy = template.Clone();
                for (int i = 0; i < copy.Children.Count && i < pose.Length; i++)
                    copy.Children[i].AsFloat = pose[i];
                replacements.Add(copy);
            }
            poses.Children.Clear();
            poses.Children.AddRange(replacements);
        }

        var hashes = field["m_BoneNameHashes"]["Array"];
        if (!hashes.IsDummy && mesh.BoneNameHashes.Count > 0 && hashes.Children.Count > 0)
        {
            var template = hashes.Children[0];
            var replacements = new List<AssetTypeValueField>();
            foreach (var hash in mesh.BoneNameHashes)
            {
                var copy = template.Clone();
                copy.AsUInt = hash;
                replacements.Add(copy);
            }
            hashes.Children.Clear();
            hashes.Children.AddRange(replacements);
        }
    }

    private static void WriteBounds(AssetTypeValueField field, UnityMesh mesh)
        => WriteBoundsInto(field["m_LocalAABB"], mesh);

    /// Stale bounds get a mesh culled when it is on screen, or drawn when it is not.
    private static void WriteBoundsInto(AssetTypeValueField bounds, UnityMesh mesh)
    {
        if (bounds.IsDummy) return;
        var positions = mesh.Get(VertexAttribute.Position);
        if (positions is null || mesh.VertexCount == 0) return;

        var min = new float[3] { float.MaxValue, float.MaxValue, float.MaxValue };
        var max = new float[3] { float.MinValue, float.MinValue, float.MinValue };
        var width = mesh.Dimensions.GetValueOrDefault(VertexAttribute.Position, 3);

        for (int v = 0; v < mesh.VertexCount; v++)
            for (int c = 0; c < 3; c++)
            {
                var value = positions[v * width + c];
                min[c] = Math.Min(min[c], value);
                max[c] = Math.Max(max[c], value);
            }

        var centre = bounds["m_Center"];
        var extent = bounds["m_Extent"];
        foreach (var (axis, i) in new[] { ("x", 0), ("y", 1), ("z", 2) })
        {
            centre[axis].AsFloat = (min[i] + max[i]) / 2;
            extent[axis].AsFloat = (max[i] - min[i]) / 2;
        }
    }

    /// Byte payloads live either on the field or on its Array child, depending on the type.
    private static void SetBytes(AssetTypeValueField field, byte[] bytes)
    {
        var target = field.Value?.ValueType == AssetValueType.ByteArray ? field : field["Array"];
        target.Value = new AssetTypeValue(AssetValueType.ByteArray, bytes);
    }
}
