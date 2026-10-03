using PGAssetTool.Core.Preview;

namespace PGAssetTool.Core.Animation;

/// Makes a clip written for one weapon move another.
///
/// A clip names what it moves by path, and every weapon's paths start with a name of its own:
/// `pig_hammer/FPS_PLAYER_Arm_Right` on #15 Hammer is `x-mas_destroyer 1/FPS_PLAYER_Arm_Right` on
/// another. The arms are called the same on every weapon, which is what makes one weapon's reload
/// worth having on another; the first name and whatever the weapon's own parts are called are not.
///
/// So each path is matched in turns, each one only for what the turns before it left:
///
/// 1. The same path, where the other weapon has it.
/// 2. The path whose last name is the same and which agrees with it for the most names counted
///    from the end — taken only when exactly one does, or failing that exactly one of those as deep
///    as this path is: #416 holds an `FPS_PLAYER_Arm_Right` inside its `FPS_PLAYER_Arm_Right`, and
///    the arm the other weapon's arm is is the one at the arm's own depth. Two equally good answers
///    beyond that are a guess.
/// 3. What those matches say about the names in front: a match of `pig_hammer/FPS_PLAYER_Arm_Right`
///    with `x-mas_destroyer 1/FPS_PLAYER_Arm_Right` says `pig_hammer` is `x-mas_destroyer 1`, and
///    that answers `pig_hammer` itself and anything else under it that has the same name below.
/// 4. An only child of something matched, when what it was matched with has an only child too: the
///    gun's own root under the hand, which every weapon calls something different. The hand holds
///    markers besides — `Point_Arm_Right`, an arm inside itself — so failing an only child, the only
///    child that has children of its own: `root` on #416 against `bone_root` on #1. One level below
///    something matched by name and no further — below the gun's root, an only child is whatever
///    that gun happens to be made of, and #416's barrel is not #1's magazine. Only when the
///    clip's own model is known: counted among the paths a clip moves rather than the objects the
///    model has, a hand moving its trigger and nothing else it holds looks like a hand holding one
///    thing, and the trigger was matched with the other gun's root.
///
/// Names are compared without minding case, every step: #2 has `FPS_PLAYER_Arm_Left` where #416 has
/// `FPS_PLAYER_Arm_left`, and they are the same arm. What a curve is moved onto is always spelt the
/// way this item spells it, since that is how the game finds it.
///
/// What is still unanswered moves nothing here and is left out, and said.
public static class Retarget
{
    public sealed record Result(IReadOnlyDictionary<string, string> Map, IReadOnlyList<string> Unmatched);

    /// <param name="sourceRig">
    /// Every object under the source clip's own component, when it is known. Step 4 counts children
    /// in it: counted among only the paths the clip moves, a hand holding a gun and a sight it never
    /// moves looks like a hand holding one thing.
    /// </param>
    public static Result Paths(
        IEnumerable<string> source, IReadOnlyCollection<string> target, IEnumerable<string>? sourceRig = null)
    {
        var wanted = source.Distinct(StringComparer.Ordinal).ToList();
        var have = new HashSet<string>(target, StringComparer.Ordinal);
        // Spelt another way, where only one object here is spelt like that.
        var folded = target
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        string? Here(string path) => have.Contains(path) ? path : folded.GetValueOrDefault(path);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        // 1. The same path.
        foreach (var path in wanted)
            if (Here(path) is { } same) map[path] = same;

        // 2. The same name, agreeing for longest from the end.
        foreach (var path in wanted.Where(p => !map.ContainsKey(p)))
        {
            var names = Split(path);
            if (names.Length == 0) continue;
            var best = target
                .Select(t => (Path: t, Score: Agree(names, Split(t))))
                .Where(c => c.Score > 0)
                .GroupBy(c => c.Score)
                .OrderByDescending(g => g.Key)
                .FirstOrDefault()
                ?.ToList();
            if (best is { Count: > 1 }) best = best.Where(c => Split(c.Path).Length == names.Length).ToList();
            if (best is { Count: 1 }) map[path] = best[0].Path;
        }

        // 3. The names in front, as the matches so far say they correspond.
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (from, to) in map)
        {
            var a = Split(from); var b = Split(to);
            var same = Agree(a, b);
            if (same == a.Length && same == b.Length) continue;
            prefixes.TryAdd(string.Join('/', a[..^same]), string.Join('/', b[..^same]));
        }
        foreach (var path in wanted.Where(p => !map.ContainsKey(p)))
        {
            foreach (var (from, to) in prefixes.OrderByDescending(p => p.Key.Length))
            {
                if (from.Length == 0) continue;
                string? candidate = null;
                if (path == from) candidate = to;
                else if (path.StartsWith(from + "/", StringComparison.Ordinal))
                    candidate = (to.Length == 0 ? "" : to + "/") + path[(from.Length + 1)..];
                if (candidate is not null && Here(candidate) is { } found) { map[path] = found; break; }
            }
        }

