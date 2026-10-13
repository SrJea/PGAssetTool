using PGAssetTool.Core.Animation;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Core.Tests;

public class ClipFileTests
{
    private static Motion Sample() => new("Reload", 1.5f, 30, [
        new MotionCurve("pig_hammer/FPS_PLAYER_Arm_Right", MotionChannel.Rotation, [
            new MotionKey(0, 0, 0, 0, 1, 0.5f, 0, 0, 0, 0.5f, 0, 0, 0),
            new MotionKey(1.5f, 0.7071068f, 0, 0, 0.7071068f, 0, 0, 0, 0, 0, 0, 0, 0),
        ]),
        new MotionCurve("pig_hammer", MotionChannel.Position, [
            // A step: Unity's way of saying hold until the next key.
            new MotionKey(0, 1, 2, 3, 0, float.PositiveInfinity, 0, 0, 0, float.PositiveInfinity, 0, 0, 0),
            new MotionKey(0.75f, 4, 5, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        ]),
    ]);

    [Fact]
    public void AClipComesBackExactlyAsItWent()
    {
        var file = ClipFile.FromMotion(Sample(), "#15 Hammer: Reload", ["pig_hammer/bone_root"]);
        var back = ClipFile.Parse(file.ToJson());
        var motion = back.ToMotion();

        Assert.Equal("#15 Hammer: Reload", back.From);
        Assert.Equal(["pig_hammer/bone_root"], back.Unmatched);
        Assert.Equal(1.5f, motion.Length);
        Assert.Equal(Sample().Curves.Select(c => (c.Path, c.Channel)), motion.Curves.Select(c => (c.Path, c.Channel)));
        for (var c = 0; c < motion.Curves.Count; c++)
            Assert.Equal(Sample().Curves[c].Keys, motion.Curves[c].Keys);
    }

    [Fact]
    public void AStepSurvivesTheText()
    {
        // JSON has no word for infinity, and a step dropped to zero would slide instead.
        var json = ClipFile.FromMotion(Sample(), "").ToJson();
        Assert.Contains("\"Infinity\"", json);
        var key = ClipFile.Parse(json).ToMotion().Curves[1].Keys[0];
        Assert.True(float.IsPositiveInfinity(key.InX));
        Assert.True(float.IsPositiveInfinity(key.OutX));
    }

    [Fact]
    public void WritingTheSameClipTwiceWritesTheSameText()
    {
        // A workspace knows an animation was edited by its file changing, so an untouched one has to
        // be written identically every time — putting an item's own animation back included.
        Assert.Equal(ClipFile.FromMotion(Sample(), "").ToJson(), ClipFile.FromMotion(Sample(), "").ToJson());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"format\":\"something-else\",\"version\":1}")]
    [InlineData("not json at all")]
    public void SomethingElseIsRefused(string json)
    {
        Assert.Throws<InvalidDataException>(() => ClipFile.Parse(json));
    }

    [Fact]
    public void ALaterVersionIsRefusedPlainly()
    {
        var json = ClipFile.FromMotion(Sample(), "").ToJson().Replace("\"version\": 1", "\"version\": 99");
        var wrong = Assert.Throws<InvalidDataException>(() => ClipFile.Parse(json));
        Assert.Contains("99", wrong.Message);
    }

    [Fact]
    public void AKeyOfTheWrongWidthIsRefused()
    {
        var json = ClipFile.FromMotion(Sample(), "").ToJson();
        var broken = json.Replace("[0, 1, 2, 3,", "[0, 1, 2,");
        Assert.Throws<InvalidDataException>(() => ClipFile.Parse(broken));
    }
}

