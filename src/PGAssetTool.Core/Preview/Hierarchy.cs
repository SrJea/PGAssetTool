using System.Runtime.CompilerServices;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Preview;

/// One file's objects as the preview asks about them, read once and kept against the file.
///
/// Every question in <see cref="Skeleton"/> — whether the game draws a mesh, where each copy of it
/// hangs, what rig moves it — was answered by walking the file's whole table and deserializing
/// every renderer in it to see which mesh it names. A bundle holds up to two thousand objects and
/// an item asks three of those questions per mesh, so one avatar read `avatars_43` eighteen times
/// over: 35ms to find six placements before a single rig was built.
///
/// The transforms go the same way. A rig read whole walks the prefab from its tops down, and that
/// walk was paid once per placement — a transport places parts 33 times over one hierarchy of a few
/// hundred objects, each of them deserialized twice on every pass, once for the transform and once
/// for the name.
///
/// Kept against the `AssetsFileInstance` rather than against the bundle's name, so closing the
/// bundles — which a reload does — drops it with them. One reader at a time, as everything else
/// reading these files assumes.
internal sealed class Hierarchy
{
    /// One renderer that names a mesh: which kind it is, where to read it from, and the object it
    /// hangs off. `Enabled` is the renderer's own switch, which only a skinned one carries; what
    /// says the same for a mesh filter is a MeshRenderer beside it, and that is asked for the few
    /// filters that name the mesh being asked about rather than for all of them here.
    internal readonly record struct Drawing(bool Skinned, AssetFileInfo Info, long Owner, bool Enabled);

    /// One object of the hierarchy: whose child it is, what it is called, where it sits, and what
    /// hangs under it.
    internal readonly record struct Node(long Parent, string Name, float[] Local, long[] Children);

    private static readonly ConditionalWeakTable<AssetsFileInstance, Hierarchy> Known = new();

    public static Hierarchy Of(BundleSet bundles, AssetsFileInstance file)
        => Known.GetValue(file, _ => new Hierarchy(bundles, file));

    private readonly BundleSet _bundles;
    private readonly AssetsFileInstance _file;
    private readonly Dictionary<long, List<Drawing>> _drawing = [];
    private readonly Dictionary<long, Node?> _objects = [];
    private readonly Dictionary<long, long> _transforms = [];

    private Hierarchy(BundleSet bundles, AssetsFileInstance file)
    {
        _bundles = bundles;
        _file = file;

        // In the order the file holds them, which is the order every caller here reads them in:
        // which of two arrangements of the same parts comes first is decided by it.
        foreach (var info in file.file.AssetInfos)
        {
            var skinned = info.TypeId == (int)AssetClassID.SkinnedMeshRenderer;
            if (!skinned && info.TypeId != (int)AssetClassID.MeshFilter) continue;

            var field = bundles.Context.Deserialize(file, info);
            if (field is null) continue;

            var mesh = field["m_Mesh"]["m_PathID"].AsLong;
            if (mesh == 0) continue;

            var owner = field["m_GameObject"];
            if (!_drawing.TryGetValue(mesh, out var drawing)) _drawing[mesh] = drawing = [];
            drawing.Add(new Drawing(
                skinned, info,
                owner.IsDummy ? 0 : owner["m_PathID"].AsLong,
                !skinned || field["m_Enabled"].IsDummy || field["m_Enabled"].AsInt != 0));
        }
    }

    /// Every renderer in this file that names the mesh, in the file's own order.
    public IReadOnlyList<Drawing> Drawn(long meshPathId)
        => _drawing.TryGetValue(meshPathId, out var drawing) ? drawing : [];

    public AssetFileInfo? Info(long pathId) => _file.file.GetAssetInfo(pathId);

    public AssetTypeValueField? Field(AssetFileInfo info) => _bundles.Context.Deserialize(_file, info);

    public AssetTypeValueField? Field(long pathId)
        => Info(pathId) is { } info ? Field(info) : null;

    /// The Transform among a GameObject's components.
    public long TransformOf(long gameObject)
    {
        if (_transforms.TryGetValue(gameObject, out var known)) return known;

        var transform = 0L;
        if (Field(gameObject) is { } field)
            foreach (var component in field["m_Component"]["Array"].Children)
            {
                var id = component["component"]["m_PathID"].AsLong;
                if (_file.file.GetAssetInfo(id) is { TypeId: (int)AssetClassID.Transform })
                {
                    transform = id;
                    break;
                }
            }

        return _transforms[gameObject] = transform;
    }

    /// One object of the hierarchy, or null for a transform that will not read.
    public Node? Object(long transform)
    {
        if (_objects.TryGetValue(transform, out var known)) return known;

        Node? node = null;
        if (Field(transform) is { } field)
        {
            var father = field["m_Father"];
            var holder = field["m_GameObject"];
            var named = holder.IsDummy ? null : Info(holder["m_PathID"].AsLong);
            var children = field["m_Children"];

            node = new Node(
                father.IsDummy ? 0 : father["m_PathID"].AsLong,
                named is null ? "?" : _bundles.Context.NameOf(_file, named),
                Trs(field),
                children.IsDummy
                    ? []
                    : [.. children["Array"].Children.Select(c => c["m_PathID"].AsLong).Where(id => id != 0)]);
        }

        return _objects[transform] = node;
    }

    private static float[] Trs(AssetTypeValueField transform)
    {
        var p = transform["m_LocalPosition"];
        var r = transform["m_LocalRotation"];
        var s = transform["m_LocalScale"];

        return
        [
            p["x"].AsFloat, p["y"].AsFloat, p["z"].AsFloat,
            r["x"].AsFloat, r["y"].AsFloat, r["z"].AsFloat, r["w"].AsFloat,
            s["x"].AsFloat, s["y"].AsFloat, s["z"].AsFloat,
        ];
    }
}