        // 4. Only children of things already matched, for as long as that keeps answering.
        if (sourceRig is null) return new Result(map, wanted.Where(p => !map.ContainsKey(p)).ToList());

        var sourceChildren = Children(wanted.Concat(sourceRig).Distinct(StringComparer.Ordinal));
        var targetChildren = Children(target);
        var byName = map.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var path in wanted.Where(p => !map.ContainsKey(p)))
        {
            var parent = Parent(path);
            if (parent is null || !byName.Contains(parent) || !map.TryGetValue(parent, out var mapped)) continue;
            if (!sourceChildren.TryGetValue(parent, out var mine)) continue;
            if (!targetChildren.TryGetValue(mapped, out var theirs)) continue;

            var match = Only(mine, theirs, path, _ => true)
                ?? Only(mine, theirs, path, p => sourceChildren.ContainsKey(p) || targetChildren.ContainsKey(p));
            if (match is null || map.ContainsValue(match)) continue;
            map[path] = match;
        }

        return new Result(map, wanted.Where(p => !map.ContainsKey(p)).ToList());
    }

    /// The motion with every curve moved onto the path it answers to here, and the rest left out.
    public static (Motion Motion, IReadOnlyList<string> Unmatched) Apply(
        Motion motion, IReadOnlyCollection<string> target, IEnumerable<string>? sourceRig = null)
    {
        var result = Paths(motion.Curves.Select(c => c.Path), target, sourceRig);
        var curves = motion.Curves
            .Where(c => result.Map.ContainsKey(c.Path))
            .Select(c => c with { Path = result.Map[c.Path] })
            // Two source paths can land on one object only through step 2's longest agreement, and
            // then the one that agreed exactly is the one to keep.
            .GroupBy(c => (c.Path, c.Channel))
            .Select(g => g.First())
            .ToList();
        return (motion with { Curves = curves }, result.Unmatched);
    }

    /// The motion moved onto this rig the way it looks rather than by its numbers: every object turns
    /// and travels from where it stands here as its counterpart does from where it stands there.
    ///
    /// Copied as numbers, a clip puts each object where the other weapon has it, and no two weapons
    /// stand their objects alike. #416 bends its right arm through an arm inside it and its gun's root
    /// is turned a quarter from #2's, so #2's idle stood #416 on end with its arm crossed through it.
    /// So what is taken from the other clip is the change from that weapon's own rest, seen from above
    /// both — from the object both components hang off, which for a weapon is the camera — and it is
    /// put on top of this weapon's own rest. Between two rigs that stand alike, that is the numbers.
    ///
    /// It is the same few constants for every key of a curve: a turn taken from one parent's space to
    /// the other's is one fixed rotation either side of the key's, and a step from one parent's
    /// space to the other's is one fixed matrix and an offset. Both are linear in the key, so the
    /// keys and their slopes go across exactly and the curve between them is the same curve.
    public static (Motion Motion, IReadOnlyList<string> Unmatched) Apply(Motion motion, AnimationRig target, AnimationRig source)
    {
        var result = Paths(motion.Curves.Select(c => c.Path), target.Paths, source.Paths);
        var there = Index(source.Paths);
        var here = Index(target.Paths);
        var (sourceRest, targetRest) = (source.Rest(), target.Rest());

        var curves = new List<MotionCurve>();
        foreach (var curve in motion.Curves)
        {
            if (!result.Map.TryGetValue(curve.Path, out var to)) continue;
            var keys = there.TryGetValue(curve.Path, out var s) && here.TryGetValue(to, out var t)
                ? Transfer(curve.Channel, curve.Keys, Space(source, sourceRest, s), source.Locals[s],
                    Space(target, targetRest, t), target.Locals[t])
                : curve.Keys;
            curves.Add(curve with { Path = to, Keys = keys });
        }

        return (motion with
        {
            Curves = curves.GroupBy(c => (c.Path, c.Channel)).Select(g => g.First()).ToList(),
        }, result.Unmatched);
    }

    private static Dictionary<string, int> Index(IReadOnlyList<string> paths)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var at = 0; at < paths.Count; at++) index.TryAdd(paths[at], at);
        return index;
    }

    /// The space an object's own numbers are in: its parent where it stands, or for the object the
    /// component is on, whatever that hangs off — the same for both weapons.
    private static float[] Space(AnimationRig rig, IReadOnlyList<float[]> rest, int at)
        => rig.Parents[at] >= 0 ? rest[rig.Parents[at]] : Matrix.Identity;

    private static IReadOnlyList<MotionKey> Transfer(
        MotionChannel channel, IReadOnlyList<MotionKey> keys, float[] from, float[] there, float[] to, float[] here)
    {
        switch (channel)
        {
            case MotionChannel.Rotation:
            {
                // Here, a turn is A · (key) · B: the key's change from that rest, turned from that
                // parent's space into this one's, on top of this rest.
                var (spaceThere, spaceHere) = (Turn(from), Turn(to));
                var (restThere, restHere) = (Unit([there[3], there[4], there[5], there[6]]), Unit([here[3], here[4], here[5], here[6]]));
                var a = Times(Inverse(spaceHere), spaceThere);
                var b = Times(Times(Times(Inverse(restThere), Inverse(spaceThere)), spaceHere), restHere);
                return keys.Select(k => k with
                {
                    X = Turned(a, [k.X, k.Y, k.Z, k.W], b, 0), Y = Turned(a, [k.X, k.Y, k.Z, k.W], b, 1),
                    Z = Turned(a, [k.X, k.Y, k.Z, k.W], b, 2), W = Turned(a, [k.X, k.Y, k.Z, k.W], b, 3),
                    InX = Turned(a, [k.InX, k.InY, k.InZ, k.InW], b, 0), InY = Turned(a, [k.InX, k.InY, k.InZ, k.InW], b, 1),
                    InZ = Turned(a, [k.InX, k.InY, k.InZ, k.InW], b, 2), InW = Turned(a, [k.InX, k.InY, k.InZ, k.InW], b, 3),
                    OutX = Turned(a, [k.OutX, k.OutY, k.OutZ, k.OutW], b, 0), OutY = Turned(a, [k.OutX, k.OutY, k.OutZ, k.OutW], b, 1),
                    OutZ = Turned(a, [k.OutX, k.OutY, k.OutZ, k.OutW], b, 2), OutW = Turned(a, [k.OutX, k.OutY, k.OutZ, k.OutW], b, 3),
                }).ToList();
            }
            case MotionChannel.Position:
            {
                // Here, a place is M · (key) + c: the step from that rest, taken from that parent's
                // space into this one's, from this rest.
                if (Inverse3(Linear(to)) is not { } back) return keys;
                var m = Times3(back, Linear(from));
                var moved = Apply3(m, [there[0], there[1], there[2]]);
                float[] c = [here[0] - moved[0], here[1] - moved[1], here[2] - moved[2]];
                return keys.Select(k =>
                {
                    var v = Apply3(m, [k.X, k.Y, k.Z]);
                    var i = Slope3(m, [k.InX, k.InY, k.InZ]);
                    var o = Slope3(m, [k.OutX, k.OutY, k.OutZ]);
                    return k with
                    {
                        X = v[0] + c[0], Y = v[1] + c[1], Z = v[2] + c[2],
                        InX = i[0], InY = i[1], InZ = i[2], OutX = o[0], OutY = o[1], OutZ = o[2],
                    };
                }).ToList();
            }
            default:
            {
                // In proportion to each rest — except from nothing, where there is no proportion and
                // the number is all there is.
                var f = Enumerable.Range(0, 3)
                    .Select(n => MathF.Abs(there[7 + n]) > 1e-6f ? here[7 + n] / there[7 + n] : 1f)
                    .ToArray();
                return keys.Select(k => k with
                {
                    X = k.X * f[0], Y = k.Y * f[1], Z = k.Z * f[2],
                    InX = Scaled(k.InX, f[0]), InY = Scaled(k.InY, f[1]), InZ = Scaled(k.InZ, f[2]),
                    OutX = Scaled(k.OutX, f[0]), OutY = Scaled(k.OutY, f[1]), OutZ = Scaled(k.OutZ, f[2]),
                }).ToList();
            }
        }
    }

    /// One component of a · q · b. A slope that is not a number is a step and stays one.
    private static float Turned(float[] a, float[] q, float[] b, int component)
        => q.All(float.IsFinite) ? Times(Times(a, q), b)[component] : float.PositiveInfinity;

    private static float Scaled(float slope, float f) => float.IsFinite(slope) ? slope * f : slope;

    private static float[] Slope3(float[][] m, float[] v)
        => v.All(float.IsFinite) ? Apply3(m, v) : [float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity];

    /// Quaternions as x, y, z, w; `Times(p, q)` turns by q and then by p, as Unity's do.
    private static float[] Times(float[] p, float[] q) =>
    [
        p[3] * q[0] + p[0] * q[3] + p[1] * q[2] - p[2] * q[1],
        p[3] * q[1] - p[0] * q[2] + p[1] * q[3] + p[2] * q[0],
        p[3] * q[2] + p[0] * q[1] - p[1] * q[0] + p[2] * q[3],
        p[3] * q[3] - p[0] * q[0] - p[1] * q[1] - p[2] * q[2],
    ];

    private static float[] Inverse(float[] q) => [-q[0], -q[1], -q[2], q[3]];

    private static float[] Unit(float[] q)
    {
        var length = MathF.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
        return length > 1e-8f ? [q[0] / length, q[1] / length, q[2] / length, q[3] / length] : [0, 0, 0, 1];
    }

    /// The turn in a column-major matrix, its scale taken out.
    private static float[] Turn(float[] m)
    {
        var columns = Enumerable.Range(0, 3).Select(c =>
        {
            var length = MathF.Sqrt(m[c * 4] * m[c * 4] + m[c * 4 + 1] * m[c * 4 + 1] + m[c * 4 + 2] * m[c * 4 + 2]);
            return length > 1e-8f ? length : 1f;
        }).ToArray();
        float R(int row, int column) => m[column * 4 + row] / columns[column];

        var trace = R(0, 0) + R(1, 1) + R(2, 2);
        float[] q;
        if (trace > 0)
        {
            var s = MathF.Sqrt(trace + 1) * 2;
            q = [(R(2, 1) - R(1, 2)) / s, (R(0, 2) - R(2, 0)) / s, (R(1, 0) - R(0, 1)) / s, s / 4];
        }
        else if (R(0, 0) > R(1, 1) && R(0, 0) > R(2, 2))
        {
            var s = MathF.Sqrt(1 + R(0, 0) - R(1, 1) - R(2, 2)) * 2;
            q = [s / 4, (R(0, 1) + R(1, 0)) / s, (R(0, 2) + R(2, 0)) / s, (R(2, 1) - R(1, 2)) / s];
        }
        else if (R(1, 1) > R(2, 2))
        {
            var s = MathF.Sqrt(1 + R(1, 1) - R(0, 0) - R(2, 2)) * 2;
            q = [(R(0, 1) + R(1, 0)) / s, s / 4, (R(1, 2) + R(2, 1)) / s, (R(0, 2) - R(2, 0)) / s];
        }
        else
        {
            var s = MathF.Sqrt(1 + R(2, 2) - R(0, 0) - R(1, 1)) * 2;
            q = [(R(0, 2) + R(2, 0)) / s, (R(1, 2) + R(2, 1)) / s, s / 4, (R(1, 0) - R(0, 1)) / s];
        }
        return Unit(q);
    }

    /// The upper 3x3 of a column-major matrix, as rows.
    private static float[][] Linear(float[] m)
        => [[m[0], m[4], m[8]], [m[1], m[5], m[9]], [m[2], m[6], m[10]]];

    private static float[][]? Inverse3(float[][] m)
    {
        var det = m[0][0] * (m[1][1] * m[2][2] - m[1][2] * m[2][1])
            - m[0][1] * (m[1][0] * m[2][2] - m[1][2] * m[2][0])
            + m[0][2] * (m[1][0] * m[2][1] - m[1][1] * m[2][0]);
        if (MathF.Abs(det) < 1e-12f || !float.IsFinite(det)) return null;
        var s = 1 / det;
        return
        [
            [(m[1][1] * m[2][2] - m[1][2] * m[2][1]) * s, (m[0][2] * m[2][1] - m[0][1] * m[2][2]) * s, (m[0][1] * m[1][2] - m[0][2] * m[1][1]) * s],
            [(m[1][2] * m[2][0] - m[1][0] * m[2][2]) * s, (m[0][0] * m[2][2] - m[0][2] * m[2][0]) * s, (m[0][2] * m[1][0] - m[0][0] * m[1][2]) * s],
            [(m[1][0] * m[2][1] - m[1][1] * m[2][0]) * s, (m[0][1] * m[2][0] - m[0][0] * m[2][1]) * s, (m[0][0] * m[1][1] - m[0][1] * m[1][0]) * s],
        ];
    }

    private static float[][] Times3(float[][] a, float[][] b)
        => Enumerable.Range(0, 3).Select(r => Enumerable.Range(0, 3)
            .Select(c => a[r][0] * b[0][c] + a[r][1] * b[1][c] + a[r][2] * b[2][c]).ToArray()).ToArray();

    private static float[] Apply3(float[][] m, float[] v)
        => [m[0][0] * v[0] + m[0][1] * v[1] + m[0][2] * v[2], m[1][0] * v[0] + m[1][1] * v[1] + m[1][2] * v[2], m[2][0] * v[0] + m[2][1] * v[1] + m[2][2] * v[2]];

    /// The one child on the other side, when this is the one child on its own side of a kind.
    private static string? Only(List<string> mine, List<string> theirs, string path, Func<string, bool> kind)
    {
        var a = mine.Where(kind).ToList();
        var b = theirs.Where(kind).ToList();
        return a.Count == 1 && a[0] == path && b.Count == 1 ? b[0] : null;
    }

    private static string[] Split(string path) => path.Length == 0 ? [] : path.Split('/');

    private static string? Parent(string path)
        => path.Length == 0 ? null : path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    /// How many names two paths share, counted from the end.
    private static int Agree(string[] a, string[] b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && string.Equals(a[^(n + 1)], b[^(n + 1)], StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    private static Dictionary<string, List<string>> Children(IEnumerable<string> paths)
    {
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (Parent(path) is not { } parent) continue;
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
            list.Add(path);
        }
        return children;
    }
}
