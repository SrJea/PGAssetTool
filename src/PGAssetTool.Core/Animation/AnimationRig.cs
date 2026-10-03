using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Animation;

/// One Animation component: the object it sits on, everything under that object, and the clips it
/// plays.
///
/// A legacy clip names what it moves by the path from the object carrying the component — `""` for
/// that object itself, `pig_hammer/FPS_PLAYER_Arm_Right` two levels down. So the component is what
/// gives a clip's paths their meaning, and which objects exist under it is what a clip borrowed from
/// another weapon can be made to move here.
/// <param name="Paths">Every object under the component's own, by the path a clip would name it by.</param>
/// <param name="Clips">The clips the component lists, by where they are.</param>
/// <param name="Parents">Which of `Paths` each one hangs off, -1 for the component's own object.</param>
/// <param name="Locals">
/// Where each sits under its parent with nothing playing: position, rotation as a quaternion, scale,
/// as the transform keeps them. What a clip that leaves an object alone leaves it at.
/// </param>
public sealed record AnimationRig(
    string Bundle, long PathId, string Name, IReadOnlyList<string> Paths,
    IReadOnlyList<(string Bundle, long PathId)> Clips,
    IReadOnlyList<int> Parents, IReadOnlyList<float[]> Locals)
{
    /// Where each object sits with nothing playing, counted from whatever the component's own object
    /// hangs off — column-major, as `Preview.Matrix` keeps them.
    public IReadOnlyList<float[]> Rest()
    {
        var world = new float[Paths.Count][];
        for (var at = 0; at < Paths.Count; at++)
        {
            var local = Preview.Matrix.Compose(Locals[at]);
            world[at] = Parents[at] >= 0 ? Preview.Matrix.Times(world[Parents[at]], local) : local;
        }
        return world;
    }

    /// The component that plays this clip, looked for in the bundles given, or null if none does.
    public static AnimationRig? Playing(BundleSet bundles, IEnumerable<string> lookIn, string clipBundle, long clipPathId)
        => lookIn
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(b => In(bundles, b))
            .FirstOrDefault(r => r.Clips.Any(c => c.PathId == clipPathId
                && string.Equals(c.Bundle, clipBundle, StringComparison.OrdinalIgnoreCase)));

    /// Every Animation component in one bundle.
    public static IReadOnlyList<AnimationRig> In(BundleSet bundles, string bundle)
    {
        AssetsFileInstance file;
        try { file = bundles.Open(bundle); }
        catch (Exception e) when (e is IOException or KeyNotFoundException) { return []; }

        var graph = new BundleGraph(bundles);
        var rigs = new List<AnimationRig>();

        foreach (var info in file.file.GetAssetsOfType(AssetClassID.Animation))
        {
            var animation = bundles.Context.Deserialize(file, info);
            if (animation is null) continue;

            var clips = new List<(string, long)>();
            foreach (var pointer in animation["m_Animations"]["Array"].Children)
            {
                var fileId = pointer["m_FileID"].AsInt;
                var pathId = pointer["m_PathID"].AsLong;
                if (pathId == 0) continue;
                if (fileId == 0) { clips.Add((bundle, pathId)); continue; }
                if (graph.Resolve(file, fileId) is { } other) clips.Add((other.Bundle, pathId));
            }

            var holder = animation["m_GameObject"]["m_PathID"].AsLong;
            var name = NameOf(bundles, file, holder);
            var paths = new List<string>();
            var parents = new List<int>();
            var locals = new List<float[]>();
            if (TransformOf(bundles, file, holder) is { } transform)
                Walk(bundles, file, transform, "", -1, paths, parents, locals, 0);

            rigs.Add(new AnimationRig(bundle, info.PathId, name, paths, clips, parents, locals));
        }

        return rigs;
    }

    private static string NameOf(BundleSet bundles, AssetsFileInstance file, long gameObject)
        => file.file.GetAssetInfo(gameObject) is { } info ? bundles.Context.NameOf(file, info) : "";

    /// The Transform among a GameObject's components.
    private static long? TransformOf(BundleSet bundles, AssetsFileInstance file, long gameObject)
    {
        if (file.file.GetAssetInfo(gameObject) is not { } info) return null;
        var field = bundles.Context.Deserialize(file, info);
        if (field is null) return null;

        foreach (var entry in field["m_Component"]["Array"].Children)
        {
            var id = entry["component"]["m_PathID"].AsLong;
            if (entry["component"]["m_FileID"].AsInt != 0) continue;
            if (file.file.GetAssetInfo(id) is { } component
                && (component.TypeId == (int)AssetClassID.Transform || component.TypeId == (int)AssetClassID.RectTransform))
                return id;
        }
        return null;
    }

    /// Every object under this transform, depth first, by its path from where the walk began.
    /// The depth limit is a guard against a hierarchy that loops, which a bundle this tool did not
    /// write could hold.
    private static void Walk(
        BundleSet bundles, AssetsFileInstance file, long transform, string path, int parent,
        List<string> into, List<int> parents, List<float[]> locals, int depth)
    {
        if (depth > 64 || file.file.GetAssetInfo(transform) is not { } info) return;
        var field = bundles.Context.Deserialize(file, info);
        if (field is null) return;

        var at = into.Count;
        into.Add(path);
        parents.Add(parent);
        locals.Add(Trs(field));
        foreach (var child in field["m_Children"]["Array"].Children)
        {
            if (child["m_FileID"].AsInt != 0) continue;
            var id = child["m_PathID"].AsLong;
            if (file.file.GetAssetInfo(id) is not { } childInfo) continue;
            var childField = bundles.Context.Deserialize(file, childInfo);
            if (childField is null) continue;

            var name = NameOf(bundles, file, childField["m_GameObject"]["m_PathID"].AsLong);
            Walk(bundles, file, id, path.Length == 0 ? name : path + "/" + name, at, into, parents, locals, depth + 1);
        }
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
