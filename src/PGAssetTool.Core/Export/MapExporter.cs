using System.Text.Json.Nodes;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Core.Export;

/// Writes one of the game's maps out as a single binary glTF: every mesh the map draws, where it
/// stands, with the picture and colour it is drawn with — to look at, measure or build from in
/// Blender or anything else that reads one.
///
/// A map is a scene bundle, `<name>_scene`: the scene itself, every object in it and where it
/// stands, and a `.sharedAssets` beside it with the meshes and materials, which in turn reach
/// textures in other bundles. So the scene is walked object by object — what is switched off, in
/// itself or in anything above it, is not drawn and not written — and each mesh renderer becomes a
/// node with its mesh and materials.
///
/// The things about it that look arbitrary:
///
/// - **Statically batched objects are written where their batch puts them.** The game joins a
///   map's still objects into a few large meshes at build time, already placed in the world, and
///   each object draws only its own range of the joined mesh's submeshes. Placing that range by the
///   object's own transform as well would put every one of them twice as far from the origin.
/// - **Only the nearest level of detail.** An object with levels of detail has a renderer for
///   each; written all together they would stand inside one another.
/// - **A mesh is written once however many objects draw it**, and a texture once however many
///   materials use it: a map repeats the same crate and the same brick hundreds of times.
/// - **A material's tiling goes with it**, as `KHR_texture_transform` — a wall's bricks are one
///   picture repeated, and without it a wall is one giant brick.
/// - **The sky is kept apart**, under a node of its own: a map sits inside a sphere a couple of
///   kilometres across with the night painted on it, which is all anybody sees of the map from
///   outside until it is hidden. Told by name — the object's or its material's says "sky".
/// - **A model on bones is written as it stands**: its vertices carried by its bones to where the
///   scene leaves them, so a flag or a fan is in the file without the bones that move it.
/// - **A mesh Unity compressed is read** (see UnityMesh): a map is where the game keeps those.
/// - **What is parked out of sight is kept apart too**, under "Out of sight": the battle royale
///   maps keep their supply chests and shuttle ten kilometres below the island until a match
///   moves them in, and in the file they made the map a speck beside them.
/// - **Lightmaps are not.** What the map's lighting baked into its surfaces is a second set of
///   pictures on a second set of coordinates, which a model viewer has no use for.
public static class MapExporter
{
    private const string Suffix = "_scene";

    /// How far from the middle of the world an object has to be to count as parked. The largest
    /// maps reach a little over two kilometres; what is parked is ten away.
    private const float OutOfSight = 5000;

    public sealed record Map(string Bundle, string Name);

    /// <param name="Skipped">What was in the map and is not in the file, each with why.</param>
    public sealed record Exported(
        string Path, int Objects, int Meshes, int Materials, int Textures, IReadOnlyList<string> Skipped);

