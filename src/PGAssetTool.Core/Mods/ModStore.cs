using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PGAssetTool.Core.Game;

namespace PGAssetTool.Core.Mods;

public sealed record InstalledMod
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Author { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public required string PackPath { get; init; }
    public required DateTimeOffset InstalledAt { get; init; }
    public required string GameVersion { get; init; }
    public bool Enabled { get; init; } = true;

    /// Which weapon and which of its looks this mod is for, copied out of the pack it came from.
    ///
    /// Kept in the ledger rather than read back from the pack each time. The manager groups every
    /// installed mod by it the moment the tab is opened, and opening a hundred packs — several of
    /// them signed and scrambled — to draw a list of headings is not a thing to do on a click.
    public Pack.PackSubject? Subject { get; init; }

    /// The bundles this mod wrote to, and the manifest hash each had when it did. A bundle whose
    /// hash has since changed was replaced by a game update, which is what makes reapplying and
    /// backup cleanup possible.
    public required Dictionary<string, string> TouchedBundles { get; init; }

    /// Operations whose asset is not in the bundle the pack names, by the target as the pack names
    /// it (`Relocation.Key`), and where it was found instead. See Relocation.
    ///
    /// Learned at a reconcile and kept, so the one after it goes straight to the right bundle rather
    /// than failing first and looking again. Null for a mod that has never needed it, which is
    /// nearly every mod, so the ledger does not grow an empty entry per mod.
    public Dictionary<string, Relocated>? Moved { get; init; }
}

/// What this tool last wrote into one bundle: the mods and packs it was built from, and the hash of
/// the file that came out.
///
/// Held so a reconcile can tell a bundle that already holds the right thing from one that does not.
/// Both halves are needed — the recipe says the answer would be the same, the hash says nobody has
/// been at the file since — and neither is trusted on its own.
public sealed record WrittenBundle(string Recipe, string Hash);

/// The tool's own directory: original bundles it has replaced, and what is installed.
///
/// Deliberately outside the game folder. Uninstalling the game removes that folder, and the
/// downloaded bundle cache in the player's profile survives it — so backups kept beside the game
/// would be destroyed exactly when a modified bundle outlived them. Each installation gets its own
/// subdirectory, named after its path, so several do not share a store.
public sealed class ModStore
{
    public const string DataDirectoryName = "PGAssetTool-data";
    private const string StateFileName = "installed.json";
    private const string WrittenFileName = "written.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly GameInstallation _game;

    /// Where everything this tool keeps lives: beside the executable, not in the user profile.
    public string Home { get; }

    public ModStore(GameInstallation game, string? home = null)
    {
        _game = game;
        Home = home ?? DefaultHome();
        Root = Path.Combine(Home, "installs", KeyFor(game.RootDirectory));
        BackupRoot = Path.Combine(Root, "backup");
    }