public class RetargetTests
{
    [Fact]
    public void TheSamePathIsItself()
    {
        var result = Retarget.Paths(["", "gun/Arm"], ["", "gun", "gun/Arm"]);
        Assert.Equal("", result.Map[""]);
        Assert.Equal("gun/Arm", result.Map["gun/Arm"]);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void TheArmsAreFoundUnderAnotherWeaponsName()
    {
        // #15 Hammer's paths start with its own name; the arms below it are called the same on
        // every weapon, and that is what the match goes by.
        var result = Retarget.Paths(
            ["pig_hammer/FPS_PLAYER_Arm_Right", "pig_hammer/FPS_PLAYER_Arm_left"],
            ["", "x-mas_destroyer 1", "x-mas_destroyer 1/FPS_PLAYER_Arm_Right", "x-mas_destroyer 1/FPS_PLAYER_Arm_left"]);

        Assert.Equal("x-mas_destroyer 1/FPS_PLAYER_Arm_Right", result.Map["pig_hammer/FPS_PLAYER_Arm_Right"]);
        Assert.Equal("x-mas_destroyer 1/FPS_PLAYER_Arm_left", result.Map["pig_hammer/FPS_PLAYER_Arm_left"]);
    }

    [Fact]
    public void TheWeaponsOwnNameFollowsFromItsArms()
    {
        // `pig_hammer` matches nothing by name, but its arms said what it corresponds to.
        var result = Retarget.Paths(
            ["pig_hammer", "pig_hammer/FPS_PLAYER_Arm_Right"],
            ["", "comet", "comet/FPS_PLAYER_Arm_Right"]);

        Assert.Equal("comet", result.Map["pig_hammer"]);
    }

    [Fact]
    public void TwoEquallyGoodAnswersAreNotGuessedBetween()
    {
        var result = Retarget.Paths(["a/Bone001"], ["", "x", "x/Bone001", "y", "y/Bone001"]);
        Assert.Contains("a/Bone001", result.Unmatched);
    }

    [Fact]
    public void TheGunsOwnRootIsFoundAsTheOnlyChildOfTheHand()
    {
        var result = Retarget.Paths(
            ["pig/Arm", "pig/Arm/Bone_pig_root"],
            ["", "comet", "comet/Arm", "comet/Arm/Bone_comet_root"],
            sourceRig: ["", "pig", "pig/Arm", "pig/Arm/Bone_pig_root"]);

        Assert.Equal("comet/Arm/Bone_comet_root", result.Map["pig/Arm/Bone_pig_root"]);
    }

    [Fact]
    public void WithoutTheSourcesModelNoOnlyChildIsGuessedAt()
    {
        var result = Retarget.Paths(
            ["pig/Arm", "pig/Arm/Bone_pig_root"],
            ["", "comet", "comet/Arm", "comet/Arm/Bone_comet_root"]);

        Assert.Contains("pig/Arm/Bone_pig_root", result.Unmatched);
    }

    [Fact]
    public void AnOnlyChildAmongTheMovedIsNotOneInTheModel()
    {
        // The hand holds a gun and a sight; the clip moves only the gun. Counted among what moves,
        // the gun looked like the hand's only child and was matched to whatever the other hand held.
        var result = Retarget.Paths(
            ["pig/Arm", "pig/Arm/Bone_pig_root"],
            ["", "comet", "comet/Arm", "comet/Arm/Bone_comet_root"],
            sourceRig: ["", "pig", "pig/Arm", "pig/Arm/Bone_pig_root", "pig/Arm/Sight"]);

        Assert.Contains("pig/Arm/Bone_pig_root", result.Unmatched);
    }

    [Fact]
    public void WhatFindsNothingIsLeftOutOfTheMotionAndSaid()
    {
        var motion = new Motion("Shoot", 1, 30, [
            new MotionCurve("pig/Arm", MotionChannel.Rotation, [new MotionKey(0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0)]),
            new MotionCurve("pig/Arm/Trigger_only_here", MotionChannel.Position, [new MotionKey(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)]),
        ]);

        var (moved, unmatched) = Retarget.Apply(motion, ["", "gun", "gun/Arm", "gun/Arm/Root", "gun/Arm/Root/A", "gun/Arm/Root/B"]);

        Assert.Equal(["gun/Arm"], moved.Curves.Select(c => c.Path));
        Assert.Equal(["pig/Arm/Trigger_only_here"], unmatched);
    }
}

public class PackedCurveTests
{
    /// Writes bits lowest first, the way Unity packs them.
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bit;

