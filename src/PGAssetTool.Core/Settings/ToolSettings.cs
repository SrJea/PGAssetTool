using System.Text.Json;
using System.Text.Json.Serialization;
using PGAssetTool.Core.Mods;

namespace PGAssetTool.Core.Settings;

/// What the tool remembers between runs.
///
/// Kept beside the executable rather than in the registry or under the user profile, so a copy of
/// the folder is a copy of the whole setup and carrying the tool on a stick carries its settings
/// with it. Nothing here is per-installation; anything that varies by game belongs in that
/// installation's own directory.
public sealed record ToolSettings
{
    public const string FileName = "settings.json";

    /// The localization bundle weapon names are read from. Not a UI language — this is the game's
    /// own translation table, and it is what decides whether the list reads "Hitman Pistol" or
    /// something a Japanese player would recognise.
    public string Language { get; init; } = "l_en-gb";

    /// Where extracted weapons and assets are written. Empty means beside the tool, under
    /// PGAssetTool-data, which is the only place that works whatever directory the exe is launched
    /// from — the CLI can default to the current one because a shell has a meaningful one.
    public string WorkspaceRoot { get; init; } = "";

    /// The game to work on, when it is not the one Steam knows about.
    ///
    /// Empty means find it, which is what it is for most people. It is here because the game is
    /// sold in more than one store and only one of them can be asked where it put things — and
    /// because a copy of the game data is a perfectly good thing to point at, which is how anyone
    /// would work on this without risking the installation they play.
    public string GameDirectory { get; init; } = "";

    /// Recorded in the manifest of anything extracted as a workspace.
    public string Author { get; init; } = "";

    /// Write textures with no alpha channel.
    ///
    /// Most of them keep something other than coverage there — emission, usually — so an editor
    /// opens them as mostly transparent and painting means fighting a mask that has nothing to do
    /// with the colours. An image brought back without an alpha channel is given the original one.
    public bool OpaqueTextures { get; init; }

    /// Whether an exported texture keeps only the part a model actually samples.
    ///
    /// On, because a weapon's texture is an atlas and most of it is nothing: which island belongs
    /// to the gun and which to the sights is written in the mesh's UVs and nowhere an image editor
    /// can see. Off writes the whole image, which is what to do when a texture is used somewhere
    /// this tool cannot see. See UvCoverage.
    public bool MaskUnusedTextures { get; init; } = true;

    /// Whether the asset tree starts filtered to what can be written back.
    public bool ReplaceableOnly { get; init; } = true;

    /// Whether the editor shows the original and the edit at once rather than one at a time.
    public bool SideBySide { get; init; }

    /// Whether the two halves of the editor's comparison are turned and dressed together.
    ///
    /// On unless somebody says otherwise: two models at two angles wearing two textures differ in
    /// three ways at once, only one of which is the mod. Off is for a replaced mesh, where the two
    /// are different models and each is worth looking at on its own terms — which is a decision
    /// about how somebody works rather than about the file in front of them, so it is kept.
    public bool LinkedPreviews { get; init; } = true;

    /// Whether the manager asks before it rewrites the game. The warning about the game being open
    /// is not covered by this and is never skipped.
    public bool ConfirmChanges { get; init; } = true;

    /// How large the manager draws an installed mod, in pixels down one side. A zoom level is set
    /// once and meant to stay set.
    public int TileSize { get; init; } = 112;

    /// How large the windows draw themselves, as a percentage of their own layout.
    ///
    /// 100 is what every size in the views is written in. The whole of a window is scaled rather
    /// than its text alone: a larger font in a column of a fixed width reads as clipped text, not
    /// as a larger interface. Held as a percentage because that is what the setting says.
    public int UiScale { get; init; } = 100;

    /// What the manager arranges its tiles by: 0 is the order they were installed in, 1 is by the
    /// item each one is for. Held as a number because this file is the tool's own and the name of
    /// an ordering is a thing the interface decides, not the settings.
    public int ModOrder { get; init; }

    /// Whether a newly extracted workspace builds a signed, scrambled pack by default. Off, because
    /// a plain zip is easier to look inside and most packs never leave the machine that made them.
    public bool ProtectPacks { get; init; }

    /// Whether writing to the game is done for speed rather than for size.
    ///
    /// The squeezing itself is about four times quicker and the bundles come out about a sixth
    /// larger; measured end to end, on a machine with cores to spare, a whole apply is about a
    /// tenth quicker, and more than that on a machine without them, where the work cannot be spread
    /// as wide. The game reads both at the same speed.
    ///
    /// Off by default, because a tool leaving the installation the size it found it is the answer
    /// that surprises nobody, and somebody with thirty mods and room to spare can say otherwise.
    public bool FasterApplies { get; init; }

    /// Whether bundles are rebuilt one at a time rather than four, for a machine short of memory.
    /// About a third slower and about a third less at the peak; see ModApplier.AtOnce.
    public bool LighterApplies { get; init; }

    /// How many megabytes of bundles the reader may keep unpacked in memory, for browsing and
    /// extracting. See BundleUnpacker: resolving a weapon went from about 110ms to about 10 with its
    /// bundles unpacked. Zero reads everything from disk a block at a time, as before.
    public int ReadMemory { get; init; } = 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathIn(string home) => Path.Combine(home, FileName);

    public string WorkspaceIn(string home)
        => WorkspaceRoot.Length > 0 ? WorkspaceRoot : Path.Combine(home, "workspace");

    /// A settings file that cannot be read is replaced by the defaults rather than stopping the
    /// tool: it holds preferences, and none of them is worth refusing to start over.
    public static ToolSettings Load(string? home = null)
    {
        var path = PathIn(home ?? ModStore.DefaultHome());
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ToolSettings>(File.ReadAllText(path), Json) ?? new ToolSettings()
                : new ToolSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new ToolSettings();
        }
    }

    public void Save(string? home = null)
    {
        var directory = home ?? ModStore.DefaultHome();
        Directory.CreateDirectory(directory);
        AtomicFile.WriteAllText(PathIn(directory), JsonSerializer.Serialize(this, Json));
    }
}