    /// Beside the tool, not beside the game and not off in the user profile.
    ///
    /// From a published build that is the executable's own directory. From a development build the
    /// executable sits several levels down in bin/, which a rebuild can wipe, so the search walks up
    /// to the checkout it belongs to and settles there instead.
    public static string DefaultHome()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var candidate = directory; candidate is not null; candidate = candidate.Parent)
        {
            if (candidate.EnumerateDirectories(".git").Any() || candidate.EnumerateFiles("*.slnx").Any())
                return Path.Combine(candidate.FullName, DataDirectoryName);
        }
        return Path.Combine(directory.FullName, DataDirectoryName);
    }

    /// Readable enough to recognise, with a digest of the full path so two installations sharing a
    /// folder name do not share a store.
    private static string KeyFor(string installPath)
    {
        var full = Path.GetFullPath(installPath).TrimEnd(Path.DirectorySeparatorChar);
        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..8];
        // Deliberately not SafeName: this one names the folder holding an installation's backups
        // and its ledger, and tidying the ends of it would move that folder for anyone whose game
        // sits in a directory ending in a space or a dot — which is to say it would lose their
        // backups. The digest follows the name, so nothing here can end in one anyway.
        var name = Path.GetFileName(full);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return $"{name}-{digest}";
    }

    public string Root { get; }

    /// Installed packs, copied here so the ledger never points at wherever an author happened to
    /// build one. Deleting a workspace used to leave an installed mod with no file to reapply from.
    public string ModsDirectory => Path.Combine(Home, "mods");
    public string BackupRoot { get; }
    private string StatePath => Path.Combine(Root, StateFileName);

    /// The copy `Write` keeps of what the ledger said before the last change. `Read` falls back to
    /// it, so it is a working part of the ledger rather than a leftover.
    private const string PreviousSuffix = ".previous";

    /// The ledger, or the copy kept beside it when the ledger itself cannot be read.
    ///
    /// Answering "no mods" for a file that is there but unreadable would be the worst of the three
    /// answers: the next thing to write the ledger would replace it with the one mod it was
    /// installing, and the record of the rest — which is what uninstalling them goes through —
    /// would be gone. So a broken ledger falls back to the copy `Write` keeps, which is the ledger
    /// as it was before the last change and therefore wrong by at most one mod; and if that cannot
    /// be read either, this says so rather than guessing. The backups are still all there; it is
    /// only the record of who put what where that is at stake.
    public List<InstalledMod> Read()
    {
        if (!File.Exists(StatePath)) return [];

        try
        {
            return Parse(StatePath);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            var previous = StatePath + PreviousSuffix;
            if (File.Exists(previous))
            {
                try
                {
                    var mods = Parse(previous);

                    // Put back as well as read: `Write` copies the ledger aside before overwriting
                    // it, and leaving the broken one in place would make the next write file it as
                    // the copy worth keeping.
                    File.Copy(previous, StatePath, overwrite: true);
                    return mods;
                }
                catch (Exception also) when (also is JsonException or IOException or UnauthorizedAccessException)
                {
                    // Fall through to the message below: neither copy can be read.
                }
            }

            throw new InvalidDataException(
                $"The list of installed mods at '{StatePath}' cannot be read: {e.Message}. "
                + "The backups of the game's bundles are unaffected. Move that file aside to start "
                + "a fresh list, and the mods already installed will have to be removed by putting "
                + "those backups back by hand.", e);
        }
    }

    private static List<InstalledMod> Parse(string path)
        => JsonSerializer.Deserialize<List<InstalledMod>>(File.ReadAllText(path), Json) ?? [];

    /// Keeps the previous contents beside the file before overwriting.
    ///
    /// Losing this file does not lose the backups — those are enumerable on their own — but it does
    /// lose the record of which mod put what where, and with it the ability to uninstall through the
    /// tool rather than by hand.
    public void Write(IEnumerable<InstalledMod> mods)
    {
        Directory.CreateDirectory(Root);
        if (File.Exists(StatePath)) File.Copy(StatePath, StatePath + PreviousSuffix, overwrite: true);
        Settings.AtomicFile.WriteAllText(StatePath, JsonSerializer.Serialize(mods.ToList(), Json));
    }

    private string WrittenPath => Path.Combine(Root, WrittenFileName);

    /// What the last reconcile left in each bundle.
    ///
    /// A convenience, never a source of truth: everything it says is checked against the file on
    /// disk before it is acted on, and the whole file being missing or unreadable means nothing
    /// worse than rebuilding every bundle once.
    public Dictionary<string, WrittenBundle> ReadWritten()
    {
        try
        {
            return File.Exists(WrittenPath)
                ? JsonSerializer.Deserialize<Dictionary<string, WrittenBundle>>(
                      File.ReadAllText(WrittenPath), Json) is { } read
                    ? new Dictionary<string, WrittenBundle>(read, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, WrittenBundle>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, WrittenBundle>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, WrittenBundle>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void WriteWritten(IReadOnlyDictionary<string, WrittenBundle> written)
    {
        Directory.CreateDirectory(Root);
        Settings.AtomicFile.WriteAllText(WrittenPath, JsonSerializer.Serialize(written, Json));
    }

    /// Backups are filed under the bundle's hash, so a backup taken before an update stays
    /// distinguishable from the version that replaced it.
    public string BackupPathFor(CacheKind cache, string bundle, string hash)
        => Path.Combine(BackupRoot, cache.ToString().ToLowerInvariant(), bundle, hash, bundle);

    /// Every bundle this store holds a backup of — the record of what has actually been written to.
    ///
    /// The installed list cannot answer that. Uninstalling drops the mod before its bundles are put
    /// back, so a bundle whose only mod is gone is still modified and no longer named anywhere else.
    public IEnumerable<(CacheKind Cache, string Bundle, string Hash)> BackedUp()
    {
        if (!Directory.Exists(BackupRoot)) yield break;

        foreach (var cacheDirectory in Directory.GetDirectories(BackupRoot))
        {
            // An older layout filed backups directly under the bundle name, with no cache segment.
            if (!Enum.TryParse<CacheKind>(Path.GetFileName(cacheDirectory), ignoreCase: true, out var cache))
                continue;

            foreach (var bundleDirectory in Directory.GetDirectories(cacheDirectory))
            {
                var bundle = Path.GetFileName(bundleDirectory);
                foreach (var hashDirectory in Directory.GetDirectories(bundleDirectory))
                    if (File.Exists(Path.Combine(hashDirectory, bundle)))
                        yield return (cache, bundle, Path.GetFileName(hashDirectory));
            }
        }
    }

    /// Takes a copy of a pack so installing it does not depend on the file staying where it was.
    ///
    /// A pack built into a workspace is one deletion away from leaving an installed mod that cannot
    /// be reapplied or removed cleanly — which happened, and cost an uninstall to recover from.
    public string Keep(string packPath, Pack.PackSubject? subject = null)
    {
        // Already here: reinstalling from the copy must not spiral into copies of copies.
        var full = Path.GetFullPath(packPath);
        if (full.StartsWith(Path.GetFullPath(ModsDirectory) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return full;

        var into = FolderFor(subject);
        Directory.CreateDirectory(into);

        var kept = KeptPathFor(into, full);
        Settings.AtomicFile.Copy(packPath, kept);
        return kept;
    }

    /// Where a pack is filed: a folder for the weapon, and one inside it for the look.
    ///
    /// Mods pile up in the tens and they are nearly all of the same handful of weapons, so a flat
    /// folder stops being readable long before it stops working. The levels are the two questions
    /// somebody actually has — which weapon, and which of its skins — and they are the same two the
    /// manager groups by, so what is on disk and what is on screen are the same arrangement.
    ///
    /// A pack with nothing to say about itself goes in a folder that says so, rather than being
    /// filed under a guess.
    public string FolderFor(Pack.PackSubject? subject)
        => subject is { IsKnown: true }
            ? Path.Combine([ModsDirectory, .. subject.Path.Select(Sanitize)])
            : Path.Combine(ModsDirectory, Unfiled);

    /// Where packs go when they do not say what they are for.
    public const string Unfiled = "unfiled";

    /// Every kept pack, wherever in the tree it sits.
    public IEnumerable<string> KeptPacks()
        => Directory.Exists(ModsDirectory)
            ? Directory.EnumerateFiles(ModsDirectory, "*" + Pack.PackBuilder.Extension, SearchOption.AllDirectories)
            : [];

    /// A folder name that means what it says and can be written down. See `SafeName`.
    private static string Sanitize(string name) => Settings.SafeName.For(name);

    /// Throws away the copy taken of a mod's pack, once nothing is installed from it.
    ///
    /// Only ever a file of this store's own: a mod installed from a pack that was already in the
    /// mods directory names that file, but one installed from a workspace names the copy, and the
    /// author's original is theirs. Anything outside the directory is left alone and reported as
    /// not deleted rather than quietly passed over, because "removed the file" and "the file was
    /// somewhere I do not touch" are different answers to the same request.
    ///
    /// Answers whether a file went. Nothing to delete is not a failure — a mod whose pack has
    /// already been deleted by hand is exactly the case this is being used to reach.
    public bool DiscardKeptPack(InstalledMod mod)
    {
        if (mod.PackPath.Length == 0) return false;

        var full = Path.GetFullPath(mod.PackPath);
        if (!full.StartsWith(Path.GetFullPath(ModsDirectory) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return false;

        if (!File.Exists(full)) return false;
        File.Delete(full);
        Prune(Path.GetDirectoryName(full));
        return true;
    }

    /// Takes away the folders a deleted pack was the last thing in.
    ///
    /// The tree is there to be read, and a weapon with nothing left under it is a heading over
    /// nothing. Stops at the mods directory itself, which stays whether or not anything is in it.
    private void Prune(string? directory)
    {
        var root = Path.GetFullPath(ModsDirectory);
        for (var at = directory; at is not null; at = Path.GetDirectoryName(at))
        {
            var here = Path.GetFullPath(at);
            if (here.Length <= root.Length || !here.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
            if (!Directory.Exists(here) || Directory.EnumerateFileSystemEntries(here).Any()) return;

            try { Directory.Delete(here); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        }
    }

    /// The name a pack built at `sourcePath` is filed under.
    ///
    /// The digest goes after the name rather than in front of it: the folder is browsed by a person
    /// looking for a mod they built, and eight hex characters at the start of every row means
    /// sorting by name sorts by nothing and reading the list means reading past the same noise each
    /// time. It still has to be there — two packs called the same thing from different directories
    /// would otherwise be one file — but it belongs where a disambiguator belongs.
    private static string KeptPathFor(string into, string sourcePath)
    {
        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath.ToLowerInvariant())))[..8];
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(into, $"{name}-{digest}{Path.GetExtension(sourcePath)}");
    }

    public bool HasBackup(CacheKind cache, string bundle, string hash) => File.Exists(BackupPathFor(cache, bundle, hash));

    /// The copy of a bundle as it was before anything was written into it, or null for one nothing
    /// has touched — which is what a reader showing the game as shipped opens. See BundleSet.
    public string? OriginalOf(CacheKind cache, string bundle, string hash)
        => HasBackup(cache, bundle, hash) ? BackupPathFor(cache, bundle, hash) : null;

    public void Backup(CacheKind cache, string bundle, string hash, string livePath)
    {
        var destination = BackupPathFor(cache, bundle, hash);
        if (File.Exists(destination)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(livePath, destination);
    }

    public bool RestoreIfBackedUp(CacheKind cache, string bundle, string hash, string livePath)
    {
        var source = BackupPathFor(cache, bundle, hash);
        if (!File.Exists(source)) return false;
        File.Copy(source, livePath, overwrite: true);
        return true;
    }

    /// What a backup holds, by MD5.
    ///
    /// Usually the hash it is filed under, since a cache directory is named after its bundle's MD5 —
    /// but not for a bundle that was already modified when it was backed up, which is what a forced
    /// install of a game modded by something else leaves. Asked whether a live bundle is the one the
    /// backup would put back, the name then says no every time, and every reconcile copied that
    /// bundle over an identical copy of itself and said it had put it back.
    ///
    /// Hashed once and kept beside the backup, with the size and time it was taken from, so a
    /// reconcile does not read every backup through to answer it. Null when there is no backup.
    public string? BackupMd5(CacheKind cache, string bundle, string hash)
    {
        var backup = BackupPathFor(cache, bundle, hash);
        var file = new FileInfo(backup);
        if (!file.Exists) return null;

        var stamp = $"{file.Length} {file.LastWriteTimeUtc.Ticks}";
        var kept = backup + ".md5";
        try
        {
            var said = File.Exists(kept) ? File.ReadAllText(kept).Split(' ', 2) : [];
            if (said.Length == 2 && said[1] == stamp) return said[0];
        }
        catch (IOException) { }

        var md5 = BundleIntegrity.Md5(backup);
        try { Settings.AtomicFile.WriteAllText(kept, $"{md5} {stamp}"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return md5;
    }

    /// A game update replaces a bundle and gives it a new hash directory. The modified copy under
    /// the old hash and the backup beside it are both dead weight at that point.
    ///
    /// Which hash counts as current depends on the cache: the shipped copy follows the manifest, the
    /// downloaded one follows its own ledger, and the two can disagree.
    public IReadOnlyList<string> PruneStaleBackups(IReadOnlyDictionary<string, string> manifestHashes)
    {
        if (!Directory.Exists(BackupRoot)) return [];

        var removed = new List<string>();
        foreach (var cacheDirectory in Directory.GetDirectories(BackupRoot))
        {
            var cacheName = Path.GetFileName(cacheDirectory);
            var downloaded = string.Equals(cacheName, nameof(CacheKind.Downloaded),
                StringComparison.OrdinalIgnoreCase);

            foreach (var bundleDirectory in Directory.GetDirectories(cacheDirectory))
            {
                var bundle = Path.GetFileName(bundleDirectory);
                var current = downloaded
                    ? _game.Downloaded?.Claimed.GetValueOrDefault(bundle)
                    : manifestHashes.GetValueOrDefault(bundle);

                foreach (var hashDirectory in Directory.GetDirectories(bundleDirectory))
                {
                    if (Path.GetFileName(hashDirectory) == current) continue;
                    Directory.Delete(hashDirectory, recursive: true);
                    removed.Add($"{cacheName}/{bundle}/{Path.GetFileName(hashDirectory)}");
                }
                if (Directory.GetFileSystemEntries(bundleDirectory).Length == 0)
                    Directory.Delete(bundleDirectory);
            }
            if (Directory.GetFileSystemEntries(cacheDirectory).Length == 0)
                Directory.Delete(cacheDirectory);
        }
        return removed;
    }
}
