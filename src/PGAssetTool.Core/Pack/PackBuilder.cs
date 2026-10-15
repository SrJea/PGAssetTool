using System.IO.Compression;

namespace PGAssetTool.Core.Pack;

public sealed record PackResult(string Path, int Operations, long Bytes, IReadOnlyList<string> Unchanged);

/// Builds a .pgmod: a zip holding the manifest and the files it references, and nothing else.
public static class PackBuilder
{
    public const string Extension = ".pgmod";

    /// What the built file is called: the mod's name, and nothing else.
    ///
    /// The folder is where somebody works. The name is what the mod is called, wherever it is
    /// written down. The id is what the ledger matches an update by, and is nobody's business but
    /// the tool's. Named after the folder instead, there are three names for one thing and renaming
    /// a working directory renames what an author is about to hand out.
    public static string FileNameFor(PackManifest manifest)
    {
        var name = Sanitise(manifest.Name);
        if (name.Length == 0) name = Sanitise(manifest.Id);
        if (name.Length == 0) name = "mod";
        return name + Extension;
    }

    /// Where a pack built from this workspace lands by default: beside the files it came from.
    public static string OutputFor(string workspace, PackManifest manifest)
        => Path.Combine(workspace, FileNameFor(manifest));

    /// As much as one entry in a pack is read as, whether into memory or onto disk.
    ///
    /// Generous for anything an author actually ships — the largest texture in the game is a couple
    /// of megabytes — and finite, which is the point. A zip says how big each entry is and then
    /// hands over as many bytes as it likes, so the size to check is the one coming out.
    public const int MostPerFile = 256 * 1024 * 1024;

    /// And for the two that are read before anything has been decided about the pack.
    public const int MostPerRead = 16 * 1024 * 1024;