    /// Every map the game has, by name.
    public static IReadOnlyList<Map> List(BundleSet bundles)
        => bundles.BundleNames
            .Where(b => b.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
            .Select(b => new Map(b, b[..^Suffix.Length]))
            .ToList();

    /// The map a name means: its own name or its bundle's, and failing that the one map whose name
    /// holds it.
    public static Map? Find(BundleSet bundles, string query)
    {
        var maps = List(bundles);
        var text = query.Trim();
        return maps.FirstOrDefault(m => string.Equals(m.Name, text, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(m.Bundle, text, StringComparison.OrdinalIgnoreCase))
               ?? (maps.Where(m => m.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList() is [var only] ? only : null);
    }

    /// <param name="progress">Told how far through the map's renderers it is.</param>
    public static Exported Export(BundleSet bundles, Map map, string path, Action<int, int>? progress = null)
        => new Writer(bundles, map).Write(path, progress);

    private sealed class Writer(BundleSet bundles, Map map)
    {
        private readonly AssetsContext _context = bundles.Context;
        private readonly Dictionary<string, (AssetsFileInstance File, string Bundle)> _files = new(StringComparer.OrdinalIgnoreCase);
        private CabIndex? _cabs;

        private readonly MemoryStream _binary = new();
        private readonly JsonArray _views = [];
        private readonly JsonArray _accessors = [];
        private readonly JsonArray _meshes = [];
        private readonly JsonArray _materials = [];
        private readonly JsonArray _textures = [];
        private readonly JsonArray _images = [];
        private readonly JsonArray _nodes = [];
        private readonly List<string> _skipped = [];
        private bool _transformed;

        private readonly Dictionary<(string, long), Vertices?> _vertices = [];
        private readonly Dictionary<(string, long, int), int> _indices = [];
        private readonly Dictionary<string, int> _meshByShape = [];
        private readonly Dictionary<(string, long), int?> _materialByAsset = [];
        private readonly Dictionary<(string, long), int?> _imageByAsset = [];

        private sealed record Vertices(UnityMesh Mesh, JsonObject Attributes);

        public Exported Write(string path, Action<int, int>? progress)
        {
            foreach (var file in bundles.OpenAll(map.Bundle)) _files[file.name] = (file, map.Bundle);
            var scene = _files.Values.FirstOrDefault(f =>
                !f.File.name.EndsWith(".sharedAssets", StringComparison.OrdinalIgnoreCase)
                && f.File.file.GetAssetsOfType(AssetClassID.GameObject).Count > 0).File
                ?? throw new InvalidDataException($"'{map.Bundle}' holds no scene.");

            var transforms = new Dictionary<long, AssetTypeValueField>();
            var transformOf = new Dictionary<long, long>();
            foreach (var info in scene.file.GetAssetsOfType(AssetClassID.Transform))
                if (_context.Deserialize(scene, info) is { } t)
                {
                    transforms[info.PathId] = t;
                    transformOf[t["m_GameObject"]["m_PathID"].AsLong] = info.PathId;
                }

            var active = new Dictionary<long, bool>();
            var names = new Dictionary<long, string>();
            foreach (var info in scene.file.GetAssetsOfType(AssetClassID.GameObject))
                if (_context.Deserialize(scene, info) is { } go)
                {
                    active[info.PathId] = go["m_IsActive"].IsDummy || go["m_IsActive"].AsBool;
                    names[info.PathId] = go["m_Name"].AsString;
                }

            var world = new Dictionary<long, float[]>();
            float[] World(long transform)
            {
                if (world.TryGetValue(transform, out var known)) return known;
                if (!transforms.TryGetValue(transform, out var t)) return Matrix.Identity;
                world[transform] = Matrix.Identity; // A loop in a hierarchy stops here rather than going round.
                var local = Matrix.Compose(Trs(t));
                var father = t["m_Father"]["m_PathID"].AsLong;
                return world[transform] = father != 0 ? Matrix.Times(World(father), local) : local;
            }

            bool Shown(long gameObject)
            {
                for (var step = 0; step < 256; step++)
                {
                    if (!active.GetValueOrDefault(gameObject, true)) return false;
                    if (!transformOf.TryGetValue(gameObject, out var t) || !transforms.TryGetValue(t, out var field)) return true;
                    var father = field["m_Father"]["m_PathID"].AsLong;
                    if (father == 0 || !transforms.TryGetValue(father, out var parent)) return true;
                    gameObject = parent["m_GameObject"]["m_PathID"].AsLong;
                }
                return true;
            }

            var meshOf = new Dictionary<long, AssetTypeValueField>();
            foreach (var info in scene.file.GetAssetsOfType(AssetClassID.MeshFilter))
                if (_context.Deserialize(scene, info) is { } filter)
                    meshOf[filter["m_GameObject"]["m_PathID"].AsLong] = filter["m_Mesh"];

            // Every level of detail but the nearest, which is the one the map is seen in up close.
            var distant = new HashSet<long>();
            foreach (var info in scene.file.GetAssetsOfType(AssetClassID.LODGroup))
                if (_context.Deserialize(scene, info) is { } group)
                    foreach (var lod in group["m_LODs"]["Array"].Children.Skip(1))
                        foreach (var r in lod["renderers"]["Array"].Children)
                            distant.Add(r["renderer"]["m_PathID"].AsLong);

            var renderers = scene.file.GetAssetsOfType(AssetClassID.MeshRenderer)
                .Concat(scene.file.GetAssetsOfType(AssetClassID.SkinnedMeshRenderer)).ToList();
            var terrains = scene.file.GetAssetsOfType(AssetClassID.Terrain).Count;
            if (terrains > 0) _skipped.Add($"{terrains} terrain(s): not a mesh, so not written");

            var root = new JsonObject { ["name"] = map.Name, ["children"] = new JsonArray() };
            var sky = new JsonObject { ["name"] = "Sky", ["children"] = new JsonArray() };
            var parked = new JsonObject { ["name"] = "Out of sight", ["children"] = new JsonArray() };
            var objects = 0;
            for (var at = 0; at < renderers.Count; at++)
            {
                progress?.Invoke(at + 1, renderers.Count);
                var info = renderers[at];
                if (distant.Contains(info.PathId)) continue;
                if (_context.Deserialize(scene, info) is not { } renderer) continue;
                if (!renderer["m_Enabled"].IsDummy && !Enabled(renderer["m_Enabled"])) continue;

                var gameObject = renderer["m_GameObject"]["m_PathID"].AsLong;
                if (!Shown(gameObject)) continue;
                var isSkinned = info.TypeId == (int)AssetClassID.SkinnedMeshRenderer;
                var meshPointer = isSkinned ? renderer["m_Mesh"] : meshOf.GetValueOrDefault(gameObject);
                if (meshPointer is null || meshPointer["m_PathID"].AsLong == 0) continue;
                if (Resolve(scene, meshPointer) is not var (meshFile, meshBundle, meshInfo)) continue;

                var name = names.GetValueOrDefault(gameObject, "object");
                var vertices = VerticesOf(meshFile, meshBundle, meshInfo, name);
                if (vertices is null) continue;

                // A batched renderer draws a range of the joined mesh's submeshes, placed in the world
                // already (or under the batch's root, where it names one). A model on bones stands
                // where its bones put it, which with nothing playing is where the scene left them:
                // written as it stands, in the world, so nothing about bones goes out.
                var batch = renderer["m_StaticBatchInfo"];
                var first = batch.IsDummy ? 0 : batch["firstSubMesh"].AsInt;
                var count = batch.IsDummy ? 0 : batch["subMeshCount"].AsInt;
                var bones = isSkinned
                    ? renderer["m_Bones"]["Array"].Children.Select(b => b["m_PathID"].AsLong).ToList()
                    : [];
                var (key, id) = (meshBundle, meshInfo.PathId);
                float[] placed;
                if (bones.Count > 0 && vertices.Mesh.BindPoses.Count > 0 && vertices.Mesh.Get(VertexAttribute.BlendIndices) is not null)
                {
                    vertices = Posed(vertices.Mesh, bones.Select(b => transforms.ContainsKey(b) ? World(b) : Matrix.Identity).ToList());
                    (first, count, placed) = (0, vertices.Mesh.SubMeshes.Count, Matrix.Identity);
                    // Its own vertices, which no other object shares.
                    (key, id) = ("posed:" + map.Bundle, info.PathId);
                }
                else if (count > 0)
                {
                    var batchRoot = renderer["m_StaticBatchRoot"]["m_PathID"].AsLong;
                    placed = batchRoot != 0 ? World(batchRoot) : Matrix.Identity;
                }
                else
                {
                    (first, count) = (0, vertices.Mesh.SubMeshes.Count);
                    placed = transformOf.TryGetValue(gameObject, out var t) ? World(t) : Matrix.Identity;
                }
                var materials = renderer["m_Materials"]["Array"].Children
                    .Select(m => Resolve(scene, m) is var (f, b, i) ? MaterialOf(f, b, i) : null)
                    .ToList();
                var isSky = name.Contains("sky", StringComparison.OrdinalIgnoreCase)
                    || materials.Any(m => m is { } at && _materials[at]!["name"]!.GetValue<string>().Contains("sky", StringComparison.OrdinalIgnoreCase));

                var primitives = new List<(int Submesh, int? Material)>();
                for (var s = 0; s < count; s++)
                {
                    var submesh = first + s;
                    if (submesh >= vertices.Mesh.SubMeshes.Count) break;
                    var sub = vertices.Mesh.SubMeshes[submesh];
                    if (sub.Topology != 0 || sub.IndexCount < 3) continue;
                    primitives.Add((submesh, materials.Count == 0 ? null : materials[Math.Min(s, materials.Count - 1)]));
                }
                if (primitives.Count == 0) continue;

                var mesh = MeshFor(key, id, vertices, primitives, name);
                var node = new JsonObject { ["name"] = name, ["mesh"] = mesh };
                if (!IsIdentity(placed)) node["matrix"] = Numbers(Sided(placed));
                _nodes.Add(node);
                var group = isSky ? sky : Parked(vertices, placed) ? parked : root;
                ((JsonArray)group["children"]!).Add(_nodes.Count - 1);
                objects++;
            }

            if (objects == 0) throw new InvalidOperationException($"'{map.Name}' draws nothing this can write.");

            var roots = new JsonArray();
            foreach (var top in new[] { root, sky, parked })
            {
                if (((JsonArray)top["children"]!).Count == 0) continue;
                _nodes.Add(top);
                roots.Add(_nodes.Count - 1);
            }
            var gltf = new JsonObject
            {
                ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "PGAssetTool" },
                ["scene"] = 0,
                ["scenes"] = new JsonArray { new JsonObject { ["name"] = map.Name, ["nodes"] = roots } },
                ["nodes"] = _nodes,
                ["meshes"] = _meshes,
            };
            if (_materials.Count > 0) gltf["materials"] = _materials;
            if (_textures.Count > 0)
            {
                gltf["textures"] = _textures;
                gltf["images"] = _images;
                gltf["samplers"] = new JsonArray { new JsonObject { ["wrapS"] = 10497, ["wrapT"] = 10497 } };
            }
            if (_transformed) gltf["extensionsUsed"] = new JsonArray { "KHR_texture_transform" };
            gltf["accessors"] = _accessors;
            gltf["bufferViews"] = _views;
            var binary = _binary.ToArray();
            gltf["buffers"] = new JsonArray { new JsonObject { ["byteLength"] = binary.Length } };

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            GlbWriter.WriteContainer(path, gltf, binary);
            return new Exported(path, objects, _meshes.Count, _materials.Count, _images.Count, _skipped);
        }

        /// Whether an object is parked out of sight: kilometres from anything, where the game keeps
        /// what it moves into place during a match — the battle royale maps keep their supply
        /// chests and shuttle ten kilometres below the island. Told by where the middle of its mesh
        /// ends up, which for a batch is the middle of the whole batch and so never this far.
        private bool Parked(Vertices vertices, float[] placed)
        {
            if (vertices.Attributes["POSITION"] is not { } index || _accessors[index.GetValue<int>()] is not JsonObject accessor) return false;
            if (accessor["min"] is not JsonArray min || accessor["max"] is not JsonArray max) return false;
            // The accessor is in glTF's terms; the matrix in Unity's.
            var (x, y, z) = ((min[0]!.GetValue<float>() + max[0]!.GetValue<float>()) / 2,
                (min[1]!.GetValue<float>() + max[1]!.GetValue<float>()) / 2,
                -(min[2]!.GetValue<float>() + max[2]!.GetValue<float>()) / 2);
            var m = placed;
            float[] centre = [m[0] * x + m[4] * y + m[8] * z + m[12], m[1] * x + m[5] * y + m[9] * z + m[13], m[2] * x + m[6] * y + m[10] * z + m[14]];
            return centre.Any(c => MathF.Abs(c) > OutOfSight);
        }

        private static bool Enabled(AssetTypeValueField field)
            => field.TypeName switch { "bool" => field.AsBool, _ => field.AsInt != 0 };

        /// Where a pointer leads: this file, another file of this bundle, or a file of another bundle
        /// found by its CAB name.
        private (AssetsFileInstance File, string Bundle, AssetFileInfo Info)? Resolve(AssetsFileInstance from, AssetTypeValueField pointer)
        {
            var fileId = pointer["m_FileID"].AsInt;
            var pathId = pointer["m_PathID"].AsLong;
            if (pathId == 0) return null;

            (AssetsFileInstance File, string Bundle) target;
            if (fileId == 0)
            {
                target = _files.Values.First(f => ReferenceEquals(f.File, from));
            }
            else
            {
                var externals = from.file.Metadata.Externals;
                if (fileId < 1 || fileId > externals.Count) return null;
                var external = externals[fileId - 1].PathName;
                var name = external[(external.LastIndexOf('/') + 1)..];
                if (!_files.TryGetValue(name, out target))
                {
                    // The game's own built-in resources are not in any bundle; nothing in them is a
                    // map's mesh or picture.
                    if (!external.StartsWith("archive:", StringComparison.OrdinalIgnoreCase)) return null;
                    _cabs ??= CabIndex.Build(bundles);
                    if (_cabs.BundleFor(name) is not { } bundle) return null;
                    try
                    {
                        foreach (var file in bundles.OpenAll(bundle)) _files.TryAdd(file.name, (file, bundle));
                    }
                    catch (Exception e) when (e is IOException or FileNotFoundException) { return null; }
                    if (!_files.TryGetValue(name, out target)) return null;
                }
            }

            return target.File.file.GetAssetInfo(pathId) is { } info ? (target.File, target.Bundle, info) : null;
        }

        private Vertices? VerticesOf(AssetsFileInstance file, string bundle, AssetFileInfo info, string owner)
        {
            if (_vertices.TryGetValue((bundle, info.PathId), out var known)) return known;

            Vertices? made = null;
            try
            {
                if (_context.Deserialize(file, info) is not { } field) return _vertices[(bundle, info.PathId)] = null;
                var mesh = UnityMesh.Read(field, (where, offset, size) => bundles.ReadResource(bundle, where, offset, size));
                if (mesh.VertexCount == 0 || mesh.Get(VertexAttribute.Position) is null)
                    _skipped.Add($"{owner}: its mesh has no vertices");
                else made = new Vertices(mesh, Attributes(mesh));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or IndexOutOfRangeException)
            {
                _skipped.Add($"{owner}: its mesh could not be read ({e.Message})");
            }

            return _vertices[(bundle, info.PathId)] = made;
        }

        /// A skinned mesh with its vertices where its bones put them: each the weighted sum of where
        /// each of its bones would carry it from its bind pose. A vertex with no weights rides on its
        /// first bone at full weight, as these meshes are rigged.
        private Vertices Posed(UnityMesh mesh, IReadOnlyList<float[]> bones)
        {
            var positions = mesh.Get(VertexAttribute.Position)!;
            var normals = mesh.Get(VertexAttribute.Normal);
            var pw = mesh.Dimensions.GetValueOrDefault(VertexAttribute.Position, 3);
            var nw = mesh.Dimensions.GetValueOrDefault(VertexAttribute.Normal, 3);
            var indices = mesh.Get(VertexAttribute.BlendIndices)!;
            var slots = mesh.Dimensions.GetValueOrDefault(VertexAttribute.BlendIndices, 1);
            var weights = mesh.Get(VertexAttribute.BlendWeight);
            var heavy = mesh.Dimensions.GetValueOrDefault(VertexAttribute.BlendWeight, 1);

            var skinning = bones.Select((world, b) => b < mesh.BindPoses.Count
                ? Matrix.Times(world, Matrix.FromRows(mesh.BindPoses[b]))
                : world).ToList();

            var moved = new float[mesh.VertexCount * 3];
            var turned = normals is null || nw < 3 ? null : new float[mesh.VertexCount * 3];
            for (var v = 0; v < mesh.VertexCount; v++)
            {
                var (px, py, pz) = (positions[v * pw], positions[v * pw + 1], positions[v * pw + 2]);
                float x = 0, y = 0, z = 0, nx = 0, ny = 0, nz = 0, total = 0;
                for (var slot = 0; slot < Math.Min(slots, 4); slot++)
                {
                    var at = v * slots + slot;
                    if (at >= indices.Length) break;
                    var w = weights is null
                        ? slot == 0 ? 1f : 0f
                        : slot < heavy && v * heavy + slot < weights.Length ? weights[v * heavy + slot] : 0f;
                    var bone = (int)indices[at];
                    if (!(w > 0) || bone < 0 || bone >= skinning.Count) continue;
                    var m = skinning[bone];
                    x += w * (m[0] * px + m[4] * py + m[8] * pz + m[12]);
                    y += w * (m[1] * px + m[5] * py + m[9] * pz + m[13]);
                    z += w * (m[2] * px + m[6] * py + m[10] * pz + m[14]);
                    if (turned is not null)
                    {
                        var (ax, ay, az) = (normals![v * nw], normals[v * nw + 1], normals[v * nw + 2]);
                        nx += w * (m[0] * ax + m[4] * ay + m[8] * az);
                        ny += w * (m[1] * ax + m[5] * ay + m[9] * az);
                        nz += w * (m[2] * ax + m[6] * ay + m[10] * az);
                    }
                    total += w;
                }
                if (total <= 0)
                {
                    var m = skinning.Count > 0 ? skinning[0] : Matrix.Identity;
                    x = m[0] * px + m[4] * py + m[8] * pz + m[12];
                    y = m[1] * px + m[5] * py + m[9] * pz + m[13];
                    z = m[2] * px + m[6] * py + m[10] * pz + m[14];
                    total = 1;
                }
                (moved[v * 3], moved[v * 3 + 1], moved[v * 3 + 2]) = (x / total, y / total, z / total);
                if (turned is not null) (turned[v * 3], turned[v * 3 + 1], turned[v * 3 + 2]) = (nx, ny, nz);
            }

            var attributes = new Dictionary<VertexAttribute, float[]>(mesh.Attributes) { [VertexAttribute.Position] = moved };
            var dimensions = new Dictionary<VertexAttribute, int>(mesh.Dimensions) { [VertexAttribute.Position] = 3 };
            if (turned is not null)
            {
                attributes[VertexAttribute.Normal] = turned;
                dimensions[VertexAttribute.Normal] = 3;
            }
            var posed = mesh with { Attributes = attributes, Dimensions = dimensions };
            return new Vertices(posed, Attributes(posed));
        }

        /// A mesh's vertices into the file, in glTF's terms: Z negated, pictures read from the top.
        private JsonObject Attributes(UnityMesh mesh)
        {
            var positions = mesh.Get(VertexAttribute.Position)!;
            var n = mesh.VertexCount;
            var wide = mesh.Dimensions.GetValueOrDefault(VertexAttribute.Position, 3);
            var place = new float[n * 3];
            for (var v = 0; v < n; v++)
                (place[v * 3], place[v * 3 + 1], place[v * 3 + 2]) =
                    (Finite(positions[v * wide]), Finite(positions[v * wide + 1]), Finite(-positions[v * wide + 2]));
            var attributes = new JsonObject { ["POSITION"] = Floats(place, "VEC3", bounded: true) };

            if (mesh.Get(VertexAttribute.Normal) is { } normals && mesh.Dimensions.GetValueOrDefault(VertexAttribute.Normal, 3) is var nw and >= 3)
            {
                var turned = new float[n * 3];
                for (var v = 0; v < n; v++)
                {
                    var (x, y, z) = (normals[v * nw], normals[v * nw + 1], -normals[v * nw + 2]);
                    var length = MathF.Sqrt(x * x + y * y + z * z);
                    (turned[v * 3], turned[v * 3 + 1], turned[v * 3 + 2]) = length > 1e-6f && float.IsFinite(length)
                        ? (x / length, y / length, z / length) : (0f, 1f, 0f);
                }
                attributes["NORMAL"] = Floats(turned, "VEC3");
            }
            if (mesh.Get(VertexAttribute.TexCoord0) is { } uv && mesh.Dimensions.GetValueOrDefault(VertexAttribute.TexCoord0, 2) is var uw and >= 2)
            {
                var mapped = new float[n * 2];
                for (var v = 0; v < n; v++)
                    (mapped[v * 2], mapped[v * 2 + 1]) = (Finite(uv[v * uw]), Finite(1f - uv[v * uw + 1]));
                attributes["TEXCOORD_0"] = Floats(mapped, "VEC2");
            }
            return attributes;
        }

        private int MeshFor(string bundle, long pathId, Vertices vertices, List<(int Submesh, int? Material)> primitives, string name)
        {
            var shape = $"{bundle}|{pathId}|{string.Join(",", primitives.Select(p => $"{p.Submesh}:{p.Material}"))}";
            if (_meshByShape.TryGetValue(shape, out var known)) return known;

            var list = new JsonArray();
            foreach (var (submesh, material) in primitives)
            {
                if (!_indices.TryGetValue((bundle, pathId, submesh), out var accessor))
                {
                    var sub = vertices.Mesh.SubMeshes[submesh];
                    var triangles = new uint[sub.IndexCount / 3 * 3];
                    for (var i = 0; i + 2 < sub.IndexCount; i += 3)
                    {
                        // Reversed, to match the negated axis.
                        triangles[i] = (uint)(vertices.Mesh.Indices[sub.IndexStart + i + 2] + sub.BaseVertex);
                        triangles[i + 1] = (uint)(vertices.Mesh.Indices[sub.IndexStart + i + 1] + sub.BaseVertex);
                        triangles[i + 2] = (uint)(vertices.Mesh.Indices[sub.IndexStart + i] + sub.BaseVertex);
                    }
                    var bytes = new byte[triangles.Length * 4];
                    Buffer.BlockCopy(triangles, 0, bytes, 0, bytes.Length);
                    accessor = Accessor(View(bytes), triangles.Length, "SCALAR", 5125);
                    _indices[(bundle, pathId, submesh)] = accessor;
                }

                var primitive = new JsonObject
                {
                    ["attributes"] = vertices.Attributes.DeepClone(),
                    ["indices"] = accessor,
                    ["mode"] = 4,
                };
                if (material is { } m) primitive["material"] = m;
                list.Add(primitive);
            }

            _meshes.Add(new JsonObject { ["name"] = vertices.Mesh.Name.Length > 0 ? vertices.Mesh.Name : name, ["primitives"] = list });
            return _meshByShape[shape] = _meshes.Count - 1;
        }

        /// A material as glTF says one: its main picture with its tiling, its colour, and whether it
        /// is see-through — read from the render queue, which says it whatever the shader is.
        private int? MaterialOf(AssetsFileInstance file, string bundle, AssetFileInfo info)
        {
            if (_materialByAsset.TryGetValue((bundle, info.PathId), out var known)) return known;
            _materialByAsset[(bundle, info.PathId)] = null;
            if (_context.Deserialize(file, info) is not { } material) return null;

            var properties = material["m_SavedProperties"];
            var entry = new JsonObject { ["name"] = material["m_Name"].AsString };
            var pbr = new JsonObject { ["metallicFactor"] = 0, ["roughnessFactor"] = 1 };

            if (Property(properties["m_Colors"], "_Color", "_BaseColor", "_MainColor", "_TintColor") is { } color)
                pbr["baseColorFactor"] = Numbers([Linear(color["r"].AsFloat), Linear(color["g"].AsFloat), Linear(color["b"].AsFloat), Math.Clamp(color["a"].AsFloat, 0, 1)]);

            var texEnvs = properties["m_TexEnvs"];
            var main = Property(texEnvs, "_MainTex", "_BaseMap", "_Albedo", "_Diffuse")
                ?? (texEnvs.IsDummy ? null : texEnvs["Array"].Children
                    .Select(p => p["second"])
                    .FirstOrDefault(t => t["m_Texture"]["m_PathID"].AsLong != 0));
            if (main is not null && Resolve(file, main["m_Texture"]) is var (tf, tb, ti) && ImageOf(tf, tb, ti) is { } image)
            {
                _textures.Add(new JsonObject { ["source"] = image, ["sampler"] = 0 });
                var reference = new JsonObject { ["index"] = _textures.Count - 1 };
                var (sx, sy) = (main["m_Scale"]["x"].AsFloat, main["m_Scale"]["y"].AsFloat);
                var (ox, oy) = (main["m_Offset"]["x"].AsFloat, main["m_Offset"]["y"].AsFloat);
                if (MathF.Abs(sx - 1) > 1e-5f || MathF.Abs(sy - 1) > 1e-5f || MathF.Abs(ox) > 1e-5f || MathF.Abs(oy) > 1e-5f)
                {
                    // Unity tiles from the bottom of a picture and glTF from the top, which moves the
                    // offset by however much of the picture the tiling has taken away.
                    reference["extensions"] = new JsonObject
                    {
                        ["KHR_texture_transform"] = new JsonObject
                        {
                            ["scale"] = Numbers([sx, sy]),
                            ["offset"] = Numbers([ox, 1 - sy - oy]),
                        },
                    };
                    _transformed = true;
                }
                pbr["baseColorTexture"] = reference;
            }
            entry["pbrMetallicRoughness"] = pbr;

            var queue = material["m_CustomRenderQueue"].IsDummy ? -1 : material["m_CustomRenderQueue"].AsInt;
            if (queue >= 3000) entry["alphaMode"] = "BLEND";
            else if (queue >= 2450)
            {
                entry["alphaMode"] = "MASK";
                if (Property(properties["m_Floats"], "_Cutoff") is { } cutoff) entry["alphaCutoff"] = cutoff.AsFloat;
            }

            _materials.Add(entry);
            return _materialByAsset[(bundle, info.PathId)] = _materials.Count - 1;
        }

        private static AssetTypeValueField? Property(AssetTypeValueField list, params string[] names)
        {
            if (list.IsDummy) return null;
            foreach (var name in names)
                foreach (var pair in list["Array"].Children)
                    if (pair["first"].AsString == name)
                        return pair["second"];
            return null;
        }

        private int? ImageOf(AssetsFileInstance file, string bundle, AssetFileInfo info)
        {
            if (_imageByAsset.TryGetValue((bundle, info.PathId), out var known)) return known;
            int? made = null;
            try
            {
                if (info.TypeId == (int)AssetClassID.Texture2D && _context.Deserialize(file, info) is { } field
                    && AssetPreview.Texture(bundles, bundle, field) is { } picture)
                {
                    var rgba = new byte[picture.Bgra.Length];
                    for (var i = 0; i + 3 < rgba.Length; i += 4)
                        (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (picture.Bgra[i + 2], picture.Bgra[i + 1], picture.Bgra[i], picture.Bgra[i + 3]);
                    using var png = new MemoryStream();
                    new StbImageWriteSharp.ImageWriter().WritePng(rgba, picture.Width, picture.Height,
                        StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, png);
                    _images.Add(new JsonObject
                    {
                        ["name"] = field["m_Name"].AsString,
                        ["bufferView"] = View(png.ToArray()),
                        ["mimeType"] = "image/png",
                    });
                    made = _images.Count - 1;
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
            {
                _skipped.Add($"a picture could not be read ({e.Message})");
            }
            return _imageByAsset[(bundle, info.PathId)] = made;
        }

        private int View(byte[] bytes)
        {
            while (_binary.Length % 4 != 0) _binary.WriteByte(0);
            _views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = _binary.Length, ["byteLength"] = bytes.Length });
            _binary.Write(bytes);
            return _views.Count - 1;
        }

        private int Accessor(int view, int count, string type, int componentType)
        {
            _accessors.Add(new JsonObject { ["bufferView"] = view, ["componentType"] = componentType, ["count"] = count, ["type"] = type });
            return _accessors.Count - 1;
        }

        private int Floats(float[] values, string type, bool bounded = false)
        {
            var width = type == "VEC2" ? 2 : 3;
            var bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            var at = Accessor(View(bytes), values.Length / width, type, 5126);
            if (bounded && values.Length >= width)
            {
                var min = Enumerable.Repeat(float.MaxValue, width).ToArray();
                var max = Enumerable.Repeat(float.MinValue, width).ToArray();
                for (var i = 0; i < values.Length; i++)
                {
                    min[i % width] = Math.Min(min[i % width], values[i]);
                    max[i % width] = Math.Max(max[i % width], values[i]);
                }
                _accessors[at]!["min"] = Numbers(min);
                _accessors[at]!["max"] = Numbers(max);
            }
            return at;
        }
    }

    private static float[] Trs(AssetTypeValueField t)
    {
        var (p, r, s) = (t["m_LocalPosition"], t["m_LocalRotation"], t["m_LocalScale"]);
        float[] trs =
        [
            p["x"].AsFloat, p["y"].AsFloat, p["z"].AsFloat,
            r["x"].AsFloat, r["y"].AsFloat, r["z"].AsFloat, r["w"].AsFloat,
            s["x"].AsFloat, s["y"].AsFloat, s["z"].AsFloat,
        ];
        return trs.All(float.IsFinite) ? trs : [0, 0, 0, 0, 0, 0, 1, 1, 1, 1];
    }

    /// A column-major matrix from Unity's side to glTF's: S·M·S with S negating Z.
    private static float[] Sided(float[] m)
    {
        var sided = new float[16];
        for (var column = 0; column < 4; column++)
            for (var row = 0; row < 4; row++)
                sided[column * 4 + row] = row == 2 ^ column == 2 ? -m[column * 4 + row] : m[column * 4 + row];
        return sided;
    }

    private static bool IsIdentity(float[] m)
    {
        var identity = Matrix.Identity;
        for (var i = 0; i < 16; i++)
            if (MathF.Abs(m[i] - identity[i]) > 1e-6f) return false;
        return true;
    }

    /// Unity's colours are as they look; glTF's are as light adds up.
    private static float Linear(float c)
    {
        c = Math.Clamp(c, 0, 1);
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    private static float Finite(float v) => float.IsFinite(v) ? v : 0f;

    private static JsonArray Numbers(float[] values) => new(values.Select(v => (JsonNode)Finite(v)).ToArray());
}
