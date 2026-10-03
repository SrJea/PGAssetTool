using System.Text.Json;
using System.Text.Json.Serialization;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Core.Animation;

/// An animation as a workspace keeps it: the curves of one clip, written out as text.
///
/// A `.anim` is to an AnimationClip what a `.png` is to a texture — the file a workspace holds for
/// it, which an operation names and a pack carries. It holds what moves and how: for each object, by
/// the path the clip names it by, the keys of its position, rotation and scale, each with the slopes
/// either side of it, exactly as the game keeps them. Nothing about the clip that is not motion is
/// in it — not the clip's name, which the game plays it by and which therefore stays the game's, and
/// not its events, which call into the game's code and are behaviour rather than appearance.
///
/// Text rather than something smaller, because the one thing anybody does to one by hand is read it:
/// which bones it moves, where it came from, and what did not find a bone to move.
public sealed record ClipFile
{
    public const string Extension = ".anim";
    public const string FormatName = "pgassettool-animation";
    public const int CurrentVersion = 1;

    public string Format { get; init; } = FormatName;
    public int Version { get; init; } = CurrentVersion;

    /// What the clip it came from was called. For reading; the clip it goes into keeps its own name.
    public string Name { get; init; } = "";

    /// Where it came from, as a person reads it: `#416 Ultimatum: Reload`.
    public string From { get; init; } = "";

    public float SampleRate { get; init; } = 30;

    /// The paths the animation it came from moved and that nothing in the item it is for answers
    /// to. Kept for saying so: they are not in `Curves`, since they would move nothing.
    public List<string> Unmatched { get; init; } = [];

    public List<ClipFileCurve> Curves { get; init; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // A slope of infinity is how Unity writes a step: hold until the next key. JSON has no word
        // for it, and dropping it would turn every step into a slide.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// The clip as the preview and the importer take it.
    public Motion ToMotion()
    {
        var curves = Curves.Select(c => new MotionCurve(c.Path, c.Channel, c.Keys.Select(k => k.ToKey(c.Channel)).ToList()))
            .Where(c => c.Keys.Count > 0)
            .ToList();
        var length = curves.SelectMany(c => c.Keys).Select(k => k.Time).DefaultIfEmpty(0f).Max();
        return new Motion(Name, length, SampleRate <= 0 ? 30 : SampleRate, curves);
    }

    public static ClipFile FromMotion(Motion motion, string from, IEnumerable<string>? unmatched = null)
        => new()
        {
            Name = motion.Name,
            From = from,
            SampleRate = motion.SampleRate,
            Unmatched = unmatched?.ToList() ?? [],
            Curves = motion.Curves
                .Select(c => new ClipFileCurve
                {
                    Path = c.Path,
                    Channel = c.Channel,
                    Keys = c.Keys.Select(k => ClipFileKey.From(k, c.Channel)).ToList(),
                })
                .ToList(),
        };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public void Write(string path) => Settings.AtomicFile.WriteAllText(path, ToJson());

    /// <exception cref="InvalidDataException">The file is not one of these, or not one this reads.</exception>
    public static ClipFile Read(string path) => Parse(File.ReadAllText(path));

    public static ClipFile Parse(string json)
    {
        ClipFile? read;
        try
        {
            // Said by the file, not assumed: the property starts out as the right name, so a file
            // that does not say what it is would otherwise pass for one of these — `{}` did.
            using (var document = JsonDocument.Parse(json))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("format", out var format)
                    || format.ValueKind != JsonValueKind.String
                    || format.GetString() != FormatName)
                    throw new InvalidDataException("This is not an animation this tool wrote.");
            }
            read = JsonSerializer.Deserialize<ClipFile>(json, Json);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"The animation is not readable: {e.Message}", e);
        }

        if (read is null) throw new InvalidDataException("The animation is empty.");
        if (read.Version > CurrentVersion)
            throw new InvalidDataException(
                $"This animation needs version {read.Version} of the format; this build understands {CurrentVersion}.");

        foreach (var curve in read.Curves)
            foreach (var key in curve.Keys)
                if (key.V.Length != ClipFileKey.Width(curve.Channel) * 3 + 1)
                    throw new InvalidDataException(
                        $"A key of '{curve.Path}' has {key.V.Length} numbers where a {curve.Channel} key has "
                        + $"{ClipFileKey.Width(curve.Channel) * 3 + 1}.");
        return read;
    }
}

public sealed record ClipFileCurve
{
    /// The object it moves, by the path from whatever carries the Animation component.
    public string Path { get; init; } = "";

    public MotionChannel Channel { get; init; }

    public List<ClipFileKey> Keys { get; init; } = [];
}

/// One key, as one row of numbers: the time, then the value, then the slope in, then the slope
/// out — three apiece for a position or a scale, four for a rotation.
///
/// A row rather than named fields, because a clip runs to thousands of them and a file a person can
/// scroll through is worth more than one that names every number it holds.
[JsonConverter(typeof(ClipFileKeyConverter))]
public sealed record ClipFileKey(float[] V)
{
    public static int Width(MotionChannel channel) => channel == MotionChannel.Rotation ? 4 : 3;

    public static ClipFileKey From(MotionKey k, MotionChannel channel)
        => new(Width(channel) == 4
            ? [k.Time, k.X, k.Y, k.Z, k.W, k.InX, k.InY, k.InZ, k.InW, k.OutX, k.OutY, k.OutZ, k.OutW]
            : [k.Time, k.X, k.Y, k.Z, k.InX, k.InY, k.InZ, k.OutX, k.OutY, k.OutZ]);

    public MotionKey ToKey(MotionChannel channel)
        => Width(channel) == 4
            ? new MotionKey(V[0], V[1], V[2], V[3], V[4], V[5], V[6], V[7], V[8], V[9], V[10], V[11], V[12])
            : new MotionKey(V[0], V[1], V[2], V[3], 0, V[4], V[5], V[6], 0, V[7], V[8], V[9], 0);
}

/// Writes a key as a bare array of numbers on one line.
internal sealed class ClipFileKeyConverter : JsonConverter<ClipFileKey>
{
    public override ClipFileKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(JsonSerializer.Deserialize<float[]>(ref reader, options) ?? []);

    public override void Write(Utf8JsonWriter writer, ClipFileKey value, JsonSerializerOptions options)
    {
        writer.WriteRawValue(
            "[" + string.Join(", ", value.V.Select(Number)) + "]",
            skipInputValidation: true);
    }

    /// Shortest round-trip form; infinity and not-a-number as the names JSON reading here accepts.
    private static string Number(float v)
        => float.IsPositiveInfinity(v) ? "\"Infinity\""
            : float.IsNegativeInfinity(v) ? "\"-Infinity\""
            : float.IsNaN(v) ? "\"NaN\""
            : v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
