using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Export.Meshes;

namespace PGAssetTool.Core.Preview;

/// One object in a model's hierarchy: where it sits, whose child it is, and what it is called all
/// the way up.
///
/// <param name="Path">
/// The names from the top down, joined by slashes. A clip names what it drives by the same path,
/// counted from whichever object carries the Animation component — so matching is done on the end
/// of this rather than the whole of it, and nothing has to work out where that component is.
/// </param>
public sealed record Joint(string Name, string Path, int Parent, float[] Local);

/// The bones of a skinned mesh and everything between them, read out of the prefab.
///
/// A weapon in this game is one skinned mesh on a handful of bones — the slide, the magazine, the
/// hands — and its animations move those bones. So posing it needs no scene and no second mesh:
/// evaluate the clip, walk the hierarchy once, and the vertices follow.
public sealed class Skeleton
{
    private Skeleton(IReadOnlyList<Joint> joints, IReadOnlyList<int> bones, int root, bool rigid = false)
        => (Joints, Bones, Root, Rigid) = (joints, bones, root, rigid);

    /// Whether this is one object carrying a plain mesh, rather than bones carrying a skinned one.
    /// See Unskinned.
    public bool Rigid { get; }

    /// Every object between the root of the model and its bones, parents before children.
    public IReadOnlyList<Joint> Joints { get; }

    /// Which joint each of the mesh's bones is, in the order its bind poses and blend indices use.
    public IReadOnlyList<int> Bones { get; }

    /// The joint the renderer itself hangs off. Kept for what it says about the model rather than
    /// for posing, which works the space out from the model at rest instead.
    public int Root { get; }

    /// Where everything sits with nothing playing, worked out once: posing is relative to it, and
    /// it is the same for every frame of every clip.
    private float[][]? _rest;

    private float[][] Rest => _rest ??= World(null, 0);

    /// The skeleton a mesh is skinned to, or null for a mesh nothing skins.
    ///
    /// Found the way the dressing is: nothing points from a mesh to the renderer that draws it, so
    /// the renderers are read and the one naming this mesh answers. Its `m_Bones` is the order
    /// everything else is in.
    /// <param name="under">
    /// The name of an object the renderer has to hang under, or null for the first renderer that
    /// names this mesh whatever it hangs under.
    ///
    /// Several renderers draw the same mesh and they disagree: a weapon's own prefab holds one, and
    /// the `<prefab>_info` prefab the game's shop shows holds another, arranged its own way — which
    /// is where #943's two swords are crossed, and where a transport's body comes back standing a
    /// metre from its own wheels. Whichever of them comes first in the bundle is what
    /// answering by mesh alone gives, and that is an accident of file order. Naming the root is how
    /// a caller says which arrangement it means.
    /// </param>
    /// <param name="whole">
    /// Read every object in the prefab rather than only the ones this mesh is skinned to. What that
    /// is for is on `Build`.
    /// </param>
    public static Skeleton? For(
        BundleSet bundles, string bundle, long meshPathId, string? under = null, bool whole = false)
    {
        AssetsFileInstance file;
        try { file = bundles.Open(bundle); }
        catch (Exception e) when (e is IOException or FileNotFoundException) { return null; }

        var read = Hierarchy.Of(bundles, file);

        foreach (var drawing in read.Drawn(meshPathId))
        {
            if (!drawing.Skinned) continue;

            var renderer = read.Field(drawing.Info);
            if (renderer is null) continue;
            if (under is not null && !RootedAt(bundles, file, renderer["m_GameObject"], under)) continue;

            var bones = renderer["m_Bones"]["Array"].Children
                .Select(b => b["m_PathID"].AsLong)
                .Where(id => id != 0)
                .ToList();

            if (bones.Count == 0) return null;

            // The renderer's own transform, found through the GameObject it hangs off. The bind
            // poses are written against this one.
            var owner = renderer["m_GameObject"];
            var mine = owner.IsDummy ? 0 : TransformOf(bundles, file, owner["m_PathID"].AsLong);

            return Build(bundles, file, bones, mine, whole: whole);
        }

        return Unskinned(bundles, file, meshPathId, under, whole);
    }

