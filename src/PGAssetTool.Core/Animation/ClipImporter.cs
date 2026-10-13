using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Core.Animation;

/// <param name="Given">How long the motion written in was before it was fitted to the clip's own length.</param>
public sealed record ClipChange(int OldCurves, int NewCurves, float OldLength, float NewLength, float Given)
{
    public override string ToString()
        => $"{OldCurves}->{NewCurves} curves, {NewLength:0.00}s"
           + (MathF.Abs(Given - NewLength) > 1e-3f ? $" (fitted from {Given:0.00}s)" : "");
}

/// Writes motion into one of the game's AnimationClips.
///
/// What moves is replaced and nothing else is: the position, rotation and scale curves are rebuilt
/// from the replacement, and the clip keeps its name, its events, its curves over anything that is
/// not a transform, its wrap mode, its sample rate and its bounds.
///
/// The name, because the game plays a clip by it — a weapon asks its Animation for "Reload" — so a
/// clip renamed to whatever it was copied from would never play. The events, because they call into
/// the game's own code: `OnReloadAnimationStart`, `DisableEffectForAnimation`. That is behaviour, the
/// one thing this tool does not write, and an event copied from another weapon would call a method
/// on a component this one may not have. The other curves, because they switch this weapon's own
/// muzzle flash on and off, by paths no other weapon has.
///
/// **And its length, always.** The game times a weapon by its clips — how long a shot takes, how long
/// a reload takes (`shotDelayForAnimation`, `GetReloadAnimationSpeed`) — so a clip of another length
/// changes how the weapon fights, not how it looks: #2's 0.67s shot in #416's 2.60s one made #416
/// fire four times as often. That is a cheat whether anybody meant it or not, and an account can be
/// banned for one. So whatever motion comes in is stretched or squeezed in time to end exactly where
/// the game's own clip ends — keys moved, slopes rescaled so the curve between them is the same
/// curve, only slower or faster — and a legacy clip's length is where its last key is, so the length
/// the game reads is the one it shipped. This is done here, on the way into the bundle, so it holds
/// for every pack: one built before this, one edited by hand, one from anywhere.
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
        var given = motion.Length;
        if (oldLength > 0) motion = Fit(motion, oldLength);

        Write(clip["m_PositionCurves"], motion, MotionChannel.Position);
        Write(clip["m_RotationCurves"], motion, MotionChannel.Rotation);
        Write(clip["m_ScaleCurves"], motion, MotionChannel.Scale);

        Clear(clip["m_CompressedRotationCurves"]);
        Clear(clip["m_EulerCurves"]);
        if (!clip["m_Compressed"].IsDummy) clip["m_Compressed"].AsBool = false;

        return new ClipChange(oldCurves, motion.Curves.Count, oldLength, motion.Length, given);
    }

    /// The same motion, taking exactly `length` seconds from its first moment to its last.
    ///
    /// Every key's time scaled by one factor and every slope by its inverse, which is the same curve
    /// played at another speed. A motion that is a single pose — all its keys at one moment — is held
    /// for the length instead, since there is nothing to stretch.
    public static Motion Fit(Motion motion, float length)
    {
        if (length <= 0 || motion.Curves.Count == 0) return motion;
        if (MathF.Abs(motion.Length - length) < 1e-5f) return motion with { Length = length };

        if (motion.Length <= 1e-6f)
            return motion with
            {
                Length = length,
                Curves = motion.Curves.Select(c => c with
                {
                    Keys = [.. c.Keys, c.Keys[^1] with { Time = length, InX = 0, InY = 0, InZ = 0, InW = 0, OutX = 0, OutY = 0, OutZ = 0, OutW = 0 }],
                }).ToList(),
            };

        var k = length / motion.Length;
        float Slope(float s) => float.IsFinite(s) ? s / k : s;
        return motion with
        {
            Length = length,
            Curves = motion.Curves.Select(c => c with
            {
                Keys = c.Keys.Select(key => key with
                {
                    // The last key lands on the length itself rather than near it.
                    Time = MathF.Abs(key.Time - motion.Length) < 1e-6f ? length : key.Time * k,
                    InX = Slope(key.InX), InY = Slope(key.InY), InZ = Slope(key.InZ), InW = Slope(key.InW),
                    OutX = Slope(key.OutX), OutY = Slope(key.OutY), OutZ = Slope(key.OutZ), OutW = Slope(key.OutW),
                }).ToList(),
            }).ToList(),
        };
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
