using System.Collections.ObjectModel;
using AssetsTools.NET.Extra;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PGAssetTool.Core.Settings;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Pack;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Gui.ViewModels;

/// One extracted weapon, as a row in the list of workspaces.
public sealed record WorkspaceItem(string Directory, string Name, int Files, int Edited)
{
    public string Summary => Edited == 0 ? $"{Files} files" : $"{Files} files, {Edited} edited";
}

/// The workspace side: what has been extracted, what has been edited in it, and what the edits look
/// like against the originals.
///
/// The files are edited by other programs — an image editor, Blender — so nothing here is
/// authoritative for long. The directory is watched and re-read rather than kept in step by hand.
public sealed partial class EditorViewModel : ObservableObject, IDisposable
{
    private readonly Func<BundleSet?> _bundles;
    private readonly SemaphoreSlim _reading;
    private FileSystemWatcher? _watcher;
    private Timer? _settle;

    public EditorViewModel(Func<BundleSet?> bundles, SemaphoreSlim reading, AlphaPreference? alpha = null)
    {
        (_bundles, _reading) = (bundles, reading);
        alpha ??= new AlphaPreference();
        Original = new PreviewViewModel(alpha);
        Edited = new PreviewViewModel(alpha);

        foreach (var preview in new[] { Original, Edited })
        {
            var pane = preview;
            var other = ReferenceEquals(pane, Original) ? Edited : Original;
            pane.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(PreviewViewModel.ChosenTexture):
                        if (pane.ChosenTexture is { PathId: -1 } choice) WearFromDisk(pane, choice);
                        if (Linked && !_pairing) other.ChosenTexture = pane.ChosenTexture;
                        break;

                    // Both panes carry the same value, so the mirroring settles at once: the other
                    // pane's own notification finds nothing to change and stops there.
                    case nameof(PreviewViewModel.Camera):
                        if (Linked && !_pairing) other.Camera = pane.Camera;
                        break;

                    // Alpha for the same reason as the rest: a comparison where one half honours
                    // the channel and the other does not shows a difference that is not the mod.
                    case nameof(PreviewViewModel.ShowAlpha):
                        if (Linked && !_pairing) other.ShowAlpha = pane.ShowAlpha;
                        break;
                }
            };
        }
    }

    public ObservableCollection<WorkspaceItem> Workspaces { get; } = [];
    public ObservableCollection<WorkspaceFile> Files { get; } = [];

    [ObservableProperty] private string _root = "";
    [ObservableProperty] private WorkspaceItem? _selectedWorkspace;
    [ObservableProperty] private WorkspaceFile? _selectedFile;
    [ObservableProperty] private string _status = "";

    /// The original as the game holds it, and the file as it stands now.
    public PreviewViewModel Original { get; }
    public PreviewViewModel Edited { get; }

    [ObservableProperty] private bool _sideBySide;

    /// Whether the two halves are turned and dressed together.
    ///
    /// On by default, because the question a comparison answers is what changed — and two models at
    /// two angles wearing two textures differ in three ways at once, only one of which is the mod.
    /// It comes off for the case it cannot serve: a replaced mesh, where the two are different
    /// models and looking at each on its own terms is the point.
    [ObservableProperty] private bool _linked = true;

    /// Set while both halves are being filled, so neither hands its view to the other mid-way.
    private bool _pairing;

    /// Brings the halves together at the moment linking is asked for, rather than leaving them
    /// apart until something is turned. The one on show leads: it is the one just been looked at.
    partial void OnLinkedChanged(bool value)
    {
        if (value) Pair();
    }

    private void Pair()
    {
        if (!Linked) return;

        var (from, to) = ShowingEdited ? (Edited, Original) : (Original, Edited);
        to.Camera = from.Camera;
        to.ChosenTexture = from.ChosenTexture;
    }

    /// Which of the two a single-pane comparison is showing. Flicking between them in place is
    /// better at exposing a small difference than putting them next to each other.
    [ObservableProperty] private bool _showingEdited = true;

    public PreviewViewModel Shown => ShowingEdited ? Edited : Original;

    partial void OnShowingEditedChanged(bool value) => OnPropertyChanged(nameof(Shown));

    [RelayCommand]
    private void Flip() => ShowingEdited = !ShowingEdited;

    /// Asked to build packs, and to install them as well when the caller wants the round trip.
    /// The shell owns the game, so the editor only says what it wants done.
    public event Func<IReadOnlyList<string>, bool, Task>? PackRequested;

    [RelayCommand]
    private Task Pack() => Build(install: false);

    [RelayCommand]
    private Task PackAndApply() => Build(install: true);

    private async Task Build(bool install)
    {
        var chosen = Chosen;
        if (chosen.Count == 0) { Status = "Nothing selected."; return; }
        if (PackRequested is null) return;

        await PackRequested(chosen.Select(w => w.Directory).ToList(), install);
        Refresh();
    }

    /// The workspaces a command acts on: everything highlighted, or the one current row.
    public IReadOnlyList<WorkspaceItem> Chosen =>
        Selection.Count > 0 ? Selection.ToList()
        : SelectedWorkspace is { } one ? [one]
        : [];

    /// Bound to the list's own selection. SelectedWorkspace stays the anchor the file list and
    /// the details form follow, because those only make sense for one workspace at a time.
    public ObservableCollection<WorkspaceItem> Selection { get; } = [];

    public bool CanPack => SelectedWorkspace is not null;

    /// What the icon list calls having no icon. A real path can never be this.
    private const string None = "(none)";

    /// The manifest's descriptive half, as a form.
    ///
    /// Held apart from the manifest on disk rather than written through on every keystroke: a
    /// half-typed version string is not something to save, and a workspace is a directory other
    /// programs are watching. Nothing moves until Apply.
    [ObservableProperty] private string _folderName = "";
    [ObservableProperty] private string _packName = "";
    [ObservableProperty] private string _packAuthor = "";
    [ObservableProperty] private string _packVersion = "";
    [ObservableProperty] private string _packDescription = "";

    /// The id is what the ledger keys an installed mod by. Changing it turns an update into a
    /// second, separate install of the same mod, so it is shown and not edited.
    [ObservableProperty] private string _packId = "";

    /// What the pack will be built as, so the name field can say so while it is being typed.
    public string PackFileName => PackBuilder.FileNameFor(new PackManifest
    {
        Id = PackId, Name = PackName ?? "",
    });

    partial void OnPackNameChanged(string value) => OnPropertyChanged(nameof(PackFileName));

    /// The picture the pack shows itself with, and the images in the workspace to choose from.
    public ObservableCollection<string> IconChoices { get; } = [];

    [ObservableProperty] private string? _iconFile;

    /// Whether this pack is signed and scrambled when built: as Options says, or decided for this
    /// pack alone.
    ///
    /// Three named choices rather than a three-state checkbox. The checkbox drew "as Options says"
    /// as a dash, and one click on the dash turned protection *off* rather than on — so a pack meant
    /// to be protected was built unsigned by somebody who had clicked exactly once, and nothing on
    /// screen said otherwise. The first choice says what Options currently answers, so what will
    /// happen is read rather than remembered.
    public IReadOnlyList<ProtectChoice> ProtectChoices { get; } =
    [
        new(null, "As Options says — protected"),
        new(true, "Protect this pack"),
        new(false, "Don't protect this pack"),
    ];

    [ObservableProperty] private ProtectChoice? _selectedProtect;

    /// What Options answers for a pack that has not decided, which the first choice names. Set by
    /// the window's model whenever the setting changes.
    public bool ProtectsByDefault
    {
        get => _protectsByDefault;
        set
        {
            _protectsByDefault = value;
            ProtectChoices[0].Label = value ? "As Options says — protected" : "As Options says — not protected";
        }
    }

    /// Starts where the setting starts, so the label above is true before anybody touches it.
    private bool _protectsByDefault = true;

    /// Set while the form is being filled from disk, so filling it is not mistaken for a choice.
    private bool _loading;

    /// A choice from a list is saved the moment it is made, and the text fields are not.
    ///
    /// The difference is deliberate. A half-typed version string is not something to write into a
    /// directory other programs are watching; picking "Protect this pack" is finished the moment it
    /// is picked, and leaving it to a Save button is how it came to be built unsigned.
    partial void OnSelectedProtectChanged(ProtectChoice? value)
    {
        if (_loading || value is null) return;

        SaveAtOnce(manifest => manifest with { Protect = value.Value }, value.Value switch
        {
            true => "Saved: this pack will be protected.",
            false => "Saved: this pack will not be protected.",
            null => "Saved: this pack does what Options says.",
        });
    }

    partial void OnIconFileChanged(string? value)
    {
        if (_loading || value is null) return;
        SaveAtOnce(manifest => manifest with { Icon = value == None ? "" : value }, "Saved: the pack's icon.");
    }

    private void SaveAtOnce(Func<PackManifest, PackManifest> change, string said)
    {
        if (SelectedWorkspace is not { } workspace) return;

        try
        {
            Workspace.Save(workspace.Directory, change(Workspace.Read(workspace.Directory)));
            Status = said;
        }
        catch (Exception ex)
        {
            Status = ErrorLog.Said(ex, "saving the pack details");
        }
    }

    /// The workspace the form was last filled from, and what its text fields said at that moment.
    private (string Directory, string Folder, string Name, string Author, string Version, string Description)? _shown;

    private bool TextEdited => _shown is { } shown
        && (FolderName != shown.Folder || PackName != shown.Name || PackAuthor != shown.Author
            || PackVersion != shown.Version || PackDescription != shown.Description);

    /// <param name="keepTyping">
    /// Whether text typed into the form and not yet saved survives the re-read. Revert is the one
    /// caller that means to throw it away.
    /// </param>
    private void ShowDetails(WorkspaceItem? workspace, bool keepTyping = true)
    {
        var manifest = workspace is null ? null : Details(workspace.Directory);

        // The watcher re-reads the workspace whenever anything in it is written — the form's own
        // saves, and a texture saved from an image editor alike — and every re-read refilled the
        // form from disk. Whatever was half-typed into the description went with it, without a
        // word. The same workspace keeps its typing now; a different one, or Revert, starts again
        // from the file.
        var keep = keepTyping && workspace is not null && TextEdited
            && string.Equals(_shown?.Directory, workspace.Directory, StringComparison.OrdinalIgnoreCase);

        _loading = true;
        try
        {
            IconChoices.Clear();
            IconChoices.Add(None);
            if (workspace is not null)
                foreach (var picture in Workspace.Pictures(workspace.Directory)) IconChoices.Add(picture);

            // Whatever the manifest names, if it is still there. A pack that claims a picture it no
            // longer carries is worse than one with none.
            IconFile = manifest?.Icon is { Length: > 0 } named && IconChoices.Contains(named) ? named : None;
            SelectedProtect = ProtectChoices.First(c => c.Value == manifest?.Protect);
            PackId = manifest?.Id ?? "";

            if (keep) return;

            FolderName = workspace?.Name ?? "";
            PackName = manifest?.Name ?? "";
            PackAuthor = manifest?.Author ?? "";
            PackVersion = manifest?.Version ?? "";
            PackDescription = manifest?.Description ?? "";

            _shown = workspace is null
                ? null
                : (workspace.Directory, FolderName, PackName, PackAuthor, PackVersion, PackDescription);
        }
        finally
        {
            _loading = false;
        }
    }

    private static PackManifest? Details(string directory)
    {
        try { return WorkspaceView.Open(directory)?.Manifest; }
        catch (Exception e) when (e is IOException or InvalidDataException) { return null; }
    }

    /// Writes the edited manifest and, if the folder was renamed too, moves the directory.
    ///
    /// The rename happens last: the manifest is written into the workspace, and writing it after a
    /// move would mean knowing which of the two paths to write to when the move half-failed.
    [RelayCommand]
    private void ApplyDetails()
    {
        if (SelectedWorkspace is not { } workspace) { Status = "Nothing selected."; return; }

        try
        {
            var manifest = Workspace.Read(workspace.Directory) with
            {
                Name = PackName.Trim(),
                Author = PackAuthor.Trim(),
                Version = PackVersion.Trim(),
                Description = PackDescription.Trim(),
                Icon = IconFile is null || IconFile == None ? "" : IconFile,
                Protect = SelectedProtect?.Value,
            };
            Workspace.Save(workspace.Directory, manifest);

            // What was typed is now what is on disk, so the re-read that follows fills the form
            // afresh rather than keeping the typing it has just saved.
            _shown = null;

            // The watcher holds a handle on the directory it is watching, and Windows will not
            // rename a directory out from under one. Rescan puts a watcher back on wherever it ends.
            Watch(null);

            var was = Path.GetFullPath(Path.TrimEndingDirectorySeparator(workspace.Directory));
            var moved = Workspace.Rename(was, FolderName);
            var renamed = !string.Equals(moved, was, StringComparison.Ordinal);

            // The watcher is still pointed at the old path, and rescanning from a moved directory
            // finds nothing under the name it was selected by.
            Rescan(Root);
            SelectedWorkspace = Workspaces.FirstOrDefault(w =>
                string.Equals(Path.GetFullPath(w.Directory), moved, StringComparison.OrdinalIgnoreCase));

            Status = renamed
                ? $"Saved, and the folder is now '{Path.GetFileName(moved)}'."
                : "Saved.";
        }
        catch (Exception ex)
        {
            Status = ErrorLog.Said(ex, "saving the pack details");
        }
    }

    /// The selected workspace's post for the Discord forum, from the form as it reads now — typed and
    /// not yet saved included, because what is on screen is what the author means to post.
    ///
    /// The game version is the installation open now, which is the one the author has just tried the
    /// mod in; the one the workspace was extracted from is the fallback.
    public async Task<string?> ForumPostAsync()
    {
        if (SelectedWorkspace is not { } workspace || WorkspaceView.Open(workspace.Directory) is not { } view)
            return null;

        string? version = null;
        if (_bundles() is { } bundles)
        {
            await _reading.WaitAsync();
            try { version = await Task.Run(() => GameVersion.Of(bundles.Context, bundles.Game)); }
            finally { _reading.Release(); }
        }

        var manifest = view.Manifest with
        {
            Name = PackName, Author = PackAuthor, Version = PackVersion, Description = PackDescription,
        };
        return ForumPost.For(manifest, view.Files.Where(f => f.Edited).Select(f => f.Operation),
            version, ForumPost.ToolVersion);
    }

    /// Puts the form back to what is on disk, for after a change nobody wants to keep.
    [RelayCommand]
    private void RevertDetails() => ShowDetails(SelectedWorkspace, keepTyping: false);

    /// What is about to be deleted, while it is being asked about; null the rest of the time.
    [ObservableProperty] private Confirmation? _asking;

    [RelayCommand]
    private void Dismiss() => Asking = null;

    [RelayCommand]
    private async Task Proceed()
    {
        if (Asking is { } asking) await asking.Proceed();
        Asking = null;
    }

    /// Throws a workspace away, folder and all.
    ///
    /// A workspace is a directory this tool wrote, and until now the only way to be rid of one was
    /// to leave the tool and find it on disk — which a finished mod and a folder full of test
    /// extracts both eventually need. It acts on the same selection everything else in this pane
    /// acts on, and it asks first: what goes is the author's own work as much as the tool's, since
    /// a mod half-built lives in exactly the directory the extract began as.
    [RelayCommand]
    private void Delete()
    {
        var chosen = Chosen;
        if (chosen.Count == 0) { Status = "Nothing selected."; return; }

        var subject = chosen.Count == 1 ? $"'{chosen[0].Name}'" : $"{chosen.Count} workspaces";
        Asking = new Confirmation(
            $"Delete {subject}?",
            (chosen.Count > 1 ? string.Join(", ", chosen.Select(w => w.Name)) + "\n\n" : "")
            + "The folder goes with everything in it, edits included, and this is not something the "
            + "tool can undo. Packs already built from it are files of their own and stay where "
            + "they are, installed or not.",
            () => Discard(chosen));
    }

    private async Task Discard(IReadOnlyList<WorkspaceItem> chosen)
    {
        // The watcher holds a handle on the directory it watches, and Windows will not delete a
        // directory out from under one. Rescan puts a watcher back on whatever is selected after.
        Watch(null);

        // And so does the comparison, which reads the selected file on a thread of its own. Nothing
        // selected, then wait for whatever was already reading to finish: choosing a workspace
        // starts a read of a file inside it, and deleting it in the next moment found that file
        // still open. It took a faster install to make that moment short enough to catch.
        SelectedFile = null;
        await _reading.WaitAsync();
        try
        {
            Erase(chosen);
        }
        finally
        {
            _reading.Release();
        }
    }

    /// The folders themselves, once nothing of this pane is holding them open.
    private void Erase(IReadOnlyList<WorkspaceItem> chosen)
    {

        var (gone, failed) = (0, new List<string>());
        foreach (var workspace in chosen)
        {
            try
            {
                // Never anywhere but the folder this pane is showing. The list is built by walking
                // that folder, so nothing else can get in — but a delete is worth checking twice,
                // and a workspace whose row is stale would otherwise be a path from anywhere.
                if (!Inside(Root, workspace.Directory))
                {
                    failed.Add($"{workspace.Name} is not in {Root}");
                    continue;
                }

                Directory.Delete(workspace.Directory, recursive: true);
                gone++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{workspace.Name}: {ex.Message}");
            }
        }

        Rescan(Root);
        Status = failed.Count == 0
            ? $"{gone} deleted."
            : $"{gone} deleted, {failed.Count} not — {string.Join("; ", failed)}";
    }

    private static bool Inside(string root, string path)
    {
        if (root.Length == 0) return false;

        var within = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(within, StringComparison.OrdinalIgnoreCase);
    }

    /// Writes a drawing of the model, as the preview is showing it, and makes it the pack's picture.
    ///
    /// The one the export draws is of the vanilla weapon, since nothing has been edited at that
    /// point. This is the mod — the author's own mesh wearing their own texture, from the angle
    /// they turned it to.
    public void CaptureIcon(PreviewImage picture)
    {
        if (SelectedWorkspace is not { } workspace) { Status = "Nothing selected."; return; }

        if (Core.Preview.PackIcon.IsBlank(picture))
        {
            Status = "There is nothing in the frame to make an icon out of.";
            return;
        }

        try
        {
            Core.Preview.PackIcon.Write(picture, Path.Combine(workspace.Directory, Core.Preview.PackIcon.FileName));

            var manifest = Workspace.Read(workspace.Directory) with { Icon = Core.Preview.PackIcon.FileName };
            Workspace.Save(workspace.Directory, manifest);

            ShowDetails(workspace);
            Status = $"The pack now shows this view of the model, {picture.Width}×{picture.Height}.";
        }
        catch (Exception ex)
        {
            Status = ErrorLog.Said(ex, "saving the icon");
        }
    }

    /// Reads the workspaces under the root again.
    ///
    /// Guarded for the same reason the manager's refresh is: a watcher on the workspace folder
    /// calls this whenever the author saves a file there, and it arrives on the UI thread with
    /// nobody waiting for it. The folder being renamed or taken away underneath — which is what an
    /// author doing housekeeping does — would otherwise take the window with it.
    public void Rescan(string root)
    {
        try { Scan(root); }
        catch (Exception ex) { Status = ErrorLog.Said(ex, "reading the workspaces"); }
    }

    private void Scan(string root)
    {
        Root = root;
        var chosen = SelectedWorkspace?.Directory;

        // The rows are about to be replaced by new instances, so anything held here refers to
        // workspaces that no longer exist as far as the list is concerned.
        Selection.Clear();
        Workspaces.Clear();
        foreach (var directory in WorkspaceView.Discover(root))
        {
            if (WorkspaceView.Open(directory) is not { } view) continue;
            Workspaces.Add(new WorkspaceItem(directory, view.Name, view.Files.Count, view.EditedCount));
        }

        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Directory == chosen) ?? Workspaces.FirstOrDefault();
        OnPropertyChanged(nameof(CanPack));
        Status = Workspaces.Count == 0
            ? $"Nothing extracted yet. Items written from Browse land in {root}."
            : $"{Workspaces.Count} extracted";
    }

    partial void OnSelectedWorkspaceChanged(WorkspaceItem? value)
    {
        ShowDetails(value);
        var chosen = SelectedFile?.RelativePath;

        Files.Clear();
        // Edited first, and stable within each band so the order the workspace is in survives
        // underneath. A workspace holds thirty files and three of them are the mod; finding those
        // three again after every save was most of what working here consisted of.
        if (value is not null && WorkspaceView.Open(value.Directory) is { } view)
            foreach (var file in view.Files.OrderByDescending(f => f.Edited)) Files.Add(file);

        Watch(value?.Directory);
        SelectedFile = Files.FirstOrDefault(f => f.RelativePath == chosen)
            ?? Files.FirstOrDefault(f => f.Edited)
            ?? Files.FirstOrDefault();
    }

    async partial void OnSelectedFileChanged(WorkspaceFile? value)
    {
        Original.Clear("Nothing selected.");
        Edited.Clear("Nothing selected.");
        ShowAnimationChoice(value);
        if (value is null) return;

        await CompareAsync(value);
    }

    /// Loads both sides of one file.
    ///
    /// The edited side goes through the same decoders the importers use, so a file that could not
    /// be packed shows as unreadable here rather than as a preview of something the pack would
    /// never contain.
    private async Task CompareAsync(WorkspaceFile file)
    {
        try
        {
            await _reading.WaitAsync();
            try
            {
                // Asked for inside the lock, not outside it. A file watcher fires this off on its
                // own thread, and reading the reader first meant holding one that the pack-and-apply
                // waiting on the same lock was about to put down — so the comparison ran against a
                // disposed reader, or kept the bundle open across a write to it.
                if (_bundles() is not { } bundles) { Edited.Clear("The game is not open."); return; }

                // Another file has been selected while this one waited its turn — going down a
                // workspace of thirty files with the arrow keys, or a watcher firing while one is
                // being read. Whatever this would load is already out of date.
                if (SelectedFile != file) return;

                // An animation is shown played by the model it moves, not as a file of its own.
                if (file.Target.Class == nameof(AssetClassID.AnimationClip))
                {
                    await CompareAnimationAsync(bundles, file);
                    return;
                }

                // Read here rather than on the thread below: the list belongs to the window and is
                // rebuilt on it whenever the workspace is read again.
                var named = Files.Select(f => f.Target.Container).Where(c => c.Length > 0).Distinct().ToList();

                var loaded = await Task.Run(() => (
                    Game: FromGame(bundles, file),
                    Disk: AssetPreview.FromFile(file.FullPath),
                    Wears: Wears(bundles, file, named)));

                _wearing = loaded.Wears;

                // The link is off while both halves are being filled, and put back afterwards.
                //
                // The two are loaded one after the other, so for a moment one holds the file being
                // opened and the other still holds the one before it. Mirroring across that moment
                // wrote the new file's angle into the old file's memory — and since each pane
                // remembers per model, going back and forth between two meshes walked the wrong
                // view from one to the other and back, coming round again every few passes.
                _pairing = true;
                try
                {
                    ShowIn(Original, loaded.Game, $"{file.Name} in the game", file, "game");
                    ShowIn(Edited, loaded.Disk,
                        file.Edited ? $"{file.Name} as edited" : $"{file.Name} unchanged", file, "disk");
                }
                finally
                {
                    _pairing = false;
                }

                Pair();
            }
            finally
            {
                _reading.Release();
            }
        }
        catch (Exception ex)
        {
            Edited.Clear(ErrorLog.Said(ex, "comparing against the game"));
        }
    }

    /// Which of the game's textures the mesh in this file is drawn with, in submesh order.
    ///
    /// Asked of the game rather than of the workspace, because the workspace holds no renderers and
    /// no materials to read: a `.glb` and a folder of `.png` say nothing about which goes on which.
    /// The answer comes back as the game's own path ids, which is exactly what the workspace files
    /// record themselves against — so it can be turned into "this picture, that one, not the other
    /// nine" without either end knowing about the other.
    /// <param name="named">
    /// Every bundle this workspace mentions, because a weapon's prefab and its geometry do not
    /// always live in the same one — and the workspace was extracted from the prefab, so whichever
    /// bundle holds the renderer is named by something in it.
    /// </param>
    private IReadOnlyList<AssetNode?> Wears(BundleSet bundles, WorkspaceFile file, IReadOnlyList<string> named)
    {
        if (file.Target.Class != nameof(AssetClassID.Mesh) || file.Target.PathId is not { } mesh) return [];

        try
        {
            // Kept per reader: the first question costs an index of which bundle holds which file,
            // and asking it again for every model opened would pay for that index every time.
            if (!ReferenceEquals(_dressed.Reader, bundles))
                _dressed = (bundles, new Dressing(bundles));

            // In submesh order, holes and all. It used to be flattened to a set of path ids here,
            // which was enough to mark the list and not enough to dress the model: #145's one mesh
            // has the gun in submesh 0 and the flashlight in submesh 1, wearing two pictures.
            return _dressed.Dressing!.For(file.Target.Container, mesh, named);
        }
        catch (Exception)
        {
            // Nothing here is worth failing a preview over; the list simply comes back unmarked.
            return [];
        }
    }

    private (BundleSet? Reader, Dressing? Dressing) _dressed;

    /// What the model now being shown is drawn with, in submesh order, as the game's renderer says.
    private IReadOnlyList<AssetNode?> _wearing = [];

    private static object? FromGame(BundleSet bundles, WorkspaceFile file)
    {
        if (!Enum.TryParse<AssetClassID>(file.Target.Class, out var cls)) return null;
        if (AssetPreview.Locate(bundles, file.Target.Container, cls, file.Target.PathId ?? 0, file.Target.Name)
            is not var (opened, info)) return null;

        var field = bundles.Context.Deserialize(opened, info);
        if (field is null) return null;

        return cls switch
        {
            AssetClassID.Texture2D => AssetPreview.Texture(bundles, file.Target.Container, field),
            // The game's side of the comparison, put together the way its bones put it together —
            // the edited side comes from a glTF and is already whole. See Skeleton.Assembled.
            AssetClassID.Mesh => AssetPreview.Mesh(field, bundles, file.Target.Container) is { } mesh
                ? Skeleton.For(bundles, file.Target.Container, info.PathId)?.Assembled(mesh) ?? mesh
                : null,
            AssetClassID.AudioClip => AssetPreview.Audio(bundles, file.Target.Container, field),
            _ => null,
        };
    }

    /// <param name="side">
    /// Which half of the comparison this is. Part of the subject because the two panes hold two
    /// readings of one file, and a pane must not take the other's view for its own.
    /// </param>
    private void ShowIn(
        PreviewViewModel preview, object? loaded, string caption, WorkspaceFile file, string side)
    {
        switch (loaded)
        {
            case PreviewImage picture: preview.Show(picture, caption, file.AlphaIsCoverage); break;

            case UnityMesh mesh:
                // Read after showing, not before: showing is what looks up the view this model was
                // last left at, and the texture that comes back with it may not be the one the
                // pane happened to be wearing a moment ago.
                // Keyed by where the file is, not by what it is called inside its workspace. Two
                // workspaces made from the same weapon hold the same relative paths, so every
                // extraction of one weapon was one model as far as the remembered views were
                // concerned: switching between them carried the angle across, and switching to a
                // workspace of anything else threw it away.
                // Dressed submesh by submesh out of the author's own files, which is what the game
                // does with the pictures these were extracted from. See Dressing.
                var worn = SelectedWorkspace is { } workspace ? Dressed(file, workspace.Directory) : null;

                preview.Show(mesh, caption, worn, $"{side}:{file.FullPath}");
                Offer(preview, preview.ChosenTexture?.Name, dressed: worn is not null);
                break;

            case PreviewSound sound: preview.Show(sound, caption); break;
            default: preview.Clear("Nothing to show for this one."); break;
        }
    }

    /// Which of the workspace's pictures goes on each submesh of the model being shown.
    ///
    /// A submesh is what decides which material draws a triangle, and a weapon that carries two is
    /// ordinary — #145's one mesh is the gun in submesh 0 and its flashlight in submesh 1, painted
    /// `mp5Gold_map` and `mp5Light_map`. One picture over the whole model gets the smaller part
    /// wrong, visibly: 1,869 of its 21,912 drawn pixels.
    ///
    /// Two accounts of it, and the manifest's comes first. The extraction wrote down which of
    /// *these* files each submesh wears, which is the only account that is right for a workspace
    /// made from a skin — the geometry is the weapon's, the renderer in the game names the weapon's
    /// paint, and every picture here is the skin's. Failing that, the renderer in the game, mapped
    /// back to the files by the asset each was written from.
    ///
    /// A submesh whose picture is not in this workspace stays null and draws grey, which is the
    /// honest answer: the file is not here to show.
    private IReadOnlyList<string?> Dressing(WorkspaceFile file)
    {
        if (file.Wears is { Count: > 0 } recorded && recorded.Any(w => w.Length > 0))
            return recorded.Select(w => w.Length == 0 ? null : w).ToList();

        return _wearing
            .Select(worn => worn is null
                ? null
                : Files.FirstOrDefault(f =>
                    f.Target.PathId == worn.PathId
                    && string.Equals(f.Target.Container, worn.Bundle, StringComparison.OrdinalIgnoreCase))
                    ?.RelativePath)
            .ToList();
    }

    /// Those pictures, read off disk, in submesh order. Null when the model wears nothing this
    /// workspace holds, which leaves the old behaviour — grey until somebody picks one.
    private IReadOnlyList<PreviewImage?>? Dressed(WorkspaceFile file, string directory)
    {
        var worn = Dressing(file);
        if (worn.All(w => w is null)) return null;

        return worn.Select(w =>
        {
            if (w is null) return null;
            try { return AssetPreview.FromFile(Path.Combine(directory, w)) as PreviewImage; }
            catch (Exception) { return null; }
        }).ToList();
    }

    /// Offers the workspace's own images to put on a mesh.
    ///
    /// The model arrives wearing what it wears in the game, out of the author's own files — see
    /// Dressing. This list is for the other question: what this geometry looks like in some other
    /// picture, which is one picture over the whole model and is what a skin is.
    /// <param name="wearing">
    /// The one to put back on, when the same model is being read again. Emptying the list empties
    /// the box bound to it, which writes a null back through the selection, so a choice that is to
    /// survive has to be made again — and the file behind it may well be what changed, which is why
    /// it is read from disk rather than assumed to be still on the model.
    /// </param>
    /// <param name="dressed">
    /// Whether the model already went on wearing its own paint, in which case the list is for
    /// trying something else rather than for choosing what it should have been wearing.
    /// </param>
    private void Offer(PreviewViewModel preview, string? wearing = null, bool dressed = false)
    {
        preview.TextureChoices.Clear();
        if (SelectedWorkspace is not { } workspace) return;

        // Which of the workspace's pictures this model is drawn with, from the same answer that
        // dresses it.
        var worn = (SelectedFile is { } showing ? Dressing(showing) : [])
            .Where(w => w is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        // Named as the browse pane names it, and meaning the same thing: the model wearing what
        // the game paints it with, submesh by submesh.
        preview.TextureChoices.Add(new TextureChoice("(automatic)", "", 0));

        // Lit first and listed first, the same way the browse pane does it: a workspace holds a
        // dozen pictures and two of them are the ones this mesh wears.
        foreach (var picture in Workspace.Pictures(workspace.Directory)
                     .OrderByDescending(worn.Contains))
            preview.TextureChoices.Add(new TextureChoice(picture, workspace.Directory, -1)
            {
                Worn = worn.Contains(picture),
            });

        // Already wearing its own paint, so the list opens on the answer it is wearing. Choosing
        // one of the others is then a deliberate act with a way back, which is what the browse
        // pane does and what the dash of a half-chosen answer never was.
        if (dressed)
        {
            // Unless somebody had put something else on this model, which is theirs to keep.
            var again = preview.TextureChoices.FirstOrDefault(c => c.Name == wearing && c.PathId != 0);
            preview.ChosenTexture = again ?? preview.TextureChoices[0];
            if (again is not null) WearFromDisk(preview, again);
            return;
        }

        // Nothing here to dress it with — a mesh whose renderer was not found, or whose paint was
        // never written into this workspace. Put one on by itself: a model drawn grey says nothing
        // about the mod, and choosing the right picture out of the list was a step everybody took
        // every time.
        // What was on before wins only while this model wears it too. Otherwise the model's own
        // paint goes on — moving from one mesh to another is exactly when carrying the last choice
        // across stops being helpful — and a deliberate choice this model knows nothing about is
        // kept rather than thrown away.
        var choice = preview.TextureChoices.FirstOrDefault(c => c.Name == wearing && c.Worn)
            ?? preview.TextureChoices.FirstOrDefault(c => c.Worn)
            ?? preview.TextureChoices.FirstOrDefault(c => c.Name == wearing);

        if (choice is null) return;

        preview.ChosenTexture = choice;
        WearFromDisk(preview, choice);
    }

    /// Reads one of those images off disk and puts it on the model.
    ///
    /// A path id of -1 marks a choice that lives in the workspace rather than in the game, which is
    /// the only kind the editor offers.
    private void WearFromDisk(PreviewViewModel preview, TextureChoice choice)
    {
        try
        {
            var path = Path.Combine(choice.Bundle, choice.Name);
            if (AssetPreview.FromFile(path) is PreviewImage picture) preview.Wear(picture);
            else Status = $"'{choice.Name}' could not be read as an image.";
        }
        catch (Exception ex)
        {
            Status = ErrorLog.Said(ex, "putting a texture on the model");
        }
    }

    /// Takes files dropped on the editor into the workspace that is selected.
    ///
    /// An author's other tools write wherever they write, and the step between them and here was
    /// finding the workspace on disk and copying the file over by hand. What a file is meant to
    /// replace is nearly always written on it — an edited `ultimatum.png` is the workspace's
    /// `ultimatum.png` — so the name decides, and the file in front of them is the fallback for
    /// when an editor has saved under a name of its own.
    public void Import(IReadOnlyList<string> paths)
    {
        if (SelectedWorkspace is not { } workspace)
        {
            Status = "Choose a workspace first, and these will go into it.";
            return;
        }

        // A .glb dropped while an animation is selected is an animation for it, unless it is named
        // like one of the workspace's models — which is then what it replaces, as before.
        if (IsAnimation && paths.Count == 1
            && string.Equals(Path.GetExtension(paths[0]), ".glb", StringComparison.OrdinalIgnoreCase)
            && Destination(paths[0]) is null)
        {
            _ = UseGlbFile(paths[0], take: null);
            return;
        }

        // A .anim going into an animation is fitted to this item rather than copied: one from another
        // workspace names that item's objects, which this one does not have.
        var animations = paths
            .Where(p => string.Equals(Path.GetExtension(p), ".anim", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Select(p => (Path: p, Slot: Destination(p)))
            .Where(a => a.Slot is { Operation: PackOperations.ReplaceAnimation })
            .Select(a => (a.Path, a.Slot!))
            .ToList();
        if (animations.Count > 0)
        {
            _ = UseAnimFiles(workspace, animations);
            paths = paths.Except(animations.Select(a => a.Path)).ToList();
            if (paths.Count == 0) return;
        }

        var (placed, refused) = (new List<string>(), new List<string>());
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) { refused.Add($"{Path.GetFileName(path)}: not a file"); continue; }

                if (Destination(path) is not { } destination)
                {
                    refused.Add($"{Path.GetFileName(path)}: nothing here it could replace");
                    continue;
                }

                placed.Add(Place(workspace.Directory, destination, path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                refused.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        Refresh();
        Status = (placed.Count > 0 ? $"Put in place: {string.Join(", ", placed)}." : "")
            + (refused.Count > 0 ? $" {refused.Count} not taken — {string.Join("; ", refused)}" : "");
    }

    /// The file in the workspace a dropped one is meant to become.
    private WorkspaceFile? Destination(string path)
    {
        var format = Path.GetExtension(path).TrimStart('.');
        if (Replaceable.OperationForFormat(format) is not { } operation) return null;

        var dropped = Path.GetFileName(path);
        if (Files.FirstOrDefault(f => string.Equals(f.Name, dropped, StringComparison.OrdinalIgnoreCase))
            is { } exact)
            return exact;

        // The same name in another format the same operation takes — a WAV where an Ogg was
        // written out. Only when one file answers to it: two would be a guess, and a guess here
        // overwrites something.
        var stem = Path.GetFileNameWithoutExtension(path);
        var alike = Files
            .Where(f => f.Operation == operation
                && string.Equals(Path.GetFileNameWithoutExtension(f.Name), stem, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (alike.Count > 0) return alike.Count == 1 ? alike[0] : null;

        // Nothing by that name: the one being looked at, if it is the same kind of thing. An
        // image editor saving as 'export.png' is the case, and the file on show is the answer to
        // "which one did you mean" that the person has already given by selecting it.
        return SelectedFile is { } selected && selected.Operation == operation ? selected : null;
    }

    /// Writes the dropped file in, and answers what it landed as.
    private static string Place(string directory, WorkspaceFile destination, string path)
    {
        var dropped = Path.GetExtension(path);
        if (string.Equals(dropped, Path.GetExtension(destination.RelativePath), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(path, destination.FullPath, overwrite: true);
            return destination.Name;
        }

        // Another format of the same kind. It goes in under its own extension and the manifest is
        // pointed at it, rather than leaving Ogg bytes in a file called .wav: the importer reads
        // the bytes and would not care, but everything a person reads afterwards would be lying.
        var renamed = Path.ChangeExtension(destination.RelativePath, dropped);
        File.Copy(path, Path.Combine(directory, renamed.Replace('/', Path.DirectorySeparatorChar)), overwrite: true);

        var manifest = Workspace.Read(directory);
        Workspace.Save(directory, manifest with
        {
            Operations = manifest.Operations
                .Select(o => string.Equals(o.Source, destination.RelativePath, StringComparison.OrdinalIgnoreCase)
                    ? o with { Source = renamed }
                    : o)
                .ToList(),
        });

        if (File.Exists(destination.FullPath)) File.Delete(destination.FullPath);
        return renamed;
    }

    /// Watches the workspace so an edit made elsewhere shows up without being asked for.
    ///
    /// A save is rarely one event — editors write, rename and touch — so the reaction is delayed
    /// until the writing stops. Manual rescanning stays available because watchers do miss things.
    private void Watch(string? directory)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (directory is null || !Directory.Exists(directory)) return;

        _watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += (_, _) => Settle();
        _watcher.Created += (_, _) => Settle();
        _watcher.Renamed += (_, _) => Settle();
        _watcher.Deleted += (_, _) => Settle();
    }

    /// Raised when the watched directory has stopped changing. The view marshals it onto the UI
    /// thread; a watcher fires on its own.
    public event Action? Settled;

    private void Settle()
    {
        _settle?.Dispose();
        _settle = new Timer(_ => Settled?.Invoke(), null, 400, Timeout.Infinite);
    }

    public void Refresh()
    {
        var file = SelectedFile?.RelativePath;
        Rescan(Root);
        if (file is not null && Files.FirstOrDefault(f => f.RelativePath == file) is { } again)
            SelectedFile = again;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _settle?.Dispose();
    }
}

/// One of the three answers to whether a pack is protected. The label of the first one changes
/// with the setting it follows, so it is an object that says so rather than a string.
public sealed partial class ProtectChoice(bool? value, string label) : ObservableObject
{
    /// What the manifest records: null for "as Options says".
    public bool? Value { get; } = value;

    [ObservableProperty] private string _label = label;

    public override string ToString() => Label;
}
