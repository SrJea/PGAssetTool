using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Core.Animation;

public sealed record ClipChange(int OldCurves, int NewCurves, float OldLength, float NewLength)
{
    public override string ToString()
        => $"{OldCurves}->{NewCurves} curves, {OldLength:0.00}s->{NewLength:0.00}s";
}

/// Writes motion into one of the game's AnimationClips.
///
/// What moves is replaced and nothing else is: the position, rotation and scale curves are rebuilt
/// from the replacement, and the clip keeps its name, its events, its curves over anything that is
/// not a transform, its wrap mode and its bounds.
///
/// The name, because the game plays a clip by it — a weapon asks its Animation for "Reload" — so a
/// clip renamed to whatever it was copied from would never play. The events, because they call into
/// the game's own code: `OnReloadAnimationStart`, `DisableEffectForAnimation`. That is behaviour, the
/// one thing this tool does not write, and an event copied from another weapon would call a method
/// on a component this one may not have. The other curves, because they switch this weapon's own
/// muzzle flash on and off, by paths no other weapon has.
///
/// The events keep their place in the motion rather than on the clock. #544's Charge calls
/// `OnAnimationFinished` at 3.0s, the end of its own three seconds; under a one-second animation
/// that moment never comes, and whatever waits for the charge to finish waits for ever. So an event's
/// time is scaled by how much longer or shorter the clip has become — one at the end stays at the
/// end, one at the start (most of them) stays at the start — and nothing about what it calls changes.
///
/// A clip the game saved compressed keeps its rotations packed in bits; those are cleared, since the
/// replacement's are written plain and two sets of rotations for one bone would fight. Unity plays a
/// clip with both kinds, so nothing else has to change for it.
public static class ClipImporter
{
    public static ClipChange Replace(AssetTypeValueField clip, Motion motion)
    {
        if (!clip["m_Legacy"].IsDummy && !clip["m_Legacy"].AsBool)
            throw new NotSupportedException(
                $"'{clip["m_Name"].AsString}' is a Mecanim clip; only the legacy clips weapons play are written.");

        var before = Motion.Read(clip);
        var oldCurves = before?.Curves.Count ?? 0;
        var oldLength = before?.Length ?? 0;

        Write(clip["m_PositionCurves"], motion, MotionChannel.Position);
        Write(clip["m_RotationCurves"], motion, MotionChannel.Rotation);
        Write(clip["m_ScaleCurves"], motion, MotionChannel.Scale);

        Clear(clip["m_CompressedRotationCurves"]);
        Clear(clip["m_EulerCurves"]);
        if (!clip["m_Compressed"].IsDummy) clip["m_Compressed"].AsBool = false;
        if (!clip["m_SampleRate"].IsDummy) clip["m_SampleRate"].AsFloat = motion.SampleRate;

        if (oldLength > 0 && motion.Length > 0 && !clip["m_Events"].IsDummy)
            foreach (var e in clip["m_Events"]["Array"].Children)
                e["time"].AsFloat = Math.Clamp(e["time"].AsFloat * motion.Length / oldLength, 0, motion.Length);

        return new ClipChange(oldCurves, motion.Curves.Count, oldLength, motion.Length);
    }

    private static void Clear(AssetTypeValueField vector)
    {
        if (!vector.IsDummy) vector["Array"].Children.Clear();
    }

    private static void Write(AssetTypeValueField vector, Motion motion, MotionChannel channel)
    {
        if (vector.IsDummy) return;
        var array = vector["Array"];
        var width = channel == MotionChannel.Rotation ? 4 : 3;

        var curves = new List<AssetTypeValueField>();
        foreach (var curve in motion.Curves.Where(c => c.Channel == channel))
        {
            var bound = ValueBuilder.DefaultValueFieldFromArrayTemplate(array);
            bound["path"].AsString = curve.Path;

            var animation = bound["curve"];
            animation["m_PreInfinity"].AsInt = 2;
            animation["m_PostInfinity"].AsInt = 2;
            if (!animation["m_RotationOrder"].IsDummy) animation["m_RotationOrder"].AsInt = 4;

            var keys = animation["m_Curve"]["Array"];
            keys.Children.Clear();
            foreach (var key in curve.Keys)
            {
                var frame = ValueBuilder.DefaultValueFieldFromArrayTemplate(keys);
                frame["time"].AsFloat = key.Time;
                Set(frame["value"], width, key.X, key.Y, key.Z, key.W);
                Set(frame["inSlope"], width, key.InX, key.InY, key.InZ, key.InW);
                Set(frame["outSlope"], width, key.OutX, key.OutY, key.OutZ, key.OutW);
                if (!frame["weightedMode"].IsDummy) frame["weightedMode"].AsInt = 0;
                // Unity's own default weight. Unused while the mode is not weighted, and what an
                // unweighted key carries when Unity writes one.
                Set(frame["inWeight"], width, 1f / 3, 1f / 3, 1f / 3, 1f / 3);
                Set(frame["outWeight"], width, 1f / 3, 1f / 3, 1f / 3, 1f / 3);
                keys.Children.Add(frame);
            }

            curves.Add(bound);
        }

        array.Children.Clear();
        array.Children.AddRange(curves);
    }

    private static void Set(AssetTypeValueField value, int width, float x, float y, float z, float w)
    {
        if (value.IsDummy) return;
        value["x"].AsFloat = x;
        value["y"].AsFloat = y;
        value["z"].AsFloat = z;
        if (width == 4 && !value["w"].IsDummy) value["w"].AsFloat = w;
    }
}
