using System.Text.Json;
using System.Text.Json.Serialization;
using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Pack;

public static class PackOperations
{
    public const string ReplaceTexture = "replaceTexture";
    public const string ReplaceMesh = "replaceMesh";
    public const string ReplaceAudio = "replaceAudio";

    /// An AnimationClip's motion, from a `.anim` the workspace holds. See Animation.ClipFile.
    public const string ReplaceAnimation = "replaceAnimation";
}

public sealed record PackOperation
{
    public required string Op { get; init; }
    public required AssetAddress Target { get; init; }
    public required string Source { get; init; }

    /// Hash of the file as it was exported. An unchanged file is not a modification, so packing
    /// drops it. Stripped from the manifest that goes into the pack.
    public string? BaselineSha256 { get; init; }

    /// For a mesh, the other files in this workspace that are the textures it is drawn with — one
    /// entry per submesh, in the renderer's own order, and empty where the picture that submesh
    /// wears was not written here.
    ///
    /// A workspace is a model and a folder of pictures and holds nothing else that says which goes
    /// on which — the renderers and materials that decide it are in the game, and a workspace made
    /// from a skin does not even agree with them: the geometry is the weapon's and the paint is the
    /// skin's. So the answer is written down at the one moment anything knows it, which is the
    /// extraction. The editor reads it to show the model dressed.
    ///
    /// Positional, because a submesh is what decides which material draws a triangle and a weapon
    /// carrying two of them is ordinary. Flattened to the set of names that happened to be here, it
    /// was enough to say which pictures a model wears and not enough to put them on it.
    public List<string> Wears { get; init; } = [];

    /// Whether the alpha channel of the file this comes from says which part of the image is used
    /// rather than carrying anything of its own.
    ///
    /// Set by an export that masked the texture to what a model samples. The alpha then belongs to
    /// the tool rather than to the author, so applying keeps what the game already had there — most
    /// of these textures hold emission in it, and writing a mask over that would put out the lights.
    public bool AlphaIsMask { get; init; }
}

/// What a pack is for: which thing in the game, and which of its looks.
///
/// Written at extraction, because that is the only moment anything knows. The operations inside a
/// pack name assets and bundles, and no amount of reading them back says "this is the nuclear
/// reactor skin of #416" — so a pack that does not carry this cannot be filed, and a tool that
/// files by guessing would file some of them wrong.
///
/// Deliberately not weapon-shaped, though weapons are the only kind extraction makes today. The
/// game's other items are the same arrangement under different names — a hat has a prefab, an
/// offer icon, related assets and skins, exactly as a weapon does, and is found by an id where a
/// weapon is found by a number. Naming the parts generally costs nothing now and saves moving
/// every installed pack and every folder on disk later.
///
/// What it is for is telling packs apart and putting them somewhere. A mod is its operations, and
/// this is the label on the box. Applying reads it for one thing only: when an operation's asset is
/// not in the bundle it names, this says which item's bundles to look through for it (Relocation).
/// Where to look, never what is found — the asset found still has to be the one the operation names.
/// The sorts of thing a pack can be for. One so far; declared here rather than spelled out at the
/// one place that writes it, so whatever adds the second finds the first already named.
public static class PackKind
{
    public const string Weapon = "weapon";
}

public sealed record PackSubject
{
    /// What sort of thing this is: `weapon`, and one day `hat`, `cape`, `map`. Lower case, because
    /// it is a folder name as much as a word.
    public string Kind { get; init; } = "";

    /// The game's own identifier: a weapon's slug, a hat's id. What filing uses, because it is
    /// stable, unique and safe as a folder name where a display name is none of the three.
    public string Id { get; init; } = "";

    /// The number the game shows, where the kind has one. Weapons do — it is what a person
    /// searches by — and hats do not, so zero means "this kind is not numbered".
    public int Number { get; init; }

    /// The prefab the game builds this from: `Weapon834`. Every asset belonging to it is named
    /// after this, which is what makes them findable at all.
    public string Prefab { get; init; } = "";

    /// What a player would call it, in whatever language the catalogue was read in.
    public string Name { get; init; } = "";

    /// Which of the thing's looks: a skin's own id, or empty for the thing as it comes.
    public string Variant { get; init; } = "";

    public string VariantName { get; init; } = "";