    /// One place a mesh is drawn: the renderer that draws it there, as a skeleton, and the name of
    /// every object it hangs under, its own first and the top of the hierarchy last.
    ///
    /// The names are what tells one placement from another, and every caller asks something
    /// different of them — which hierarchy this is, whether the game draws this copy as an outline
    /// shell — so what is handed over is where it hangs rather than a verdict about it.
    public sealed record Placement(Skeleton Skeleton, IReadOnlyList<string> Under)
    {
        /// The top of the hierarchy: the prefab this placement belongs to.
        public string Root => Under.Count > 0 ? Under[^1] : "";
    }

    /// Every place a mesh is drawn, in the order the file holds them.
    ///
    /// The game draws one copy of a mesh per renderer that names it, and a prefab puts the same mesh
    /// down more than once: a transport's wheels are one 72-vertex mesh placed ten times, and 82 of
    /// the 90 transports place something more than once — up to twenty times on
    /// `fps_all_terrain_vehicle`. <see cref="For"/> answers one of them, which is one wheel.
    ///
    /// `Root` is what tells one arrangement from another, and a caller takes the placements of one
    /// root rather than all of them: the same parts appear again under the `<item>_shop` prefab,
    /// arranged its own way, so drawing every placement there is draws each wheel twice, in two
    /// different places.
    public static IReadOnlyList<Placement> Every(
        BundleSet bundles, string bundle, long meshPathId, bool whole = false)
    {
        if (Opened(bundles, bundle) is not { } file) return [];

        var placements = new List<Placement>();

        foreach (var (skinned, renderer, mine) in Renderers(bundles, file, meshPathId))
        {
            Skeleton? skeleton;
            if (skinned)
            {
                var bones = renderer["m_Bones"]["Array"].Children
                    .Select(b => b["m_PathID"].AsLong)
                    .Where(id => id != 0)
                    .ToList();
                if (bones.Count == 0) continue;

                skeleton = Build(bundles, file, bones, mine, whole: whole);
            }
            else skeleton = Build(bundles, file, [mine], mine, rigid: true, whole: whole);

            if (skeleton is null) continue;

            placements.Add(new Placement(skeleton, Ancestry(bundles, file, mine)));
        }

        return placements;
    }

    /// Where each copy of a mesh hangs, as <see cref="Placement.Under"/> and nothing else.
    ///
    /// The same scan without the rigs, for a caller deciding which hierarchy it wants before paying
    /// for them: a transport's parts come to twenty placements and a rig read whole is most of what
    /// a placement costs.
    public static IReadOnlyList<IReadOnlyList<string>> Placings(
        BundleSet bundles, string bundle, long meshPathId)
    {
        if (Opened(bundles, bundle) is not { } file) return [];

        return [.. Renderers(bundles, file, meshPathId).Select(r => Ancestry(bundles, file, r.Transform))];
    }

    private static AssetsFileInstance? Opened(BundleSet bundles, string bundle)
    {
        try { return bundles.Open(bundle); }
        catch (Exception e) when (e is IOException or FileNotFoundException) { return null; }
    }

    /// Every renderer in a bundle that names this mesh, with the transform it hangs off.
    private static IEnumerable<(bool Skinned, AssetTypeValueField Renderer, long Transform)> Renderers(
        BundleSet bundles, AssetsFileInstance file, long meshPathId)
    {
        var read = Hierarchy.Of(bundles, file);

        foreach (var drawing in read.Drawn(meshPathId))
        {
            if (drawing.Owner == 0 || read.Field(drawing.Info) is not { } field) continue;

            var mine = read.TransformOf(drawing.Owner);
            if (mine != 0) yield return (drawing.Skinned, field, mine);
        }
    }

