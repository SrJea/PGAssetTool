using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Export.Meshes;

namespace PGAssetTool.Core.Preview;

/// Which way round a weapon is, from the prefab rather than from the shape of it.
///
/// The renderer stands a model up by sorting its bounding box, which says which side is longest and
/// nothing about which end is the barrel — so left to that, half the weapons in the game open
/// pointing at the viewer's left and half at their right, with no telling which without looking.
///
/// Guessing it from the geometry does not work. Four signals were measured against 157 weapons the
/// author labelled by hand, and all four read the same thing — that one end of a weapon carries
/// more of it than the other. The best of them, counting vertices, called 74.5%. The thinner-end
/// guess that DESIGN.md records at 46 of 73 came out at 61.1% over the larger set.
///
/// The prefab answers it outright: a gun has a `GunFlash` at its muzzle, a
/// `BulletSpawnPoint` where the shot leaves it, and a `Point_Arm_Left` where the hand goes. Over
/// the same 157 the first of those the prefab carries is right for 150 of the 151 it answers for.
public static class Facing
{
    /// Objects at the far end of a weapon, and at the near one, in the order they are trusted.
    ///
    /// `GunFlash` first because it is right for all 147 it answers for; `BulletSpawnPoint` next
    /// at 148 of 149; `Point_Arm_Left` last at 149 of 151, and it is the only one a blade has.
    public static IReadOnlyList<(string Name, bool AtMuzzle)> Landmarks { get; } =
    [
        ("GunFlash", true),
        ("BulletSpawnPoint", true),
        ("Point_Arm_Left", false),
    ];

    /// How to stand this model up so it opens pointing the way every other one does.
    ///
    /// The renderer's own bounding box, turned a half circle about the vertical when the muzzle would
    /// otherwise be on the wrong side. A half circle rather than a mirror: negating one axis alone
    /// would reverse the model's handedness, and every texture with writing on it would read
    /// backwards.
    ///
    /// A model whose prefab carries none of the landmarks — six of the 157 — is left as the box
    /// laid it.
    public static MeshRenderer.Basis Standing(
        BundleSet bundles, string bundle, UnityMesh mesh, long meshPathId)
    {
        var standing = MeshRenderer.Standing(mesh);
        var found = LandmarksIn(bundles, bundle, mesh, meshPathId);
        if (found.Count == 0) return standing;

        var centre = Centre(mesh);

        // Which way along the model's long axis the screen's right lies, asked of the view the
        // preview opens at rather than assumed: the opening angle is measured against the game's
        // own pictures and can change, and an assumption about it buried here would turn every
        // weapon in the game round without anything saying so.
        //
        // Only the long axis is asked about, because the question is which *end* the barrel is on,
        // and the turn that answers it is a half circle about the vertical — which negates this and
        // nothing else. Projecting the whole offset instead lets the other two axes swing into the
        // answer as the opening angle changes, and at this one they very nearly cancel it.
        var rightwards = MeshRenderer.View(new Camera()).Rx;

        foreach (var (name, atMuzzle) in Landmarks)
        {
            if (!found.TryGetValue(name, out var at)) continue;

            // How far along that axis the landmark sits, from the middle of the model: an absolute
            // position says nothing about which end of it anything is on.
            var along = (at.X - centre.X) * standing.Rx
                + (at.Y - centre.Y) * standing.Ry
                + (at.Z - centre.Z) * standing.Rz;

            if (along == 0) continue;

            // A muzzle belongs on the right and a hand on the left; a weapon whose landmark says
            // otherwise is turned round.
            return (along * rightwards > 0) == atMuzzle
                ? standing
                : standing with
                {
                    Rx = -standing.Rx, Ry = -standing.Ry, Rz = -standing.Rz,
                    Fx = -standing.Fx, Fy = -standing.Fy, Fz = -standing.Fz,
                };
        }

        return standing;
    }

