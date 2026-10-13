using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Import.Meshes;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Core.Animation;

/// An item's animations as a binary glTF, for another program to edit, and an animation read back
/// out of one.
///
/// The file holds the objects the animations move, by their own names and hung the way the game
/// hangs them, the item's models on them so there is something to watch, and each clip as an
/// animation of the clip's name. Blender reads that as one armature with an action per clip, and
/// what it writes back names the same objects — which is what lets a clip edited there land on the
/// same objects here.
///
/// Every object is a joint of one skin, whether or not a model bends with it: one armature and one
/// action per clip, rather than a skeleton for the gun, another for the arms, and every object
/// neither of them bends as a loose empty with an action of its own that an exporter then has to be
/// told to put back together. The models are bound to it where they stand, so nothing about how
/// this game happened to rig them — bind poses written against an origin the prefab no longer has —
/// reaches the other program.
///
/// Unity is left-handed and glTF right-handed, so Z is negated on the way out and back on the way
/// in: a position's z, a rotation's x and y.
public static partial class AnimationGlb
{
    public const string FileName = "animations.glb";

    private const int Float = 5126;
    private const int UnsignedShort = 5123;
    private const int UnsignedInt = 5125;

    /// One of the item's models: the mesh, what it hangs off, and the picture each of its submeshes
    /// wears where the workspace has one — a PNG's bytes, or null.
    public sealed record Model(UnityMesh Mesh, Skeleton Skeleton, IReadOnlyList<byte[]?> Pictures);

    /// <param name="LeftOut">Models and curves that are not in the file, each with why.</param>
    public sealed record Written(int Objects, IReadOnlyList<string> Models, int Animations, IReadOnlyList<string> LeftOut);