    /// Whether the game draws this mesh at all.
    ///
    /// **A MeshFilter says which mesh; a MeshRenderer beside it is what draws one.** A filter with
    /// no renderer on its object is a collider's geometry — `fps_all_terrain_vehicle_collider` says
    /// so in its name, #28 Tree calls its one `box`, and the pair of arms a few weapons carry under
    /// `FPS_PLAYER_Arm_left`/`_Right` are the same thing. The game draws none of them and nor should
    /// a preview: drawn, they are the white slab in the middle of #7, #11, #14 and #28. A renderer
    /// switched off is not drawn either.
    ///
    /// Size is no guide to it. Measured over 618 items: of the 26 parts small enough to be a
    /// marker, 21 are drawn by nothing and the other five are real — `avatar_agent_harn`'s eye, a
    /// rover's glass — so the renderer is what answers.
    ///
    /// Asked of the parts drawn beside a model rather than of the model on the row, which is shown
    /// because somebody asked for it whatever draws it.
    public static bool Drawn(BundleSet bundles, string bundle, long meshPathId)
    {
        AssetsFileInstance file;
        try { file = bundles.Open(bundle); }
        catch (Exception e) when (e is IOException or FileNotFoundException) { return false; }

        var read = Hierarchy.Of(bundles, file);

        foreach (var drawing in read.Drawn(meshPathId))
        {
            if (drawing.Skinned)
            {
                if (drawing.Enabled) return true;
                continue;
            }

            if (read.Field(drawing.Owner) is not { } holder) continue;

            foreach (var component in holder["m_Component"]["Array"].Children)
            {
                var id = component["component"]["m_PathID"].AsLong;
                if (file.file.GetAssetInfo(id) is not { TypeId: (int)AssetClassID.MeshRenderer } found) continue;

                var renderer = read.Field(found);
                if (renderer is null) continue;
                if (renderer["m_Enabled"].IsDummy || renderer["m_Enabled"].AsInt != 0) return true;
            }
        }

        return false;
    }

    /// A mesh nothing skins, moved by moving the object it hangs off.
    ///
    /// A MeshFilter names the mesh, and a clip moves the MeshFilter's own transform by name, so that
    /// transform is the one bone and every vertex rides on it. #8's Old Combat Knife skin is built
    /// this way: its blade is a plain mesh that five clips of its own swing about.
    ///
    /// `under` means here what it means above, and it matters most here: the parts of a transport
    /// are plain meshes, each put down several times over two arrangements, so the first filter in
    /// the file is a wheel of the shop's copy as often as not.
    private static Skeleton? Unskinned(
        BundleSet bundles, AssetsFileInstance file, long meshPathId, string? under, bool whole = false)
    {
        var read = Hierarchy.Of(bundles, file);

        foreach (var drawing in read.Drawn(meshPathId))
        {
            if (drawing.Skinned || read.Field(drawing.Info) is not { } filter) continue;

            var owner = filter["m_GameObject"];
            if (under is not null && !RootedAt(bundles, file, owner, under)) continue;

            var transform = drawing.Owner == 0 ? 0 : read.TransformOf(drawing.Owner);
            if (transform == 0) continue;

            return Build(bundles, file, [transform], transform, rigid: true, whole: whole);
        }

        return null;
    }

    /// Whether this object's hierarchy is the one of the given name, read at its top.
    ///
    /// The top rather than any step of the chain, because the game names the two arrangements of an
    /// item after the item: `royale_car_braided`'s body hangs under an object of its own name in
    /// both, and the shop's — `royale_car_braided < royale_car_braided_shop` — answers a test that
    /// asks whether the name appears anywhere above. Its body stands 2.76 higher than the item's
    /// own, so taken for the item's it is a car floating over its own wheels. `Placement.Root`
    /// reads the same end of the same chain.
    private static bool RootedAt(
        BundleSet bundles, AssetsFileInstance file, AssetTypeValueField owner, string root)
        => !owner.IsDummy
            && Ancestry(bundles, file, TransformOf(bundles, file, owner["m_PathID"].AsLong))
                is { Count: > 0 } names
            && string.Equals(names[^1], root, StringComparison.OrdinalIgnoreCase);

