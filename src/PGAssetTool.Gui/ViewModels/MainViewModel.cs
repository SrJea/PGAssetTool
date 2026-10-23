using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;
using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Export;
using PGAssetTool.Core.Game;
using PGAssetTool.Core.Mods;
using PGAssetTool.Core.Settings;
using PGAssetTool.Core.Pack;
using PGAssetTool.Core.Preview;
using PGAssetTool.Core.Weapons;

namespace PGAssetTool.Gui.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private BundleSet? _bundles;
    private GameCatalogs? _catalogs;
    private WeaponResolver? _resolver;

    /// Guards the bundle reader, which is not safe to use from two threads at once.
    private readonly SemaphoreSlim _reading = new(1, 1);

    /// One answer to the alpha question for the whole window; see AlphaPreference.
    private readonly AlphaPreference _alpha = new();

    public MainViewModel()
    {
        Preview = new PreviewViewModel(_alpha);
        Preview.PropertyChanged += OnPreviewChanged;
        Editor = new EditorViewModel(() => _bundles, _reading, _alpha) { Catalogs = () => _catalogs };
        Editor.PackRequested += BuildPack;

        Manager = new ManagerViewModel(() => _installation);
        Manager.Around = WithReaderClosed;

        // These two live where they are used rather than in the options window — they are worked
        // mid-task, and a round trip through a dialog for each would be worse. Being where they are
        // used is no reason to forget them between runs, though, so the shell writes them out.
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorViewModel.SideBySide) or nameof(EditorViewModel.Linked))
                Remember();
        };
        Manager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ManagerViewModel.ConfirmChanges) or nameof(ManagerViewModel.TileSize)
                or nameof(ManagerViewModel.Order))
                Remember();

            if (e.PropertyName is nameof(ManagerViewModel.Busy)) OnPropertyChanged(nameof(Working));
        };
    }

    [ObservableProperty] private string _status = "Looking for the game…";
    [ObservableProperty] private bool _busy = true;

    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(Working));

    /// How large the windows draw themselves, as a percentage. See ToolSettings.UiScale.
    [ObservableProperty] private int _uiScale = 100;

    partial void OnUiScaleChanged(int value)
    {
        OnPropertyChanged(nameof(Zoom));
        OnPropertyChanged(nameof(OptionsWidth));
        Remember();
    }

    /// The percentage as the transform on each window's content wants it.
    public double Zoom => UiScale / 100.0;

    /// What is offered. Down as well as up: somebody on a laptop fitting more of the tree on the
    /// page is the same wish as somebody on a 4K screen reading it.
    public IReadOnlyList<int> UiScales { get; } = [75, 90, 100, 110, 125, 150, 175, 200];

    /// The options window is sized to its content and cannot be resized, so its one fixed
    /// dimension has to grow with the content.
    ///
    /// The main window's own smallest size is deliberately left alone. Scaled with the content it
    /// would be 1800 wide at 200%, which on a screen narrower than that is a window that cannot be
    /// brought back; a squeezed layout is the lesser of the two and is undone here.
    public double OptionsWidth => 460 * Zoom;

    /// Whether anything is going on that the window should say is going on.
    ///
    /// The manager's work is the other half of it: turning a mod on restores and rewrites bundles
    /// for a second or two, on its own tab, and the bar in the status line — the one thing on
    /// screen that says the tool is doing something — is bound to this rather than to the busy flag
    /// the manager keeps of its own.
    public bool Working => Busy || Manager.Busy;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private WeaponListItem? _selected;
    [ObservableProperty] private WeaponDetailViewModel? _detail;

    public PreviewViewModel Preview { get; }

    /// The workspace side. Given the same reader and the same lock as everything else, because
    /// there is one BundleSet and it is not safe to use from two places at once.
    public EditorViewModel Editor { get; }

    /// The game side: what is installed and what state the bundles are in.
    public ManagerViewModel Manager { get; }

    /// The resolved weapon behind the current tree, kept so the tree can be rebuilt when the filter
    /// changes without reading the bundles again.
    private WeaponTree? _tree;

    /// The installation itself, which holds no files open — so it survives the reader being put
    /// down to write, which is exactly when the manager needs it.
    private GameInstallation? _installation;

    /// Set while preferences are being applied, so reading them back does not write them out again
    /// or reload the game once per setting.
    private bool _loading;

    /// The preferences as last read or written. Held so extraction knows where to write and whose
    /// name to record without going back to disk for each.
    private ToolSettings _settings = new();

    /// Hides everything that cannot be written back. What counts comes from the import registry.
    [ObservableProperty] private bool _replaceableOnly = true;

    /// The game translation table names are read from, and the languages this installation offers.
    [ObservableProperty] private string _language = "l_en-gb";

    /// Textures written with no alpha channel. The original is put back on the way in.
    [ObservableProperty] private bool _opaqueTextures;

    /// Whether an exported texture keeps only the part a model samples. See UvCoverage.
    [ObservableProperty] private bool _maskUnusedTextures = true;

    partial void OnMaskUnusedTexturesChanged(bool value) => Remember();

    /// Recorded in the manifest of anything extracted from here on. Editable per pack afterwards.
    [ObservableProperty] private string _author = "";

    /// Which installation to work on. Empty finds the one Steam knows about.
    [ObservableProperty] private string _gameDirectory = "";

    /// Reopening the game is the whole of what this changes, and everything downstream — the
    /// weapons, the workspaces, what is installed — is read from whatever it lands on.
    partial void OnGameDirectoryChanged(string value)
    {
        Remember();
        OnPropertyChanged(nameof(GameDescribed));

        // Nobody waits for this one — it is a property changing — so anything it throws would go
        // past the window rather than into its status line.
        if (!_loading) _ = Reloading();
    }

    private async Task Reloading()
    {
        try { await ReloadAsync(); }
        catch (Exception ex) { Status = Describe(ex, "opening the game that was chosen"); }
    }

    /// What the setting is doing right now, said where it is set. A path that is not a game is the
    /// one mistake this invites, and it is worth hearing about before the tree comes up empty.
    public string GameDescribed => GameDirectory.Length == 0
        ? "Found through Steam."
        : Game is { } open && string.Equals(
              Path.GetFullPath(open.RootDirectory), Path.GetFullPath(GameDirectory),
              StringComparison.OrdinalIgnoreCase)
            ? $"Open: {open.BundlesDirectory}"
            : "Not open. It has to be the folder holding the game's own *_Data directory.";

    /// Whether a built pack is signed and scrambled unless its own manifest says otherwise.
    ///
    /// Starts where ToolSettings starts, and has to: the editor is told what this answers only when
    /// it changes, so a saved 'off' read into a field that was already 'off' would tell it nothing
    /// and leave its list naming the wrong answer.
    [ObservableProperty] private bool _protectPacks = true;

    /// Whether a weapon is shown in the arms that hold it. See ToolSettings.ShowArms.
    [ObservableProperty] private bool _showArms;

    /// Shown again with the answer that changed, since what is on screen was built for the old one.
    partial void OnShowArmsChanged(bool value)
    {
        Remember();
        if (Detail?.SelectedNode is { } showing) ShowPreview(showing);
    }

    partial void OnProtectPacksChanged(bool value)
    {
        // The editor names this answer in its own list, so it has to hear when it changes.
        Editor.ProtectsByDefault = value;
        Remember();
    }

    /// Whether writing to the game is done for speed rather than for size. See BundlePacking.
    [ObservableProperty] private bool _fasterApplies;

    partial void OnFasterAppliesChanged(bool value)
    {
        Manager.Packing = Packing;
        Remember();
    }

    public BundlePacking Packing => FasterApplies ? BundlePacking.Faster : BundlePacking.Smaller;

    /// Whether bundles are rebuilt one at a time, to use less memory. See ModApplier.AtOnce.
    [ObservableProperty] private bool _lighterApplies;

    partial void OnLighterAppliesChanged(bool value)
    {
        Manager.AtOnce = AtOnce;
        Remember();
    }

    public int AtOnce => LighterApplies ? 1 : 4;

    /// How many megabytes of bundles browsing may keep unpacked. Takes effect when the game is next
    /// read. See BundleUnpacker.
    [ObservableProperty] private int _readMemory = 1024;

    partial void OnReadMemoryChanged(int value) => Remember();

    public IReadOnlyList<MemoryChoice> ReadMemoryChoices { get; } =
    [
        new(0, "None — read from disk"), new(1024, "1 GB"), new(2048, "2 GB"), new(4096, "4 GB"),
    ];

    /// The key packs are signed with, shown so an author can publish it: somebody who knows this
    /// can tell a pack you built from one re-signed by whoever altered it.
    public string AuthorFingerprint
    {
        get
        {
            try
            {
                using var mine = PackAuthor.Mine(SettingsHome);
                return $"your key: {mine.Fingerprint}";
            }
            catch (Exception e) when (e is IOException or System.Security.Cryptography.CryptographicException)
            {
                return $"no key could be made: {e.Message}";
            }
        }
    }

    public ObservableCollection<LanguageOption> Languages { get; } = [];

    /// Shown in the options window, because where a portable tool keeps its state is worth being
    /// able to see rather than having to guess.
    public string SettingsPath => ToolSettings.PathIn(SettingsHome ?? ModStore.DefaultHome());

    /// Somewhere other than beside the executable to keep preferences.
    ///
    /// Only the self-test sets it. That test drives the asset filter, the language and the alpha
    /// toggle, and every one of those is written back the moment it moves — so a run rewrote the
    /// author's settings file as a side effect, and left their tree filter switched off.
    public string? SettingsHome { get; set; }

    /// Which workspace tab is showing: browse, editor, manager.
    [ObservableProperty] private int _workspace;

    public ObservableCollection<WeaponListItem> Weapons { get; } = [];

    /// The skins of the selected weapon, and which of them extraction should also write out.
    ///
    /// One at a time rather than all of them: a weapon carries up to a dozen, each with its own
    /// materials and textures and sometimes a whole model, and an author works on one.
    public ObservableCollection<SkinChoice> SkinChoices { get; } = [];

    [ObservableProperty] private SkinChoice? _chosenSkin;

    /// Whether this weapon has any, so the picker stays out of the way of the ones that do not.
    public bool HasSkins => SkinChoices.Count > 1;

    /// Which sort of thing the list is showing.
    ///
    /// Weapons are what the tool was built for and what it opens on; the rest of the game's
    /// cosmetics are the same arrangement under different roots, so switching kinds changes which
    /// registry the list is filled from and nothing else.
    public ObservableCollection<ItemKind> Kinds { get; } = [];

    /// Whether there is more than one kind to choose between, so the picker stays out of the way of
    /// a game that turns out to have nothing but weapons.
    public bool OffersKinds => Kinds.Count > 1;

    [ObservableProperty] private ItemKind _kind = ItemKinds.Weapon;

    partial void OnKindChanged(ItemKind value)
    {
        if (_catalogs is null || _loading) return;

        // The search is about the list, and the list has just been replaced. Kept when it still
        // finds something, cleared when it would leave somebody looking at nothing.
        Show(Found());
        if (Weapons.Count == 0 && Search.Length > 0) Search = "";
        Selected = Weapons.FirstOrDefault();
    }

    public string Title => _catalogs is null
        ? "PGAssetTool"
        : $"PGAssetTool — {Weapons.Count} of {_catalogs.Of(Kind).Count} {Kind.Name.ToLowerInvariant()}s";

    public async Task LoadAsync()
    {
        // Preferences are read before the game, so the first catalog load is already in the right
        // language rather than being read once in English and then again. Nothing is resolved yet,
        // so the change handlers below find nothing to rebuild.
        ErrorLog.Home = SettingsHome;
        _settings = ToolSettings.Load(SettingsHome);
        _loading = true;
        Language = _settings.Language;
        ReplaceableOnly = _settings.ReplaceableOnly;
        OpaqueTextures = _settings.OpaqueTextures;
        Author = _settings.Author;
        GameDirectory = _settings.GameDirectory;
        Editor.SideBySide = _settings.SideBySide;
        Editor.Linked = _settings.LinkedPreviews;
        Manager.ConfirmChanges = _settings.ConfirmChanges;
        Manager.TileSize = _settings.TileSize;
        Manager.Order = (ModOrder)_settings.ModOrder;
        ProtectPacks = _settings.ProtectPacks;
        ShowArms = _settings.ShowArms;
        FasterApplies = _settings.FasterApplies;
        LighterApplies = _settings.LighterApplies;
        ReadMemory = _settings.ReadMemory;
        MaskUnusedTextures = _settings.MaskUnusedTextures;
        UiScale = _settings.UiScale;
        Manager.Packing = Packing;
        _loading = false;

        try
        {
            await Task.Run(() =>
            {
                // Named, or found. Only one store can be asked where it put the game, and it is not
                // the only one selling it — so a path that was given is taken as given.
                _installation = GameDirectory is { Length: > 0 } chosen
                    ? GameInstallation.Open(chosen)
                    : GameInstallation.OpenDetected();

                var game = _installation;

                // The game as shipped, not as modded. Read as it is, browsing an installed weapon
                // shows the mod and extracting it writes the mod's bytes out as the weapon's own —
                // and since a pack carries only what changed after the extract, an author can build
                // a pack around somebody else's installed work without either of them seeing it.
                // It also stops a protected pack being lifted straight back out of the game it went
                // into, which is the weaker reason. Mods are checked in the game.
                _bundles = new BundleSet(game, originals: new ModStore(game).OriginalOf)
                {
                    UnpackBudget = ReadMemory * 1024L * 1024,
                };
                _catalogs = GameCatalogs.Load(_bundles, string.IsNullOrEmpty(Language) ? _settings.Language : Language);
                _resolver = new WeaponResolver(_bundles, _catalogs);
            });

            Languages.Clear();
            foreach (var (bundle, name) in GameCatalogs.Languages(_bundles!))
                Languages.Add(new LanguageOption(bundle, name));

            // Only the kinds the game turns out to have anything in, and the one being browsed put
            // back by name: a reload rebuilds these objects, and the one held before it is not the
            // one in the list afterwards.
            var was = Kind.Name;
            // Every kind: they resolve, preview, extract, pack and install through the same code as
            // weapons, and the command line answers for them the same way. Measured over all of
            // them, every non-weapon kind resolves a bundle, a model and an icon for every item it
            // has, in 6 to 19ms each against a weapon's 38.
            Kinds.Clear();
            foreach (var kind in _catalogs!.Kinds) Kinds.Add(kind);
            OnPropertyChanged(nameof(OffersKinds));

            // Put back quietly. Changing kinds by hand means "show me these instead", and takes the
            // selection to the top of the new list; a reload is the opposite — it happens under
            // somebody in the middle of something, and putting the selection back is the caller's,
            // which is why ReloadAsync remembers what was selected before it started.
            var quiet = _loading;
            _loading = true;
            Kind = Kinds.FirstOrDefault(k => k.Name == was) ?? ItemKinds.Weapon;
            _loading = quiet;

            // Whatever is being searched for, still. A reload happens under somebody who is in the
            // middle of something — building a pack reloads the game — and putting the whole list
            // back while the search box still says what they typed reads as the search breaking.
            Show(Found());

            Editor.Rescan(WorkspaceRoot);

            // The manager files mods by weapon and by skin, and both are things only the catalogues
            // can name — and name differently in each language. Searching goes the same way: the
            // weapon list's own search, so the manager finds a weapon by any of its names too.
            Manager.Names = Naming;
            Manager.FindWeapons = text => _catalogs is not { } catalogs
                ? null
                : catalogs.Items.Search(text, catalogs.Names).Select(w => w.GameNumber).ToHashSet();

            Manager.Refresh();
            UpdateNotice = UpdateNoticeFor(_installation!, _bundles!);
            if (ErrorLog.UntoldCrash() is { } crash)
            {
                CrashReport = crash;
                CrashNotice = $"The last session closed unexpectedly. What happened is written in {Path.GetFileName(crash)} — sending that file with a report is what makes it fixable.";
            }
            var others = _catalogs.Kinds.Where(k => k != ItemKinds.Weapon).Sum(k => _catalogs.Of(k).Count);
            Status = others > 0
                ? $"{_catalogs.Items.Count} weapons, {others} other items in {_catalogs.Kinds.Count - 1} kinds"
                : $"{_catalogs.Items.Count} weapons";
            OnPropertyChanged(nameof(GameDescribed));
        }
        catch (Exception ex)
        {
            Status = Describe(ex, "opening the game");
        }
        finally
        {
            Busy = false;
        }
    }

    /// Re-reads the game from scratch, for after a game update or an external edit.
    public async Task ReloadAsync()
    {
        // By kind and id rather than by number: only weapons are numbered, and every other item's
        // number is the same nought, so a reload put the selection on the first hat in the list.
        var wanted = Selected?.Record is { } record ? (record.Kind, record.Slug) : ((ItemKind, string)?)null;

        // Closed under the lock everything reads through. A preview or a resolve that is running
        // right now holds it and is reading the BundleSet on a thread of its own; disposing it from
        // here left that thread reading a closed file, and this is reached from a menu item and
        // from the game folder being changed in the options — both of them while somebody is
        // looking at something.
        await _reading.WaitAsync();
        try
        {
            Editor.Dispose();
            CloseReader();
        }
        finally
        {
            _reading.Release();
        }

        Busy = true;
        Status = "Reloading…";

        await LoadAsync();

        // Put the reader back where it was, so a reload is not also a loss of place.
        if (wanted is var (kind, slug))
            Selected = Weapons.FirstOrDefault(w => w.Record.Kind == kind
                && string.Equals(w.Record.Slug, slug, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnSearchChanged(string value)
    {
        if (_catalogs is null) return;
        Show(Found());
    }

    /// What the list should hold: the kind being browsed, narrowed by what is being searched for.
    ///
    /// Weapons search through their own index, which knows every name in every language and the
    /// in-game number. Everything else is matched on what it has — its id and the name in the
    /// language on screen — which is enough, because those names are what the picker is showing.
    private IEnumerable<WeaponRecord> Found()
    {
        if (_catalogs is null) return [];

        var items = _catalogs.Of(Kind);
        if (Search.Length == 0) return items;

        if (Kind == ItemKinds.Weapon) return _catalogs.Items.Search(Search, _catalogs.Names);

        return items.Where(r =>
            r.Slug.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || r.Tag.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || _catalogs.Localization.Translate(r.LocalizationKey) is { } named
               && named.Contains(Search, StringComparison.OrdinalIgnoreCase));
    }

    private void Show(IEnumerable<WeaponRecord> records)
    {
        Weapons.Clear();
        foreach (var record in records)
            Weapons.Add(new WeaponListItem(record, _catalogs!.Localization.Translate(record.LocalizationKey)));
        OnPropertyChanged(nameof(Title));
    }

    async partial void OnSelectedChanged(WeaponListItem? value)
    {
        if (value is null || _resolver is null) { Detail = null; return; }

        Busy = true;
        Status = $"Resolving {value.Name}…";
        try
        {
            // One reader, one weapon at a time: clicking down the list must not have two resolves
            // in the same BundleSet.
            await _reading.WaitAsync();
            try
            {
                var tree = await Task.Run(() => _resolver.Resolve(value.Record));
                Preview.Clear();
                _tree = tree;
                _clips = null;
                _modelClips.Clear();

                // What was learned about the last weapon's skins goes with the tree it was learned
                // into: every row carries its own, and the rows are about to be thrown away.
                Detail = new WeaponDetailViewModel(tree, ReplaceableOnly) { NodeSelected = ShowPreview, NodeOpened = OpenSkin };
                Status = $"{value.Name} — {tree.PrefabAssets.Count} objects in {tree.PrefabBundle ?? "no bundle"}";

                // Straight to the weapon itself. The tree opens on seventy-odd rows and the one
                // thing everybody has come to see is the gun, dressed as the game dresses it —
                // waiting to be found among the meshes is a click that always has the same answer.
                Detail.SelectedNode = WeaponMesh();
            }
            finally
            {
                _reading.Release();
            }
        }
        catch (Exception ex)
        {
            Status = Describe(ex, "reloading");
            Detail = null;
        }
        finally
        {
            Busy = false;
        }
    }

    /// Loads whatever the clicked node stands for, if it is something that can be looked at.
    ///
    /// Reading goes through the same lock as resolving: one BundleSet, one reader, and clicking
    /// quickly down a tree must not put two decodes into it at once.
    /// Raised when the search box should take focus. The view owns the control; the model only
    /// knows that someone asked.
    public event Action? SearchRequested;

    /// Writes the whole weapon out as a workspace: every replaceable file plus a manifest that
    /// already names each target, so the directory is ready to edit and pack without anything
    /// being wired up by hand.
    [RelayCommand]
    private async Task ExtractWeapon()
    {
        if (_tree is null || _bundles is null) { Status = "Select an item first."; return; }

        var tree = _tree;
        if (ReferenceEquals(ChosenSkin, SkinChoice.All))
        {
            await ExtractEveryLook(tree);
            return;
        }

        await RunExclusively($"extracting the {tree.Record.Kind.Name.ToLowerInvariant()}", async () =>
        {
            var version = GameVersion.Of(_bundles.Context, _bundles.Game);

            var skin = ChosenSkin?.Id;
            var export = await Task.Run(() =>
                new WeaponExporter(_bundles)
                {
                    Opaque = _settings.OpaqueTextures, Skin = skin,
                    MaskUnused = _settings.MaskUnusedTextures,
                }
                    .ExportAsWorkspace(tree, WorkspaceRoot, _settings.Author, version));

            LastExport = export.Directory;
            Editor.Rescan(WorkspaceRoot);
            Manager.Refresh();
            // By file: one picture can be written to two assets, and that is still one file.
            Status = $"{export.Assets.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()} files written to {export.Directory}"
                + (export.Skipped.Count > 0 ? $", {export.Skipped.Count} skipped" : "")
                + (export.Notes is { Count: > 0 } notes ? ".  " + string.Join("  ", notes) : "");
        });
    }

    /// Writes the item as it comes and then each of its skins, a workspace apiece. See
    /// WeaponExporter.ExportEveryLook.
    private async Task ExtractEveryLook(WeaponTree tree)
    {
        await RunExclusively($"extracting the {tree.Record.Kind.Name.ToLowerInvariant()} and its skins", async () =>
        {
            var version = GameVersion.Of(_bundles!.Context, _bundles.Game);
            // Made here, so what it reports arrives on the window's thread.
            var progress = new Progress<string>(said => Status = said);
            IProgress<string> report = progress;

            var looks = await Task.Run(() => WeaponExporter.ExportEveryLook(
                _bundles, tree, WorkspaceRoot, _settings.Author, version,
                opaque: _settings.OpaqueTextures, maskUnused: _settings.MaskUnusedTextures,
                progress: (at, of, name) => report.Report($"Extracting {at} of {of}: {name}…")));

            if (looks.LastOrDefault(l => l.Export is not null)?.Export is { } last) LastExport = last.Directory;
            Editor.Rescan(WorkspaceRoot);
            Manager.Refresh();

            var failed = looks.Where(l => l.Export is null).ToList();
            Status = $"{looks.Count - failed.Count} of {looks.Count} written, each a workspace of its own, in {WorkspaceRoot}"
                + (failed.Count > 0
                    ? ". Not written: " + string.Join("; ", failed.GroupBy(f => f.Failed)
                        .Select(g => $"{string.Join(", ", g.Select(f => f.Name))} ({g.Key})"))
                    : ".");
        });
    }

    /// Writes out the one object the tree has selected, for when the whole weapon is not wanted.
    [RelayCommand]
    private async Task ExtractSelected()
    {
        if (Detail?.SelectedNode is not { Class: not null, Bundle.Length: > 0 } node)
        {
            Status = "Select an asset in the tree first.";
            return;
        }

        await RunExclusively("extracting the selected asset", async () =>
        {
            var directory = Path.Combine(WorkspaceRoot, "assets");
            var written = await Task.Run(() =>
            {
                // The same refusal a whole weapon gets: a bundle another copy of the tool modded is,
                // from here, just what the game holds now. See BundleSet.ReadsAsShipped.
                if (_bundles!.Altered([node.Bundle]) is { Count: > 0 })
                    throw new InvalidOperationException(
                        $"'{node.Bundle}' has been changed by something other than this copy of the tool, which has "
                        + "no original to read instead. Extracting would write that change out as the game's own.");

                if (AssetPreview.Locate(_bundles!, node.Bundle, node.Class.Value, node.PathId, node.Label)
                    is not var (file, info)) return null;

                return new AssetExporter(_bundles!) { Opaque = _settings.OpaqueTextures }
                    .Export(node.Bundle, file, info, directory);
            });

            if (written is null || written.Count == 0) { Status = $"'{node.Label}' could not be written."; return; }

            LastExport = directory;
            Status = $"{string.Join(", ", written.Select(w => Path.GetFileName(w.Path)))} -> {directory}";
            Editor.Rescan(WorkspaceRoot);
            Manager.Refresh();
        });
    }

    /// Runs something that rewrites the game with the reader put down, then opens it again.
    ///
    /// The bundles are held open for browsing and applying rewrites those same files. A separate
    /// process never had to care; a window that browses and installs does.
    ///
    /// Under the same lock as every other use of that reader. Closing it is not enough on its own:
    /// a preview or a workspace comparison already part-way through a bundle keeps that file open
    /// however firmly the reader is disposed underneath it, and the write that follows then fails
    /// with the file in use — intermittently, and only ever on whichever bundle was last looked at.
    private async Task WithReaderClosed(Func<Task> work)
    {
        await _reading.WaitAsync();
        try
        {
            CloseReader();
            try { await work(); }
            finally { await LoadAsync(); }
        }
        finally
        {
            _reading.Release();
        }
    }

    private void CloseReader()
    {
        _bundles?.Dispose();
        (_bundles, _catalogs, _resolver, _tree, _clips) = (null, null, null, null, null);
        Detail = null;
        Preview.Clear();
    }

    /// Builds a pack from each workspace, and installs them when asked.
    ///
    /// Building and applying are one gesture because an author repeats them: edit, pack, apply,
    /// look at it in the game. Building alone stays separate for packs meant to be handed out.
    ///
    /// Several at once install together rather than one after another. Installing is a whole-game
    /// reconcile — restore everything modified, reapply everything enabled — so doing it per pack
    /// repeats that work per pack and passes through states with some of them applied and not the
    /// rest. A pack that will not build does not stop the others; it is reported by name.
    private async Task BuildPack(IReadOnlyList<string> workspaces, bool install)
    {
        await RunExclusively(install ? "building and applying the packs" : "building the packs", async () =>
        {
            var built = new List<PackResult>();
            var refused = new List<string>();

            // Made once for the batch, and only if something in it asks to be signed. Reading the
            // key is cheap; making one the first time is not something to do unasked.
            PackAuthor? signer = null;

            foreach (var workspace in workspaces)
            {
                // Named after the mod, not after the directory it was built in: renaming a
                // working folder should not rename what is about to be handed out.
                var manifest = Core.Pack.Workspace.Read(workspace);
                var output = PackBuilder.OutputFor(workspace, manifest);

                var protect = manifest.Protect ?? ProtectPacks;
                if (protect) signer ??= PackAuthor.Mine(SettingsHome);
                var with = protect ? signer : null;
                try { built.Add(await Task.Run(() => PackBuilder.Build(workspace, output, with))); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    refused.Add($"{Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace))}: {ex.Message}");
                }
            }

            signer?.Dispose();

            var trouble = refused.Count > 0 ? "  " + string.Join("  ", refused) : "";
            Status = $"{built.Count} pack(s), {built.Sum(b => b.Operations)} operation(s), "
                + $"{built.Sum(b => b.Bytes):N0} bytes" + trouble;

            if (!install || built.Count == 0) return;
            await ApplyPacks(built.Select(b => b.Path).ToList(), trouble, $"{built.Count} pack(s) built. ");
        });
    }

    /// Installs packs that came from somewhere other than a workspace — dropped on the window,
    /// most likely, which is how somebody else's mod gets in.
    ///
    /// The same reconcile the rest of the tool installs through, so a pack from outside is subject
    /// to the same backups, the same ledger and the same undo as one built here.
    /// Installs packs from outside — dropped on the window, or handed over some other way.
    ///
    /// Their seals are read before anything is written, which is the only moment saying so is any
    /// use. A pack that has been changed since it was signed still installs if that is what somebody
    /// wants: a pack is a description of changes, and whoever holds one may install it. Read when a
    /// row is selected in the manager instead — after the game has been rewritten — and the one fact
    /// worth having before deciding arrives only after the decision.
    public Task InstallPacks(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return Task.CompletedTask;

        var wrong = paths
            .Select(p => (File: p, Seal: PackFile.Inspect(p)))
            .Where(x => x.Seal.Wrong)
            .ToList();

        if (wrong.Count == 0) return Install(paths);

        Manager.Asking = new Confirmation(
            wrong.Count == 1
                ? "This pack is not what its author signed"
                : $"{wrong.Count} of these packs are not what their authors signed",
            string.Join("\n", wrong.Select(w => $"{Path.GetFileName(w.File)} — {w.Seal.Describe}"))
                + "\n\nA pack carrying a signature it no longer matches has been changed since it was "
                + "built, by its author or by somebody else. Nothing here can tell which. Install it?",
            () => Install(paths));

        return Task.CompletedTask;
    }

    private Task Install(IReadOnlyList<string> paths)
        => RunExclusively("installing the packs", () => ApplyPacks(paths, "", ""));

    /// <param name="trouble">What went wrong earlier in the same gesture, to be repeated at the end.</param>
    /// <param name="sofar">What has already happened, for the message that says why nothing more will.</param>
    private async Task ApplyPacks(IReadOnlyList<string> paths, string trouble, string sofar)
    {
        if (_bundles is null) { Status = "The game is not open."; return; }
        var game = _bundles.Game;

        // The manager has always refused to write while the game is running; this path never
        // did, and it is the one an author uses over and over. The packs are built and on disk,
        // so nothing is lost by stopping here — only the writing waits.
        if (GameProcess.IsRunning(game))
        {
            Status = sofar + "The game is running, and rewriting its bundles now can leave a "
                + "half-written file behind — close it, then apply.";
            return;
        }

        CloseReader();

        ReconcileResult result;
        try
        {
            var store = new ModStore(game);
            string version;
            using (var reading = new BundleSet(game))
                version = GameVersion.Of(reading.Context, game) ?? GameVersion.Unknown;

            result = await Task.Run(() => new ModApplier(game, store) { Packing = Packing, AtOnce = AtOnce }.Install(paths, version));
        }
        finally
        {
            await LoadAsync();
        }

        // The warning is the reason applying from here is worth having: it is the moment an
        // author can still decide that reaching another weapon was not what they meant.
        var shared = result.Shared.Count > 0
            ? "  " + string.Join("  ", result.Shared.Select(s => s.ToString()))
            : "";

        var moved = result.Moved.Count > 0
            ? "  Followed into another bundle: " + string.Join("  ", result.Moved)
            : "";

        Status = $"{result.Applied.Count} applied, {result.Failed.Count} failed"
            + (result.Unchanged.Count > 0 ? $", {result.Unchanged.Count} bundle(s) left alone." : ".")
            + (result.Failed.Count > 0 ? "  " + string.Join("  ", result.Failed) : "") + moved + shared + trouble;
    }

    /// Where extraction writes. Beside the tool unless the settings say otherwise, because a
    /// window has no meaningful current directory to fall back on.
    /// Somewhere other than the author's workspace to extract into.
    ///
    /// Only the self-test sets it. That test writes, edits, packs and installs for real, and doing
    /// so among directories a person is keeping actual work in means its scratch extracts sit in
    /// the same list as theirs — and, worse, that whichever workspace it happened to select was
    /// sometimes one of theirs.
    public string? WorkspaceOverride { get; set; }

    public string WorkspaceRoot => WorkspaceOverride ?? _settings.WorkspaceIn(SettingsHome ?? ModStore.DefaultHome());

    /// The installation, once it is open. The self-test uses it to put the game back.
    public GameInstallation? Game => _installation;

    /// The last directory written to, so the view can offer to open it.
    [ObservableProperty] private string? _lastExport;

    /// Opens the workspace being worked on, in Explorer.
    ///
    /// The selected one first: the reason to open a folder is almost always to put a file into the
    /// one on the screen, and after a session of editing, the last extraction is whichever happened
    /// to be made most recently rather than the one anybody is looking at.
    [RelayCommand]
    private void OpenWorkspace()
    {
        var path = Editor.SelectedWorkspace?.Directory ?? LastExport ?? WorkspaceRoot;
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = ErrorLog.Said(ex, "opening the workspace folder");
        }
    }

    /// Names what was being done, because a bare exception message says nothing about which of the
    /// several things that can fail here did — "Value cannot be null" on its own is unactionable.
    private static string Describe(Exception ex, string what)
        => ErrorLog.Said(ex, what);

    /// Long jobs share the one reader and say so while they run.
    private async Task RunExclusively(string what, Func<Task> work)
    {
        Busy = true;
        await _reading.WaitAsync();
        try { await work(); }
        catch (Exception ex) { Status = Describe(ex, what); }
        finally { _reading.Release(); Busy = false; }
    }

    /// Puts the keyboard in the search box of whichever workspace is showing, and does nothing in
    /// one that has none.
    ///
    /// A shortcut that means "find" is about what is in front of you: switching to Browse first
    /// would have Ctrl+F in the manager — which has a search of its own — throw away the shelf
    /// being looked at to search the weapon list instead.
    [RelayCommand]
    private void FocusSearch() => SearchRequested?.Invoke();

    [RelayCommand]
    private Task Reload() => ReloadAsync();

    /// Toggled through a command rather than a two-way binding on the menu item. A checkable
    /// MenuItem owns its own IsChecked, and letting it write back means the value the menu happens
    /// to hold when it is first realized wins over the one the model started with.
    [RelayCommand]
    private void ToggleReplaceableOnly() => ReplaceableOnly = !ReplaceableOnly;

    [RelayCommand]
    private void ToggleAlpha()
    {
        // The pane in front, rather than the browse one wherever you are: the editor is where a
        // texture is being worked on, which is when the question comes up.
        if (Showing is { CanToggleAlpha: true } preview) preview.ShowAlpha = !preview.ShowAlpha;
    }

    [RelayCommand]
    private void ShowBrowse() => Workspace = BrowseTab;

    [RelayCommand]
    private void ShowEditor() => Workspace = EditorTab;

    [RelayCommand]
    private void ShowManager() => Workspace = ManagerTab;

    public const int BrowseTab = 0;
    public const int EditorTab = 1;
    public const int ManagerTab = 2;

    /// The pane a key press means, which depends on which workspace is showing.
    public PreviewViewModel? Showing => Workspace switch
    {
        BrowseTab => Preview,
        EditorTab => Editor.Shown,
        _ => null,
    };

    partial void OnOpaqueTexturesChanged(bool value) => Remember();

    /// Re-reads the workspace directory. Files there are edited by other programs, and a watcher
    /// does miss things, so asking outright stays available.
    [RelayCommand]
    private void RescanWorkspaces() => Editor.Rescan(WorkspaceRoot);

    // Typed rather than picked, so it lands here on every keystroke. The file is a few hundred
    // bytes and writing it costs nothing worth debouncing for.
    partial void OnAuthorChanged(string value) => Remember();

    partial void OnReplaceableOnlyChanged(bool value)
    {
        Remember();
        if (_tree is null) return;

        Preview.Clear();
        Detail = new WeaponDetailViewModel(_tree, value) { NodeSelected = ShowPreview, NodeOpened = OpenSkin };
    }

    /// Changing the language means every name in the catalogs, so the game is read again.
    /// Only the translation table depends on the language. The items, the lookup table, the skins
    /// and the search index do not, so changing which name is displayed re-reads one small bundle
    /// rather than throwing the reader away and starting over.
    partial void OnLanguageChanged(string value)
    {
        // Nothing is not a language. A picker whose list is being refilled or rebuilt can pass on an
        // empty choice, and taken at its word that was read as the name of a translation table and
        // failed with "Value cannot be null (Parameter 'key')" in the status bar. Whatever is in use
        // stays in use, and is what gets saved, until a real one is chosen.
        if (string.IsNullOrEmpty(value)) return;

        Remember();
        if (_loading || _bundles is null || _catalogs is null) return;

        try
        {
            _catalogs = _catalogs.WithLanguage(_bundles, value);
            _resolver = new WeaponResolver(_bundles, _catalogs);
            Show(Found());

            // The manager's headings are weapon and skin names too, and they are read out of the
            // table that has just changed rather than out of what a pack was called when it was
            // built — so this is where they follow.
            Manager.Relabel();
        }
        catch (Exception ex)
        {
            Status = Describe(ex, "changing the language");
        }
    }

    /// What a pack's weapon and skin are called in the language the catalogues are open in.
    ///
    /// Looked up by number and by skin id, not by anything the pack carries: the pack was built in
    /// whatever language its author was using, and some carry nothing to translate at all. What the
    /// pack recorded is the last resort — better than a blank, and the honest answer for a weapon
    /// this installation does not have.
    private (string? Name, string? Variant) Naming(Core.Pack.PackSubject subject)
    {
        if (_catalogs is not { } catalogs) return (null, null);

        // A weapon by its number, anything else by the id the game knows it as — the id the pack
        // was filed under, or failing that the prefab it was made from, which for everything but a
        // weapon is the same string in its own capitals. The keys the pack recorded are the last
        // resort, for an item this installation does not have.
        var record = subject.Kind == Core.Pack.PackKind.Weapon
            ? catalogs.Items.ByGameNumber(subject.Number)
            : OtherNamed(catalogs, subject);

        var variant = record is null || subject.Variant.Length == 0
            ? null
            : catalogs.Skins.ForWeapon(record.Index).FirstOrDefault(s =>
                string.Equals(s.Id, subject.Variant, StringComparison.OrdinalIgnoreCase));

        return (Say(record?.LocalizationKey ?? subject.NameKey),
                Say(variant?.LocalizationKey ?? subject.VariantKey));

        string? Say(string? key)
            => key is { Length: > 0 } ? catalogs.Localization.Translate(key) : null;
    }

    /// An item that is not a weapon, by what a pack recorded of it.
    private static WeaponRecord? OtherNamed(GameCatalogs catalogs, Core.Pack.PackSubject subject)
    {
        if (ItemKinds.ByName(subject.Kind) is not { } kind || kind == ItemKinds.Weapon) return null;

        var of = catalogs.Of(kind);
        return of.FirstOrDefault(r => string.Equals(r.Slug, subject.Prefab, StringComparison.OrdinalIgnoreCase))
            ?? of.FirstOrDefault(r => string.Equals(r.Slug, subject.Id, StringComparison.OrdinalIgnoreCase));
    }

    private void Remember()
    {
        if (_loading) return;
        _settings = _settings with
        {
            Language = string.IsNullOrEmpty(Language) ? _settings.Language : Language,
            ReplaceableOnly = ReplaceableOnly, OpaqueTextures = OpaqueTextures,
            Author = Author.Trim(), GameDirectory = GameDirectory.Trim(),
            SideBySide = Editor.SideBySide, LinkedPreviews = Editor.Linked,
            ConfirmChanges = Manager.ConfirmChanges,
            TileSize = Manager.TileSize, ModOrder = (int)Manager.Order, ProtectPacks = ProtectPacks,
            ShowArms = ShowArms,
            FasterApplies = FasterApplies, LighterApplies = LighterApplies, ReadMemory = ReadMemory,
            MaskUnusedTextures = MaskUnusedTextures, UiScale = UiScale,
        };
        // A folder that cannot be written to is not an IOException — it is its own kind — and this
        // runs from a property changing, which is to say from somebody ticking a box. The tool
        // installed somewhere it may not write took the window down on the first tick.
        try { _settings.Save(SettingsHome); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Record(e, "saving the settings");
        }
    }

    /// A texture to put on the next mesh shown, if it is on the list when that mesh arrives.
    ///
    /// Set rather than applied, because it takes effect after the model does: showing a mesh puts
    /// back the texture that mesh was last left wearing, so a choice made before it lands is
    /// undone by it.
    private TextureChoice? _asked;

    /// The row standing for one of the meshes that were walked. Null if the filter took it out.
    private static TreeNode? RowFor(IEnumerable<TreeNode> rows, AssetNode? mesh)
        => mesh is null
            ? null
            : AllNodes(rows).FirstOrDefault(n => n.Class == AssetClassID.Mesh && n.PathId == mesh.PathId);

    /// The weapon's own model. Rows read out of a skin's model are left out: those are that skin.
    private TreeNode? WeaponMesh()
        => Detail is not { } detail || _tree is null
            ? null
            : RowFor(AllNodes(detail.Roots).Where(n => n.Within is null), _tree.MainMesh);

    /// Whether a mesh of the prefab is the player's arms rather than the item.
    ///
    /// Three names, because the game uses three: `Arms_Mesh` (and `ARMS_Mesh`, and `Arms_Mesh001`
    /// where the model was exported beside another of the same name), which nearly every weapon
    /// prefab carries; `FPS_PLAYER_Arm_left` and `_Right`, a pair modelled separately; and
    /// `player001`/`player002`, which is #44 Mauser's name for the same pair. The number on the end
    /// is the exporter's and means nothing, so it is taken off before the name is read — but only
    /// off the end, and never loosely enough to catch an item called after its own arms: an
    /// avatar's model can be `avatar_ programmer_arms_mesh`, and that one *is* the item.
    private static bool AreArms(string name)
        => name.AsSpan().TrimEnd("0123456789")
                .Equals(Core.Weapons.WeaponResolver.Arms, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("FPS_PLAYER_Arm", StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith("player", StringComparison.OrdinalIgnoreCase)
                && name.Length > 6 && char.IsDigit(name[^1]));

    /// Whether a part of the prefab is something the item is drawn *with* rather than part of it:
    /// the shadow under a car, a collider, a pivot marker.
    ///
    /// **A part nothing dresses is one of these, nearly always.** The game draws a car's shadow
    /// through a material that makes it a shadow on the ground, and the preview has none, so it came
    /// out as the grey box a car was sitting on — 41 of 90 transports have one. Measured over 2,633
    /// items: 3,913 of the parts drawn beside a model wear a texture and 263 wear none, and the 263
    /// are shadows, colliders, `UnityPivot`, `Box001`, `firecracker`, and meshes belonging to other
    /// items entirely.
    ///
    /// **Except the ones named after the item**, which are the real parts the game simply paints
    /// some other way: 26 of the 263, among them `glider_mystery_bug`'s two wings,
    /// `scorching_split`'s chain, `avatar_party_gummy`'s hair and a dozen of
    /// `avatar_storms_overlord`'s limbs. Those are kept and drawn plain.
    ///
    /// And not even then if the name says what it is. The game calls these `_shadow`, `_shadows`,
    /// `_shade`, `_shadou` (a car spells it that way), `_collider`, `_colider` and `_holo`, and
    /// `royale_car_patriot_shadow` is named after its car as surely as a wing is. Eight items are
    /// *called* shadow — `sword_of_shadows`, `avatar_the_shadow` — and their own meshes say so all
    /// the way through; those are models on a row rather than parts beside one, so this is never
    /// asked of them.
    private static bool IsDecoration(string name, string? slug)
    {
        string[] said = ["shadow", "shadows", "shade", "shadou", "collider", "colider", "holo"];
        var stem = name.EndsWith("_mesh", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
        if (said.Any(s => stem.EndsWith(s, StringComparison.OrdinalIgnoreCase))) return true;

        // Named after the item it belongs to, which is what tells a wing from a leftover. Compared
        // without the underscores, since the two are not punctuated alike, and on the first ten
        // characters, which is enough to tell `glider_mystery_bug` from `royale_pressed_cars_01`.
        if (slug is not { Length: > 4 }) return true;

        var mine = name.Replace("_", "").ToLowerInvariant();
        var theirs = slug.Replace("_", "").ToLowerInvariant();
        var head = Math.Min(10, Math.Min(mine.Length, theirs.Length));
        return !mine.StartsWith(theirs[..head], StringComparison.Ordinal)
            && !theirs.StartsWith(mine[..head], StringComparison.Ordinal);
    }

    /// This mesh's skeleton, read under the item's own prefab where there is one to name.
    ///
    /// **Several prefabs draw the same meshes and they disagree.** A weapon's own prefab holds one
    /// arrangement and `<prefab>_info`, the one the shop shows, holds another — and `Skeleton.For`
    /// answering by mesh alone takes whichever renderer comes first in the file, which is an
    /// accident of file order. Read that way, #1's gun comes back under `Weapon1_info` and its arms
    /// under `Weapon1`: two hierarchies, so the two land a metre apart with nothing saying so.
    ///
    /// **And where the item's own root holds no copy of this mesh, the first the shop does not
    /// own.** `avatar_bronze_hero`'s parts hang under another item's root and come from Places the
    /// same way: taking them from one arrangement and the model on the row from another puts the
    /// head beside the body. The walk costs a rig per placement, which is why it is asked only when
    /// the item's own root has answered nothing.
    private Skeleton? Under(string bundle, long pathId)
        => (PrefabRoot is { } root ? Skeleton.For(_bundles!, bundle, pathId, root, whole: true) : null)
            ?? Skeleton.Every(_bundles!, bundle, pathId, whole: true)
                .FirstOrDefault(place => !Outlined(place.Under) && !Copy(place.Root))?.Skeleton
            ?? Skeleton.For(_bundles!, bundle, pathId, whole: true);

    private string? PrefabRoot
        => _tree?.PrefabPath is { Length: > 0 } path ? path[(path.LastIndexOf('/') + 1)..] : null;

    /// Everywhere this item draws a mesh, which is once per renderer that names it.
    ///
    /// **The placements under the item's own prefab root, wherever it has any.** A `_shop` or
    /// `_info` root holds the same parts arranged its own way, so taking every placement there is
    /// draws each part twice, in two places.
    ///
    /// **And where it has none, every placement that is not somebody else's.** An item's prefab and
    /// the model it is built from are not always one hierarchy: `avatar_bronze_hero` names no
    /// renderer at all, its head, body, legs and arms hanging under `avatar_lvl_65_hero`, which is
    /// itself an item — the level-65 hero the bronze, silver and gold tiers are built on. An item
    /// that has a hierarchy of its own and still reaches into another's is reaching through the
    /// closure rather than wearing what it finds: `mask_medical_mask` reaches
    /// `mask_doctor_plague_mesh`, and a medical mask is not worn with a plague doctor's. An item
    /// with no hierarchy of its own has nothing else to be, so whatever it reaches is what it is.
    ///
    /// Measured over every item: 214 parts are drawn that were not, and two are left out — the
    /// plague doctor's mask above, and one weapon reaching another's mesh the same way.
    private List<Skeleton.Placement> Places(AssetNode mesh, string bundle, bool hasOwn)
    {
        var places = Skeleton.Every(_bundles!, bundle, mesh.PathId, whole: true)
            .Where(p => !Outlined(p.Under) && !Copy(p.Root))
            .ToList();

        var own = places
            .Where(p => string.Equals(p.Root, PrefabRoot, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return own.Count > 0
            ? own
            : [.. places.Where(p => !hasOwn || !Somebody(p.Root))];
    }

    /// A pair of boots is drawn as a pair.
    ///
    /// The game puts a boot on each foot as it dresses a player, and the prefab holds one boot — or
    /// a left and a right that both stand in the same place. Measured over all 47: 44 carry one
    /// mesh and three carry two, none carries more, and every one of them is centred within a
    /// hundredth of x=0. So what the preview drew was one boot, or two boots inside each other.
    ///
    /// Nothing in the bundle says how far apart the feet go, so they are set side by side by the
    /// boot's own width. The one on the row is drawn by the preview itself and cannot be moved, so
    /// it is the other that steps across; where there is no other, it is this one mirrored, because
    /// a boot is a left or a right and a pair is not two of the same.
    private void Pair(
        TreeNode node,
        IReadOnlyList<(AssetNode Node, UnityMesh Mesh, List<Skeleton.Placement> Places)> found,
        List<(UnityMesh Mesh, Skeleton Skeleton, bool Frames)> with,
        List<PreviewImage?> wearing)
    {
        if (with.Count > 1) return;

        var row = found.FirstOrDefault(f => f.Node.PathId == node.PathId);
        if (row.Mesh is not { } standing || row.Places is not { Count: > 0 } places) return;

        var box = MeshBounds.Of(places[0].Skeleton.Placed(standing, null, 0f));
        if (box.Size.X <= 0) return;

        if (with.Count == 1)
        {
            var (mesh, skeleton, frames) = with[0];
            if (Beside(skeleton, mesh, 1f, box.Size.X, 0f) is { } aside)
                with[0] = (aside, skeleton, frames);
            return;
        }

        if (Beside(places[0].Skeleton, standing, -1f, box.Size.X, box.Centre.X) is not { } other) return;

        with.Add((other, places[0].Skeleton, true));
        wearing.AddRange(PerSubMesh(
            TexturesFor(_tree!.MeshTextures.FirstOrDefault(m => m.MeshPathId == node.PathId)), standing));
    }

    /// The same mesh a step along the item's own X, mirrored where it has to become the other foot.
    ///
    /// Moved in the prefab's space, which is where the pair stands: the mesh is taken there, moved,
    /// and brought back again, so a boot the prefab turns or scales steps along the axis it stands
    /// on rather than along whichever way its own vertices happen to be written.
    private static UnityMesh? Beside(
        Skeleton skeleton, UnityMesh mesh, float facing, float step, float centre)
    {
        if (skeleton.Home(mesh) is not { } home) return null;

        // Column-major, as everything here keeps them: x' = facing * x + step, mirrored about the
        // plane the mesh's own middle stands on so that it turns over where it is.
        float[] across =
        [
            facing, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            (facing < 0 ? 2 * centre : 0) + step, 0, 0, 1,
        ];

        return Skeleton.Moved(mesh, Matrix.Times(home, Matrix.Times(across, Matrix.Inverse(home))));
    }

    /// Which way up the model opens.
    ///
    /// A weapon by its muzzle, which the prefab's landmarks answer and the bounding box cannot.
    /// Everything else standing the way the prefab hangs it: the box would sort an avatar's height
    /// across the screen and lay it down. A mesh nothing in the prefab places has no prefab space
    /// to borrow and is left to the box. See Facing.
    private MeshRenderer.Basis Standing(TreeNode node, UnityMesh model, Skeleton? skeleton)
        => _tree?.Record.Kind == ItemKinds.Weapon || skeleton?.Home(model) is not { } home
            ? Facing.Standing(_bundles!, node.Bundle, skeleton?.Assembled(model) ?? model, node.PathId)
            : Facing.Upright(home);

    /// Whether a root is the item's own prefab root.
    private bool Mine(string root)
        => string.Equals(root, PrefabRoot, StringComparison.OrdinalIgnoreCase);

    /// The sphere the item's own parts fill, in the prefab's space.
    ///
    /// The arms are in it, so that turning them off does not move it and change what is drawn
    /// beside the model.
    private Reach? Around(
        IReadOnlyList<(AssetNode Node, UnityMesh Mesh, List<Skeleton.Placement> Places)> found)
        => Reach.Round(found
            .SelectMany(part => part.Places
                .Where(place => Mine(place.Root))
                .Select(place => place.Skeleton.Placed(part.Mesh, null, 0f))));

    /// The ball an item fills, and whether something drawn in the same space lands on it.
    private readonly record struct Reach((float X, float Y, float Z) Centre, float Radius)
    {
        public static Reach? Round(IEnumerable<UnityMesh> parts)
        {
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            var any = false;

            foreach (var part in parts)
            {
                if (part.VertexCount == 0) continue;

                var box = MeshBounds.Of(part);
                minX = Math.Min(minX, box.Centre.X - box.Size.X / 2);
                maxX = Math.Max(maxX, box.Centre.X + box.Size.X / 2);
                minY = Math.Min(minY, box.Centre.Y - box.Size.Y / 2);
                maxY = Math.Max(maxY, box.Centre.Y + box.Size.Y / 2);
                minZ = Math.Min(minZ, box.Centre.Z - box.Size.Z / 2);
                maxZ = Math.Max(maxZ, box.Centre.Z + box.Size.Z / 2);
                any = true;
            }

            var (x, y, z) = ((maxX - minX) / 2, (maxY - minY) / 2, (maxZ - minZ) / 2);
            return any
                ? new Reach((minX + x, minY + y, minZ + z), MathF.Sqrt(x * x + y * y + z * z))
                : null;
        }

        public bool Holds(UnityMesh placed)
        {
            if (placed.VertexCount == 0) return true;

            var box = MeshBounds.Of(placed);
            var away = MathF.Sqrt(
                MathF.Pow(box.Centre.X - Centre.X, 2)
                + MathF.Pow(box.Centre.Y - Centre.Y, 2)
                + MathF.Pow(box.Centre.Z - Centre.Z, 2));
            return away - box.Radius <= Radius;
        }
    }

    /// One texture per submesh of the mesh they belong to, however many the renderer named.
    ///
    /// A renderer can carry more materials than the mesh it draws has submeshes, and the surplus
    /// draws nothing: `avatar_earth` has two over a one-submesh pair of arms. The parts of an item
    /// are joined submesh by submesh and the textures follow that join, so a surplus taken as given
    /// moves every texture after it onto the wrong part — the avatar's head wears its body.
    private static IReadOnlyList<PreviewImage?> PerSubMesh(
        IReadOnlyList<PreviewImage?>? pictures, UnityMesh mesh)
    {
        var wanted = Math.Max(mesh.SubMeshes.Count, 1);
        if (pictures is null) return [.. Enumerable.Repeat((PreviewImage?)null, wanted)];
        if (pictures.Count == wanted) return pictures;

        return [.. Enumerable.Range(0, wanted).Select(at => at < pictures.Count ? pictures[at] : null)];
    }

    /// Whether any of this item's meshes is placed under the item's own prefab root.
    ///
    /// Asked of the names alone, before any rig is read: a transport comes to twenty placements and
    /// reading the rig is most of what one costs. See Skeleton.Placings.
    private bool HasOwnHierarchy(IReadOnlyList<AssetNode> meshes, string prefabBundle)
        => PrefabRoot is { } root && meshes.Any(mesh => Skeleton
            .Placings(_bundles!, mesh.Bundle.Length > 0 ? mesh.Bundle : prefabBundle, mesh.PathId)
            .Any(under => !Outlined(under)
                && under.Count > 0
                && string.Equals(under[^1], root, StringComparison.OrdinalIgnoreCase)));

    /// A prefab holding the game's own second arrangement of an item: the shop's, and the one the
    /// preview in a weapon's info panel uses.
    private static bool Copy(string root)
        => root.Length == 0
            || root.EndsWith("_shop", StringComparison.OrdinalIgnoreCase)
            || root.EndsWith("_info", StringComparison.OrdinalIgnoreCase);

    /// Whether a root is another item's prefab rather than this one's.
    private bool Somebody(string root)
        => _tree?.Record is { } record
            && !string.Equals(root, record.PrefabName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(root, record.Slug, StringComparison.OrdinalIgnoreCase)
            && ItemNames().Contains(root);

    /// Every name the game files an item under, kept per reading of the catalogues.
    private (GameCatalogs? Of, IReadOnlySet<string>? Names) _itemNames;

    private IReadOnlySet<string> ItemNames()
    {
        if (ReferenceEquals(_itemNames.Of, _catalogs) && _itemNames.Names is { } known) return known;

        IReadOnlySet<string> names = _catalogs is not { } catalogs
            ? new HashSet<string>()
            : new HashSet<string>(
                catalogs.Kinds.SelectMany(catalogs.Of).SelectMany(r => new[] { r.PrefabName, r.Slug }),
                StringComparer.OrdinalIgnoreCase);

        _itemNames = (_catalogs, names);
        return names;
    }

    /// Whether the game draws this copy of a mesh as the cel-shaded outline behind the model.
    ///
    /// An outline is a second placement of the same mesh under a `_outline` object, turned inside
    /// out by its own transform. Drawn as a surface it covers the model: `avatar_cube_the_greatest`
    /// carries one per head decoration, which is 74 parts where the avatar has 39.
    private static bool Outlined(IReadOnlyList<string> under)
        => under.Any(n => n.EndsWith("_outline", StringComparison.OrdinalIgnoreCase)
            || n.Equals("outline", StringComparison.OrdinalIgnoreCase));

    /// The rest of the prefab, to be drawn with the mesh on this row.
    ///
    /// **Most of what this finds is the item itself, not something it is held in.** Measured over
    /// 811 items: of the extra meshes something places, 1,100 are parts of the item, 149 are
    /// `Arms_Mesh` and 50 are named `vfx=`. An avatar is a head, a body and legs — 120 of 120 — a
    /// transport is a hull and its wheels, 90 of 90 and up to 29 pieces, and a pair of boots is two
    /// meshes. Shown one mesh at a time, every one of those is a fragment: `avatar_ programmer` is
    /// two slabs.
    ///
    /// So the parts of the item always come, and only the arms are the setting's business. An effect
    /// — the `vfx=` names, a muzzle flash or a glow — is left out either way: it is drawn by a
    /// particle system in the game rather than standing there, and a quad hanging in the middle of
    /// the model is not what anybody opened the row to see.
    ///
    /// **Only what something places.** A mesh no renderer in this prefab owns a skeleton for has
    /// nowhere to be put, and drawn unposed it sits at the origin.
    ///
    /// **And only what lands on the item.** A placement under another hierarchy is positioned in
    /// that hierarchy's space, which says nothing about where this item is: `magicbook_love`'s
    /// bullet comes out a metre from the book. Framing the pair is what the loss is — the book is
    /// then a third of the picture, with a speck beside it. Measured over every item: 26 placements
    /// stand clear of the ball their item fills, and they are projectiles, spare states of a part
    /// (`small_assistant_rifle` carries an empty, a half and a full battery), effects and `Box001`.
    /// Judged against the item's own hierarchy, so an item that has none keeps whatever it reaches.
    ///
    /// `Frames` is what the view is sized by: the item, never the arms. Framing by the pair shrinks
    /// every weapon to make room for a forearm reaching out of the picture.
    private (List<(UnityMesh Mesh, Skeleton Skeleton, bool Frames)> With, List<PreviewImage?> Wearing)
        Held(TreeNode node)
    {
        var with = new List<(UnityMesh, Skeleton, bool)>();
        var wearing = new List<PreviewImage?>();
        if (_tree?.PrefabBundle is not { } prefabBundle) return (with, wearing);

        var meshes = _tree.PrefabAssets
            .Where(a => a.Class == AssetClassID.Mesh
                && !a.Name.StartsWith("vfx=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var hasOwn = HasOwnHierarchy(meshes, prefabBundle);

        var found = new List<(AssetNode Node, UnityMesh Mesh, List<Skeleton.Placement> Places)>();
        foreach (var other in meshes)
        {
            var bundle = other.Bundle.Length > 0 ? other.Bundle : prefabBundle;
            try
            {
                if (AssetPreview.Locate(_bundles!, bundle, AssetClassID.Mesh, other.PathId, other.Name)
                    is not var (file, info)) continue;

                var field = _bundles!.Context.Deserialize(file, info);
                // Through AssetPreview, because a part can keep its vertices in the bundle's
                // companion file as a texture keeps its pixels, and read without one it has no
                // geometry at all. Three transports' bodies are stored that way.
                if (field is null || AssetPreview.Mesh(field, _bundles, bundle)
                    is not { VertexCount: > 0 } mesh) continue;
                if (!Skeleton.Drawn(_bundles, bundle, other.PathId)) continue;

                found.Add((other, mesh, Places(other, bundle, hasOwn)));
            }
            catch (Exception)
            {
                // One part that will not read is a part not drawn. The model is still the model.
            }
        }

        var reach = hasOwn ? Around(found) : null;

        foreach (var (other, mesh, placed) in found)
        {
            var arms = AreArms(other.Name);
            if (arms && !ShowArms) continue;

            try
            {
                // Every place the item puts this mesh, less the first of the mesh on the row: that
                // one is the model being shown, drawn by the preview itself. A wheel selected out
                // of the ten a transport places is still the other nine, which is why the row's
                // mesh is here at all rather than skipped.
                var places = reach is { } sphere
                    ? [.. placed.Where(p => Mine(p.Root) || sphere.Holds(p.Skeleton.Placed(mesh, null, 0f)))]
                    : placed;
                if (other.PathId == node.PathId) places = [.. places.Skip(1)];
                if (places.Count == 0) continue;

                // Dressed, or named after the item and so one of its own. See IsDecoration.
                var pictures = TexturesFor(_tree.MeshTextures
                    .FirstOrDefault(m => m.MeshPathId == other.PathId));
                if (pictures?.Any(p => p is not null) != true
                    && IsDecoration(other.Name, _tree.Record.Slug)) continue;

                foreach (var place in places)
                {
                    with.Add((mesh, place.Skeleton, !arms));
                    wearing.AddRange(PerSubMesh(pictures, mesh));
                }
            }
            catch (Exception)
            {
                // One part that will not read is a part not drawn. The model is still the model.
            }
        }

        if (ItemKinds.Boots.Equals(_tree.Record.Kind)) Pair(node, found, with, wearing);

        return (with, wearing);
    }

    /// One texture list for the model and everything drawn with it, in the order the submeshes end
    /// up in. A model with nothing beside it keeps the list it had, null and all.
    private static IReadOnlyList<PreviewImage?>? Alongside(
        IReadOnlyList<PreviewImage?>? mine, UnityMesh model, IReadOnlyList<PreviewImage?> theirs)
        => theirs.Count == 0 ? mine : [.. PerSubMesh(mine, model), .. theirs];

    /// What the game draws the mesh on this row with. Null for a row nothing was found for.
    ///
    /// The row's own answer first, and only then the weapon's. A skin's model can point at the
    /// weapon's own mesh rather than carrying a copy, so the same asset is on the tree twice under
    /// two sets of materials, and the weapon's answer is right for only one of them.
    private MeshTextures? SlotsFor(TreeNode node)
        => node.Within?.Wearing ?? _tree?.MeshTextures.FirstOrDefault(m => m.MeshPathId == node.PathId);

    /// Decodes the textures the renderer drawing this mesh uses, one per submesh.
    ///
    /// A Mesh asset carries no appearance of its own, so without this a weapon previews as grey
    /// geometry and the thing being judged — how a texture sits on the model — is invisible.
    private IReadOnlyList<PreviewImage?>? TexturesFor(MeshTextures? slots)
    {
        if (slots is null) return null;

        return slots.BySubMesh.Select(node =>
        {
            if (node is null) return null;
            try
            {
                if (AssetPreview.Locate(_bundles!, node.Bundle, AssetClassID.Texture2D, node.PathId, node.Name)
                    is not var (file, info)) return null;

                var field = _bundles!.Context.Deserialize(file, info);
                return field is null ? null : AssetPreview.Texture(_bundles, node.Bundle, field);
            }
            catch (Exception)
            {
                // A preview is never worth failing a selection over.
                return null;
            }
        }).ToList();
    }

    partial void OnDetailChanged(WeaponDetailViewModel? value)
    {
        OfferTextures();
        OfferSkins(value?.Tree);
    }

    /// Reads what a skin's own model is made of, the first time somebody opens its row.
    ///
    /// Deferred rather than done when the weapon is selected: a weapon carries up to a dozen skins,
    /// several of which bring a model, and walking every one of them to fill rows nobody opens
    /// would be paid on every click in the weapon list. Walking one when it is asked for is paid by
    /// whoever asked.
    /// Nothing waits for this one, and an exception in a method that returns void and awaits is not
    /// handed to anybody — it goes straight past the window and takes it down. Opening a skin reads
    /// the game, so it is exactly the kind of thing that can fail on data nothing here has seen.
    private async void OpenSkin(TreeNode node)
    {
        try { await ShowSkin(node); }
        catch (Exception ex) { Status = Describe(ex, "opening a skin"); }
    }

    private async Task ShowSkin(TreeNode node)
    {
        if (node.Skin is not { } skin) return;

        // Read once; shown every time. The reading is what a second opening must not repeat.
        if (skin.Model is { } model && !node.ModelRead)
        {
            node.ModelRead = true;
            await ReadModel(node, model);
        }

        // Its own model if it brought one, and the weapon's own if it did not — a skin that only
        // repaints is seen on the weapon's geometry, because that is where the game puts it.
        if (skin.Model is null)
        {
            _asked = skin.Materials.FirstOrDefault(m => m.Main is not null) is { Main: { } main } holder
                ? new TextureChoice(main.Name, holder.Locate(main).Bundle, main.PathId)
                : null;

            Show(WeaponMesh());
        }
        else
        {
            // Worked out the same way for the model a skin brought as for the weapon's own, which
            // matters because a skin's model carries the arms too. Named after the skin where the
            // model is the skin's own, and after the weapon where it turns out to point back at
            // the weapon's mesh; both happen on #416.
            Show(RowFor(node.Children, node.Body));
        }
    }

    /// Selects a row, and shows it again if it is already the one selected.
    private void Show(TreeNode? node)
    {
        if (Detail is not { } detail || node is null) return;

        if (ReferenceEquals(detail.SelectedNode, node)) ShowPreview(node);
        else detail.SelectedNode = node;
    }

    private async Task ReadModel(TreeNode node, SkinModel model)
    {
        try
        {
            await _reading.WaitAsync();
            try
            {
                if (_bundles is not { } bundles) return;

                var walked = await Task.Run(() =>
                {
                    var name = model.AssetPath[(model.AssetPath.LastIndexOf('/') + 1)..];
                    var file = bundles.Open(model.Bundle);
                    var root = ReferenceWalker.FindByName(
                        bundles.Context, file, AssetClassID.GameObject, name);

                    return root is null
                        ? []
                        : ReferenceWalker
                            .Closure(bundles.Context, file, root.PathId,
                                new BundleGraph(bundles).Resolve, skip: WeaponResolver.Opaque)
                            .Where(a => Replaceable.CanShow(a.Class))
                            .ToList();
                });

                // Which texture belongs on which part is decided by the renderers, and they are in
                // what was just walked — so it is asked before the filter takes them out again.
                // Without this a skin's own model came up grey and somebody had to guess.
                var dressing = _resolver is { } resolver
                    ? await Task.Run(() => resolver.TexturesFor(model.Bundle, walked))
                    : [];

                node.Body = WeaponResolver.MainMesh(model.Bundle, walked, dressing,
                    node.Skin?.Record.Id ?? "", _tree?.Record.Slug ?? "");

                // Its own clips, read now while what was walked is in hand. They are what move this
                // model; the weapon's name a hierarchy that only some skins' models copy.
                _modelClips[model.AssetPath] = await Task.Run(() => ClipsAmong(walked, model.Bundle));

                var found = walked
                    .Where(a => !ReplaceableOnly || Replaceable.Supports(a.Class))
                    .ToList();

                // Grouped the way the weapon's own objects are, so a skin's model reads as the same
                // kind of thing as the weapon it replaces.
                foreach (var group in found
                             .GroupBy(a => a.Class)
                             .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
                {
                    var into = new TreeNode(group.Key.ToString(), $"{group.Count()}")
                    {
                        IsExpanded = Replaceable.Supports(group.Key),
                    };

                    foreach (var asset in group.OrderBy(a => a.Name, StringComparer.Ordinal))
                        into.With(new TreeNode(
                            asset.Name.Length > 0 ? asset.Name : $"(unnamed {asset.PathId})",
                            asset.Bundle.Length > 0 ? asset.Bundle : model.Bundle,
                            asset.Class, asset.PathId,
                            asset.Bundle.Length > 0 ? asset.Bundle : model.Bundle)
                        {
                            // Every row carries which model it came out of, and a mesh carries what
                            // that model draws it with — the row is the only place either can be
                            // asked once two models point at the same asset.
                            Within = new ModelSource(model.AssetPath, asset.Class == AssetClassID.Mesh
                                ? dressing.FirstOrDefault(d => d.MeshPathId == asset.PathId)
                                : null),
                        });

                    node.With(into);
                }

                if (node.Children.Count == 0)
                    Status = $"'{node.Label}' brings a model and nothing in it could be read.";

                // The picker is built from the tree, and the tree has just grown. Without this a
                // skin's own model arrived with its own textures nowhere to be found, which is the
                // one thing somebody opening that row is going to want to put on it.
                OfferTextures();
            }
            finally
            {
                _reading.Release();
            }
        }
        catch (Exception ex)
        {
            Status = Describe(ex, $"reading the model behind '{node.Label}'");
        }
    }

    /// The skins this weapon has, offered so one of them can be extracted alongside its own files.
    private void OfferSkins(PGAssetTool.Core.Weapons.WeaponTree? tree)
    {
        SkinChoices.Clear();
        SkinChoices.Add(SkinChoice.None);
        if (tree?.Skins.Count > 0) SkinChoices.Add(SkinChoice.All);
        foreach (var skin in tree?.Skins ?? [])
            SkinChoices.Add(new SkinChoice(
                skin.Record.Id, skin.DisplayName ?? skin.Record.Id, skin.Model is not null));

        // Deliberately not remembered across weapons: "the Christmas one" is not a thing another
        // weapon has, and silently extracting a skin nobody asked for is worse than asking again.
        ChosenSkin = SkinChoices[0];
        OnPropertyChanged(nameof(HasSkins));
    }

    /// A chosen texture is loaded when it is first picked, not when the list is built: a weapon
    /// offers a dozen or more and almost none of them will be looked at.
    private async void OnPreviewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PreviewViewModel.ChosenTexture)) return;
        if (Preview.ChosenTexture is not { PathId: not 0 } choice) return;

        try
        {
            await _reading.WaitAsync();
            try
            {
                var loaded = await Task.Run(() =>
                {
                    if (AssetPreview.Locate(_bundles!, choice.Bundle, AssetClassID.Texture2D, choice.PathId, choice.Name)
                        is not var (file, info)) return null;
                    var field = _bundles!.Context.Deserialize(file, info);
                    return field is null ? null : AssetPreview.Texture(_bundles, choice.Bundle, field);
                });

                // Only if the choice still stands: reading a texture takes long enough for someone
                // to have moved on to another one.
                if (loaded is not null && Preview.ChosenTexture == choice) Preview.Wear(loaded);
            }
            finally { _reading.Release(); }
        }
        catch (Exception ex) { Status = Describe(ex, "loading a texture for the preview"); }
    }

    /// Every texture the weapon reaches, offered so a skin can be tried on a mesh by hand.
    ///
    /// Built from the tree as it stands, so it is built again when the tree grows: a skin's own
    /// model is read when its row is opened, and its textures are exactly the ones somebody wants
    /// on the mesh they have just been given.
    /// <param name="forMesh">
    /// The row being looked at, which decides the order. Every texture the weapon reaches stays on
    /// the list — trying another skin's paint on a mesh is the point of the list existing — but a
    /// weapon reaches thirty of them, so they come in three bands: what this mesh is already drawn
    /// with, then the paint that would go on it, then everything else.
    /// </param>
    private void OfferTextures(TreeNode? forMesh = null)
    {
        // Emptying the list empties the box bound to it, which writes a null back through the
        // selection. What was on the mesh goes back on it.
        var wearing = Preview.ChosenTexture;

        var mine = (forMesh is null ? null : SlotsFor(forMesh))?.BySubMesh
            .Where(n => n is not null)
            .Select(n => (n!.Bundle, n.PathId))
            .ToHashSet() ?? [];

        // A skin's materials are what the game hands the weapon's own renderers, so the skin each
        // of them paints with is the next most likely answer after what is already on the mesh:
        // it is what this geometry is going to be seen wearing.
        //
        // The main slot only. A skin material also binds gloss, noise and mask maps, and those
        // outnumber the skins themselves — bringing all of them up buries the eight answers
        // somebody is looking for among fifteen they are not. They stay on the list below.
        //
        // And only the skins that repaint this weapon rather than replacing it. A skin bringing a
        // model of its own is a different object wearing its own paint, whatever that paint
        // resolves to out here — one of this weapon's brings a model that is a copy of the
        // weapon's own mesh, which is why its materials resolve at all, and it still belongs with
        // the skins that replace rather than the ones that repaint. None of this applies to a mesh
        // that arrived with a skin of its own, whose UVs are its own, so those keep the plain
        // order.
        HashSet<(string, long)> paint = [];
        if (forMesh is { Within: null })
            paint = (_tree?.Skins ?? [])
                .Where(s => s.Model is null)
                .SelectMany(s => s.Materials)
                .Where(m => m.Main is not null)
                .Select(m => m.Locate(m.Main!))
                .ToHashSet();

        var seen = new HashSet<(string, long)>();
        var found = new List<TextureChoice>();

        foreach (var node in AllNodes(Detail?.Roots ?? []))
        {
            if (node.Class != AssetClassID.Texture2D || node.Bundle.Length == 0) continue;
            if (!seen.Add((node.Bundle, node.PathId))) continue;

            var worn = mine.Contains((node.Bundle, node.PathId));
            found.Add(new TextureChoice(node.Label, node.Bundle, node.PathId)
            {
                Worn = worn,
                Skin = !worn && paint.Contains((node.Bundle, node.PathId)),
            });
        }

        Preview.TextureChoices.Clear();
        Preview.TextureChoices.Add(new TextureChoice("(automatic)", "", 0));

        // Stable within each band, so the order the tree is in survives the sorting.
        foreach (var choice in found.OrderBy(c => c.Worn ? 0 : c.Skin ? 1 : 2))
            Preview.TextureChoices.Add(choice);

        // The instance out of the new list rather than the one that was selected: they are the same
        // texture, but only the new one knows whether it is worn on the mesh now in front of you.
        if (wearing is not null && Preview.TextureChoices.FirstOrDefault(c => c == wearing) is { } again)
            Preview.ChosenTexture = again;
    }

    private static IEnumerable<TreeNode> AllNodes(IEnumerable<TreeNode> nodes)
        => nodes.SelectMany(n => new[] { n }.Concat(AllNodes(n.Children)));

    /// The animations this item carries, read once per item and kept.
    ///
    /// They belong to the whole thing rather than to any one of its meshes — the prefab holds them
    /// and the clips name what they move by path — so they are read when the item is and handed to
    /// whichever model turns out to be moved by them.
    private IReadOnlyList<Motion> Clips()
    {
        if (_clips is not null) return _clips;
        if (_tree is not { } tree || _bundles is null) return [];

        return _clips = ClipsAmong(tree.PrefabAssets, tree.PrefabBundle);
    }

    /// Cleared with the tree, because they are the tree's.
    private IReadOnlyList<Motion>? _clips;

    /// The animations a skin's own model carries, by the model's path, read when its row is opened.
    ///
    /// A model a skin brings is a prefab of its own with clips of its own, and they are what move it.
    /// The weapon's clips name the weapon's hierarchy — `ultimatum 1/FPS_PLAYER_Arm_Right/root` —
    /// and of #416's four models only one is built under that name. Across the game, 77 of 130 skin
    /// models are moved by their own clips and not the weapon's, and every one of them stood still.
    private readonly Dictionary<string, IReadOnlyList<Motion>> _modelClips = new(StringComparer.OrdinalIgnoreCase);

    /// What a mesh row can be made to do: the clips of the model it was reached through, or the
    /// item's own for a row of the item's own.
    ///
    /// A skin's model that none of its own clips move is played the weapon's, with the weapon's own
    /// first name taken off every path (Motion.WithoutRoot). Eleven of 130 skin models carry no clips
    /// at all; seven of those are built on the weapon's rig under a name of their own.
    private IReadOnlyList<Motion> ClipsFor(TreeNode node, Skeleton? skeleton)
    {
        if (node.Within is not { } within) return Clips();

        var own = _modelClips.GetValueOrDefault(within.Path) ?? [];
        if (skeleton is null || own.Any(skeleton.Moves)) return own;

        return [.. Clips().Select(m => m.WithoutRoot())];
    }

    /// Reads every clip among some walked assets, skipping any that will not read.
    private IReadOnlyList<Motion> ClipsAmong(IEnumerable<AssetNode> assets, string? fallback)
    {
        var found = new List<Motion>();
        if (_bundles is null) return found;

        foreach (var node in assets.Where(a => a.Class == AssetClassID.AnimationClip))
        {
            var bundle = node.Bundle.Length > 0 ? node.Bundle : fallback;
            if (bundle is null) continue;

            try
            {
                var file = _bundles.Open(bundle);
                var info = file.file.GetAssetInfo(node.PathId);
                var field = info is null ? null : _bundles.Context.Deserialize(file, info);

                if (field is not null && Motion.Read(field) is { } motion) found.Add(motion);
            }
            catch (Exception e) when (e is IOException or FileNotFoundException)
            {
                // A clip that will not read costs its own row and nothing else.
            }
        }

        return found;
    }

    private async void ShowPreview(TreeNode? node)
    {
        // A skin row stands for a look rather than for an asset, so selecting one shows the weapon
        // wearing it without opening the row, which would unfold it — and going down a weapon's
        // eight skins to see what they look like is the ordinary thing to do here.
        if (node is { Skin: not null }) { OpenSkin(node); return; }

        if (node?.Class is not (AssetClassID.Texture2D or AssetClassID.Mesh or AssetClassID.AudioClip))
        {
            Preview.Clear(node?.Class is null ? null : $"No preview for {node.Class}.");
            return;
        }

        if (node.Bundle.Length == 0) { Preview.Clear("This object's container is not known."); return; }

        try
        {
            // Reading goes through the same lock as resolving: one BundleSet, one reader, and
            // clicking quickly down a tree must not put two decodes into it at once.
            await _reading.WaitAsync();
            try
            {
                // Somebody has moved on while this waited its turn. Going down a tree of seventy
                // rows with the arrow keys queues one decode per row, and each of them had to
                // finish before the row actually wanted could start.
                if (!ReferenceEquals(Detail?.SelectedNode, node)) return;

                var loaded = await Task.Run(object? () =>
                {
                    // An icon is registered by name with no path id, and may live in the game's own
                    // resources.assets rather than in a bundle, so finding it is not a lookup by id.
                    if (AssetPreview.Locate(_bundles!, node.Bundle, node.Class.Value, node.PathId, node.Label)
                        is not var (file, info)) return null;

                    var field = _bundles!.Context.Deserialize(file, info);
                    if (field is null) return null;

                    return node.Class switch
                    {
                        AssetClassID.Texture2D => AssetPreview.Texture(_bundles, node.Bundle, field),
                        AssetClassID.AudioClip => AssetPreview.Audio(_bundles, node.Bundle, field),
                        _ => AssetPreview.Mesh(field, _bundles, node.Bundle),
                    };
                });

                // What this model can be made to do, read on the same thread and behind the same
                // lock. Only for a model, and only for one something in the prefab actually skins:
                // the bones are the renderer's and the clips are the prefab's, and a mesh with
                // neither is shown standing still as it always was. The prefab being the one the row
                // was reached through: a skin's own model moves by its own clips.
                // The whole prefab when the arms are wanted, because they hang off objects no mesh
                // is skinned to and a bone list has nowhere to put them. See Skeleton.For.
                var (skeleton, motions) = loaded is UnityMesh
                    ? await Task.Run(() =>
                    {
                        var bones = Under(node.Bundle, node.PathId);
                        return (bones, ClipsFor(node, bones));
                    })
                    : (null, []);

                // The rest of the prefab, and what those parts wear. An item is drawn whole because
                // it is one thing — a head, a body and legs, or a hull and its wheels — and showing
                // one mesh of it shows a fragment. Only the arms wait to be asked for. See Held.
                var (with, alongside) = loaded is UnityMesh && skeleton is not null
                    ? await Task.Run(() => Held(node))
                    : ([], []);

                // Which way round it opens, from the prefab rather than from the bounding box, so
                // every weapon points the same way. Read here because it needs the bundle, which
                // the renderer has no business holding; on the same thread and lock as the rest.
                // Of the model put together rather than of the model as written: the box a
                // scattered model fills says nothing about which way it lies. See
                // Skeleton.Assembled.
                //
                // Of the model in its own space, whatever is shown beside it. The parts are joined in
                // the prefab's space and taken back into this one before anything sees them, because
                // a weapon's facing is read against landmarks that answer here. See PreviewViewModel.
                var standing = loaded is UnityMesh model
                    ? await Task.Run(() => Standing(node, model, skeleton))
                    : (MeshRenderer.Basis?)null;

                switch (loaded)
                {
                    case PreviewImage picture:
                        Preview.Show(picture, $"{node.Label}   @ {node.Bundle}", node.AlphaIsCoverage);
                        break;
                    case UnityMesh mesh:
                        // Reordered before it is shown, so the choice this mesh is remembered with
                        // lands in a list that already holds it.
                        OfferTextures(node);
                        // The parts drawn with it follow this mesh's submeshes, so their textures
                        // follow its textures: the renderer takes one per submesh, in order.
                        Preview.Show(mesh, $"{node.Label}   @ {node.Bundle}",
                            Alongside(TexturesFor(SlotsFor(node)), mesh, alongside),
                            // The model it was reached through is part of which model this is: two
                            // rows can stand for the same asset, and an angle turned to on one of
                            // them is not the angle the other was left at.
                            subject: node.Within is { } within
                                ? $"{within.Path}/{node.Bundle}:{node.PathId}"
                                : $"{node.Bundle}:{node.PathId}",
                            skeleton: skeleton, motions: motions, standing: standing, with: with);

                        // After the model, which puts back whatever this mesh was last wearing.
                        // Taken whether or not it was found, so it cannot arrive on the next one.
                        if (_asked is { } asked
                            && Preview.TextureChoices.FirstOrDefault(c => c == asked) is { } onTheList)
                            Preview.ChosenTexture = onTheList;
                        _asked = null;
                        break;
                    case PreviewSound sound:
                        Preview.Show(sound, $"{node.Label}   @ {node.Bundle}");
                        break;
                    default:
                        Preview.Clear(_bundles!.Context.HasClassDatabase || !node.Bundle.Contains('.')
                            ? $"'{node.Label}' could not be read from {node.Bundle}."
                            : $"'{node.Label}' lives in {node.Bundle}, which needs {ClassPackage.FileName}.");
                        break;
                }
            }
            finally
            {
                _reading.Release();
            }
        }
        catch (Exception ex)
        {
            Preview.Clear(ex.Message);
        }
    }

    /// Whether something is reading the game right now.
    ///
    /// A preview is loaded on a thread of its own and opens bundles as it goes, so putting the
    /// reader down while one is in flight walks the very list it is adding to. The window closing
    /// is when that happens, and a test that drives the window has to wait for quiet before it
    /// disposes — which it can do, because it is the one pumping the thread the read finishes on.
    public bool Reading => _reading.CurrentCount == 0;

    /// What Browse and extraction read through, for a check that it is the game as shipped.
    internal BundleSet? Reader => _bundles;

    /// Said across the top of the window when the game has been updated under installed mods.
    [ObservableProperty] private string? _updateNotice;

    /// Goes to the manager and asks it to reapply, which confirms first and refuses while the game
    /// is running like everything else there that writes.
    [RelayCommand]
    private void ReapplyAfterUpdate()
    {
        UpdateNotice = null;
        Workspace = ManagerTab;
        Manager.ReapplyCommand.Execute(null);
    }

    [RelayCommand]
    private void DismissUpdate() => UpdateNotice = null;

    /// Said across the top when the last session ended in a crash, once, naming the report it left.
    /// Nobody reads a log folder unprompted, and the moment after a crash is when somebody is
    /// willing to send one.
    [ObservableProperty] private string? _crashNotice;

    /// The report the notice is about, for the button that shows it.
    public string? CrashReport { get; private set; }

    [RelayCommand]
    private void DismissCrash() => CrashNotice = null;

    [RelayCommand]
    private void OpenLogs()
    {
        var folder = ErrorLog.Folder;
        Directory.CreateDirectory(folder);
        var target = CrashNotice is not null && CrashReport is { } report && File.Exists(report)
            ? $"/select,\"{report}\""
            : $"\"{folder}\"";
        System.Diagnostics.Process.Start("explorer.exe", target);
    }

    /// Worked out on opening the game, not on being asked: after an update nothing else about the
    /// tool looks wrong. See GameUpdate.
    private static string? UpdateNoticeFor(GameInstallation game, BundleSet bundles)
    {
        try
        {
            var stale = GameUpdate.Stale(new ModStore(game).Read(), game.ReadManifest());
            if (stale.Count == 0) return null;

            return GameUpdate.Notice(stale, GameVersion.Of(bundles.Context, game));
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // Not being able to tell is not worth failing to open the game over.
            return null;
        }
    }

    public void Dispose()
    {
        Editor.Dispose();
        _bundles?.Dispose();
        _reading.Dispose();
    }
}

public sealed record WeaponListItem(WeaponRecord Record, string? Translated)
{
    public string Name => Translated is { Length: > 0 } named ? named
        : Record.Tag.Length > 0 ? Record.Tag
        : Record.Slug;

    public string Number => $"#{Record.GameNumber}";

    /// Only weapons are numbered, and the column that shows it is theirs alone: a hat's id is what
    /// identifies it, and it is already on the second line.
    public bool IsNumbered => Record.IsNumbered;

    /// Shown next to the name because the two numbering systems disagree for all but six weapons,
    /// and the prefab number is the one that appears in asset paths.
    public string Prefab => Record.PrefabName;
}

/// One of the game's own translation tables, named in its own script.
public sealed record MemoryChoice(int Megabytes, string Label)
{
    public override string ToString() => Label;
}

public sealed record LanguageOption(string Bundle, string Name)
{
    public override string ToString() => Name;
}

/// A skin offered for extraction. The null id means the weapon on its own.
public sealed record SkinChoice(string? Id, string Name, bool HasModel)
{
    public static SkinChoice None { get; } = new(null, "(no skin)", false);

    /// The item as it comes and every skin, each into a workspace of its own.
    public static SkinChoice All { get; } = new(null, "(all of them, each a workspace of its own)", false);

    public override string ToString() => HasModel ? $"{Name}  — its own model" : Name;
}
