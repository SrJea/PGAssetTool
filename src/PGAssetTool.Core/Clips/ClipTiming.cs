using AssetsTools.NET;

namespace PGAssetTool.Core.Clips;

/// Changes how long an animation clip lasts, in the clip itself.
///
/// The game's clips are the old kind — `m_Legacy`, no muscle clip, curves of keyframes bound to
/// objects by path — and they carry no duration field at all: a clip lasts until its last keyframe
/// and that is the whole of it. So this walks the keys and scales their times, and divides the
/// slopes by the same factor because a slope is a value per second. Nothing else in the object has
/// to change; 422 clips were read and not one carries an animation event, so there are no event
/// times to move either.
///
/// **It is only ever called to fit a borrowed motion to the length the target clip already had**,
/// because the game reads a clip's length as behaviour rather than as decoration, and behaviour is
/// the line `Replaceable` draws for everything else this tool writes. So this is the arithmetic
/// that keeps a transplant from changing how a weapon behaves, not a way to change one: the only
/// caller is `ClipInstaller.Transplant`, and the only factor it is ever given is the one the
/// target's own length asks for. Do not add a caller whose factor comes from anywhere else.
public static class ClipTiming
{
    /// Scales the times in a clip where it sits, and answers what it now lasts.
    ///
    /// The field is edited rather than rebuilt from a `Motion`: the structure — which curves, bound
    /// to which paths, with how many keys — is exactly the structure that was there, and the only
    /// thing that differs is a number per key. Rebuilding it would be a second account of the
    /// layout to keep in step with the reader's.
    public static float Stretch(AssetTypeValueField clip, float factor)
    {
        if (factor <= 0 || !float.IsFinite(factor)) throw new ArgumentOutOfRangeException(nameof(factor));

        var last = 0f;
        foreach (var name in Curves)
        {
            var array = clip[name];
            if (array.IsDummy) continue;

            foreach (var bound in array["Array"].Children)
            foreach (var key in bound["curve"]["m_Curve"].Children.SelectMany(a => a.Children))
            {
                var time = key["time"];
                if (time.IsDummy) continue;

                var moved = time.AsFloat * factor;
                time.AsFloat = moved;
                last = MathF.Max(last, moved);

                // In and out both, and every component of each: a slope is a value per second and
                // the second has changed length. Leaving these alone keeps the keys where they
                // belong and bends the curve between them — a reload that snaps where it used to
                // ease, which is the one way this could be wrong and still look nearly right.
                Divide(key["inSlope"], factor);
                Divide(key["outSlope"], factor);
            }
        }

        return last;
    }

    /// The three curve arrays a clip of this game's kind actually uses. `m_EulerCurves`,
    /// `m_FloatCurves` and `m_PPtrCurves` are present in the type and empty in every clip read.
    private static readonly string[] Curves =
        ["m_PositionCurves", "m_RotationCurves", "m_ScaleCurves"];

    /// A curve over a vector stores x, y, z (and w for a rotation); one over a single float stores
    /// one component. Whichever it is, every component is divided.
    private static void Divide(AssetTypeValueField slope, float factor)
    {
        if (slope.IsDummy) return;

        foreach (var part in slope.Children)
            if (!part.IsDummy && part.Value is not null)
                part.AsFloat = part.AsFloat / factor;
    }
}