    /// One entry, up to a limit, counted as it arrives.
    ///
    /// The declared length is not the limit and is not consulted: it is a number in the pack's own
    /// directory, written by whoever built the pack. Nothing here is decompressed without a ceiling
    /// on what comes out of it, because a few kilobytes of pack can expand without bound and the
    /// manager reads the picture out of every pack it lists before anyone installs anything.
    public static byte[] ReadEntry(ZipArchiveEntry entry, int most, string what)
    {
        using var stream = entry.Open();
        var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];

        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            if (bytes.Length + read > most) throw TooBig(what, most);
            bytes.Write(buffer, 0, read);
        }

        return bytes.ToArray();
    }

    /// The same, onto disk, for the files an install stages before importing them.
    public static void WriteEntry(ZipArchiveEntry entry, string path, int most, string what)
    {
        using var stream = entry.Open();
        using var file = File.Create(path);
        var buffer = new byte[64 * 1024];

        long total = 0;
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            total += read;
            if (total > most) throw TooBig(what, most);
            file.Write(buffer, 0, read);
        }
    }

    private static InvalidDataException TooBig(string what, int most)
        => new($"{what} unpacks to more than {most / (1024 * 1024)}MB, which is more than this reads.");

    /// A file a workspace names, resolved and checked to be inside it.
    ///
    /// Every path in a manifest is relative to the workspace by definition, and this is what says
    /// so: unchecked, an absolute path, a drive-relative one or enough of `..` reaches anywhere the
    /// person building can read, and whatever it finds goes into the pack under the name the
    /// manifest gave it. A manifest is data — most of them written by this tool, but a workspace is
    /// a folder, and folders are shared. The file worth naming is `author.key`, which sits at a
    /// known place beside the executable and is the one thing that would let somebody sign as you.
    ///
    /// Resolved with the separator on the end of the root, so a sibling folder whose name merely
    /// starts the same way — `workspace-old` beside `workspace` — is outside, not inside.
    public static string Inside(string workspace, string relative, string what)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(workspace, relative));

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{what} '{relative}' is outside the workspace, and a pack carries only what the "
                + "workspace holds.");

        // The name it is stored under travels with it, and is read back on the way in. Kept to the
        // same rule so a pack cannot describe a file as living somewhere a workspace could not.
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Contains(".."))
            throw new InvalidOperationException(
                $"{what} '{relative}' has to be a plain path inside the workspace.");

        return full;
    }

    /// Clears out what this workspace built before under another name.
    ///
    /// A pack is named after the mod, so renaming the mod builds a second file beside the first and
    /// leaves the old one in the folder to be picked up and handed out by mistake — with the same
    /// id, so installing it would look like an update of the same mod under its old name.
    ///
    /// Only this workspace's own output goes, and only by id. A pack that arrived in the folder
    /// some other way — downloaded, dropped in to look at — carries a different id and is somebody
    /// else's file. Anything that will not open is left where it is for the same reason.
    private static void Supersede(string workspace, string keep, string id)
    {
        if (id.Length == 0 || !Directory.Exists(workspace)) return;

        foreach (var other in Directory.EnumerateFiles(workspace, "*" + Extension))
        {
            if (string.Equals(Path.GetFullPath(other), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (ReadManifest(other).Id == id) File.Delete(other);
            }
            catch (Exception e) when (e is IOException or InvalidDataException
                                          or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// Whether an operation names a class this tool will not write back.
    private static bool Refused(PackOperation operation)
        => Enum.TryParse<AssetsTools.NET.Extra.AssetClassID>(operation.Target.Class, out var cls)
            && !Replaceable.Supports(cls);

    /// Empty when the name was nothing Windows would take, because the caller has a second name to
    /// try — the mod's id — before settling for anything. See `SafeName`.
    private static string Sanitise(string name) => Settings.SafeName.For(name, "");

    /// <param name="signer">
    /// Signs and scrambles the pack when given. Null leaves it a plain zip, which is what a pack
    /// built with protection off is.
    /// </param>
    public static PackResult Build(string workspace, string outputPath, PackAuthor? signer = null)
    {
        var manifest = Workspace.Read(workspace);
        var changed = Workspace.Changed(workspace, manifest);
        var unchanged = manifest.Operations
            .Where(o => !changed.Contains(o))
            .Select(o => o.Source)
            .ToList();

        if (changed.Count == 0)
            throw new InvalidOperationException(
                $"Nothing in '{workspace}' differs from what was exported, so there is no change to pack.");

        // Said now rather than dropped quietly. A file left out of a pack without a word is a
        // change the author believes they shipped, and they find out from the game.
        if (changed.FirstOrDefault(Refused) is { } refused)
            throw new InvalidOperationException(Replaceable.WhyRefused(
                Enum.Parse<AssetsTools.NET.Extra.AssetClassID>(refused.Target.Class), refused.Source));

        // The baseline hash is a working-directory concern; it says nothing to whoever applies the pack.
        var kept = changed.Select(o => o with { BaselineSha256 = null }).ToList();

        var packed = manifest with
        {
            Operations = kept,
        };

        // The icon goes in whether or not it is one of the files being replaced, and drops out of
        // the manifest if it is not there to go in — a pack claiming a picture it does not carry
        // would be a broken pack rather than one without a picture.
        var icon = packed.Icon.Length > 0 && File.Exists(Inside(workspace, packed.Icon, "the picture"))
            ? packed.Icon
            : "";
        packed = packed with { Icon = icon };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        if (File.Exists(outputPath)) File.Delete(outputPath);

        // Built in memory rather than straight to the file: a protected pack is this zip with a
        // header in front of it and a keystream over it, and it has to be signed as a whole before
        // any of it reaches disk.
        var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry(PackManifest.FileName).Open()))
                writer.Write(packed.ToJson());

            var files = packed.Operations.Select(o => o.Source);
            if (icon.Length > 0) files = files.Append(icon);

            foreach (var source in files.Distinct(StringComparer.OrdinalIgnoreCase))
                archive.CreateEntryFromFile(Inside(workspace, source, "the file"), source);
        }

        PackFile.Write(outputPath, bytes.ToArray(), signer, packed.Author);
        Supersede(workspace, outputPath, packed.Id);

        return new PackResult(outputPath, packed.Operations.Count, new FileInfo(outputPath).Length, unchanged);
    }

    /// The pack's picture, as the bytes of whatever image it carries. Null when it has none.
    ///
    /// Read from the pack each time rather than unpacked to somewhere: a manager listing a dozen
    /// mods reads a dozen small images once, and keeping copies of them would mean a second place
    /// that can disagree with the pack about what the pack looks like.
    public static byte[]? ReadIcon(string packPath)
    {
        try
        {
            using var archive = PackFile.Open(packPath);
            var manifest = ReadManifest(archive);
            if (manifest.Icon.Length == 0) return null;

            if (archive.GetEntry(manifest.Icon) is not { } entry) return null;
            return ReadEntry(entry, MostPerRead, "the picture");
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static PackManifest ReadManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry(PackManifest.FileName)
            ?? throw new InvalidDataException($"the pack has no {PackManifest.FileName}.");
        return PackManifest.Parse(
            System.Text.Encoding.UTF8.GetString(ReadEntry(entry, MostPerRead, "the manifest")));
    }

    public static PackManifest ReadManifest(string packPath)
    {
        using var archive = PackFile.Open(packPath);
        var entry = archive.GetEntry(PackManifest.FileName)
            ?? throw new InvalidDataException($"'{packPath}' has no {PackManifest.FileName}.");
        return PackManifest.Parse(
            System.Text.Encoding.UTF8.GetString(ReadEntry(entry, MostPerRead, "the manifest")));
    }
}