        public void Write(uint value, int width)
        {
            for (var i = 0; i < width; i++)
            {
                if (_bit == 0) _bytes.Add(0);
                if (((value >> i) & 1) != 0) _bytes[^1] |= (byte)(1 << _bit);
                _bit = (_bit + 1) % 8;
            }
        }

        public byte[] Bytes => [.. _bytes];
    }

    [Fact]
    public void IntegersAreReadAcrossByteBoundaries()
    {
        var writer = new BitWriter();
        foreach (var v in new uint[] { 0, 5, 127, 64, 3 }) writer.Write(v, 7);
        Assert.Equal([0, 5, 127, 64, 3], PackedCurve.Ints(writer.Bytes, 7, 5));
    }

    [Fact]
    public void ARotationIsThreeComponentsAndTheFourthWorkedOut()
    {
        // w left out (index 3), positive; x in 9 bits since it follows w round the four, y and z in 10.
        float x = 0.5f, y = -0.5f, z = 0.5f;
        uint Pack(float v, int width) => (uint)MathF.Round((v + 1) * 0.5f * ((1 << width) - 1));

        var writer = new BitWriter();
        writer.Write(3, 3);
        writer.Write(Pack(x, 9), 9);
        writer.Write(Pack(y, 10), 10);
        writer.Write(Pack(z, 10), 10);

        var q = PackedCurve.Quaternions(writer.Bytes, 1)[0];
        Assert.Equal(x, q[0], 0.005f);
        Assert.Equal(y, q[1], 0.005f);
        Assert.Equal(z, q[2], 0.005f);
        Assert.Equal(0.5f, q[3], 0.01f);
        Assert.Equal(1f, MathF.Sqrt(q.Sum(c => c * c)), 0.0001f);
    }

    [Fact]
    public void TheSignBitTurnsTheMissingComponentNegative()
    {
        var writer = new BitWriter();
        writer.Write(3 | 4, 3);
        writer.Write(255, 9); writer.Write(511, 10); writer.Write(511, 10);   // about zero each
        var q = PackedCurve.Quaternions(writer.Bytes, 1)[0];
        Assert.True(q[3] < -0.99f);
    }

    [Fact]
    public void FloatsWithNoBitsAreAllWhereTheRangeStarts()
    {
        // Every packed curve in the game with no slopes says so this way, and dividing by a width of
        // zero would make them not-a-number instead.
        Assert.All(PackedCurve.Floats([], 0, 0.25f, 0, 8), v => Assert.Equal(0.25f, v));
    }

    [Fact]
    public void FloatsSpreadOverTheirRange()
    {
        var writer = new BitWriter();
        foreach (var v in new uint[] { 0, 63, 32 }) writer.Write(v, 6);
        var values = PackedCurve.Floats(writer.Bytes, 6, -2f, 4f, 3);
        Assert.Equal(-2f, values[0], 0.0001f);
        Assert.Equal(2f, values[1], 0.0001f);
        Assert.Equal(-2f + 32 / 63f * 4, values[2], 0.0001f);
    }
}

