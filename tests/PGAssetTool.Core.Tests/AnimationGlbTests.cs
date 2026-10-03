using System.Text;
using System.Text.Json.Nodes;
using PGAssetTool.Core.Animation;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Core.Tests;

/// An item's animations out to a glTF and back: what Blender is handed, and what is made of what
/// it hands back.
public class AnimationGlbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pgat-glb-" + Guid.NewGuid().ToString("N")[..8]);

    public AnimationGlbTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    /// A gun's root with an arm under it, a hand under that kept at a scale a Blender bone cannot
    /// rest at, and a flash beside the arm kept at nothing, the way a muzzle flash waits.
    private static readonly AnimationRig Rig = new(
        "b", 1, "Weapon", ["", "Arm", "Arm/Hand", "Flash"], [], [-1, 0, 1, 0],
        [
            [0, 1, 0, 0, 0, 0, 1, 1, 1, 1],
            [0.2f, 0, 0.5f, 0, 0.38268343f, 0, 0.9238795f, 1, 1, 1],
            [0, -0.1f, 0.3f, 0, 0, 0, 1, 0.95f, 0.95f, 0.95f],
            [0, 0, 1, 0, 0, 0, 1, 0, 0, 0],
        ]);

    /// The arm turns with slopes of its own; the hand jumps a quarter of the way through.
    private static Motion Reload() => new("Reload", 1f, 30,
    [
        new MotionCurve("Arm", MotionChannel.Rotation,
        [
            new MotionKey(0, 0, 0.38268343f, 0, 0.9238795f, 0, 0.6f, 0, -0.2f, 0, 0.6f, 0, -0.2f),
            new MotionKey(0.5f, 0.2f, 0.6f, 0.1f, 0.7681146f, 0.3f, 0, 0, 0, 0.3f, 0, 0, 0),
            new MotionKey(1f, 0, 0.38268343f, 0, 0.9238795f, 0, -0.5f, 0, 0.2f, 0, -0.5f, 0, 0.2f),
        ]),
        new MotionCurve("Arm/Hand", MotionChannel.Position,
        [
            new MotionKey(0, 0, -0.1f, 0.3f, 0, 0, 0, 0, 0, float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, 0),
            new MotionKey(0.25f, 0.1f, 0.2f, 0.4f, 0, float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, 0, 0, 0, 0, 0),
            new MotionKey(1f, 0, -0.1f, 0.3f, 0, -0.1f, 0, 0, 0, 0, 0, 0, 0),
        ]),
    ]);

    private string Written(params Motion[] motions)
    {
        var path = Path.Combine(_dir, AnimationGlb.FileName);
        AnimationGlb.Write(path, Rig, [], motions);
        return path;
    }

    [Fact]
    public void AnAnimationNobodyEditedComesBackAsTheOneThatWentOut()
    {
        var path = Written(Reload(), Reload() with { Name = "Idle" });

        var back = AnimationGlb.Read(path, Rig, ["Reload"], original: Reload(), current: Reload());

        Assert.True(back.Same);
        Assert.Equal("Reload", back.Take);
        // Not the samples: the keys and slopes the game had, every one.
        Assert.Equal(Reload().Curves.Select(c => (c.Path, c.Channel)), back.Motion.Curves.Select(c => (c.Path, c.Channel)));
        foreach (var (was, now) in Reload().Curves.Zip(back.Motion.Curves))
            Assert.Equal(was.Keys, now.Keys);
    }

    [Fact]
    public void WhatIsWrittenPlaysAsTheGamePlaysItOnEveryFrame()
    {
        var back = AnimationGlb.Read(Written(Reload()), Rig, ["Reload"]);
        var game = Reload();

        // The objects the clip leaves alone go out held at rest, so Blender shows them where the
        // game has them — and come back holding still, which moves nothing and is left out.
        Assert.Equal(["Arm:Rotation", "Arm/Hand:Position"], back.Motion.Curves.Select(c => $"{c.Path}:{c.Channel}"));

        for (var frame = 0; frame <= 24; frame++)
        {
            var t = frame / AnimationGlb.Frames;
            var arm = (game.Curves[0], back.Motion.Curves[0]);
            var (gx, gy, gz, gw) = Motion.Sample(arm.Item1, t);
            var (bx, by, bz, bw) = Motion.Sample(arm.Item2, t);
            var length = MathF.Sqrt(gx * gx + gy * gy + gz * gz + gw * gw);
            var dot = (gx * bx + gy * by + gz * bz + gw * bw) / length;
            Assert.True(MathF.Abs(dot) > 0.99999f, $"the arm at frame {frame}: {dot}");

            var hand = (Motion.Sample(game.Curves[1], t), Motion.Sample(back.Motion.Curves[1], t));
            Assert.Equal(hand.Item1.X, hand.Item2.X, 4);
            Assert.Equal(hand.Item1.Y, hand.Item2.Y, 4);
            Assert.Equal(hand.Item1.Z, hand.Item2.Z, 4);
        }
    }

    [Fact]
    public void WhatIsWrittenNamesTheObjectsAndHoldsTheRestWhereTheGameHasIt()
    {
        var gltf = Json(Written(Reload()));
        var nodes = gltf["nodes"]!.AsArray();

        Assert.Equal(["Weapon", "Arm", "Hand", "Flash"], nodes.Take(4).Select(n => n!["name"]!.GetValue<string>()));
        // Every object a joint of the one skin, so Blender makes one armature of them.
        Assert.Equal(4, gltf["skins"]![0]!["joints"]!.AsArray().Count);
        // Z negated: a position's z, a rotation's x and y.
        Assert.Equal(-0.5f, nodes[1]!["translation"]![2]!.GetValue<float>());
        Assert.Equal(0.38268343f, nodes[1]!["rotation"]![1]!.GetValue<float>() * -1, 5);

        var animation = gltf["animations"]![0]!;
        var hand = animation["channels"]!.AsArray()
            .Where(c => c!["target"]!["node"]!.GetValue<int>() == 2)
            .Select(c => c!["target"]!["path"]!.GetValue<string>())
            .ToList();
        Assert.Contains("scale", hand);
        Assert.All(animation["samplers"]!.AsArray(), s => Assert.Equal("LINEAR", s!["interpolation"]!.GetValue<string>()));
    }

    [Fact]
    public void ABlenderFileIsReadUnderItsArmatureWithItsNumberedNamesAndItsOwnStart()
    {
        var file = new GlbFixture();
        var frame = 1 / 24f;
        var rotations = file.Floats([frame, 0.5f + frame], "SCALAR");
        // The second key the other way round: the same turn as far as a rotation goes.
        var turned = file.Floats([0, 0, 0, 1, 0, 0.38268343f, 0, -0.9238795f], "VEC4");
        var steps = file.Floats([frame, 0.25f + frame], "SCALAR");
        var places = file.Floats([0, 0, 0, 1, 2, 3], "VEC3");
        var cubicTimes = file.Floats([frame, 1 + frame], "SCALAR");
        var cubic = file.Floats([0, 0, 0, 1, 1, 1, 2, 0, 0, 0.5f, 0, 0, 2, 2, 2, 0, 0, 0], "VEC3");
        var weights = file.Floats([0, 1], "SCALAR");

        var path = file.Save(Path.Combine(_dir, "blender.glb"), new JsonObject
        {
            ["scene"] = 0,
            ["scenes"] = new JsonArray { new JsonObject { ["nodes"] = new JsonArray { 0 } } },
            ["nodes"] = new JsonArray
            {
                new JsonObject { ["name"] = "Armature", ["children"] = new JsonArray { 1, 4 } },
                new JsonObject { ["name"] = "Weapon", ["children"] = new JsonArray { 2 } },
                new JsonObject { ["name"] = "Arm", ["children"] = new JsonArray { 3 } },
                new JsonObject { ["name"] = "Hand.001" },
                new JsonObject { ["name"] = "Body" },
            },
            ["animations"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "Reload_Armature",
                    ["samplers"] = new JsonArray
                    {
                        new JsonObject { ["input"] = rotations, ["output"] = turned, ["interpolation"] = "LINEAR" },
                        new JsonObject { ["input"] = steps, ["output"] = places, ["interpolation"] = "STEP" },
                        new JsonObject { ["input"] = cubicTimes, ["output"] = cubic, ["interpolation"] = "CUBICSPLINE" },
                        new JsonObject { ["input"] = steps, ["output"] = weights },
                    },
                    ["channels"] = new JsonArray
                    {
                        Channel(0, 2, "rotation"), Channel(1, 3, "translation"), Channel(2, 2, "scale"), Channel(3, 4, "weights"),
                    },
                },
                new JsonObject { ["name"] = "Idle_Armature", ["samplers"] = new JsonArray(), ["channels"] = new JsonArray() },
            },
        });

        var back = AnimationGlb.Read(path, Rig, ["Reload"]);
        Assert.Equal("Reload_Armature", back.Take);
        Assert.Contains(back.Notes, n => n.Contains("started at"));
        Assert.Contains(back.Notes, n => n.Contains("shape-key"));
        var curves = back.Motion.Curves.ToDictionary(c => (c.Path, c.Channel));

        // A step: held until the next key, which Unity says with a slope of infinity.
        var hand = curves[("Arm/Hand", MotionChannel.Position)].Keys;
        Assert.Equal([0f, 0.25f], hand.Select(k => MathF.Round(k.Time, 5)));
        Assert.Equal((1f, 2f, -3f), (hand[1].X, hand[1].Y, hand[1].Z));
        Assert.True(float.IsPositiveInfinity(hand[0].OutX));

        // The turn kept going the short way, and arrives at the arm's own rest, in Unity's terms.
        var arm = curves[("Arm", MotionChannel.Rotation)].Keys;
        Assert.Equal(0.38268343f, arm[^1].Y, 5);
        Assert.Equal(0.9238795f, arm[^1].W, 5);
        Assert.True(arm.Zip(arm.Skip(1)).All(p => p.First.X * p.Second.X + p.First.Y * p.Second.Y
            + p.First.Z * p.Second.Z + p.First.W * p.Second.W > 0));
        // Forty-five degrees in one step is cut into steps of at most ten, each turned at one rate.
        Assert.True(arm.Count >= 6, $"{arm.Count} keys");

        // A cubic keeps its tangents as the slopes either side.
        var scale = curves[("Arm", MotionChannel.Scale)].Keys;
        Assert.Equal(2f, scale[0].OutX);
        Assert.Equal(0.5f, scale[1].InX);
    }

    [Fact]
    public void TheAnimationReadIsTheOneNamedLikeTheSlot()
    {
        var path = Written(Reload() with { Name = "Idle" }, Reload() with { Name = "Reload_Armature" }, Reload() with { Name = "Walk" });

        Assert.Equal("Reload_Armature", AnimationGlb.Read(path, Rig, ["Reload"]).Take);
        Assert.Equal("Walk", AnimationGlb.Read(path, Rig, ["Reload"], take: "walk").Take);

        var none = Assert.Throws<KeyNotFoundException>(() => AnimationGlb.Read(path, Rig, ["Shoot"]));
        Assert.Contains("'Idle'", none.Message);
        Assert.Throws<KeyNotFoundException>(() => AnimationGlb.Read(path, Rig, ["Reload"], take: "Jump"));

        // One animation is the one, whatever it is called.
        var only = Written(Reload() with { Name = "mixamo.com" });
        Assert.Equal("mixamo.com", AnimationGlb.Read(only, Rig, ["Reload"]).Take);
    }

    [Fact]
    public void HoldingStillIsKeptOnlyWhereTheGameHeldStillToo()
    {
        var path = Written(Reload());
        var pinning = Reload() with
        {
            Curves = [.. Reload().Curves, new MotionCurve("Flash", MotionChannel.Scale, [new MotionKey(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)])],
        };

        Assert.DoesNotContain(AnimationGlb.Read(path, Rig, ["Reload"], original: Reload()).Motion.Curves, c => c.Path == "Flash");
        Assert.Contains(AnimationGlb.Read(path, Rig, ["Reload"], original: pinning).Motion.Curves,
            c => c.Path == "Flash" && c.Channel == MotionChannel.Scale);
    }

    [Fact]
    public void AFileThatIsNotAGlbIsRefusedForWhatItIs()
    {
        var path = Path.Combine(_dir, "not.glb");
        File.WriteAllText(path, "{}");
        Assert.Throws<InvalidDataException>(() => AnimationGlb.Read(path, Rig, ["Reload"]));

        var still = Path.Combine(_dir, "still.glb");
        AnimationGlb.Write(still, Rig, [], []);
        Assert.Throws<InvalidDataException>(() => AnimationGlb.Read(still, Rig, ["Reload"]));
    }

    private static JsonObject Channel(int sampler, int node, string path)
        => new() { ["sampler"] = sampler, ["target"] = new JsonObject { ["node"] = node, ["path"] = path } };

    private static JsonObject Json(string glb)
    {
        var bytes = File.ReadAllBytes(glb);
        var length = BitConverter.ToInt32(bytes, 12);
        return JsonNode.Parse(Encoding.UTF8.GetString(bytes, 20, length))!.AsObject();
    }

    /// A glTF put together by hand, the way another program would write one.
    private sealed class GlbFixture
    {
        private readonly MemoryStream _binary = new();
        private readonly JsonArray _accessors = [];
        private readonly JsonArray _views = [];

        public int Floats(float[] values, string type)
        {
            while (_binary.Length % 4 != 0) _binary.WriteByte(0);
            var offset = _binary.Length;
            foreach (var v in values) _binary.Write(BitConverter.GetBytes(v));
            _views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = values.Length * 4 });

            var width = type switch { "SCALAR" => 1, "VEC3" => 3, _ => 4 };
            var accessor = new JsonObject
            {
                ["bufferView"] = _views.Count - 1, ["componentType"] = 5126, ["count"] = values.Length / width, ["type"] = type,
            };
            if (type == "SCALAR")
            {
                accessor["min"] = new JsonArray { values.Min() };
                accessor["max"] = new JsonArray { values.Max() };
            }
            _accessors.Add(accessor);
            return _accessors.Count - 1;
        }

        public string Save(string path, JsonObject gltf)
        {
            gltf["asset"] = new JsonObject { ["version"] = "2.0" };
            gltf["accessors"] = _accessors;
            gltf["bufferViews"] = _views;
            while (_binary.Length % 4 != 0) _binary.WriteByte(0);
            var binary = _binary.ToArray();
            gltf["buffers"] = new JsonArray { new JsonObject { ["byteLength"] = binary.Length } };

            var json = Encoding.UTF8.GetBytes(gltf.ToJsonString());
            var padded = json.Concat(Enumerable.Repeat((byte)' ', (4 - json.Length % 4) % 4)).ToArray();
            using var output = new BinaryWriter(File.Create(path));
            output.Write(0x46546C67u);
            output.Write(2u);
            output.Write((uint)(12 + 8 + padded.Length + 8 + binary.Length));
            output.Write((uint)padded.Length);
            output.Write(0x4E4F534Au);
            output.Write(padded);
            output.Write((uint)binary.Length);
            output.Write(0x004E4942u);
            output.Write(binary);
            return path;
        }
    }
}