    /// What the game's own translation table calls these.
    ///
    /// Kept beside the names rather than instead of them, because the two answer different
    /// questions. The name is what it was called when the pack was made, which a pack handed to
    /// somebody else still has to be able to say for itself; the key is what it is called *now*, in
    /// whatever language the tool is set to — so a pack extracted in English does not go on reading
    /// as English to somebody working in Japanese.
    public string NameKey { get; init; } = "";

    public string VariantKey { get; init; } = "";

    /// Whether this says enough to file by. A pack that was not made by extracting an item says
    /// nothing and is filed under nothing.
    [JsonIgnore]
    public bool IsKnown => Kind.Length > 0 && Id.Length > 0;

    /// Where this pack is filed, as one folder name per level: the kind, the thing, the look.
    ///
    /// A numbered kind puts the number first so the folder sorts the way the game's own list does
    /// and two things of the same name never collide. A skin's id begins with the prefab name,
    /// which the folder above already says, so that much is dropped.
    [JsonIgnore]
    public IReadOnlyList<string> Path =>
    [
        Kind.Length > 0 ? Kind : "unknown",
        Number > 0 ? $"{Number:D4}_{Id}" : Id,
        Variant.Length == 0 ? "default"
            : Prefab.Length > 0 && Variant.StartsWith(Prefab + "_", StringComparison.OrdinalIgnoreCase)
                ? Variant[(Prefab.Length + 1)..]
                : Variant,
    ];

    /// The thing as a person reads it, for a heading.
    [JsonIgnore]
    public string Describe =>
        (Number > 0 ? $"#{Number} " : "") + (Name.Length > 0 ? Name : Id);

    /// The look as a person reads it.
    [JsonIgnore]
    public string DescribeVariant =>
        Variant.Length == 0 ? "default" : VariantName.Length > 0 ? VariantName : Path[2];
}

public sealed record PackManifest
{
    public const int CurrentFormatVersion = 2;
    public const string FileName = "pgmod.json";

    /// What a pack claims to be. Always the current one: a build claiming an older format so that
    /// older builds could still read it was a promise to packs that were never published, and the
    /// version is still written down because reading one from a later build has to fail plainly.
    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Author { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public string Description { get; init; } = "";

    /// Whether the built pack is signed and scrambled. Null follows the setting, so a workspace
    /// made before anybody chose does whatever the tool is set to now.
    public bool? Protect { get; init; }

    /// A picture of the pack, as a path inside it. Empty means it has none.
    ///
    /// Whoever installs a pack sees a list of names, and a name is a poor way to tell one weapon
    /// re-skin from another. Extraction points this at the weapon's own icon, which is already
    /// being written out, so a pack has one without anybody deciding to give it one — and an author
    /// who would rather show their own work can point it somewhere else.
    ///
    /// Packed even when it is not one of the files being replaced, which is the usual case: the
    /// icon is what the pack looks like, not part of what it does.
    public string Icon { get; init; } = "";

    /// Recorded, not enforced. Refusing to apply a pack built against an older version would break
    /// every pack on every update, which is the opposite of what the address design is for.
    public string? BuiltAgainstGameVersion { get; init; }

    /// Which weapon and which of its looks this pack is for. Null for a pack that was not made by
    /// extracting one.
    public PackSubject? Subject { get; init; }

    public List<PackOperation> Operations { get; init; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <exception cref="InvalidDataException">
    /// Anything wrong with the text, including its not being JSON at all.
    ///
    /// Said as one kind of trouble rather than two, because everything that reads a manifest — the
    /// editor listing workspaces, the builder, the applier opening a pack — already answers for a
    /// manifest it cannot use, and a JsonException went past all of them. A workspace's manifest is
    /// a file the author edits by hand, so a stray comma there is an ordinary event and not a
    /// reason to take the window down.
    /// </exception>
    public static PackManifest Parse(string json)
    {
        PackManifest? read;
        try
        {
            read = JsonSerializer.Deserialize<PackManifest>(json, Json);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"The manifest is not readable: {e.Message}", e);
        }

        var manifest = read ?? throw new InvalidDataException("The manifest is empty.");
        if (manifest.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $"This pack needs format version {manifest.FormatVersion}; this build understands {CurrentFormatVersion}.");
        return manifest;
    }
}
