using System.Security.Cryptography;
using PGAssetTool.Core.Export;

namespace PGAssetTool.Core.Pack;

/// The directory an author works in: the exported files plus a manifest that already names the
/// target of every replaceable one.
///
/// Each operation records the hash of its file as exported. Packing keeps only the operations whose
/// file has since changed, so an author edits the two images they care about and the pack contains
/// those two, without having to prune anything by hand.
public static class Workspace
{
    public static PackManifest Create(
        string directory, string id, string name, string author, string? gameVersion,
        IEnumerable<ExportedAsset> assets, PackSubject? subject = null)
    {
        var kept = assets as IReadOnlyCollection<ExportedAsset> ?? assets.ToList();
        var operations = new List<PackOperation>();
        foreach (var asset in kept)
        {
            if (Replaceable.OperationForFormat(asset.Format) is not { } op) continue;
            if (asset.Address.Container.Length == 0) continue;

            operations.Add(new PackOperation
            {
                Op = op,
                Target = asset.Address,
                Source = Relative(directory, asset.Path),
                BaselineSha256 = HashFile(asset.Path),
                AlphaIsMask = asset.AlphaIsMask,

                // Named by file rather than by address, because that is what the editor has in
                // front of it. One entry per submesh, in the renderer's own order, because that is
                // what decides which picture goes on which part — the editor dresses the model
                // from this. A texture the mesh wears and the export did not write — the base
                // weapon's, in a workspace made from one of its skins — keeps its place as an
                // empty entry rather than closing the gap and moving every picture after it.
                Wears = (asset.Wears ?? [])
                    .Select(t => kept.FirstOrDefault(k => Same(k.Address, t)))
                    .Select(k => k is null ? "" : Relative(directory, k.Path))
                    .ToList(),
            });
        }

        return Create(directory, id, name, author, gameVersion, operations, IconIn(directory, kept), subject);
    }

    /// The same asset, wherever the two were written down. A texture reached through a material in
    /// another file carries that file's bundle; the same texture reached directly carries none.
    private static bool Same(Assets.AssetAddress asset, Assets.AssetAddress other)
        => asset.PathId == other.PathId
            && (asset.Container.Length == 0 || other.Container.Length == 0
                || string.Equals(asset.Container, other.Container, StringComparison.OrdinalIgnoreCase));

    /// Writes a workspace whose operations are already worked out.
    public static PackManifest Create(
        string directory, string id, string name, string author, string? gameVersion,
        IReadOnlyList<PackOperation> operations, string icon, PackSubject? subject = null)
    {
        var manifest = new PackManifest
        {
            Id = id,
            Name = name,
            Author = author,
            BuiltAgainstGameVersion = gameVersion,
            Icon = icon,
            Subject = subject,
            Operations = operations.ToList(),
        };
        Settings.AtomicFile.WriteAllText(Path.Combine(directory, PackManifest.FileName), manifest.ToJson());
        return manifest;
    }

    /// The drawing of the model if the export made one, and the game's own icon otherwise.
    ///
    /// The drawing wins because these are texture and mesh mods: the game's icon says which weapon
    /// a pack is for and nothing about what the pack does to it. Guessed once, at extraction, and
    /// recorded — so it is a starting point an author can change rather than a rule the rest of the
    /// tool has to keep agreeing with.
    private static string IconIn(string directory, IEnumerable<ExportedAsset> assets)
        => File.Exists(Path.Combine(directory, Preview.PackIcon.FileName))
            ? Preview.PackIcon.FileName
            : GameIconIn(directory, assets);

    /// The icon folder is where the export puts the one picture that stands for the whole weapon;
    /// the largest of the images there is the one meant to be looked at, the others being chat and
    /// profile sizes.
    private static string GameIconIn(string directory, IEnumerable<ExportedAsset> assets)
        => assets
            .Where(a => a.Format == "png"
                && Path.GetDirectoryName(Relative(directory, a.Path))?.Equals("icon",
                    StringComparison.OrdinalIgnoreCase) == true)
            .OrderByDescending(a => a.Bytes)
            .Select(a => Relative(directory, a.Path))
            .FirstOrDefault() ?? "";