    /// <param name="motions">Each becomes an animation called by its `Name`.</param>
    public static Written Write(string path, AnimationRig rig, IReadOnlyList<Model> models, IReadOnlyList<Motion> motions)
    {
        var data = new Data();
        var leftOut = new List<string>();
        var rest = rig.Rest();
        var count = rig.Paths.Count;

        var nodes = new JsonArray();
        for (var at = 0; at < count; at++)
        {
            var l = Finite(rig.Locals[at]);
            nodes.Add(new JsonObject
            {
                ["name"] = NameOf(rig, at),
                ["translation"] = Numbers([l[0], l[1], -l[2]]),
                ["rotation"] = Numbers(Unit([-l[3], -l[4], l[5], l[6]])),
                ["scale"] = Numbers([l[7], l[8], l[9]]),
            });
        }
        var scene = new JsonArray();
        for (var at = 0; at < count; at++)
        {
            if (rig.Parents[at] < 0) { scene.Add(at); continue; }
            var parent = (JsonObject)nodes[rig.Parents[at]]!;
            if (parent["children"] is not JsonArray children) parent["children"] = children = [];
            children.Add(at);
        }

        // Each joint's inverse bind matrix undoes where it stands at rest, which is where the models
        // are bound below: at rest the skin moves nothing.
        var inverse = new float[count * 16];
        for (var at = 0; at < count; at++)
            Array.Copy(Sided(Matrix.Inverse(rest[at])), 0, inverse, at * 16, 16);
        var skin = new JsonObject
        {
            ["joints"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)i).ToArray()),
            ["inverseBindMatrices"] = data.Floats(inverse, "MAT4"),
        };

        var meshes = new JsonArray();
        var materials = new JsonArray();
        var textures = new JsonArray();
        var images = new JsonArray();
        var dressed = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
        var shown = new List<string>();

        foreach (var model in models)
        {
            if (Bake(model, rig, rest, out var why) is not { } baked)
            {
                leftOut.Add($"{model.Mesh.Name}: {why}");
                continue;
            }

            var attributes = new JsonObject { ["POSITION"] = data.Floats(baked.Positions, "VEC3", bounded: true) };
            if (baked.Normals is { } normals) attributes["NORMAL"] = data.Floats(normals, "VEC3");
            if (baked.Uvs is { } uvs) attributes["TEXCOORD_0"] = data.Floats(uvs, "VEC2");
            attributes["JOINTS_0"] = data.Shorts(baked.Joints, "VEC4");
            attributes["WEIGHTS_0"] = data.Floats(baked.Weights, "VEC4");

            var primitives = new JsonArray();
            var mesh = model.Mesh;
            for (var s = 0; s < mesh.SubMeshes.Count; s++)
            {
                var sub = mesh.SubMeshes[s];
                if (sub.Topology != 0 || sub.IndexCount < 3) continue;

                // Reversed, to match the negated axis.
                var triangles = new uint[sub.IndexCount / 3 * 3];
                for (var i = 0; i + 2 < sub.IndexCount; i += 3)
                {
                    triangles[i] = (uint)(mesh.Indices[sub.IndexStart + i + 2] + sub.BaseVertex);
                    triangles[i + 1] = (uint)(mesh.Indices[sub.IndexStart + i + 1] + sub.BaseVertex);
                    triangles[i + 2] = (uint)(mesh.Indices[sub.IndexStart + i] + sub.BaseVertex);
                }

                var primitive = new JsonObject
                {
                    ["attributes"] = attributes.DeepClone(),
                    ["indices"] = data.Ints(triangles),
                    ["mode"] = 4,
                };
                if (s < model.Pictures.Count && model.Pictures[s] is { } picture && IsPng(picture))
                {
                    if (!dressed.TryGetValue(picture, out var material))
                    {
                        images.Add(new JsonObject { ["bufferView"] = data.View(picture), ["mimeType"] = "image/png" });
                        textures.Add(new JsonObject { ["source"] = images.Count - 1 });
                        materials.Add(new JsonObject
                        {
                            ["name"] = $"{mesh.Name} {s}",
                            ["pbrMetallicRoughness"] = new JsonObject
                            {
                                ["baseColorTexture"] = new JsonObject { ["index"] = textures.Count - 1 },
                                ["metallicFactor"] = 0,
                                ["roughnessFactor"] = 1,
                            },
                        });
                        dressed[picture] = material = materials.Count - 1;
                    }
                    primitive["material"] = material;
                }
                primitives.Add(primitive);
            }
            if (primitives.Count == 0)
            {
                leftOut.Add($"{mesh.Name}: it has no triangles");
                continue;
            }

            meshes.Add(new JsonObject { ["name"] = mesh.Name, ["primitives"] = primitives });
            scene.Add(nodes.Count);
            nodes.Add(new JsonObject { ["name"] = mesh.Name, ["mesh"] = meshes.Count - 1, ["skin"] = 0 });
            shown.Add(mesh.Name);
        }

        var animations = new JsonArray();
        foreach (var motion in motions)
        {
            var channels = new JsonArray();
            var samplers = new JsonArray();
            foreach (var curve in motion.Curves)
            {
                var node = IndexOf(rig, curve.Path);
                if (node < 0)
                {
                    leftOut.Add($"{motion.Name}: '{curve.Path}' is not under '{rig.Name}'");
                    continue;
                }
                if (Sampler(data, curve) is not { } sampler) continue;

                samplers.Add(sampler);
                channels.Add(new JsonObject
                {
                    ["sampler"] = samplers.Count - 1,
                    ["target"] = new JsonObject { ["node"] = node, ["path"] = PathOf(curve.Channel) },
                });
            }
            if (channels.Count == 0)
            {
                leftOut.Add($"{motion.Name}: it moves nothing here");
                continue;
            }

            // Everything the clip leaves alone, held where the game has it. A Blender bone cannot
            // keep a scale at rest, so one the game keeps at 0.95 stood at 1 in every action that
            // did not say otherwise — and went back out at 1, as a curve the clip never had. Held
            // here, it stands where it should, and comes back holding still where it already
            // stands, which reading leaves out.
            var said = motion.Curves.Select(c => (c.Path, c.Channel)).ToHashSet();
            for (var at = 0; at < count; at++)
                foreach (var channel in new[] { MotionChannel.Position, MotionChannel.Rotation, MotionChannel.Scale })
                {
                    if (said.Contains((rig.Paths[at], channel))) continue;
                    var l = rig.Locals[at];
                    float[] value = channel switch
                    {
                        MotionChannel.Position => [l[0], l[1], l[2]],
                        MotionChannel.Rotation => Unit([l[3], l[4], l[5], l[6]]),
                        _ => [l[7], l[8], l[9]],
                    };
                    samplers.Add(Sampled(data, [0f], Glb(value, channel), value.Length, "LINEAR"));
                    channels.Add(new JsonObject
                    {
                        ["sampler"] = samplers.Count - 1,
                        ["target"] = new JsonObject { ["node"] = at, ["path"] = PathOf(channel) },
                    });
                }
            animations.Add(new JsonObject { ["name"] = motion.Name, ["channels"] = channels, ["samplers"] = samplers });
        }

        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "PGAssetTool" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray { new JsonObject { ["nodes"] = scene } },
            ["nodes"] = nodes,
            ["skins"] = new JsonArray { skin },
        };
        if (meshes.Count > 0) gltf["meshes"] = meshes;
        if (materials.Count > 0)
        {
            gltf["materials"] = materials;
            gltf["textures"] = textures;
            gltf["images"] = images;
        }
        if (animations.Count > 0) gltf["animations"] = animations;
        gltf["accessors"] = data.Accessors;
        gltf["bufferViews"] = data.Views;

        var binary = data.Binary();
        gltf["buffers"] = new JsonArray { new JsonObject { ["byteLength"] = binary.Length } };
        GlbWriter.WriteContainer(path, gltf, binary);

        return new Written(count, shown, animations.Count, leftOut);
    }

    /// One of the animations a file holds.
    public sealed record Take(string Name, float Length, int Channels);

    public static IReadOnlyList<Take> Takes(string path) => Takes(GltfFile.Open(path));

    private static IReadOnlyList<Take> Takes(GltfFile gltf)
    {
        if (!gltf.Root.TryGetProperty("animations", out var animations)) return [];

        var takes = new List<Take>();
        var at = 0;
        foreach (var animation in animations.EnumerateArray())
        {
            var name = animation.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } s ? s : $"Animation {at}";
            var length = 0f;
            if (animation.TryGetProperty("samplers", out var samplers))
                foreach (var sampler in samplers.EnumerateArray())
                    if (gltf.Find("accessors", sampler.GetProperty("input").GetInt32()) is { } input
                        && input.TryGetProperty("max", out var max) && max.GetArrayLength() > 0)
                        length = Math.Max(length, max[0].GetSingle());
            var channels = animation.TryGetProperty("channels", out var c) ? c.GetArrayLength() : 0;
            takes.Add(new Take(name, length, channels));
            at++;
        }
        return takes;
    }

    /// What reading an animation out of a file came to.
    /// <param name="Take">The animation in the file that was read.</param>
    /// <param name="Unmatched">What it moved that nothing here answers to.</param>
    /// <param name="Notes">Anything else worth saying about how it was read.</param>
    /// <param name="Same">
    /// Whether it is the animation as it was: every curve it had, each unchanged, and nothing else.
    /// What a file written by Write and opened and saved without an edit comes back as.
    /// </param>
    public sealed record Imported(
        Motion Motion, string Take, IReadOnlyList<string> Unmatched, IReadOnlyList<string> Notes, bool Same = false);

    /// Reads one animation out of a file and fits it to this rig.
    /// <param name="names">
    /// What the animation wanted is called, most particular first — the slot's file, then its clip.
    /// An animation of one of those names is the one read; failing that, one whose name starts with
    /// one, which is how an action comes back from Blender (`Reload_Armature`); failing that, the
    /// only one there is.
    /// </param>
    /// <param name="take">The animation to read, by name, overriding all of that.</param>
    /// <param name="original">
    /// The game's own clip for the slot. A curve that holds an object exactly where it stands
    /// anyway is left out — an exporter writes one for every bone, moved or not, and each would
    /// pin that object against whatever else moves it — unless the game's clip has that curve too,
    /// in which case pinning it is part of what the clip does.
    /// </param>
    /// <param name="current">
    /// The slot as it is now. A curve the file holds unchanged from it — every sample on it, which
    /// is what one written by Write and left alone in Blender comes back as — is kept as it is now
    /// rather than as the samples, so the curves nobody edited keep the game's own keys and slopes.
    /// </param>
    /// <exception cref="InvalidDataException">The file is not a binary glTF or holds no animation.</exception>
    /// <exception cref="KeyNotFoundException">No animation in it is the one asked for.</exception>
    public static Imported Read(
        string path, AnimationRig rig, IReadOnlyList<string> names, string? take = null,
        Motion? original = null, Motion? current = null)
    {
        var gltf = GltfFile.Open(path);
        var takes = Takes(gltf);
        var file = Path.GetFileName(path);
        if (takes.Count == 0) throw new InvalidDataException($"'{file}' holds no animation.");

        var chosen = Choose(takes, names, take, file);
        var animation = gltf.Root.GetProperty("animations")[chosen];
        var paths = PathsOf(gltf, rig);
        var notes = new List<string>();

        var raw = new List<(string Path, MotionChannel Channel, float[] Times, float[] Values, int Width, string Interpolation)>();
        var samplers = animation.GetProperty("samplers");
        var morphs = 0;
        foreach (var channel in animation.GetProperty("channels").EnumerateArray())
        {
            var target = channel.GetProperty("target");
            if (!target.TryGetProperty("node", out var nodeIndex)) continue;
            MotionChannel? kind = target.GetProperty("path").GetString() switch
            {
                "translation" => MotionChannel.Position,
                "rotation" => MotionChannel.Rotation,
                "scale" => MotionChannel.Scale,
                _ => null,
            };
            if (kind is not { } moved) { morphs++; continue; }

            var node = nodeIndex.GetInt32();
            if (node < 0 || node >= paths.Count) continue;

            var sampler = samplers[channel.GetProperty("sampler").GetInt32()];
            var times = gltf.ReadFloats(sampler.GetProperty("input").GetInt32(), out _);
            var values = gltf.ReadFloats(sampler.GetProperty("output").GetInt32(), out var width);
            var interpolation = sampler.TryGetProperty("interpolation", out var i) ? i.GetString() ?? "LINEAR" : "LINEAR";
            if (width != (moved == MotionChannel.Rotation ? 4 : 3))
                throw new InvalidDataException($"An animation of '{paths[node]}' has {width} numbers to a key where it needs "
                    + $"{(moved == MotionChannel.Rotation ? 4 : 3)}.");

            if (times.Length > 0) raw.Add((paths[node], moved, times, values, width, interpolation));
        }
        if (morphs > 0) notes.Add($"{morphs} shape-key animation(s) left out: nothing in a weapon has shape keys.");
        if (raw.Count == 0) throw new InvalidDataException($"'{takes[chosen].Name}' in '{file}' moves nothing.");

        // An exporter starts the animation where the timeline starts, which can be anywhere. The
        // game starts a clip at its own zero, so whatever came first starts there.
        var start = raw.Min(r => r.Times[0]);
        if (Math.Abs(start) <= 1e-4f) start = 0;
        else notes.Add($"It started at {start:0.###}s; here it starts at 0.");

        var curves = new List<MotionCurve>();
        var unchanged = new HashSet<(string, MotionChannel)>();
        foreach (var (at, moved, times, values, width, interpolation) in raw)
        {
            var shifted = times.Select(t => t - start).ToArray();
            if (current?.Curves.FirstOrDefault(c => c.Path == at && c.Channel == moved) is { } before
                && Unchanged(before, shifted, values, width, interpolation == "CUBICSPLINE"))
            {
                curves.Add(before);
                unchanged.Add((before.Path, before.Channel));
                continue;
            }

            var keys = Keys(shifted, values, width, interpolation, moved);
            if (keys.Count > 0) curves.Add(new MotionCurve(at, moved, keys));
        }
        if (curves.Count == 0) throw new InvalidDataException($"'{takes[chosen].Name}' in '{file}' moves nothing.");

        var length = curves.SelectMany(c => c.Keys).Max(k => k.Time);
        var motion = new Motion(takes[chosen].Name, length, Rate(curves), curves);
        var (fitted, unmatched) = Retarget.Apply(motion, rig.Paths, paths);

        var kept = new List<MotionCurve>();
        var pinned = original?.Curves.Select(c => (c.Path, c.Channel)).ToHashSet() ?? [];
        var still = 0;
        foreach (var curve in fitted.Curves)
        {
            if (Held(curve) is not { } value) { kept.Add(curve); continue; }

            var at = IndexOf(rig, curve.Path);
            if (at >= 0 && !pinned.Contains((curve.Path, curve.Channel)) && Same(value, rig.Locals[at], curve.Channel))
            {
                still++;
                continue;
            }
            // Held somewhere else: the first and last keys say as much as every frame between.
            kept.Add(curve with { Keys = Hold(curve.Keys) });
        }
        if (still > 0) notes.Add($"{still} curve(s) that only hold an object where it already stands left out.");
        var same = false;
        if (current is not null)
        {
            // In the order the slot had them, so a file read back differs from the one before only
            // where the animation does — an exporter writes its channels in an order of its own.
            var order = current.Curves.Select((c, i) => (c.Path, c.Channel, i)).ToDictionary(x => (x.Path, x.Channel), x => x.i);
            kept = kept.Select((c, i) => (c, i))
                .OrderBy(x => order.TryGetValue((x.c.Path, x.c.Channel), out var was) ? was : current.Curves.Count + x.i)
                .Select(x => x.c)
                .ToList();

            var differ = kept.Count(c => !unchanged.Contains((c.Path, c.Channel)));
            same = differ == 0 && kept.Count == current.Curves.Count;
            if (unchanged.Count > 0 && !same)
                notes.Add($"{differ} curve(s) differ from before; the {unchanged.Count} that do not keep the keys they had.");
        }

        return new Imported(fitted with { Curves = kept }, takes[chosen].Name, unmatched, notes, same);
    }

    private static int Choose(IReadOnlyList<Take> takes, IReadOnlyList<string> names, string? take, string file)
    {
        var listed = string.Join(", ", takes.Select(t => $"'{t.Name}'"));
        if (take is not null)
        {
            for (var at = 0; at < takes.Count; at++)
                if (string.Equals(takes[at].Name, take, StringComparison.OrdinalIgnoreCase)) return at;
            throw new KeyNotFoundException($"'{file}' has no animation called '{take}'. It has {listed}.");
        }

        foreach (var name in names)
            for (var at = 0; at < takes.Count; at++)
                if (string.Equals(takes[at].Name, name, StringComparison.OrdinalIgnoreCase)) return at;

        foreach (var name in names)
        {
            var starting = Enumerable.Range(0, takes.Count)
                .Where(at => takes[at].Name.Length > name.Length
                    && takes[at].Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                    && "_ .|-".Contains(takes[at].Name[name.Length]))
                .OrderBy(at => takes[at].Name.Length)
                .ToList();
            if (starting.Count > 0) return starting[0];
        }

        if (takes.Count == 1) return 0;
        throw new KeyNotFoundException(
            $"'{file}' has {takes.Count} animations and none is called '{names.FirstOrDefault()}': {listed}. Say which one to use.");
    }

    /// Each node's path the way a clip here would name it.
    ///
    /// Counted from the object named like the one carrying the Animation component — under
    /// whatever the other program hung it from, which for Blender is the armature — and where more
    /// than one is named that, the one with the most of this rig under it. A node outside it keeps
    /// its whole path, for Retarget to make what it can of. Blender keeps names unique by adding
    /// `.001`; a name that only differs from one of this rig's by that is taken for it.
    private static IReadOnlyList<string> PathsOf(GltfFile gltf, AnimationRig rig)
    {
        if (!gltf.Root.TryGetProperty("nodes", out var array)) return [];

        var known = rig.Paths.Select(p => p[(p.LastIndexOf('/') + 1)..]).Append(rig.Name).ToHashSet(StringComparer.Ordinal);
        var names = new List<string>();
        var parents = new List<int>();
        var at = 0;
        foreach (var node in array.EnumerateArray())
        {
            var name = node.TryGetProperty("name", out var n) ? n.GetString() ?? $"node {at}" : $"node {at}";
            if (!known.Contains(name) && Numbered().Match(name) is { Success: true } m && known.Contains(m.Groups[1].Value))
                name = m.Groups[1].Value;
            names.Add(name);
            parents.Add(-1);
            at++;
        }
        at = 0;
        foreach (var node in array.EnumerateArray())
        {
            if (node.TryGetProperty("children", out var children))
                foreach (var child in children.EnumerateArray())
                    if (child.GetInt32() is var c && c >= 0 && c < parents.Count) parents[c] = at;
            at++;
        }

        string Under(int node, int root)
        {
            var parts = new List<string>();
            for (var step = 0; node >= 0 && node != root && step < 256; step++, node = parents[node]) parts.Add(names[node]);
            parts.Reverse();
            return string.Join('/', parts);
        }

        bool Below(int node, int root)
        {
            for (var step = 0; node >= 0 && step < 256; step++, node = parents[node])
                if (node == root) return true;
            return false;
        }

        var have = rig.Paths.ToHashSet(StringComparer.Ordinal);
        var root = Enumerable.Range(0, names.Count)
            .Where(i => names[i] == rig.Name)
            .Select(i => (Node: i, Score: Enumerable.Range(0, names.Count).Count(j => Below(j, i) && have.Contains(Under(j, i)))))
            .OrderByDescending(c => c.Score)
            .Select(c => (int?)c.Node)
            .FirstOrDefault();

        return Enumerable.Range(0, names.Count)
            .Select(i => root is { } r && Below(i, r) ? Under(i, r) : Under(i, -1))
            .ToList();
    }

    [GeneratedRegex(@"^(.*)\.\d{3}$")]
    private static partial Regex Numbered();

    /// One channel's keys, in Unity's terms.
    ///
    /// A cubic spline's tangents are what Unity calls slopes, per second either side of the key. A
    /// straight line is a cubic whose slopes are the line's own; a step is a key whose slopes are
    /// infinite, which is how the game writes one. A rotation's straight line is a turn at an even
    /// rate, which a cubic over the four numbers only is when the turn is small — so a wide one is
    /// cut into narrow ones first.
    private static List<MotionKey> Keys(float[] times, float[] values, int width, string interpolation, MotionChannel channel)
    {
        var cubic = interpolation == "CUBICSPLINE";
        var count = Math.Min(times.Length, values.Length / (width * (cubic ? 3 : 1)));

        var time = new List<float>();
        var value = new List<float[]>();
        var inward = new List<float[]>();
        var outward = new List<float[]>();
        for (var k = 0; k < count; k++)
        {
            if (!float.IsFinite(times[k]) || time.Count > 0 && times[k] <= time[^1]) continue;
            var v = Unity(values, (cubic ? k * 3 + 1 : k) * width, width, channel);
            var a = cubic ? Unity(values, k * 3 * width, width, channel) : null;
            var b = cubic ? Unity(values, (k * 3 + 2) * width, width, channel) : null;

            // q and -q are the same turn; read as numbers, going from one to the other passes
            // through nothing at all.
            if (channel == MotionChannel.Rotation && value.Count > 0 && Dot(v, value[^1]) < 0)
            {
                Negate(v);
                if (a is not null) Negate(a);
                if (b is not null) Negate(b);
            }

            time.Add(times[k]);
            value.Add(v);
            inward.Add(a ?? new float[width]);
            outward.Add(b ?? new float[width]);
        }

        if (!cubic && interpolation != "STEP" && channel == MotionChannel.Rotation) Narrow(time, value);

        var keys = new List<MotionKey>(time.Count);
        for (var k = 0; k < time.Count; k++)
        {
            float[] a, b;
            if (cubic) (a, b) = (inward[k], outward[k]);
            else if (interpolation == "STEP")
                (a, b) = (Enumerable.Repeat(float.PositiveInfinity, width).ToArray(), Enumerable.Repeat(float.PositiveInfinity, width).ToArray());
            else
            {
                b = k + 1 < time.Count ? Secant(value[k], value[k + 1], time[k + 1] - time[k]) : new float[width];
                a = k > 0 ? Secant(value[k - 1], value[k], time[k] - time[k - 1]) : new float[width];
                if (k == 0 && time.Count > 1) a = b;
                if (k == time.Count - 1 && time.Count > 1) b = a;
            }
            keys.Add(Key(time[k], value[k], a, b));
        }
        return keys;
    }

    /// Whether every sample a file holds for a curve is where the curve as it is now puts it.
    private static bool Unchanged(MotionCurve before, float[] times, float[] values, int width, bool cubic)
    {
        for (var k = 0; k < times.Length; k++)
        {
            var at = (cubic ? k * 3 + 1 : k) * width;
            if (at + width > values.Length) return false;
            var v = Unity(values, at, width, before.Channel);
            var (x, y, z, w) = Motion.Sample(before, times[k]);

            if (before.Channel == MotionChannel.Rotation)
            {
                var (p, q) = (Unit([x, y, z, w]), Unit(v));
                if (MathF.Abs(Dot(p, q)) < 1 - 2e-6f) return false;
                continue;
            }
            float[] was = [x, y, z];
            for (var c = 0; c < 3; c++)
                if (MathF.Abs(v[c] - was[c]) > 1e-4f + 1e-4f * MathF.Abs(was[c])) return false;
        }
        return true;
    }

    /// Turns of more than ten degrees between keys cut into turns of less, each at the same rate.
    private static void Narrow(List<float> time, List<float[]> value)
    {
        for (var k = 0; k + 1 < time.Count; k++)
        {
            var (p, q) = (value[k], value[k + 1]);
            var angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Dot(p, q)), -1f, 1f));
            var pieces = (int)MathF.Ceiling(angle / (MathF.PI / 18));
            if (pieces <= 1) continue;

            var (from, to) = (time[k], time[k + 1]);
            for (var piece = 1; piece < pieces; piece++)
            {
                var t = (float)piece / pieces;
                time.Insert(k + piece, from + (to - from) * t);
                value.Insert(k + piece, Slerp(p, q, t));
            }
            k += pieces - 1;
        }
    }

    private static float[] Slerp(float[] p, float[] q, float t)
    {
        var dot = Math.Clamp(Dot(p, q), -1f, 1f);
        var angle = MathF.Acos(dot);
        if (angle < 1e-5f) return (float[])p.Clone();
        var (a, b) = (MathF.Sin((1 - t) * angle) / MathF.Sin(angle), MathF.Sin(t * angle) / MathF.Sin(angle));
        return [a * p[0] + b * q[0], a * p[1] + b * q[1], a * p[2] + b * q[2], a * p[3] + b * q[3]];
    }

    private static float[] Secant(float[] from, float[] to, float span)
        => span <= 0 ? new float[from.Length] : from.Select((f, c) => (to[c] - f) / span).ToArray();

    private static MotionKey Key(float time, float[] v, float[] a, float[] b)
        => v.Length == 4
            ? new MotionKey(time, v[0], v[1], v[2], v[3], a[0], a[1], a[2], a[3], b[0], b[1], b[2], b[3])
            : new MotionKey(time, v[0], v[1], v[2], 0, a[0], a[1], a[2], 0, b[0], b[1], b[2], 0);

    /// A value out of glTF's side and into Unity's.
    private static float[] Unity(float[] values, int at, int width, MotionChannel channel)
    {
        var v = values.AsSpan(at, width).ToArray();
        switch (channel)
        {
            case MotionChannel.Position: v[2] = -v[2]; break;
            case MotionChannel.Rotation: (v[0], v[1]) = (-v[0], -v[1]); break;
        }
        return v;
    }

    /// Where the animation holds an object still throughout, the value it holds it at; otherwise null.
    private static float[]? Held(MotionCurve curve)
    {
        var first = Value(curve.Keys[0], curve.Channel);
        foreach (var key in curve.Keys)
        {
            var v = Value(key, curve.Channel);
            for (var c = 0; c < v.Length; c++)
                if (MathF.Abs(v[c] - first[c]) > 1e-5f + 1e-5f * MathF.Abs(first[c])) return null;
            foreach (var slope in Slopes(key, v.Length))
                if (float.IsFinite(slope) && MathF.Abs(slope) > 1e-4f) return null;
        }
        return first;
    }

    private static bool Same(float[] value, float[] rest, MotionChannel channel)
    {
        switch (channel)
        {
            case MotionChannel.Rotation:
                var length = MathF.Sqrt(Dot(value, value));
                return length > 0 && MathF.Abs(Dot(value, [rest[3], rest[4], rest[5], rest[6]])) / length > 1 - 1e-6f;
            default:
                var from = channel == MotionChannel.Position ? 0 : 7;
                for (var c = 0; c < 3; c++)
                    if (MathF.Abs(value[c] - rest[from + c]) > 1e-4f + 1e-4f * MathF.Abs(rest[from + c])) return false;
                return true;
        }
    }

    private static List<MotionKey> Hold(IReadOnlyList<MotionKey> keys)
    {
        var first = keys[0] with { InX = 0, InY = 0, InZ = 0, InW = 0, OutX = 0, OutY = 0, OutZ = 0, OutW = 0 };
        return keys.Count == 1 ? [first] : [first, first with { Time = keys[^1].Time }];
    }

    /// The rate the keys came at, where they came evenly — as an exporter that samples writes them.
    private static float Rate(IReadOnlyList<MotionCurve> curves)
    {
        var densest = curves.MaxBy(c => c.Keys.Count)!.Keys;
        if (densest.Count < 3) return 30;
        var gaps = densest.Zip(densest.Skip(1), (a, b) => b.Time - a.Time).ToList();
        var (low, high) = (gaps.Min(), gaps.Max());
        return low > 0 && high - low < 1e-3f ? Math.Clamp(MathF.Round(1 / low), 1, 240) : 30;
    }

    /// The rate a curve goes into the file at: Blender's own, so every key it reads lands on a frame.
    public const float Frames = 24;

    /// How a curve goes into the file: sampled, a straight line between each sample and the next.
    ///
    /// Unity's curves are cubics through each key with a slope either side, which glTF can say
    /// exactly — and which Blender reads by keeping the keys and throwing the slopes away, putting
    /// handles of its own in their place. A weapon's motion is mostly its slopes, so what Blender
    /// showed was not what the game plays, and what it wrote back was not what it had been given.
    /// Straight lines it reads as they are. Sampled on its own frames, and where Blender writes the
    /// animation back it samples on the same frames, so a curve nobody touched comes back as exactly
    /// the numbers it went out as — which is how reading it back knows to keep the game's own.
    private static JsonObject? Sampler(Data data, MotionCurve curve)
    {
        var keys = new List<MotionKey>();
        foreach (var key in curve.Keys)
            if (float.IsFinite(key.Time) && (keys.Count == 0 || key.Time > keys[^1].Time)) keys.Add(key);
        if (keys.Count == 0) return null;

        var width = curve.Channel == MotionChannel.Rotation ? 4 : 3;
        var times = SampleTimes(keys);
        var line = new float[times.Length * width];
        var whole = curve with { Keys = keys };
        for (var i = 0; i < times.Length; i++)
        {
            var (x, y, z, w) = Motion.Sample(whole, times[i]);
            float[] v = width == 4 ? Unit([x, y, z, w]) : [x, y, z];
            Glb(v, curve.Channel).CopyTo(line, i * width);
        }
        return Sampled(data, times, line, width, "LINEAR");
    }

    /// Every frame from the first key to the last, and the last, so the clip keeps its length.
    private static float[] SampleTimes(IReadOnlyList<MotionKey> keys)
    {
        var (first, last) = (keys[0].Time, keys[^1].Time);
        var times = new List<float>();
        for (var frame = (int)MathF.Ceiling(first * Frames - 1e-3f); frame / Frames < last - 1e-4f; frame++)
            times.Add(frame / Frames);
        if (times.Count == 0 || times[0] > first + 1e-4f) times.Insert(0, first);
        if (last > times[^1] + 1e-4f) times.Add(last);
        return times.ToArray();
    }

    private static JsonObject Sampled(Data data, float[] times, float[] output, int width, string interpolation)
        => new()
        {
            ["input"] = data.Floats(times, "SCALAR", bounded: true),
            ["output"] = data.Floats(output, width == 4 ? "VEC4" : "VEC3"),
            ["interpolation"] = interpolation,
        };

    /// A value out of Unity's side and into glTF's, with anything that is not a number made one.
    private static float[] Glb(float[] v, MotionChannel channel)
    {
        var glb = v.Select(c => float.IsFinite(c) ? c : 0f).ToArray();
        switch (channel)
        {
            case MotionChannel.Position: glb[2] = -glb[2]; break;
            case MotionChannel.Rotation: (glb[0], glb[1]) = (-glb[0], -glb[1]); break;
        }
        return glb;
    }

    private static float[] Value(MotionKey k, MotionChannel channel)
        => channel == MotionChannel.Rotation ? [k.X, k.Y, k.Z, k.W] : [k.X, k.Y, k.Z];

    private static float[] In(MotionKey k, int width) => width == 4 ? [k.InX, k.InY, k.InZ, k.InW] : [k.InX, k.InY, k.InZ];

    private static float[] Out(MotionKey k, int width) => width == 4 ? [k.OutX, k.OutY, k.OutZ, k.OutW] : [k.OutX, k.OutY, k.OutZ];

    private static IEnumerable<float> Slopes(MotionKey k, int width) => In(k, width).Concat(Out(k, width));

    private static string PathOf(MotionChannel channel) => channel switch
    {
        MotionChannel.Position => "translation",
        MotionChannel.Rotation => "rotation",
        _ => "scale",
    };

    /// A model's vertices where its bones put them at rest, bound to the joints of this rig.
    ///
    /// The game skins a vertex by where its bone is now times the bone's bind pose; at rest that is
    /// where the model stands, whatever the bind poses were written against. Binding it there, to
    /// joints whose inverse bind matrices undo where they stand, gives exactly the same model moving
    /// exactly the same way, with no bind pose of the game's left for another program to get wrong.
    private sealed record Baked(float[] Positions, float[]? Normals, float[]? Uvs, ushort[] Joints, float[] Weights);

    private static Baked? Bake(Model model, AnimationRig rig, IReadOnlyList<float[]> rest, out string why)
    {
        var (mesh, skeleton) = (model.Mesh, model.Skeleton);
        why = "";
        if (mesh.Get(VertexAttribute.Position) is not { } positions || mesh.VertexCount == 0)
        {
            why = "it has no vertices";
            return null;
        }
        if (skeleton.Bones.Count == 0)
        {
            why = "nothing carries it";
            return null;
        }

        var bones = new int[skeleton.Bones.Count];
        for (var b = 0; b < bones.Length; b++)
        {
            var joint = skeleton.Joints[skeleton.Bones[b]];
            bones[b] = RigIndex(rig, joint.Path);
            if (bones[b] < 0)
            {
                why = $"its bone '{joint.Name}' is not under '{rig.Name}'";
                return null;
            }
        }

        var skinning = new float[bones.Length][];
        for (var b = 0; b < bones.Length; b++)
            skinning[b] = !skeleton.Rigid && b < mesh.BindPoses.Count
                ? Matrix.Times(rest[bones[b]], Matrix.FromRows(mesh.BindPoses[b]))
                : rest[bones[b]];

        var wide = mesh.Dimensions.GetValueOrDefault(VertexAttribute.Position, 3);
        var indices = skeleton.Rigid ? null : mesh.Get(VertexAttribute.BlendIndices);
        var slots = indices is null ? 0 : mesh.Dimensions.GetValueOrDefault(VertexAttribute.BlendIndices, 1);
        var weights = indices is null ? null : mesh.Get(VertexAttribute.BlendWeight);
        var heavy = weights is null ? 0 : mesh.Dimensions.GetValueOrDefault(VertexAttribute.BlendWeight, 1);
        var normals = mesh.Get(VertexAttribute.Normal);
        var normalWide = mesh.Dimensions.GetValueOrDefault(VertexAttribute.Normal, 3);
        var uv = mesh.Get(VertexAttribute.TexCoord0);
        var uvWide = uv is null ? 0 : mesh.Dimensions.GetValueOrDefault(VertexAttribute.TexCoord0, 2);

        var n = mesh.VertexCount;
        var placed = new float[n * 3];
        var turned = normals is null || normalWide < 3 ? null : new float[n * 3];
        var mapped = uv is null || uvWide < 2 ? null : new float[n * 2];
        var joints = new ushort[n * 4];
        var weighed = new float[n * 4];
        var carriers = new int[4];
        var shares = new float[4];

        for (var v = 0; v < n; v++)
        {
            var used = 0;
            var total = 0f;
            for (var slot = 0; slot < Math.Min(slots, 4); slot++)
            {
                var at = v * slots + slot;
                if (at >= indices!.Length) break;
                var weight = weights is null
                    ? slot == 0 ? 1f : 0f
                    : slot < heavy && v * heavy + slot < weights.Length ? weights[v * heavy + slot] : 0f;
                var bone = (int)indices[at];
                if (!(weight > 0) || bone < 0 || bone >= bones.Length) continue;
                (carriers[used], shares[used]) = (bone, weight);
                used++;
                total += weight;
            }
            // A vertex nothing carries rides on the first bone, the one a rigid model has.
            if (used == 0) (carriers[0], shares[0], used, total) = (0, 1f, 1, 1f);

            float x = 0, y = 0, z = 0, nx = 0, ny = 0, nz = 0;
            var (px, py, pz) = (positions[v * wide], positions[v * wide + 1], positions[v * wide + 2]);
            for (var s = 0; s < used; s++)
            {
                var m = skinning[carriers[s]];
                var w = shares[s] / total;
                x += w * (m[0] * px + m[4] * py + m[8] * pz + m[12]);
                y += w * (m[1] * px + m[5] * py + m[9] * pz + m[13]);
                z += w * (m[2] * px + m[6] * py + m[10] * pz + m[14]);
                if (turned is not null)
                {
                    var (ax, ay, az) = (normals![v * normalWide], normals[v * normalWide + 1], normals[v * normalWide + 2]);
                    nx += w * (m[0] * ax + m[4] * ay + m[8] * az);
                    ny += w * (m[1] * ax + m[5] * ay + m[9] * az);
                    nz += w * (m[2] * ax + m[6] * ay + m[10] * az);
                }
                joints[v * 4 + s] = (ushort)bones[carriers[s]];
                weighed[v * 4 + s] = w;
            }

            (placed[v * 3], placed[v * 3 + 1], placed[v * 3 + 2]) = (Finite(x), Finite(y), Finite(-z));
            if (turned is not null)
            {
                var length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                (turned[v * 3], turned[v * 3 + 1], turned[v * 3 + 2]) = length > 1e-6f && float.IsFinite(length)
                    ? (nx / length, ny / length, -nz / length)
                    : (0f, 1f, 0f);
            }
            if (mapped is not null)
                (mapped[v * 2], mapped[v * 2 + 1]) = (Finite(uv![v * uvWide]), Finite(1f - uv[v * uvWide + 1]));
        }

        return new Baked(placed, turned, mapped, joints, weighed);
    }

    /// Which of the rig's objects a joint of a model's skeleton is. The skeleton counts its paths
    /// from the top of the prefab and the rig from the object carrying the Animation component, so
    /// the one ends with the other — the component's object's name included, so a hand under the
    /// gun's root is not taken for a hand somewhere else.
    private static int RigIndex(AnimationRig rig, string path)
    {
        var (best, length) = (-1, -1);
        for (var at = 0; at < rig.Paths.Count; at++)
        {
            var full = rig.Paths[at].Length == 0 ? rig.Name : rig.Name + "/" + rig.Paths[at];
            if ((path == full || path.EndsWith("/" + full, StringComparison.Ordinal)) && full.Length > length)
                (best, length) = (at, full.Length);
        }
        return best;
    }

    private static int IndexOf(AnimationRig rig, string path)
    {
        for (var at = 0; at < rig.Paths.Count; at++)
            if (rig.Paths[at] == path) return at;
        return -1;
    }

    private static string NameOf(AnimationRig rig, int at)
        => at == 0 || rig.Paths[at].Length == 0 ? rig.Name : rig.Paths[at][(rig.Paths[at].LastIndexOf('/') + 1)..];

    /// A column-major matrix from Unity's side to glTF's: S·M·S with S negating Z.
    private static float[] Sided(float[] m)
    {
        var sided = new float[16];
        for (var column = 0; column < 4; column++)
            for (var row = 0; row < 4; row++)
            {
                var value = Finite(m[column * 4 + row]);
                sided[column * 4 + row] = row == 2 ^ column == 2 ? -value : value;
            }
        return sided;
    }

    private static float[] Finite(float[] trs)
    {
        if (trs.All(float.IsFinite)) return trs;
        return [0, 0, 0, 0, 0, 0, 1, 1, 1, 1];
    }

    private static float Finite(float v) => float.IsFinite(v) ? v : 0f;

    private static float[] Unit(float[] q)
    {
        var length = MathF.Sqrt(Dot(q, q));
        return length > 1e-8f && float.IsFinite(length) ? q.Select(c => c / length).ToArray() : [0, 0, 0, 1];
    }

    private static float Dot(float[] a, float[] b)
    {
        var sum = 0f;
        for (var c = 0; c < Math.Min(a.Length, b.Length); c++) sum += a[c] * b[c];
        return sum;
    }

    private static void Negate(float[] v)
    {
        for (var c = 0; c < v.Length; c++) v[c] = -v[c];
    }

    private static JsonArray Numbers(float[] values) => new(values.Select(v => (JsonNode)Finite(v)).ToArray());

    private static bool IsPng(byte[] bytes)
        => bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G';

    /// The binary half of the file and the accessors into it.
    private sealed class Data
    {
        private readonly MemoryStream _buffer = new();

        public JsonArray Accessors { get; } = [];
        public JsonArray Views { get; } = [];

        public byte[] Binary() => _buffer.ToArray();

        public int View(byte[] bytes)
        {
            while (_buffer.Length % 4 != 0) _buffer.WriteByte(0);
            Views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = _buffer.Length, ["byteLength"] = bytes.Length });
            _buffer.Write(bytes);
            return Views.Count - 1;
        }

        public JsonNode Floats(float[] values, string type, bool bounded = false)
        {
            var bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            var width = Width(type);
            var accessor = Accessor(View(bytes), values.Length / width, type, Float);

            // An animation's times need their bounds as much as a model's positions do.
            if (bounded)
            {
                var min = Enumerable.Repeat(float.MaxValue, width).ToArray();
                var max = Enumerable.Repeat(float.MinValue, width).ToArray();
                for (var i = 0; i < values.Length; i++)
                {
                    min[i % width] = Math.Min(min[i % width], values[i]);
                    max[i % width] = Math.Max(max[i % width], values[i]);
                }
                accessor["min"] = Numbers(min);
                accessor["max"] = Numbers(max);
            }
            return Accessors.Count - 1;
        }

        public JsonNode Shorts(ushort[] values, string type)
        {
            var bytes = new byte[values.Length * 2];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            Accessor(View(bytes), values.Length / Width(type), type, UnsignedShort);
            return Accessors.Count - 1;
        }

        public JsonNode Ints(uint[] values)
        {
            var bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            Accessor(View(bytes), values.Length, "SCALAR", UnsignedInt);
            return Accessors.Count - 1;
        }

        private JsonObject Accessor(int view, int count, string type, int componentType)
        {
            var accessor = new JsonObject
            {
                ["bufferView"] = view,
                ["componentType"] = componentType,
                ["count"] = count,
                ["type"] = type,
            };
            Accessors.Add(accessor);
            return accessor;
        }

        private static int Width(string type) => type switch
        {
            "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => 16,
        };
    }
}