    /// The names of this transform's own object and of every object above it, the top last.
    ///
    /// Walked up rather than down: an object has one parent and a root has hundreds of descendants,
    /// and the chain from a weapon's mesh to its prefab root is a handful of steps. The count is a
    /// guard against a hierarchy that points at itself, which a bundle this tool did not write
    /// could hold.
    private static List<string> Ancestry(BundleSet bundles, AssetsFileInstance file, long transform)
    {
        var read = Hierarchy.Of(bundles, file);
        var names = new List<string>();

        for (var step = 0; transform != 0 && step < 64; step++)
        {
            if (read.Object(transform) is not { } node) break;

            names.Add(node.Name);
            transform = node.Parent;
        }

        return names;
    }

    private static long TransformOf(BundleSet bundles, AssetsFileInstance file, long gameObject)
        => Hierarchy.Of(bundles, file).TransformOf(gameObject);

    /// Walks up from each bone to the top, so every transform between them is present and a parent
    /// always comes before its children.
    ///
    /// <param name="whole">
    /// Also walk down from the top, so that every object in the prefab is a joint and not only the
    /// ones a mesh is skinned to.
    ///
    /// A bone list answers what moves the vertices, which is all posing one mesh needs. It is not
    /// what a clip drives: measured over 400 weapons, the clips of 90 of them drive objects no mesh
    /// is skinned to — `Point_Arm_Left` and `Point_Arm_Right` most often, then `Bone_Shutter`,
    /// `Bone_Bullet`, `Bone_Clips` — which a skeleton built from bones alone has nowhere to put. The
    /// extra joints carry no vertices and change no pose; they give the rest of the clip somewhere
    /// to land, and they are what lets two meshes of one prefab be posed in the same space.
    /// </param>
    private static Skeleton? Build(
        BundleSet bundles, AssetsFileInstance file, IReadOnlyList<long> bones, long renderer,
        bool rigid = false, bool whole = false)
    {
        var read = Hierarchy.Of(bundles, file);
        var joints = new List<Joint>();
        var at = new Dictionary<long, int>();

        int Add(long transform)
        {
            if (at.TryGetValue(transform, out var known)) return known;

            if (read.Object(transform) is not { } node) return -1;

            var parent = node.Parent == 0 ? -1 : Add(node.Parent);

            // After the parent, always: everything downstream reads this list in order and expects
            // to have seen whoever it hangs off already.
            var path = parent >= 0 ? $"{joints[parent].Path}/{node.Name}" : node.Name;

            joints.Add(new Joint(node.Name, path, parent, node.Local));
            return at[transform] = joints.Count - 1;
        }

        var order = bones.Select(Add).ToList();
        var root = renderer == 0 ? -1 : Add(renderer);
        if (order.Any(i => i < 0)) return null;

        // Down from whatever the walk up reached, if the caller wants the whole prefab. Taken from
        // the tops rather than from the renderer, because the renderer sits part way down and the
        // objects a clip drives are spread across the whole of it.
        if (whole)
        {
            var tops = at.Where(pair => joints[pair.Value].Parent < 0).Select(pair => pair.Key).ToList();
            foreach (var top in tops) Descend(top, 0);
        }

        void Descend(long transform, int depth)
        {
            // A hierarchy that points at itself would otherwise be walked for ever. The deepest
            // rig measured in this game is 78 objects, so this is far above anything real.
            if (depth > 64) return;
            if (read.Object(transform) is not { } node) return;

            foreach (var id in node.Children)
            {
                // Already a joint from the walk up, or added now. Either way it is descended into:
                // the objects a clip drives hang below bones as often as beside them.
                if (!at.ContainsKey(id) && Add(id) < 0) continue;
                Descend(id, depth + 1);
            }
        }

        return new Skeleton(joints, order, root, rigid);
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

    /// Whether a clip has anything to say about this skeleton.
    ///
    /// A weapon's prefab carries clips for the whole of it — the flash, the shell casing — and only
    /// some of them move the gun. One that names nothing here would play as a model sitting still.
    public bool Moves(Motion motion)
        => motion.Curves.Any(c => Joints.Any(j => Matches(j.Path, c.Path)));

    /// A clip counts its paths from the object carrying the Animation component, which is somewhere
    /// below the root; the joints count theirs from the root. So one ends with the other.
    private static bool Matches(string joint, string curve)
        => joint.Equals(curve, StringComparison.Ordinal)
            || joint.EndsWith("/" + curve, StringComparison.Ordinal);

    /// Where every joint ends up, at one moment of one clip.
    ///
    /// Each joint's own place unless the clip says otherwise, then multiplied down the hierarchy —
    /// which is the whole of what an animation does to a model of this kind.
    public float[][] World(Motion? motion, float time)
    {
        var pose = motion?.Pose(time);
        var world = new float[Joints.Count][];

        for (var at = 0; at < Joints.Count; at++)
        {
            var joint = Joints[at];
            var local = joint.Local;

            if (pose is not null)
                foreach (var (path, placed) in pose)
                {
                    if (!Matches(joint.Path, path)) continue;
                    local = Moved(local, placed);
                    break;
                }

            var matrix = Matrix.Compose(local);
            world[at] = joint.Parent >= 0 ? Matrix.Times(world[joint.Parent], matrix) : matrix;
        }

        return world;
    }

    private static float[] Moved(float[] local, Placed placed)
    {
        var moved = (float[])local.Clone();

        if (placed is { HasPosition: true, Position: { } p }) (moved[0], moved[1], moved[2]) = (p[0], p[1], p[2]);
        if (placed is { HasRotation: true, Rotation: { } r })
            (moved[3], moved[4], moved[5], moved[6]) = (r[0], r[1], r[2], r[3]);
        if (placed is { HasScale: true, Scale: { } s }) (moved[7], moved[8], moved[9]) = (s[0], s[1], s[2]);

        return moved;
    }

    /// The model put together the way its own bones put it together.
    ///
    /// A skinned mesh is written in the space its bind poses map from, and what makes that the
    /// assembled model is every bone carrying the same skinning matrix at rest — which is what
    /// nearly every weapon in this game does. It is not true of all of them: measured over every
    /// weapon, **518 of 1517** have a vertex further than a fiftieth of the model's own radius from
    /// where its bones put it, and the worst of them throw a piece two thousand radii away. Drawn
    /// as written that is one weapon in three scattered and tiny, the far-flung pieces being inside
    /// the bounding box the framing is worked out from.
    ///
    /// Beretta_3_Mesh moves by 0.000 through this and adventure_swords_mesh by 0.012, so a weapon
    /// that needs none of it is drawn identically.
    ///
    /// Posing a clip starts from the mesh as it was written, never from this: the pose already
    /// carries the bones' own transforms, and starting here would apply them twice.
    public UnityMesh Assembled(UnityMesh mesh) => Pose(mesh, null, 0f);

    /// The same, but left where the prefab hangs it rather than where it was read.
    ///
    /// Posing normally takes a mesh back into its own space, so that a model at rest is the model as
    /// it was written (see `home` in Pose). That is right for one mesh and wrong for two: a weapon
    /// and the arms that hold it are different meshes of one prefab, each written in its own space,
    /// and normalising each to its own puts them in two different places. Asking for the prefab's
    /// space instead is what lets them be shown together. One mesh on its own has no use for it.
    public UnityMesh Placed(UnityMesh mesh, Motion? motion, float time)
        => Pose(mesh, motion, time, common: true);

    /// The mesh with its vertices moved to where this clip puts them at this moment.
    ///
    /// Ordinary linear blend skinning: every vertex is carried by up to four bones, each with a
    /// weight, and ends up at the weighted sum of where each of them would put it. The bind pose is
    /// what takes a vertex out of the model's own space and into the bone's, which is why it is
    /// stored with the mesh rather than with the skeleton.
    public UnityMesh Pose(UnityMesh mesh, Motion? motion, float time, bool common = false)
    {
        if (mesh.Get(VertexAttribute.Position) is not { } positions) return mesh;

        // One object, one matrix: where it is now, taken back out of where it sits at rest, so the
        // model at rest is the model as it was read.
        if (Rigid)
            return Carried(mesh, positions, common
                ? World(motion, time)[Bones[0]]
                : Matrix.Times(Matrix.Inverse(Rest[Bones[0]]), World(motion, time)[Bones[0]]));

        if (mesh.Get(VertexAttribute.BlendIndices) is not { } joints) return mesh;
        if (mesh.BindPoses.Count == 0) return mesh;

        // A mesh carried by one bone per vertex has no weights at all — there is nothing to weigh
        // against — and that is how this game's weapons are rigged: the slide belongs to the slide's
        // bone and to nothing else. Reading the missing channel as "no weight anywhere" leaves
        // every vertex where it started, which looks exactly like an animation that does nothing.
        var weights = mesh.Get(VertexAttribute.BlendWeight);
        var wide = mesh.Dimensions.GetValueOrDefault(VertexAttribute.BlendIndices, 1);

        var world = World(motion, time);

        // Back into the space the mesh's own vertices are written in.
        //
        // A bind pose is written against whatever the model was parented to when it was rigged, and
        // that is not something the prefab still says: the beretta's bind poses put its bones at the
        // origin, where its prefab hangs them a metre and a half up and to the right. So the space
        // is taken from the model itself — at rest, every bone's skinning matrix is the same one,
        // and its inverse is by construction the transform that leaves the mesh where it was read.
        var home = common
            ? Matrix.Identity
            : Matrix.Inverse(Matrix.Times(Rest[Bones[0]], Matrix.FromRows(mesh.BindPoses[0])));

        // One matrix per bone: where the bone is now, applied after taking the vertex into its space.
        var skinning = new float[Bones.Count][];
        for (var bone = 0; bone < Bones.Count; bone++)
            skinning[bone] = bone < mesh.BindPoses.Count && Bones[bone] < world.Length
                ? Matrix.Times(home,
                    Matrix.Times(world[Bones[bone]], Matrix.FromRows(mesh.BindPoses[bone])))
                : Matrix.Identity;

        var moved = new float[positions.Length];
        var normals = mesh.Get(VertexAttribute.Normal);
        var turned = normals is null ? null : new float[normals.Length];

        for (var vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            float x = 0, y = 0, z = 0, nx = 0, ny = 0, nz = 0, total = 0;

            for (var slot = 0; slot < wide; slot++)
            {
                var at = vertex * wide + slot;
                if (at >= joints.Length) break;

                var weight = weights is null
                    ? slot == 0 ? 1f : 0f
                    : at < weights.Length ? weights[at] : 0f;

                if (weight <= 0) continue;

                var bone = (int)joints[at];
                if (bone < 0 || bone >= skinning.Length) continue;

                var m = skinning[bone];
                var (px, py, pz) = (positions[vertex * 3], positions[vertex * 3 + 1], positions[vertex * 3 + 2]);

                x += weight * (m[0] * px + m[4] * py + m[8] * pz + m[12]);
                y += weight * (m[1] * px + m[5] * py + m[9] * pz + m[13]);
                z += weight * (m[2] * px + m[6] * py + m[10] * pz + m[14]);

                if (normals is not null)
                {
                    var (ax, ay, az) = (normals[vertex * 3], normals[vertex * 3 + 1], normals[vertex * 3 + 2]);
                    nx += weight * (m[0] * ax + m[4] * ay + m[8] * az);
                    ny += weight * (m[1] * ax + m[5] * ay + m[9] * az);
                    nz += weight * (m[2] * ax + m[6] * ay + m[10] * az);
                }

                total += weight;
            }

            // A vertex no bone carries stays where the model has it rather than collapsing to the
            // origin, which is what a weight of zero would otherwise do to it.
            if (total <= 0)
            {
                Array.Copy(positions, vertex * 3, moved, vertex * 3, 3);
                if (normals is not null && turned is not null)
                    Array.Copy(normals, vertex * 3, turned, vertex * 3, 3);
                continue;
            }

            (moved[vertex * 3], moved[vertex * 3 + 1], moved[vertex * 3 + 2]) = (x, y, z);

            if (turned is not null)
            {
                var length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length > 1e-6f) (nx, ny, nz) = (nx / length, ny / length, nz / length);
                (turned[vertex * 3], turned[vertex * 3 + 1], turned[vertex * 3 + 2]) = (nx, ny, nz);
            }
        }

        var attributes = new Dictionary<VertexAttribute, float[]>(mesh.Attributes)
        {
            [VertexAttribute.Position] = moved,
        };
        if (turned is not null) attributes[VertexAttribute.Normal] = turned;

        return mesh with { Attributes = attributes };
    }

