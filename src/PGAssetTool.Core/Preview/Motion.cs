using AssetsTools.NET;

namespace PGAssetTool.Core.Preview;

/// Which part of a transform a curve drives.
public enum MotionChannel { Position, Rotation, Scale }

/// One keyframe: a time, a value of up to four components, and the slopes either side of it.
///
/// Unity's curves are Hermite, and a weapon's animations lean on that — a reload is two keyframes
/// and a pair of tangents that make the magazine drop and snap back. Reading the values and joining
/// them with straight lines produces a motion that arrives in the same places by a visibly different
/// route, so the slopes come too.
public readonly record struct MotionKey(
    float Time,
    float X, float Y, float Z, float W,
    float InX, float InY, float InZ, float InW,
    float OutX, float OutY, float OutZ, float OutW);

public sealed record MotionCurve(string Path, MotionChannel Channel, IReadOnlyList<MotionKey> Keys);

/// One animation clip, in the terms the preview needs: which object moves, how, and for how long.
///
/// The game's weapons animate the old way — legacy clips whose curves are bound to objects by the
/// path from the object carrying the Animation component. There is no avatar, no muscle clip and no
/// retargeting, which is why this is a few hundred lines rather than a reimplementation of Mecanim.
public sealed record Motion(string Name, float Length, float SampleRate, IReadOnlyList<MotionCurve> Curves)
{
    /// Reads a clip, or answers null for one there is nothing to play.
    public static Motion? Read(AssetTypeValueField clip)
    {
        var curves = new List<MotionCurve>();

        Gather(clip["m_PositionCurves"], MotionChannel.Position, 3, curves);
        Gather(clip["m_RotationCurves"], MotionChannel.Rotation, 4, curves);
        Gather(clip["m_ScaleCurves"], MotionChannel.Scale, 3, curves);

        // And the rotations a compressed clip keeps packed instead. See PackedCurve.
        curves.AddRange(PackedCurve.Rotations(clip));

        if (curves.Count == 0) return null;

        var length = curves.SelectMany(c => c.Keys).Select(k => k.Time).DefaultIfEmpty(0f).Max();
        var rate = clip["m_SampleRate"].IsDummy ? 30f : clip["m_SampleRate"].AsFloat;

        return new Motion(clip["m_Name"].AsString, length, rate <= 0 ? 30f : rate, curves);
    }

    /// The same clip with the first name taken off every path it drives, and the curves on that
    /// first object itself left out.
    ///
    /// A clip names what it moves from the object under the Animation component down:
    /// `comet_sniper_rifle/FPS_PLAYER_Arm_Right/Bone_comet_sniper_rifle_root/Bone_sniper_clip`. A
    /// skin that brings a model and no clips of its own is built on the weapon's rig under a name of
    /// its own — `Weapon893_deepwater_comet/FPS_PLAYER_Arm_Right/Bone_comet_sniper_rifle_root/...` —
    /// and it is the weapon's clips that play on it. That first name is the one thing the two do not
    /// share, and a path is matched by how it ends, so taking it off is all it needs.
    public Motion WithoutRoot() => this with
    {
        Curves = Curves
            .Where(c => c.Path.Contains('/'))
            .Select(c => c with { Path = c.Path[(c.Path.IndexOf('/') + 1)..] })
            .ToList(),
    };

    private static void Gather(
        AssetTypeValueField array, MotionChannel channel, int width, List<MotionCurve> into)
    {
        if (array.IsDummy) return;

        foreach (var bound in array["Array"].Children)
        {
            var keys = new List<MotionKey>();

            foreach (var key in bound["curve"]["m_Curve"]["Array"].Children)
            {
                var value = key["value"];
                var inSlope = key["inSlope"];
                var outSlope = key["outSlope"];

                keys.Add(new MotionKey(
                    key["time"].AsFloat,
                    At(value, "x"), At(value, "y"), At(value, "z"), width == 4 ? At(value, "w") : 0,
                    At(inSlope, "x"), At(inSlope, "y"), At(inSlope, "z"), width == 4 ? At(inSlope, "w") : 0,
                    At(outSlope, "x"), At(outSlope, "y"), At(outSlope, "z"), width == 4 ? At(outSlope, "w") : 0));
            }

            if (keys.Count > 0)
                into.Add(new MotionCurve(bound["path"].AsString, channel, keys));
        }
    }

    /// A curve over a single float is stored as a struct of one; one over a vector as x, y, z.
    private static float At(AssetTypeValueField value, string name)
    {
        var part = value[name];
        return part.IsDummy ? 0f : part.AsFloat;
    }

    /// Where everything is at a moment, by the path the clip names it by.
    public IReadOnlyDictionary<string, Placed> Pose(float time)
    {
        var pose = new Dictionary<string, Placed>(StringComparer.Ordinal);

        foreach (var curve in Curves)
        {
            if (!pose.TryGetValue(curve.Path, out var placed)) placed = new Placed();
            var (x, y, z, w) = Sample(curve, time);

            pose[curve.Path] = curve.Channel switch
            {
                MotionChannel.Position => placed with { Position = [x, y, z], HasPosition = true },
                MotionChannel.Rotation => placed with { Rotation = [x, y, z, w], HasRotation = true },
                _ => placed with { Scale = [x, y, z], HasScale = true },
            };
        }

        return pose;
    }

    /// One curve at one moment, between the keys either side of it.
    public static (float X, float Y, float Z, float W) Sample(MotionCurve curve, float time)
    {
        var keys = curve.Keys;
        if (keys.Count == 1 || time <= keys[0].Time)
            return (keys[0].X, keys[0].Y, keys[0].Z, keys[0].W);

        var last = keys[^1];
        if (time >= last.Time) return (last.X, last.Y, last.Z, last.W);

        var at = 0;
        while (at + 1 < keys.Count && keys[at + 1].Time <= time) at++;

        var a = keys[at];
        var b = keys[at + 1];
        var span = b.Time - a.Time;
        if (span <= 0) return (b.X, b.Y, b.Z, b.W);

        var t = (time - a.Time) / span;

        return (
            Hermite(a.X, a.OutX, b.X, b.InX, t, span),
            Hermite(a.Y, a.OutY, b.Y, b.InY, t, span),
            Hermite(a.Z, a.OutZ, b.Z, b.InZ, t, span),
            Hermite(a.W, a.OutW, b.W, b.InW, t, span));
    }

    /// Unity's own interpolation: a cubic through both values with both slopes.
    ///
    /// A slope that is not finite is how the game writes a step — hold this value until the next
    /// key — and reading it as a number produces a value of infinity somewhere in the middle.
    private static float Hermite(float from, float outSlope, float to, float inSlope, float t, float span)
    {
        if (!float.IsFinite(outSlope) || !float.IsFinite(inSlope)) return from;

        var t2 = t * t;
        var t3 = t2 * t;

        return (2 * t3 - 3 * t2 + 1) * from
            + (t3 - 2 * t2 + t) * span * outSlope
            + (-2 * t3 + 3 * t2) * to
            + (t3 - t2) * span * inSlope;
    }
}

/// Where one object is, as far as a clip says. What a clip leaves alone keeps what the model has.
public readonly record struct Placed
{
    public float[]? Position { get; init; }
    public float[]? Rotation { get; init; }
    public float[]? Scale { get; init; }

    public bool HasPosition { get; init; }
    public bool HasRotation { get; init; }
    public bool HasScale { get; init; }
}