    /// How to stand up an item the game draws standing: an avatar, a transport, a glider.
    ///
    /// The bounding box cannot answer it. Sorting the sides puts the longest across the screen,
    /// which is the barrel on a weapon and the height on an avatar — so `avatar_fortune_speaker`
    /// opened lying on its side, head to the right, and a transport opened seen from above.
    ///
    /// These are not meshes standing on their own: a prefab places them where the game draws them,
    /// and in that prefab's space Y is up. So the model is stood up by the prefab's own axes.
    ///
    /// Which side faces the viewer is the prefab's answer too, and the game does not give the same
    /// one twice: a transport's nose and an avatar's face are not both the same way along Z. The
    /// named angles are how a view is turned to the one that was wanted; this says which way is up.
    /// <param name="home">
    /// The matrix taking the prefab's space into the one this model's vertices are written in,
    /// column-major — <see cref="Skeleton.Home"/>. Its first three columns are the prefab's own X,
    /// Y and Z in that space.
    /// </param>
    public static MeshRenderer.Basis Upright(float[] home)
    {
        var right = Unit(0);
        var up = Unit(1);
        var forward = Unit(2);
        if (up is (0, 0, 0)) return MeshRenderer.Basis.Identity;

        return MeshRenderer.Basis.Of(
            right.X, right.Y, right.Z, up.X, up.Y, up.Z, forward.X, forward.Y, forward.Z);

        (float X, float Y, float Z) Unit(int column)
        {
            var (x, y, z) = (home[column * 4], home[column * 4 + 1], home[column * 4 + 2]);
            var length = MathF.Sqrt(x * x + y * y + z * z);
            return length > 0 ? (x / length, y / length, z / length) : (0, 0, 0);
        }
    }

    /// Where each landmark sits, in the space the mesh's own vertices are written in.
    ///
    /// The awkward part is that space. Vertices are written against whatever the model was parented
    /// to when it was rigged, which the prefab no longer says — <see cref="Skeleton"/> works it out
    /// from the model at rest, and the same matrix carries a prefab position into it.
    public static IReadOnlyDictionary<string, (float X, float Y, float Z)> LandmarksIn(
        BundleSet bundles, string bundle, UnityMesh mesh, long meshPathId)
    {
        var found = new Dictionary<string, (float, float, float)>(StringComparer.Ordinal);

        if (Skeleton.For(bundles, bundle, meshPathId) is not { } skeleton) return found;
        if (skeleton.Bones.Count == 0 || mesh.BindPoses.Count == 0) return found;

        AssetsFileInstance file;
        try { file = bundles.Open(bundle); }
        catch (Exception e) when (e is IOException or FileNotFoundException) { return found; }

        float[] home;
        try
        {
            home = Matrix.Inverse(Matrix.Times(
                skeleton.World(null, 0)[skeleton.Bones[0]], Matrix.FromRows(mesh.BindPoses[0])));
        }
        catch (IndexOutOfRangeException)
        {
            return found;
        }

        var root = RootOf(bundles, file, meshPathId, out var chain);
        if (root == 0) return found;

        // Every GameObject carrying a landmark's name, in one pass over the bundle rather than one
        // pass per landmark: a prefab bundle holds thousands of GameObjects.
        var wanted = Landmarks.Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
        var named = new Dictionary<string, List<AssetFileInfo>>(StringComparer.Ordinal);
        foreach (var info in file.file.AssetInfos)
        {
            if (info.TypeId != (int)AssetClassID.GameObject) continue;
            var name = bundles.Context.NameOf(file, info);
            if (!wanted.Contains(name)) continue;
            if (!named.TryGetValue(name, out var holders)) named[name] = holders = [];
            holders.Add(info);
        }

        foreach (var (name, _) in Landmarks)
        {
            if (Find(bundles, file, named.GetValueOrDefault(name) ?? [], root, chain) is not { } transform) continue;
            if (WorldOf(bundles, file, transform) is not { } world) continue;

            found[name] = (
                home[0] * world[12] + home[4] * world[13] + home[8] * world[14] + home[12],
                home[1] * world[12] + home[5] * world[13] + home[9] * world[14] + home[13],
                home[2] * world[12] + home[6] * world[13] + home[10] * world[14] + home[14]);
        }

        return found;
    }

