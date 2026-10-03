using PGAssetTool.Core.Preview;

namespace PGAssetTool.Core.Tests;

/// Retiming a clip: the same motion over a different span.
///
/// A clip of this game's kind has no duration field — it lasts until its last keyframe — so its
/// length is changed by moving the keys, and the slopes have to move with them because a slope is a
/// value per second. Getting the slopes wrong keeps every key where it belongs and bends the curve
/// between them, which is the one way this could be wrong and still look nearly right.
public class MotionTimingTests
{
    /// Two keys a third of a second apart, rising from 0 to 1 at a slope of 3 per second — which is
    /// what a straight line between those two keys has.
    private static Motion Clip(float length = 1f / 3f) => new(
        "Shoot", length, 30f,
        [
            new MotionCurve("gun/Bone_Root", MotionChannel.Position,
            [
                new MotionKey(0f, 0, 0, 0, 0, 3, 3, 3, 0, 3, 3, 3, 0),
                new MotionKey(length, 1, 1, 1, 0, 3, 3, 3, 0, 3, 3, 3, 0),
            ]),
        ]);

    [Fact]
    public void StretchingMovesTheKeysAndTheLength()
    {
        var half = Clip().Stretched(0.5f);

        Assert.Equal(1f / 6f, half.Length, 5);
        Assert.Equal(0f, half.Curves[0].Keys[0].Time, 5);
        Assert.Equal(1f / 6f, half.Curves[0].Keys[1].Time, 5);
    }

    /// Half the time for the same travel is twice the speed, so the slopes double. Values do not
    /// move: where the thing is at each key is the motion, and that is what is being kept.
    [Fact]
    public void TheSlopesAreDividedAndTheValuesAreNot()
    {
        var key = Clip().Stretched(0.5f).Curves[0].Keys[1];

        Assert.Equal(6f, key.InX, 4);
        Assert.Equal(6f, key.OutZ, 4);
        Assert.Equal(1f, key.X, 4);
    }

    /// The slope stays the slope of the line between the keys, which is what says the shape is kept
    /// rather than merely the keys: rise over run is the same before and after.
    [Fact]
    public void TheCurveBetweenTwoKeysIsTheSameShape()
    {
        foreach (var factor in new[] { 0.25f, 0.5f, 2f, 7.5f })
        {
            var stretched = Clip().Stretched(factor);
            var keys = stretched.Curves[0].Keys;
            var run = keys[1].Time - keys[0].Time;
            var rise = keys[1].X - keys[0].X;

            Assert.Equal(rise / run, keys[1].InX, 3);
        }
    }

    /// Halfway through is halfway through, whatever the clock says. Sampled through Pose, which is
    /// what the preview and the posing both go through.
    [Fact]
    public void WhereEverythingIsPartWayThroughIsUnchanged()
    {
        var clip = Clip();
        var half = clip.Stretched(0.5f);

        var before = clip.Pose(clip.Length / 2)["gun/Bone_Root"];
        var after = half.Pose(half.Length / 2)["gun/Bone_Root"];

        Assert.Equal(before.Position![0], after.Position![0], 4);
        Assert.Equal(before.Position[1], after.Position[1], 4);
    }

    /// What a motion moved onto another weapon is asked for: that weapon's own length, whatever this
    /// one lasts, because the length is what the game appears to read as the rate of fire.
    [Fact]
    public void AClipCanBeMadeToLastExactlyAsLongAsAnother()
    {
        var ported = Clip(1.667f).Lasting(0.833f);

        Assert.Equal(0.833f, ported.Length, 4);
        Assert.Equal(0.833f, ported.Curves[0].Keys[^1].Time, 4);
    }

    /// A length that says nothing is left alone rather than turned into infinities.
    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void AFactorThatIsNotALengthChangesNothing(float factor)
    {
        var clip = Clip();
        var same = clip.Stretched(factor);

        Assert.Equal(clip.Length, same.Length, 5);
        Assert.Equal(clip.Curves[0].Keys[1].Time, same.Curves[0].Keys[1].Time, 5);
    }

    [Fact]
    public void AClipOfNoLengthCannotBeMadeToLastLonger()
        => Assert.Equal(0f, new Motion("Empty", 0f, 30f, []).Lasting(2f).Length);
}