    /// The matrix that takes this skeleton's own space back into the space `mesh` was written in.
    ///
    /// It is what `Pose` applies when it is not asked for the prefab's space — the thing that makes
    /// a model at rest the model as it was read. Handed out because several meshes shown together
    /// have to be joined in the prefab's space, where they share one, and then taken back into the
    /// model's own: everything that reads a weapon's facing reads it there. `Facing.LandmarksIn`
    /// answers in the model's space, and measured against a model in the prefab's the muzzle comes
    /// out at whichever end the two spaces happen to agree on.
    public float[]? Home(UnityMesh mesh)
    {
        if (Bones.Count == 0 || Bones[0] >= Rest.Length) return null;
        if (Rigid) return Matrix.Inverse(Rest[Bones[0]]);
        return mesh.BindPoses.Count == 0
            ? null
            : Matrix.Inverse(Matrix.Times(Rest[Bones[0]], Matrix.FromRows(mesh.BindPoses[0])));
    }

    /// The same mesh moved by one matrix, positions and normals alike.
    public static UnityMesh Moved(UnityMesh mesh, float[] matrix)
        => mesh.Get(VertexAttribute.Position) is { } positions
            ? Carried(mesh, positions, matrix)
            : mesh;

    private static UnityMesh Carried(UnityMesh mesh, float[] positions, float[] m)
    {
        var moved = new float[positions.Length];
        for (var at = 0; at + 2 < positions.Length; at += 3)
        {
            var (px, py, pz) = (positions[at], positions[at + 1], positions[at + 2]);
            moved[at] = m[0] * px + m[4] * py + m[8] * pz + m[12];
            moved[at + 1] = m[1] * px + m[5] * py + m[9] * pz + m[13];
            moved[at + 2] = m[2] * px + m[6] * py + m[10] * pz + m[14];
        }

        var attributes = new Dictionary<VertexAttribute, float[]>(mesh.Attributes)
        {
            [VertexAttribute.Position] = moved,
        };

        if (mesh.Get(VertexAttribute.Normal) is { } normals)
        {
            var turned = new float[normals.Length];
            for (var at = 0; at + 2 < normals.Length; at += 3)
            {
                var (ax, ay, az) = (normals[at], normals[at + 1], normals[at + 2]);
                var nx = m[0] * ax + m[4] * ay + m[8] * az;
                var ny = m[1] * ax + m[5] * ay + m[9] * az;
                var nz = m[2] * ax + m[6] * ay + m[10] * az;
                var length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length > 1e-6f) (nx, ny, nz) = (nx / length, ny / length, nz / length);
                (turned[at], turned[at + 1], turned[at + 2]) = (nx, ny, nz);
            }
            attributes[VertexAttribute.Normal] = turned;
        }