    /// The top of the hierarchy this mesh hangs in, which is the weapon's own prefab root.
    ///
    /// Found through the renderer that names this mesh rather than by any name: a bundle holds a
    /// dozen weapons and every one of them has a `BulletSpawnPoint`, so a name is not an address.
    /// <param name="chain">
    /// Every transform between the renderer and the root, so a candidate that joins the chain
    /// partway up is recognised without walking to the top again.
    /// </param>
    private static long RootOf(
        BundleSet bundles, AssetsFileInstance file, long meshPathId, out HashSet<long> chain)
    {
        chain = [];

        long start = 0;
        foreach (var info in file.file.AssetInfos)
        {
            if (info.TypeId != (int)AssetClassID.SkinnedMeshRenderer) continue;

            var renderer = bundles.Context.Deserialize(file, info);
            if (renderer is null || renderer["m_Mesh"]["m_PathID"].AsLong != meshPathId) continue;

            var owner = renderer["m_GameObject"];
            if (owner.IsDummy) continue;

            var held = file.file.GetAssetInfo(owner["m_PathID"].AsLong);
            var field = held is null ? null : bundles.Context.Deserialize(file, held);
            if (field is not null) start = TransformIn(file, field);
            break;
        }

        if (start == 0) return 0;

        for (var at = start; at != 0;)
        {
            chain.Add(at);

            var info = file.file.GetAssetInfo(at);
            var field = info is null ? null : bundles.Context.Deserialize(file, info);
            var father = field?["m_Father"];

            if (father is null || father.IsDummy || father["m_PathID"].AsLong == 0) return at;
            at = father["m_PathID"].AsLong;
        }

        return 0;
    }

    /// The transform of the first of these GameObjects whose ancestors reach the given root.
    private static long? Find(
        BundleSet bundles, AssetsFileInstance file, IEnumerable<AssetFileInfo> holders, long root, HashSet<long> chain)
    {
        foreach (var info in holders)
        {
            var field = bundles.Context.Deserialize(file, info);
            if (field is null) continue;

            var transform = TransformIn(file, field);
            if (transform != 0 && Reaches(bundles, file, transform, root, chain)) return transform;
        }

        return null;
    }

    private static bool Reaches(
        BundleSet bundles, AssetsFileInstance file, long transform, long root, HashSet<long> chain)
    {
        for (var at = transform; at != 0;)
        {
            if (at == root || chain.Contains(at)) return true;

            var info = file.file.GetAssetInfo(at);
            var field = info is null ? null : bundles.Context.Deserialize(file, info);
            var father = field?["m_Father"];
            if (father is null || father.IsDummy) return false;

            at = father["m_PathID"].AsLong;
        }

        return false;
    }

    private static long TransformIn(AssetsFileInstance file, AssetTypeValueField gameObject)
    {
        foreach (var component in gameObject["m_Component"]["Array"].Children)
        {
            var id = component["component"]["m_PathID"].AsLong;
            if (file.file.GetAssetInfo(id) is { TypeId: (int)AssetClassID.Transform }) return id;
        }

        return 0;
    }

    /// Composed up the chain to the root, the same way Skeleton composes its own — so the two land
    /// in the same space and `home` means what it says.
    private static float[]? WorldOf(BundleSet bundles, AssetsFileInstance file, long transform)
    {
        var locals = new List<float[]>();

        for (var at = transform; at != 0;)
        {
            var info = file.file.GetAssetInfo(at);
            var field = info is null ? null : bundles.Context.Deserialize(file, info);
            if (field is null) return null;

            var p = field["m_LocalPosition"];
            var r = field["m_LocalRotation"];
            var s = field["m_LocalScale"];

            locals.Add([
                p["x"].AsFloat, p["y"].AsFloat, p["z"].AsFloat,
                r["x"].AsFloat, r["y"].AsFloat, r["z"].AsFloat, r["w"].AsFloat,
                s["x"].AsFloat, s["y"].AsFloat, s["z"].AsFloat,
            ]);

            var father = field["m_Father"];
            if (father.IsDummy) break;
            at = father["m_PathID"].AsLong;
        }

        var world = Matrix.Identity;
        for (var i = locals.Count - 1; i >= 0; i--)
            world = Matrix.Times(world, Matrix.Compose(locals[i]));

        return world;
    }

    /// The middle of the bounding box, which is the point the renderer builds its frame around —
    /// the same box the renderer measures, so that the two cannot come to different answers.
    private static (float X, float Y, float Z) Centre(UnityMesh mesh) => MeshBounds.Of(mesh).Centre;
}