public class RetargetGunRootTests
{
    [Fact]
    public void TheGunsRootIsTheOnlyChildOfTheHandThatHoldsAnything()
    {
        // #416 against #1: each hand holds a marker besides the gun, so neither gun root is an only
        // child, but each is the only child with children of its own.
        var target = new[]
        {
            "", "Pixlgun", "Pixlgun/FPS_PLAYER_Arm_Right", "Pixlgun/FPS_PLAYER_Arm_Right/bone_root",
            "Pixlgun/FPS_PLAYER_Arm_Right/bone_root/bone_clip", "Pixlgun/FPS_PLAYER_Arm_Right/Point_Arm_Right",
        };
        var sourceRig = new[]
        {
            "", "ultimatum 1", "ultimatum 1/FPS_PLAYER_Arm_Right", "ultimatum 1/FPS_PLAYER_Arm_Right/root",
            "ultimatum 1/FPS_PLAYER_Arm_Right/root/barrel_side", "ultimatum 1/FPS_PLAYER_Arm_Right/Point_Arm_Right",
            "ultimatum 1/FPS_PLAYER_Arm_Right/FPS_PLAYER_Arm_Right",
        };

        var result = Retarget.Paths(
            ["ultimatum 1/FPS_PLAYER_Arm_Right", "ultimatum 1/FPS_PLAYER_Arm_Right/root", "ultimatum 1/FPS_PLAYER_Arm_Right/root/barrel_side"],
            target, sourceRig);

        Assert.Equal("Pixlgun/FPS_PLAYER_Arm_Right/bone_root", result.Map["ultimatum 1/FPS_PLAYER_Arm_Right/root"]);
        // Below the root the two guns are made of different things, and nothing is guessed.
        Assert.Equal(["ultimatum 1/FPS_PLAYER_Arm_Right/root/barrel_side"], result.Unmatched);
    }

    /// #2 Shotgun's clips onto #416 Ultimatum, which fitted nothing at all: #416 keeps an arm inside
    /// its own arm, so two of its objects end in the right arm's name, and spells the left arm with
    /// a small l. Both arms went unmatched, and with them everything matched from them.
    private static readonly string[] Shotgun =
    [
        "", "shotgun", "shotgun/Arms_Mesh", "shotgun/Arms_Mesh/Shotgun_Mesh", "shotgun/FPS_PLAYER_Arm_Left",
        "shotgun/FPS_PLAYER_Arm_Left/FPS_PLAYER_Arm_Left", "shotgun/FPS_PLAYER_Arm_Left/Point_Arm_Left",
        "shotgun/FPS_PLAYER_Arm_Right", "shotgun/FPS_PLAYER_Arm_Right/Bone_Root",
        "shotgun/FPS_PLAYER_Arm_Right/Bone_Root/Bone_Bullet", "shotgun/FPS_PLAYER_Arm_Right/Bone_Root/Bone_Shutter",
        "shotgun/FPS_PLAYER_Arm_Right/Bone_Root/Particle_Bullet", "shotgun/FPS_PLAYER_Arm_Right/Point_Arm_Right",
    ];

    private static readonly string[] Ultimatum =
    [
        "", "ultimatum 1", "ultimatum 1/Arms_Mesh", "ultimatum 1/Arms_Mesh/ultimatum_mesh", "ultimatum 1/FPS_PLAYER_Arm_left",
        "ultimatum 1/FPS_PLAYER_Arm_left/Point_Arm_Left", "ultimatum 1/FPS_PLAYER_Arm_left/FPS_PLAYER_Arm_left",
        "ultimatum 1/FPS_PLAYER_Arm_Right", "ultimatum 1/FPS_PLAYER_Arm_Right/root",
        "ultimatum 1/FPS_PLAYER_Arm_Right/root/barrel_side", "ultimatum 1/FPS_PLAYER_Arm_Right/root/element_01",
        "ultimatum 1/FPS_PLAYER_Arm_Right/Point_Arm_Right", "ultimatum 1/FPS_PLAYER_Arm_Right/FPS_PLAYER_Arm_Right",
    ];

