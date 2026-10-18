using System.Reflection;

namespace PGAssetTool.Core.Pack;

/// The text a pack is posted to the community's Discord with.
///
/// The forum is filed by weapon already — a category of number ranges, a thread per weapon — so the
/// post says nothing about which weapon, only what a reader in that thread needs to tell one mod from
/// the next: whose it is and which version, which look it changes (a mod for a skin you do not own
/// shows you nothing), what kind of files it replaces, and what it was made with. The layout is the
/// user's. Previews are left to whoever posts.
///
/// Which assets it replaces was considered and left out at the user's word: it would make each post
/// longer, and fewer mods to a screen is the greater cost.
public static class ForumPost
{
    /// What Discord accepts in one message without Nitro.
    public const int Limit = 2000;

    /// <param name="changed">The edited files, by operation — what a built pack would carry. One row
    /// per file, so a picture written to both default looks counts once.</param>
    /// <param name="gameVersion">The game the author has in front of them, or null to fall back on
    /// the one the workspace was extracted from.</param>
    /// What a pack says of the game when the game will not say. Reading the version wants an
    /// engine class database to describe `PlayerSettings` as the built game writes it, and that is
    /// the one class it does not always get right.
    public const string UnknownVersion = "unknown";

    public static string For(
        PackManifest manifest, IEnumerable<string> changed, string? gameVersion, string toolVersion)
    {
        var lines = new List<string>();

        var heading = new List<string> { manifest.Name.Trim() };
        if (manifest.Author.Trim() is { Length: > 0 } author) heading.Add(author);
        if (manifest.Version.Trim() is { Length: > 0 } version)
            heading.Add(version.StartsWith('v') || version.StartsWith('V') ? version : "v" + version);
        lines.Add(string.Join(" / ", heading.Where(h => h.Length > 0)));

        if (manifest.Description.Trim() is { Length: > 0 } description) lines.Add(description);

        if (manifest.Subject is { } subject)
            lines.Add("Skin: " + (subject.Variant.Length == 0 ? "Default" : subject.DescribeVariant));

        lines.Add("Changes: " + Changes(changed));
        lines.Add("GameVersion: " + (gameVersion ?? manifest.BuiltAgainstGameVersion ?? UnknownVersion));
        lines.Add("ToolVersion: " + toolVersion);

        return string.Join("\n", lines);
    }

    /// "2 textures, 1 sound", in the order an extract lists its folders.
    public static string Changes(IEnumerable<string> operations)
    {
        var counts = operations.GroupBy(o => o).ToDictionary(g => g.Key, g => g.Count());

        var parts = new[]
            {
                (PackOperations.ReplaceTexture, "texture"),
                (PackOperations.ReplaceMesh, "model"),
                (PackOperations.ReplaceAudio, "sound"),
            }
            .Where(k => counts.GetValueOrDefault(k.Item1) > 0)
            .Select(k => counts[k.Item1] is var n ? $"{n} {k.Item2}{(n == 1 ? "" : "s")}" : "")
            .ToList();

        return parts.Count > 0 ? string.Join(", ", parts) : "nothing yet";
    }

    /// This build's version, as the project file sets it. The SDK appends the commit it was built
    /// from after a `+`, which is noise to anybody reading a post.
    public static string ToolVersion =>
        typeof(ForumPost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? UnknownVersion;
}
