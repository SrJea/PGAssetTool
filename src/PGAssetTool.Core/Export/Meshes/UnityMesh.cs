using AssetsTools.NET;

namespace PGAssetTool.Core.Export.Meshes;

/// Unity's vertex attribute slots, in the order the channel array uses.
public enum VertexAttribute
{
    Position, Normal, Tangent, Color,
    TexCoord0, TexCoord1, TexCoord2, TexCoord3, TexCoord4, TexCoord5, TexCoord6, TexCoord7,
    BlendWeight, BlendIndices,
}

public enum VertexFormat
{
    Float32, Float16, UNorm8, SNorm8, UNorm16, SNorm16, UInt8, SInt8, UInt16, SInt16, UInt32, SInt32,
}

public sealed record SubMesh(int IndexStart, int IndexCount, int Topology, int BaseVertex);

/// The box a mesh's vertices fill: where its middle is, how large it is, and the radius of the
/// sphere that holds it.
///
/// One answer for the renderer, which frames a model by all three, and for the facing, which wants
/// the middle alone. Both worked it out for themselves, with the same six lines of min and max, and
/// two accounts of where a model's middle is would be two accounts of where the camera points.
public readonly record struct MeshBounds(
    (float X, float Y, float Z) Centre, (float X, float Y, float Z) Size, float Radius)
{
    /// Three floats to a vertex, which the mesh importer alone reads out of the mesh instead.
    /// Counted: of 14,021 meshes in the first 400 bundles, 14,020 have three-component positions
    /// and the one that does not has no position channel at all. Every other channel varies — the
    /// last UV set is four components on 378 of them — so this holds for positions and for nothing
    /// else.
    public static MeshBounds Of(UnityMesh mesh, float[]? positions = null)
    {
        positions ??= mesh.Get(VertexAttribute.Position);
        if (positions is null || mesh.VertexCount == 0) return new MeshBounds((0, 0, 0), (0, 0, 0), 0);

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

        for (var v = 0; v < mesh.VertexCount; v++)
        {
            minX = Math.Min(minX, positions[v * 3]); maxX = Math.Max(maxX, positions[v * 3]);
            minY = Math.Min(minY, positions[v * 3 + 1]); maxY = Math.Max(maxY, positions[v * 3 + 1]);
            minZ = Math.Min(minZ, positions[v * 3 + 2]); maxZ = Math.Max(maxZ, positions[v * 3 + 2]);
        }

        var size = (maxX - minX, maxY - minY, maxZ - minZ);
        return new MeshBounds(
            ((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2),
            size,
            MathF.Sqrt(MathF.Pow(size.Item1 / 2, 2) + MathF.Pow(size.Item2 / 2, 2) + MathF.Pow(size.Item3 / 2, 2)));
    }
}

/// A mesh decoded out of its packed vertex streams into plain float arrays.
///
/// Everything is widened to float (and joints to int) rather than kept in its source layout. The
/// packing is an engine detail — interleaved streams, per-attribute formats, alignment padding — and
/// carrying it further would only make every consumer re-implement the same unpacking.
public sealed record UnityMesh
{
    public required string Name { get; init; }
    public required int VertexCount { get; init; }
    public required IReadOnlyDictionary<VertexAttribute, float[]> Attributes { get; init; }
    public required IReadOnlyDictionary<VertexAttribute, int> Dimensions { get; init; }
    public required int[] Indices { get; init; }
    public required IReadOnlyList<SubMesh> SubMeshes { get; init; }
    public required IReadOnlyList<float[]> BindPoses { get; init; }
    public required IReadOnlyList<uint> BoneNameHashes { get; init; }

    public bool IsSkinned => BindPoses.Count > 0 && Attributes.ContainsKey(VertexAttribute.BlendIndices);

    public float[]? Get(VertexAttribute attribute) => Attributes.GetValueOrDefault(attribute);

    /// Several meshes of one prefab as one mesh, for showing them together.
    ///
    /// A weapon in this game is not only the weapon: `Arms_Mesh` is in 398 of 400 prefabs, and the
    /// clips move it — 1,437 of 1,523 weapons animate an arm that actually moves. The renderer draws
    /// one mesh into one depth buffer, so showing the pair means one mesh.
    ///
    /// **The parts must already be posed** (`Skeleton.Assembled`, from a skeleton read with the
    /// whole prefab, so that both are in the prefab's own space). What comes back is therefore for
    /// looking at and not for writing back: the skinning is dropped rather than carried, because
    /// blend indices count against a bone list that is per mesh and means nothing once two are
    /// joined. Nothing exports this.
    ///
    /// Each part keeps its own submeshes, in order, so a texture per submesh still lands where it
    /// belongs. `BaseVertex` is folded into the indices here — the renderer does not add it, the glb
    /// writer does — so the result needs it from nobody.
    ///
    /// A part that carries an outline shell comes too. Whether to cull by facing is a property of a
    /// whole mesh and is lost in the join — a shell that fills its own part's box does not fill the
    /// whole item's — so the caller works it out from the parts and tells the renderer.
    /// `avatar_bugged_cosmus` is why: its body, head and legs each carry one, and left out there is
    /// nothing of it to see but the arms.
    public static UnityMesh? Combined(string name, IReadOnlyList<UnityMesh> parts)
    {
        if (parts.Count == 0) return null;
        if (parts.Count == 1) return parts[0];

        var dimensions = new Dictionary<VertexAttribute, int>();
        foreach (var part in parts)
            foreach (var (attribute, width) in part.Dimensions)
                dimensions.TryAdd(attribute, width);

        // Position is what everything downstream indexes three at a time; a part without one has
        // no geometry to contribute and would only shift every index after it.
        if (!dimensions.ContainsKey(VertexAttribute.Position)) return null;

        var total = parts.Sum(p => p.VertexCount);
        var attributes = dimensions.ToDictionary(d => d.Key, d => new float[total * d.Value]);
        var indices = new List<int>();
        var subMeshes = new List<SubMesh>();
        var vertexBase = 0;

        foreach (var part in parts)
        {
            foreach (var (attribute, width) in dimensions)
            {
                var into = attributes[attribute];
                var from = part.Get(attribute);
                if (from is null) continue;

                // A part may carry fewer components than another — the last UV set is four wide on
                // 378 of the game's meshes and two on the rest. Copy what there is and leave the
                // rest at zero, rather than reading off the end of the shorter one.
                var mine = part.Dimensions.GetValueOrDefault(attribute, width);
                var copy = Math.Min(mine, width);

                for (var vertex = 0; vertex < part.VertexCount; vertex++)
                for (var component = 0; component < copy; component++)
                {
                    var source = vertex * mine + component;
                    if (source < from.Length)
                        into[(vertexBase + vertex) * width + component] = from[source];
                }
            }

            var own = part.SubMeshes.Count > 0
                ? part.SubMeshes
                : [new SubMesh(0, part.Indices.Length, 0, 0)];

            foreach (var sub in own)
            {
                var start = indices.Count;
                for (var i = 0; i < sub.IndexCount; i++)
                {
                    var at = sub.IndexStart + i;
                    if (at >= 0 && at < part.Indices.Length)
                        indices.Add(part.Indices[at] + sub.BaseVertex + vertexBase);
                }

                subMeshes.Add(new SubMesh(start, indices.Count - start, sub.Topology, BaseVertex: 0));
            }

            vertexBase += part.VertexCount;
        }

        return new UnityMesh
        {
            Name = name,
            VertexCount = total,
            Attributes = attributes,
            Dimensions = dimensions,
            Indices = [.. indices],
            SubMeshes = subMeshes,
            BindPoses = [],
            BoneNameHashes = [],
        };
    }

    /// Whether this mesh carries an outline shell: a copy of itself, a little larger and turned
    /// inside out, which the game draws as a silhouette by showing only the side facing away from
    /// you. Nothing in the object says so, so it is measured, and two things have to hold.
    ///
    /// **The normals have to mean something.** Compared against the winding of the triangle they
    /// belong to, which is the other account of which side is out. A mesh whose two accounts disagree
    /// cannot be asked which side is out, and culling it by facing hollows it — so one that disagrees
    /// on more than a tenth of its triangles carries no shell as far as this is concerned. That is
    /// the guard against normals that really are wrong.
    ///
    /// **And the mesh has to lean inwards.** Each triangle's normal is weighed against the
    /// direction out from the middle: a surface facing outwards counts for, one facing inwards
    /// counts against, and a shell counts against by more than the body counts for, because it is
    /// the larger of the two. Punk's Shovel comes to -13 and Black Hole to -4; Ultimatum, which has
    /// no shell, comes to +169.
    ///
    /// Neither test alone is enough. The lean finds the shell but also flags a mesh whose normals
    /// are simply wrong; the agreement says whether the answer can be trusted.
    ///
    /// Worked out on every ask rather than kept: it is one pass over the triangles and a handful of
    /// multiplications, which is nothing beside the rasterizing it decides, and a cached answer in a
    /// record is a field that quietly joins its equality.
    ///
    /// **A shell can also be one submesh of the model rather than the whole of it**, and then the
    /// sum above is drowned: Turbo Cat's 504-triangle shell comes to -435 and its 752-triangle body
    /// to +579, so the mesh as a whole leans outwards by +145 and the shell is drawn as a surface
    /// over the cat. So each submesh is weighed on its own as well.
    ///
    /// A submesh leaning inwards is not enough by itself — 58 of the game's items have one, and
    /// most are the inside of a barrel or the back face of a sight, a few triangles each. What
    /// tells a shell from one of those is that **a shell wraps the whole model**: its bounding box
    /// fills the model's in all three directions. Measured over every item: of those 58, exactly
    /// two fill nine tenths of the model — Turbo Cat at 0.98 and the salesman avatar at 1.00 — and
    /// the next one down fills 0.89 while leaning inwards by only -0.013, a twentieth of Turbo
    /// Cat's -0.204. Both margins are wide and on different axes, so both are required. No weapon
    /// is caught.
    public bool CarriesAnOutline
    {
        get
        {
            var positions = Get(VertexAttribute.Position);
            var normals = Get(VertexAttribute.Normal);
            if (positions is null || normals is null || VertexCount == 0) return false;

            // The average vertex rather than the middle of the bounding box: a shell is concentric
            // with the body it wraps, and where the mass is is what says which way outwards points
            // on a model that is longer at one end than the other.
            var (cx, cy, cz) = (0f, 0f, 0f);
            for (var i = 0; i < VertexCount; i++)
                (cx, cy, cz) = (cx + positions[i * 3], cy + positions[i * 3 + 1], cz + positions[i * 3 + 2]);
            var (mx, my, mz) = (cx / VertexCount, cy / VertexCount, cz / VertexCount);

            var lean = 0f;
            var (agree, all) = (0, 0);

            for (var i = 0; i + 2 < Indices.Length; i += 3)
            {
                var (a, b, c) = (Indices[i], Indices[i + 1], Indices[i + 2]);
                if (a < 0 || b < 0 || c < 0) continue;
                if (a >= VertexCount || b >= VertexCount || c >= VertexCount) continue;

                var nx = normals[a * 3] + normals[b * 3] + normals[c * 3];
                var ny = normals[a * 3 + 1] + normals[b * 3 + 1] + normals[c * 3 + 1];
                var nz = normals[a * 3 + 2] + normals[b * 3 + 2] + normals[c * 3 + 2];

                lean += (Middle(positions, a, b, c, 0) - mx) * nx
                    + (Middle(positions, a, b, c, 1) - my) * ny
                    + (Middle(positions, a, b, c, 2) - mz) * nz;

                var (ux, uy, uz) = Edge(positions, a, b);
                var (vx, vy, vz) = Edge(positions, a, c);
                var (wx, wy, wz) = (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);

                all++;
                if (wx * nx + wy * ny + wz * nz >= 0) agree++;
            }

            if (all == 0 || agree < all * 0.9f) return false;
            return lean < 0 || SubMeshes.Count > 1 && SubMeshes.Any(part => Wraps(positions, normals, part));
        }
    }

    /// Whether one submesh is a shell around the whole model: leaning inwards, and filling the
    /// model's own box in all three directions. See CarriesAnOutline for the measurements.
    private bool Wraps(float[] positions, float[] normals, SubMesh part)
    {
        var whole = MeshBounds.Of(this, positions);
        var (wx, wy, wz) = whole.Size;
        if (wx <= 0 || wy <= 0 || wz <= 0) return false;

        var lean = 0f;
        var triangles = 0;
        var radius = 0f;
        var (lowX, lowY, lowZ) = (float.MaxValue, float.MaxValue, float.MaxValue);
        var (highX, highY, highZ) = (float.MinValue, float.MinValue, float.MinValue);

        var to = Math.Min(part.IndexStart + part.IndexCount, Indices.Length);
        for (var i = Math.Max(part.IndexStart, 0); i + 2 < to; i += 3)
        {
            var (a, b, c) = (Indices[i], Indices[i + 1], Indices[i + 2]);
            if (a < 0 || b < 0 || c < 0) continue;
            if (a >= VertexCount || b >= VertexCount || c >= VertexCount) continue;

            var nx = normals[a * 3] + normals[b * 3] + normals[c * 3];
            var ny = normals[a * 3 + 1] + normals[b * 3 + 1] + normals[c * 3 + 1];
            var nz = normals[a * 3 + 2] + normals[b * 3 + 2] + normals[c * 3 + 2];

            lean += (Middle(positions, a, b, c, 0) - whole.Centre.X) * nx
                + (Middle(positions, a, b, c, 1) - whole.Centre.Y) * ny
                + (Middle(positions, a, b, c, 2) - whole.Centre.Z) * nz;
            triangles++;

            foreach (var vertex in stackalloc[] { a, b, c })
            {
                var (x, y, z) = (positions[vertex * 3], positions[vertex * 3 + 1], positions[vertex * 3 + 2]);
                (lowX, lowY, lowZ) = (MathF.Min(lowX, x), MathF.Min(lowY, y), MathF.Min(lowZ, z));
                (highX, highY, highZ) = (MathF.Max(highX, x), MathF.Max(highY, y), MathF.Max(highZ, z));

                var (dx, dy, dz) = (x - whole.Centre.X, y - whole.Centre.Y, z - whole.Centre.Z);
                radius = MathF.Max(radius, MathF.Sqrt(dx * dx + dy * dy + dz * dz));
            }
        }

        if (triangles == 0 || radius <= 0) return false;

        // Against what a surface of this many triangles facing straight out would come to, so that
        // a cat and a pistol are the same question.
        if (lean / (triangles * 3f * radius) > -0.05f) return false;

        var fills = MathF.Min(
            MathF.Min((highX - lowX) / wx, (highY - lowY) / wy), (highZ - lowZ) / wz);
        return fills >= 0.9f;
    }

    private static float Middle(float[] positions, int a, int b, int c, int axis)
        => (positions[a * 3 + axis] + positions[b * 3 + axis] + positions[c * 3 + axis]) / 3;

    private static (float X, float Y, float Z) Edge(float[] positions, int from, int to)
        => (positions[to * 3] - positions[from * 3],
            positions[to * 3 + 1] - positions[from * 3 + 1],
            positions[to * 3 + 2] - positions[from * 3 + 2]);

    /// Byte payloads sit either on the field itself or on its Array child, depending on the type.
    private static byte[] Bytes(AssetTypeValueField field)
    {
        if (field.IsDummy) return [];
        if (field.Value?.ValueType == AssetValueType.ByteArray) return field.AsByteArray ?? [];
        var array = field["Array"];
        return !array.IsDummy && array.Value?.ValueType == AssetValueType.ByteArray
            ? array.AsByteArray ?? []
            : [];
    }

    /// <param name="resource">
    /// Fetches bytes from a companion stream file: its path as the object names it, the offset and
    /// the length. Null for a caller with no bundle at hand, which then cannot read such a mesh.
    /// </param>
    public static UnityMesh Read(
        AssetTypeValueField field, Func<string, long, long, byte[]?>? resource = null)
    {
        var name = field["m_Name"].AsString;
        var vertexData = field["m_VertexData"];
        var vertexCount = vertexData["m_VertexCount"].AsInt;
        var raw = Bytes(vertexData["m_DataSize"]);

        // Counted rather than assumed: of 14,021 meshes in the first 400 bundles, 358 are packed
        // this way and every one of them is in a scene bundle — the maps. Nothing an item is made of
        // is compressed; a map written out reads them. See Compressed.
        if (field["m_MeshCompression"].AsInt != 0) return Compressed(field, name);

        // Some meshes keep their vertices in the bundle's .resS rather than in the object, exactly
        // as most textures keep their pixels — the object then carries an empty buffer and a place
        // to find the real one. #14 Battle Shovel is one. The index buffer stays in the object
        // either way.
        var stream = field["m_StreamData"];
        if (stream["path"].AsString is { Length: > 0 } outside)
        {
            raw = resource?.Invoke(outside, stream["offset"].AsLong, stream["size"].AsLong)
                ?? throw new NotSupportedException(
                    $"'{name}' keeps its vertex data in {Path.GetFileName(outside)}, "
                    + "which is not reachable from here.");
        }

        var channels = ReadChannels(vertexData["m_Channels"]["Array"]);
        var (attributes, dimensions) = Unpack(channels, raw, vertexCount);

        return new UnityMesh
        {
            Name = name,
            VertexCount = vertexCount,
            Attributes = attributes,
            Dimensions = dimensions,
            Indices = ReadIndices(field),
            SubMeshes = ReadSubMeshes(field),
            // A Matrix4x4 is sixteen flat fields named e00 through e33, not four nested rows.
            BindPoses = field["m_BindPose"]["Array"].Children
                .Select(m => m.Children.Select(v => v.AsFloat).ToArray()).ToList(),
            BoneNameHashes = field["m_BoneNameHashes"]["Array"].Children.Select(c => c.AsUInt).ToList(),
        };
    }

    /// A mesh Unity packed into bit streams: positions, texture coordinates, normals and colours
    /// each a PackedFloatVector, triangles a PackedIntVector — the same packing the game's clips
    /// keep their rotations in (see PackedCurve).
    ///
    /// Two things are not as they look. The texture coordinates are every channel one after
    /// another, and `m_UVInfo` says which channels there are, four bits apiece: whether it exists
    /// and how many numbers it has. And a normal is kept as two of its numbers, with whether the
    /// third is negative in `m_NormalSigns`; it is a unit vector, so its size gives the third back.
    private static UnityMesh Compressed(AssetTypeValueField field, string name)
    {
        var packed = field["m_CompressedMesh"];
        var positions = Preview.PackedCurve.Floats(packed["m_Vertices"]);
        var vertexCount = positions.Length / 3;
        var attributes = new Dictionary<VertexAttribute, float[]> { [VertexAttribute.Position] = positions[..(vertexCount * 3)] };
        var dimensions = new Dictionary<VertexAttribute, int> { [VertexAttribute.Position] = 3 };

        var uv = packed["m_UV"];
        if (!uv.IsDummy && uv["m_NumItems"].AsUInt > 0 && vertexCount > 0)
        {
            var all = Preview.PackedCurve.Floats(uv);
            var info = packed["m_UVInfo"].IsDummy ? 0u : packed["m_UVInfo"].AsUInt;
            var offset = 0;
            for (var channel = 0; channel < 8; channel++)
            {
                int dimension;
                if (info != 0)
                {
                    var bits = (info >> (channel * 4)) & 0xF;
                    if ((bits & 4) == 0) continue;
                    dimension = 1 + (int)(bits & 3);
                }
                else if (channel < 2) dimension = 2; // Before m_UVInfo: two channels of two, if they fit.
                else break;

                var length = dimension * vertexCount;
                if (offset + length > all.Length) break;
                attributes[VertexAttribute.TexCoord0 + channel] = all[offset..(offset + length)];
                dimensions[VertexAttribute.TexCoord0 + channel] = dimension;
                offset += length;
            }
        }

        var normals = packed["m_Normals"];
        if (!normals.IsDummy && normals["m_NumItems"].AsUInt / 2 == vertexCount && vertexCount > 0)
        {
            var xy = Preview.PackedCurve.Floats(normals);
            var signs = Preview.PackedCurve.Ints(packed["m_NormalSigns"], (int)packed["m_NormalSigns"]["m_NumItems"].AsUInt);
            var turned = new float[vertexCount * 3];
            for (var v = 0; v < vertexCount; v++)
            {
                var (x, y) = (xy[v * 2], xy[v * 2 + 1]);
                var square = 1 - x * x - y * y;
                var z = 0f;
                if (square >= 0) z = MathF.Sqrt(square);
                else
                {
                    var length = MathF.Sqrt(x * x + y * y);
                    if (length > 1e-6f) (x, y) = (x / length, y / length);
                }
                if (v < signs.Length && signs[v] == 0) z = -z;
                (turned[v * 3], turned[v * 3 + 1], turned[v * 3 + 2]) = (x, y, z);
            }
            attributes[VertexAttribute.Normal] = turned;
            dimensions[VertexAttribute.Normal] = 3;
        }

        var colours = packed["m_FloatColors"];
        if (!colours.IsDummy && colours["m_NumItems"].AsUInt == vertexCount * 4 && vertexCount > 0)
        {
            attributes[VertexAttribute.Color] = Preview.PackedCurve.Floats(colours);
            dimensions[VertexAttribute.Color] = 4;
        }

        var triangles = packed["m_Triangles"];
        var indices = triangles.IsDummy
            ? []
            : Preview.PackedCurve.Ints(triangles, (int)triangles["m_NumItems"].AsUInt);

        return new UnityMesh
        {
            Name = name,
            VertexCount = vertexCount,
            Attributes = attributes,
            Dimensions = dimensions,
            Indices = indices,
            SubMeshes = ReadSubMeshes(field),
            BindPoses = field["m_BindPose"]["Array"].Children
                .Select(m => m.Children.Select(v => v.AsFloat).ToArray()).ToList(),
            BoneNameHashes = field["m_BoneNameHashes"]["Array"].Children.Select(c => c.AsUInt).ToList(),
        };
    }

    private readonly record struct Channel(VertexAttribute Attribute, int Stream, int Offset, VertexFormat Format, int Dimension);

    private static List<Channel> ReadChannels(AssetTypeValueField array)
    {
        var channels = new List<Channel>();
        for (int i = 0; i < array.Children.Count && i <= (int)VertexAttribute.BlendIndices; i++)
        {
            var c = array.Children[i];
            var dimension = c["dimension"].AsInt & 0x0F;
            if (dimension == 0) continue;
            channels.Add(new Channel((VertexAttribute)i, c["stream"].AsInt, c["offset"].AsInt,
                (VertexFormat)c["format"].AsInt, dimension));
        }
        return channels;
    }

    private static int SizeOf(VertexFormat format) => format switch
    {
        VertexFormat.Float32 or VertexFormat.UInt32 or VertexFormat.SInt32 => 4,
        VertexFormat.Float16 or VertexFormat.UNorm16 or VertexFormat.SNorm16
            or VertexFormat.UInt16 or VertexFormat.SInt16 => 2,
        _ => 1,
    };

    /// Streams sit one after another in the buffer, each starting on a 16-byte boundary.
    private static (Dictionary<VertexAttribute, float[]>, Dictionary<VertexAttribute, int>) Unpack(
        List<Channel> channels, byte[] raw, int vertexCount)
    {
        var strides = channels.GroupBy(c => c.Stream)
            .ToDictionary(g => g.Key, g => g.Max(c => c.Offset + SizeOf(c.Format) * c.Dimension));

        var starts = new Dictionary<int, int>();
        var cursor = 0;
        foreach (var stream in strides.Keys.Order())
        {
            starts[stream] = cursor;
            cursor += Align(strides[stream] * vertexCount, 16);
        }

        var attributes = new Dictionary<VertexAttribute, float[]>();
        var dimensions = new Dictionary<VertexAttribute, int>();

        foreach (var channel in channels)
        {
            var values = new float[vertexCount * channel.Dimension];
            var stride = strides[channel.Stream];
            var start = starts[channel.Stream];

            for (int v = 0; v < vertexCount; v++)
            {
                var at = start + v * stride + channel.Offset;
                for (int c = 0; c < channel.Dimension; c++)
                    values[v * channel.Dimension + c] = ReadComponent(raw, at + c * SizeOf(channel.Format), channel.Format);
            }
            // A position or a normal is three components however it is kept. Unity pads a half-float
            // one out to four, because an attribute has to fill whole four-byte steps, and everything
            // that reads these takes three a vertex. Left four wide, every vertex after the first is
            // read out of its neighbours' components, and 55 main meshes in the game — eleven
            // weapons, #14 Battle Shovel among them, and King's Crown — shade in alternating light
            // and dark triangles.
            var keep = channel.Attribute is VertexAttribute.Position or VertexAttribute.Normal
                ? Math.Min(channel.Dimension, 3)
                : channel.Dimension;

            if (keep < channel.Dimension)
            {
                var narrowed = new float[vertexCount * keep];
                for (int v = 0; v < vertexCount; v++)
                    Array.Copy(values, v * channel.Dimension, narrowed, v * keep, keep);
                values = narrowed;
            }

            attributes[channel.Attribute] = values;
            dimensions[channel.Attribute] = keep;
        }
        return (attributes, dimensions);
    }

    private static int Align(int value, int to) => (value + to - 1) / to * to;

    private static float ReadComponent(byte[] data, int at, VertexFormat format)
    {
        if (at < 0 || at + SizeOf(format) > data.Length) return 0f;
        return format switch
        {
            VertexFormat.Float32 => BitConverter.ToSingle(data, at),
            VertexFormat.Float16 => (float)BitConverter.ToHalf(data, at),
            VertexFormat.UNorm8 => data[at] / 255f,
            VertexFormat.SNorm8 => Math.Max((sbyte)data[at] / 127f, -1f),
            VertexFormat.UNorm16 => BitConverter.ToUInt16(data, at) / 65535f,
            VertexFormat.SNorm16 => Math.Max(BitConverter.ToInt16(data, at) / 32767f, -1f),
            VertexFormat.UInt8 => data[at],
            VertexFormat.SInt8 => (sbyte)data[at],
            VertexFormat.UInt16 => BitConverter.ToUInt16(data, at),
            VertexFormat.SInt16 => BitConverter.ToInt16(data, at),
            VertexFormat.UInt32 => BitConverter.ToUInt32(data, at),
            VertexFormat.SInt32 => BitConverter.ToInt32(data, at),
            _ => 0f,
        };
    }

    private static int[] ReadIndices(AssetTypeValueField field)
    {
        var raw = Bytes(field["m_IndexBuffer"]);
        // 0 means 16-bit indices, which is what every mesh here uses.
        if (field["m_IndexFormat"].AsInt == 0)
        {
            var indices = new int[raw.Length / 2];
            for (int i = 0; i < indices.Length; i++) indices[i] = BitConverter.ToUInt16(raw, i * 2);
            return indices;
        }
        var wide = new int[raw.Length / 4];
        for (int i = 0; i < wide.Length; i++) wide[i] = (int)BitConverter.ToUInt32(raw, i * 4);
        return wide;
    }

    private static List<SubMesh> ReadSubMeshes(AssetTypeValueField field)
    {
        var indexSize = field["m_IndexFormat"].AsInt == 0 ? 2 : 4;
        return field["m_SubMeshes"]["Array"].Children.Select(s => new SubMesh(
            IndexStart: (int)(s["firstByte"].AsUInt / indexSize),
            IndexCount: (int)s["indexCount"].AsUInt,
            Topology: s["topology"].AsInt,
            BaseVertex: s["baseVertex"].IsDummy ? 0 : (int)s["baseVertex"].AsUInt)).ToList();
    }
}