    [Fact]
    public void AnArmInsideItselfAndAnArmSpeltSmallStillMatch()
    {
        var moved = new[]
        {
            "shotgun", "shotgun/FPS_PLAYER_Arm_Right", "shotgun/FPS_PLAYER_Arm_Right/Bone_Root",
            "shotgun/FPS_PLAYER_Arm_Right/Bone_Root/Bone_Shutter", "shotgun/FPS_PLAYER_Arm_Left",
            "shotgun/FPS_PLAYER_Arm_Left/FPS_PLAYER_Arm_Left",
        };

        var result = Retarget.Paths(moved, Ultimatum, Shotgun);

        Assert.Equal("ultimatum 1/FPS_PLAYER_Arm_Right", result.Map["shotgun/FPS_PLAYER_Arm_Right"]);
        // Spelt the way #416 spells it, which is the way the game will look for it.
        Assert.Equal("ultimatum 1/FPS_PLAYER_Arm_left", result.Map["shotgun/FPS_PLAYER_Arm_Left"]);
        Assert.Equal("ultimatum 1/FPS_PLAYER_Arm_left/FPS_PLAYER_Arm_left", result.Map["shotgun/FPS_PLAYER_Arm_Left/FPS_PLAYER_Arm_Left"]);
        Assert.Equal("ultimatum 1", result.Map["shotgun"]);
        Assert.Equal("ultimatum 1/FPS_PLAYER_Arm_Right/root", result.Map["shotgun/FPS_PLAYER_Arm_Right/Bone_Root"]);
        Assert.Equal(["shotgun/FPS_PLAYER_Arm_Right/Bone_Root/Bone_Shutter"], result.Unmatched);
    }

    private static AnimationRig Rig(string name, string arm, float[] gunRest) => new(
        "b", 1, "W", ["", name, name + "/" + arm], [], [-1, 0, 1],
        [[0, 0, 0, 0, 0, 0, 1, 1, 1, 1], gunRest, [0.3f, -0.2f, 0.5f, 0, 0, 0, 1, 1, 1, 1]]);

