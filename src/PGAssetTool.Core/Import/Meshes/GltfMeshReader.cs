using System.Text.Json;
using PGAssetTool.Core.Export.Meshes;

namespace PGAssetTool.Core.Import.Meshes;

/// Pulls the skinned mesh out of a glTF scene and puts it back into Unity's terms.
///
/// The exporter that produced the file will not have preserved the layout this tool wrote: Blender
/// splits vertices at UV and normal seams, so a mesh that went out with 595 vertices comes back with
/// 624. Nothing can be patched in place; the buffers are rebuilt from what the file says.
///
/// Joints arrive as a hierarchy of translation and rotation rather than flat matrices, so each
/// joint's transform is composed down from the scene root before use.
public static class GltfMeshReader
{
    public static UnityMesh Read(string path)
    {
        var file = GltfFile.Open(path);
        var (meshNode, meshIndex, skinIndex) = FindMeshNode(file);
        var mesh = file.Find("meshes", meshIndex)!.Value;

        var primitives = mesh.GetProperty("primitives").EnumerateArray()
            .Where(p => !p.TryGetProperty("mode", out var m) || m.GetInt32() == 4)
            .ToList();
        if (primitives.Count == 0)
            throw new NotSupportedException("The file has no triangle geometry.");

        var attributes = new Dictionary<VertexAttribute, List<float>>();
        var dimensions = new Dictionary<VertexAttribute, int>();
        var indices = new List<int>();
        var subMeshes = new List<SubMesh>();
        var vertexCount = 0;

        // Where each set of vertex accessors already went, so primitives that share one share it
        // here too. A Unity mesh is one vertex buffer with several index ranges over it, and that is
        // how this tool writes one out: every submesh names the same accessors. Read back a
        // primitive at a time, each brought its own copy — #1200's Space Dessert, two submeshes over
        // 2,779 vertices, went back into the game with 5,558, and every further round trip doubled
        // it again. Blender writes a separate set per primitive, which comes through as before.
        var placed = new Dictionary<string, (int Base, int Count)>(StringComparer.Ordinal);

        foreach (var primitive in primitives)
        {
            var slot = primitive.GetProperty("attributes");
            var signature = string.Join(";", slot.EnumerateObject()
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .Select(a => $"{a.Name}={a.Value.GetRawText()}"));

            if (!placed.TryGetValue(signature, out var vertices))
            {
                vertices = (vertexCount, AppendVertices(file, slot, attributes, dimensions));
                placed[signature] = vertices;
                vertexCount += vertices.Count;
            }

            var start = indices.Count;
            var local = primitive.TryGetProperty("indices", out var accessor)
                ? file.ReadIndices(accessor.GetInt32())
                : Enumerable.Range(0, vertices.Count).ToArray();

            // Reversed on the way in for the same reason it was reversed on the way out.
            for (int i = 0; i + 2 < local.Length; i += 3)
            {
                indices.Add(local[i + 2] + vertices.Base);
                indices.Add(local[i + 1] + vertices.Base);
                indices.Add(local[i] + vertices.Base);
            }
            subMeshes.Add(new SubMesh(start, indices.Count - start, Topology: 0, BaseVertex: 0));
        }

        var name = meshNode.TryGetProperty("name", out var n) ? n.GetString()! : "mesh";
        return new UnityMesh
        {
            Name = mesh.TryGetProperty("name", out var mn) ? mn.GetString()! : name,
            VertexCount = vertexCount,
            Attributes = attributes.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
            Dimensions = dimensions,
            Indices = [.. indices],
            SubMeshes = subMeshes,
            BindPoses = ReadBindPoses(file, skinIndex),
            BoneNameHashes = ReadBoneNames(file, skinIndex),
        };
    }

    private static (JsonElement Node, int Mesh, int? Skin) FindMeshNode(GltfFile file)
    {
        if (!file.Root.TryGetProperty("nodes", out var nodes))
            throw new InvalidDataException("The file has no nodes.");

        foreach (var node in nodes.EnumerateArray())
            if (node.TryGetProperty("mesh", out var mesh))
                return (node, mesh.GetInt32(),
                    node.TryGetProperty("skin", out var skin) ? skin.GetInt32() : null);

        throw new InvalidDataException("No node in the file carries a mesh.");
    }

