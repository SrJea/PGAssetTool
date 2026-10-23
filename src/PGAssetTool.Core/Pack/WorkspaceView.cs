using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Pack;

/// One replaceable file in a workspace, and whether it has been touched since it was written out.
/// <param name="Wears">
/// For a model, the other files in this workspace that are the textures it is drawn with, recorded
/// at extraction because nothing here could work it out afterwards. See PackOperation.Wears.
/// </param>
public sealed record WorkspaceFile(
    string RelativePath, string FullPath, AssetAddress Target, string Operation, bool Edited, long Bytes,
    bool AlphaIsMask = false, IReadOnlyList<string>? Wears = null)
{
    public string Folder
    {
        get
        {
            var slash = RelativePath.LastIndexOf('/');
            return slash < 0 ? "" : RelativePath[..slash];
        }
    }

    public string Name => RelativePath[(RelativePath.LastIndexOf('/') + 1)..];

    /// Whether this file's alpha channel means transparency rather than something else.
    ///
    /// Icons are the ones that mean it — every one of them sits on an empty background — and the
    /// export puts them in folders that say so. A model texture keeps emission there instead, so
    /// honouring it blanks the picture. Decided from the folder because that is what the workspace
    /// actually records; the asset it came from is no longer at hand by the time anyone looks.
    ///
    /// A masked texture means it too, and for the same reason the other way round: its alpha is
    /// exactly which part of the image is used, so honouring it is what shows the islands.
    public bool AlphaIsCoverage =>
        AlphaIsMask || Folder.Contains("icon", StringComparison.OrdinalIgnoreCase);
}

/// A directory an author is working in, as it stands right now.
///
/// Read fresh each time rather than watched into a model: the files are edited by other programs,
/// and the only trustworthy answer to what has changed is the one taken at the moment of asking.
public sealed record WorkspaceView(string Directory, PackManifest Manifest, IReadOnlyList<WorkspaceFile> Files)
{
    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(Directory));
    public int EditedCount => Files.Count(f => f.Edited);

    /// Every directory under `root` that holds a manifest, most recently written first.
    ///
    /// Ordered by the manifest rather than the directory: a directory's timestamp only moves when
    /// its own entries change, so writing files into subfolders leaves it looking untouched.
    public static IReadOnlyList<string> Discover(string root)
    {
        if (!System.IO.Directory.Exists(root)) return [];

        // A folder this cannot look into is a folder with no workspace in it as far as this is
        // concerned. The overload that takes a SearchOption throws at the first one instead, which
        // makes one unreadable directory anywhere under the workspace root — and the root is
        // wherever the author points it — the end of the whole list.
        return System.IO.Directory
            .EnumerateDirectories(root, "*",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(d => File.Exists(Path.Combine(d, PackManifest.FileName)))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, PackManifest.FileName)))
            .ToList();
    }

    public static WorkspaceView? Open(string directory)
    {
        PackManifest manifest;
        try { manifest = Workspace.Read(directory); }
        catch (Exception e) when (e is IOException or InvalidDataException) { return null; }

        var changed = Workspace.Changed(directory, manifest).ToHashSet();

        // One row per file rather than per operation. A file can stand for more than one asset — a
        // weapon's paint and the identical copy its default skin paints with are both written from
        // the one picture — and a row per operation lists the same image twice.
        var files = manifest.Operations
            .GroupBy(o => o.Source, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var operation = group.First();
                var full = Path.Combine(directory, operation.Source);
                var info = new FileInfo(full);
                return new WorkspaceFile(
                    operation.Source, full, operation.Target, operation.Op,
                    group.Any(changed.Contains), info.Exists ? info.Length : 0, operation.AlphaIsMask,
                    operation.Wears);
            })
        .OrderBy(f => f.Folder, StringComparer.Ordinal)
        .ThenBy(f => f.Name, StringComparer.Ordinal)
        .ToList();

        return new WorkspaceView(directory, manifest, files);
    }
}