    private static Motion Swing(string gun) => new("Reload", 1, 30,
    [
        new MotionCurve(gun + "/FPS_PLAYER_Arm_Right", MotionChannel.Rotation,
        [
            new MotionKey(0, 0, 0, 0, 1, 0.2f, 0, 0, 0, 0.2f, 0, 0, 0),
            new MotionKey(1, 0.2588190f, 0, 0, 0.9659258f, 0, 0, 0, 0, 0, 0, 0, 0),
        ]),
        new MotionCurve(gun + "/FPS_PLAYER_Arm_Right", MotionChannel.Position,
        [
            new MotionKey(0, 0.3f, -0.2f, 0.5f, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            new MotionKey(1, 0.3f, -0.1f, 0.5f, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        ]),
    ]);

    [Fact]
    public void BetweenRigsThatStandAlikeTheNumbersGoAcrossAsTheyAre()
    {
        var there = Rig("shotgun", "FPS_PLAYER_Arm_Right", [0, 0, 0, 0, 0, 0, 1, 1, 1, 1]);
        var here = Rig("ultimatum 1", "FPS_PLAYER_Arm_Right", [0, 0, 0, 0, 0, 0, 1, 1, 1, 1]);

        var (moved, _) = Retarget.Apply(Swing("shotgun"), here, there);

        foreach (var (a, b) in Swing("shotgun").Curves.Zip(moved.Curves))
            foreach (var (k, m) in a.Keys.Zip(b.Keys))
            {
                Assert.Equal(k.X, m.X, 5); Assert.Equal(k.Y, m.Y, 5); Assert.Equal(k.Z, m.Z, 5); Assert.Equal(k.W, m.W, 5);
                Assert.Equal(k.OutX, m.OutX, 5);
            }
    }

    [Fact]
    public void AGunStoodOtherwiseKeepsItsOwnStanceAndTakesTheMotion()
    {
        // The arm's parent turned a quarter about Y here: #2's arm swinging about the camera's X has
        // to swing about the camera's X here too, from where this arm stands.
        var there = Rig("shotgun", "FPS_PLAYER_Arm_Right", [0, 0, 0, 0, 0, 0, 1, 1, 1, 1]);
        var here = Rig("ultimatum 1", "FPS_PLAYER_Arm_Right", [0, 0, 0, 0, 0.70710677f, 0, 0.70710677f, 1, 1, 1]);

        var (moved, _) = Retarget.Apply(Swing("shotgun"), here, there);
        var turn = moved.Curves.Single(c => c.Channel == MotionChannel.Rotation).Keys;
        var place = moved.Curves.Single(c => c.Channel == MotionChannel.Position).Keys;

        // At rest it stands as this rig has it.
        Assert.Equal(1f, MathF.Abs(turn[0].W), 5);
        Assert.Equal(0.3f, place[0].X, 5);
        Assert.Equal(-0.2f, place[0].Y, 5);
        Assert.Equal(0.5f, place[0].Z, 5);

        // The swing about the camera's X is, in this parent's quarter-turned space, a swing about its Z.
        Assert.Equal(0f, turn[1].X, 5);
        Assert.Equal(MathF.Abs(0.2588190f), MathF.Abs(turn[1].Z), 5);
        // And the step up stays a step up.
        Assert.Equal(-0.1f, place[1].Y, 5);
    }

    [Fact]
    public void TwoAnswersAtTheSameDepthAreStillAGuess()
    {
        var result = Retarget.Paths(["gun/Hand"], ["a/Hand", "b/Hand"]);
        Assert.Equal(["gun/Hand"], result.Unmatched);
    }
}

/// A clip keeps the length the game shipped it with, whatever is written into it: the game times
/// shots and reloads by its clips, and another length is another fire rate.
public class ClipLengthTests
{
    private static Motion Shot(float length) => new("Shoot", length, 30,
    [
        new MotionCurve("gun", MotionChannel.Position,
        [
            new MotionKey(0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0),
            new MotionKey(length / 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            new MotionKey(length, 0, 0, 0, 0, float.PositiveInfinity, 0, 0, 0, -1, 0, 0, 0),
        ]),
    ]);

    [Theory]
    [InlineData(0.67f, 2.6f)]
    [InlineData(3.23f, 0.17f)]
    public void AMotionIsMadeExactlyAsLongAsTheClipItGoesInto(float given, float length)
    {
        var fitted = ClipImporter.Fit(Shot(given), length);

        Assert.Equal(length, fitted.Length);
        Assert.Equal(length, fitted.Curves.SelectMany(c => c.Keys).Max(k => k.Time));
        // The same curve at another speed: where it was halfway, it is halfway.
        var k = length / given;
        for (var f = 0f; f <= 1f; f += 0.125f)
            Assert.Equal(Motion.Sample(Shot(given).Curves[0], given * f).X, Motion.Sample(fitted.Curves[0], length * f).X, 4);
        Assert.Equal(2 / k, fitted.Curves[0].Keys[0].OutX, 4);
        Assert.True(float.IsPositiveInfinity(fitted.Curves[0].Keys[2].InX));
    }

    [Fact]
    public void APoseIsHeldForTheLength()
    {
        var pose = new Motion("Idle", 0, 30, [new MotionCurve("gun", MotionChannel.Position, [new MotionKey(0, 1, 2, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0)])]);
        var fitted = ClipImporter.Fit(pose, 0.17f);
        Assert.Equal(0.17f, fitted.Curves[0].Keys[^1].Time);
        Assert.Equal((1f, 2f, 3f), (fitted.Curves[0].Keys[^1].X, fitted.Curves[0].Keys[^1].Y, fitted.Curves[0].Keys[^1].Z));
    }

    [Fact]
    public void TheRightLengthIsLeftAlone()
        => Assert.Equal(Shot(1).Curves[0].Keys, ClipImporter.Fit(Shot(1), 1).Curves[0].Keys);
}