        return mesh with { Attributes = attributes };
    }
}

/// Column-major 4x4s, the way Unity writes a bind pose, so the two can be multiplied without
/// transposing anything on the way past.
public static class Matrix
{
    public static float[] Identity => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    /// A local transform — position, rotation as a quaternion, scale — as one matrix.
    public static float[] Compose(float[] trs)
    {
        float x = trs[3], y = trs[4], z = trs[5], w = trs[6];
        float sx = trs[7], sy = trs[8], sz = trs[9];

        float xx = x * x, yy = y * y, zz = z * z;
        float xy = x * y, xz = x * z, yz = y * z;
        float wx = w * x, wy = w * y, wz = w * z;

        return
        [
            (1 - 2 * (yy + zz)) * sx, (2 * (xy + wz)) * sx, (2 * (xz - wy)) * sx, 0,
            (2 * (xy - wz)) * sy, (1 - 2 * (xx + zz)) * sy, (2 * (yz + wx)) * sy, 0,
            (2 * (xz + wy)) * sz, (2 * (yz - wx)) * sz, (1 - 2 * (xx + yy)) * sz, 0,
            trs[0], trs[1], trs[2], 1,
        ];
    }

    /// A bind pose as the mesh keeps it, which is the other way round.
    ///
    /// Unity writes a Matrix4x4 as sixteen fields named e00 through e33 — row then column — and the
    /// mesh reader keeps that order. Everything here is column-major, so one of the two has to turn.
    public static float[] FromRows(float[] rows)
    {
        var columns = new float[16];

        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 4; column++)
            columns[column * 4 + row] = row * 4 + column < rows.Length ? rows[row * 4 + column] : 0f;