    /// Every image in a workspace, as paths relative to it — what an icon can be chosen from.
    public static IReadOnlyList<string> Pictures(string directory)
    {
        if (!Directory.Exists(directory)) return [];

        return Directory.EnumerateFiles(directory, "*.png", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(directory, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// A directory to extract into that is not already somebody's workspace, numbering up until it
    /// finds one.
    ///
    /// Extracting wrote to a name made from the weapon and the skin, so asking for the same one
    /// twice wrote over the first: the files an author had edited, and the manifest that said which
    /// mod their work was. Two mods of one weapon are two mods — the id has said so since it began
    /// being minted per extraction — and two workspaces are two directories for the same reason.
    ///
    /// The manifest is what makes a directory somebody's rather than merely present: an empty
    /// folder, or one holding something else entirely, is not a workspace and is not stepped over.
    public static string Free(string wanted)
    {
        for (var n = 1; n < 1000; n++)
        {
            var at = n == 1 ? wanted : $"{wanted}_{n}";
            if (!File.Exists(Path.Combine(at, PackManifest.FileName))) return at;
        }

        return wanted;
    }

    public static PackManifest Read(string directory)
        => Once(PackManifest.Parse(File.ReadAllText(Path.Combine(directory, PackManifest.FileName))));

    /// The same operation written down more than once is one operation.
    ///
    /// One asset is reached by several routes — a texture four materials name, a mesh that both the
    /// weapon and a skin's model use — and an export that follows each route added a row for each.
    /// The file written was right, and the same file every time; what was wrong was the list. Here
    /// as well as at the export, because the manifests already on disk have it written into them.
    private static PackManifest Once(PackManifest manifest)
    {
        var seen = new HashSet<(string Op, string Source, Assets.AssetAddress Target)>();
        var kept = manifest.Operations.Where(o => seen.Add((o.Op, o.Source, o.Target))).ToList();

        return kept.Count == manifest.Operations.Count ? manifest : manifest with { Operations = kept };
    }

    /// Writes the manifest back, keeping the operations exactly as they were.
    ///
    /// Only the descriptive half is ever edited by hand — what the mod is called, who wrote it,
    /// which version it is. The operations are the addresses the export resolved, and nothing that
    /// edits a name has any business rewriting those.
    public static void Save(string directory, PackManifest manifest)
        => Settings.AtomicFile.WriteAllText(Path.Combine(directory, PackManifest.FileName), manifest.ToJson());

    /// Gives the workspace directory a different name, in place, and answers where it went.
    ///
    /// The directory name is what the built pack is called, so this is how an author decides what
    /// their mod's file is named rather than living with the number and prefab the export chose.
    /// Nothing inside the workspace refers to the directory by name — operations are relative — so
    /// there is nothing to rewrite afterwards.
    public static string Rename(string directory, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("A workspace needs a name.", nameof(name));
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{trimmed}' cannot be a folder name.", nameof(name));

        var from = Path.GetFullPath(Path.TrimEndingDirectorySeparator(directory));
        var to = Path.Combine(Path.GetDirectoryName(from)!, trimmed);
        if (from == to) return from;

        // Changing only the capitalisation is a real rename, and the directory it "already exists"
        // as is the one being renamed — so that check has to let this one case through.
        if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && Directory.Exists(to))
            throw new IOException($"There is already a workspace called '{trimmed}'.");

        Directory.Move(from, to);
        return to;
    }

    /// The operations whose source file no longer matches what was exported.
    public static List<PackOperation> Changed(string directory, PackManifest manifest)
    {
        var changed = new List<PackOperation>();
        foreach (var operation in manifest.Operations)
        {
            var path = Path.Combine(directory, operation.Source);
            if (!File.Exists(path)) continue;
            if (operation.BaselineSha256 is { } baseline && HashFile(path) == baseline) continue;
            changed.Add(operation);
        }
        return changed;
    }

    public static string HashFile(string path)
        => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string Relative(string directory, string path)
        => Path.GetRelativePath(directory, path).Replace('\\', '/');
}