    private static int AppendVertices(
        GltfFile file, JsonElement attributes,
        Dictionary<VertexAttribute, List<float>> into, Dictionary<VertexAttribute, int> dimensions)
    {
        var count = 0;
        foreach (var (name, attribute, width) in Mapping())
        {
            if (!attributes.TryGetProperty(name, out var accessor)) continue;
            var values = file.ReadFloats(accessor.GetInt32(), out var components);
            count = Math.Max(count, values.Length / components);

            if (!into.TryGetValue(attribute, out var list)) into[attribute] = list = [];
            dimensions[attribute] = width;

            for (int v = 0; v < values.Length / components; v++)
                for (int c = 0; c < width; c++)
                    list.Add(Convert(attribute, c, c < components ? values[v * components + c] : Default(attribute, c)));
        }
        return count;
    }

    private static IEnumerable<(string Name, VertexAttribute Attribute, int Width)> Mapping()
    {
        yield return ("POSITION", VertexAttribute.Position, 3);
        yield return ("NORMAL", VertexAttribute.Normal, 3);
        yield return ("TANGENT", VertexAttribute.Tangent, 4);
        yield return ("COLOR_0", VertexAttribute.Color, 4);
        yield return ("TEXCOORD_0", VertexAttribute.TexCoord0, 2);
        yield return ("TEXCOORD_1", VertexAttribute.TexCoord1, 2);
        yield return ("JOINTS_0", VertexAttribute.BlendIndices, 4);
        yield return ("WEIGHTS_0", VertexAttribute.BlendWeight, 4);
    }

    private static float Default(VertexAttribute attribute, int component) => attribute switch
    {
        VertexAttribute.Tangent or VertexAttribute.Color => component == 3 ? 1f : 0f,
        VertexAttribute.BlendWeight => 0f,
        _ => 0f,
    };

    /// The inverse of what the writer did: negate Z, flip tangent handedness, put the texture origin
    /// back at the bottom.
    private static float Convert(VertexAttribute attribute, int component, float value) => attribute switch
    {
        VertexAttribute.Position or VertexAttribute.Normal when component == 2 => -value,
        VertexAttribute.Tangent when component is 2 or 3 => -value,
        VertexAttribute.TexCoord0 or VertexAttribute.TexCoord1 when component == 1 => 1f - value,
        _ => value,
    };

    private static List<float[]> ReadBindPoses(GltfFile file, int? skinIndex)
    {
        if (skinIndex is not { } index || file.Find("skins", index) is not { } skin) return [];
        if (!skin.TryGetProperty("inverseBindMatrices", out var accessor)) return [];

        var values = file.ReadFloats(accessor.GetInt32(), out _);
        var poses = new List<float[]>();

        for (int b = 0; b * 16 < values.Length; b++)
        {
            // Column major with Z negated on the way out; row major with it negated back on the way in.
            var pose = new float[16];
            for (int column = 0; column < 4; column++)
                for (int row = 0; row < 4; row++)
                {
                    var value = values[b * 16 + column * 4 + row];
                    if (row == 2 ^ column == 2) value = -value;
                    pose[row * 4 + column] = value;
                }
            poses.Add(pose);
        }
        return poses;
    }

    /// Bone names carry the hash the mesh was written with, so a round trip keeps them; anything else
    /// gets a hash of zero rather than a wrong one.
    private static List<uint> ReadBoneNames(GltfFile file, int? skinIndex)
    {
        if (skinIndex is not { } index || file.Find("skins", index) is not { } skin) return [];
        if (!skin.TryGetProperty("joints", out var joints)) return [];

        var hashes = new List<uint>();
        foreach (var joint in joints.EnumerateArray())
        {
            var node = file.Find("nodes", joint.GetInt32());
            var name = node?.TryGetProperty("name", out var n) == true ? n.GetString() : null;
            hashes.Add(name is not null && name.StartsWith("bone_", StringComparison.Ordinal)
                && uint.TryParse(name.AsSpan(5), out var hash) ? hash : 0u);
        }
        return hashes;
    }
}