        return columns;
    }

    /// The inverse of a transform, which is all these ever are: a rotation and scale with a
    /// translation, and no perspective in the last row to worry about.
    public static float[] Inverse(float[] m)
    {
        // The determinant of the upper 3x3. A model with a flattened axis has none, and an identity
        // is the only answer that cannot make things worse.
        var determinant =
            m[0] * (m[5] * m[10] - m[9] * m[6])
            - m[4] * (m[1] * m[10] - m[9] * m[2])
            + m[8] * (m[1] * m[6] - m[5] * m[2]);

        if (MathF.Abs(determinant) < 1e-12f) return Identity;
        var scale = 1f / determinant;

        var inverse = new float[16];

        inverse[0] = (m[5] * m[10] - m[9] * m[6]) * scale;
        inverse[1] = (m[9] * m[2] - m[1] * m[10]) * scale;
        inverse[2] = (m[1] * m[6] - m[5] * m[2]) * scale;
        inverse[4] = (m[8] * m[6] - m[4] * m[10]) * scale;
        inverse[5] = (m[0] * m[10] - m[8] * m[2]) * scale;
        inverse[6] = (m[4] * m[2] - m[0] * m[6]) * scale;
        inverse[8] = (m[4] * m[9] - m[8] * m[5]) * scale;
        inverse[9] = (m[8] * m[1] - m[0] * m[9]) * scale;
        inverse[10] = (m[0] * m[5] - m[4] * m[1]) * scale;

        inverse[12] = -(inverse[0] * m[12] + inverse[4] * m[13] + inverse[8] * m[14]);
        inverse[13] = -(inverse[1] * m[12] + inverse[5] * m[13] + inverse[9] * m[14]);
        inverse[14] = -(inverse[2] * m[12] + inverse[6] * m[13] + inverse[10] * m[14]);
        inverse[15] = 1;

        return inverse;
    }

    public static float[] Times(float[] a, float[] b)
    {
        var result = new float[16];

        for (var column = 0; column < 4; column++)
        for (var row = 0; row < 4; row++)
            result[column * 4 + row] =
                a[row] * b[column * 4]
                + a[4 + row] * b[column * 4 + 1]
                + a[8 + row] * b[column * 4 + 2]
                + a[12 + row] * b[column * 4 + 3];

        return result;
    }
}
