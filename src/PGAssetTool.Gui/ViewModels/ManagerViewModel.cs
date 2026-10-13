using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PGAssetTool.Core.Settings;
using PGAssetTool.Core.Game;
using Avalonia.Media.Imaging;
using PGAssetTool.Core.Mods;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Gui.ViewModels;

/// An installed mod as a row.
public sealed record InstalledRow(InstalledMod Mod)
{
    public string Name => Mod.Name.Length > 0 ? Mod.Name : Mod.Id;
    public bool Enabled => Mod.Enabled;

    public string Detail =>
        $"{Mod.Id}   {Mod.Version}   {Mod.TouchedBundles.Count} bundle(s)"
        + $"   installed {Mod.InstalledAt:yyyy-MM-dd} for {Mod.GameVersion}";

    /// The picture the pack carries, read out of it once.
    ///
    /// A list of installed mods is a list of names, and a name is a poor way to tell one weapon
    /// re-skin from another when several are installed. Read lazily because most of them are never
    /// looked at, and held afterwards because the rows are rebuilt on every refresh.
    public Bitmap? Icon => _icon ??= Load();

    private Bitmap? _icon;

    private Bitmap? Load()
    {
        try
        {
            if (Mod.PackPath.Length == 0 || !File.Exists(Mod.PackPath)) return null;
            if (PackBuilder.ReadIcon(Mod.PackPath) is not { Length: > 0 } bytes) return null;
            return new Bitmap(new MemoryStream(bytes));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    public bool HasIcon => Icon is not null;
}

/// What a pack says about itself, read out of the manifest inside it.
///
/// The ledger keeps only what the tool needs to undo an install. Everything an author wrote — what
/// the mod is called, who made it, what it says it does, and which assets it replaces — lives in
/// the pack, and is worth showing to whoever is deciding whether to keep it.
public sealed record ModDetails(
    string Name, string Id, string Author, string Version, string Description,
    string BuiltAgainst, string Pack, IReadOnlyList<string> Replaces, PackSeal Seal)
{
    /// Said plainly and left at that. A pack somebody altered still installs — running a mod you
    /// changed yourself is nobody else's business — but it no longer passes for the one its author
    /// built, and the person about to install it is the one who should know.
    public string Signature => Seal.Describe;

    public bool WasAltered => Seal.State == SealState.Altered;

    public bool HasDescription => Description.Length > 0;
    public string Summary => $"{Version}   by {(Author.Length > 0 ? Author : "nobody in particular")}";

    public string Built => BuiltAgainst.Length > 0
        ? $"built against game {BuiltAgainst}"
        : "built against an unrecorded game version";

    public string Changes => Replaces.Count == 1 ? "Replaces one asset" : $"Replaces {Replaces.Count} assets";

    public static ModDetails? Read(InstalledMod mod)
    {
        try
        {
            var manifest = PackBuilder.ReadManifest(mod.PackPath);
            return new ModDetails(
                manifest.Name, manifest.Id, manifest.Author, manifest.Version, manifest.Description,
                manifest.BuiltAgainstGameVersion ?? "", mod.PackPath,
                manifest.Operations
                    .Select(o => $"{Verb(o.Op)}  {o.Target.Name}  ({o.Target.Class} in {o.Target.Container})")
                    .ToList(),
                PackFile.Inspect(mod.PackPath));
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            // The pack is gone or unreadable, which the manager says elsewhere; there is simply
            // nothing to show here.
            return null;
        }
    }

    private static string Verb(string op) => op switch
    {
        PackOperations.ReplaceTexture => "texture",
        PackOperations.ReplaceMesh => "mesh",
        PackOperations.ReplaceAudio => "sound",
        _ => op,
    };
}

/// What a request needs confirming before it happens.
public sealed record Confirmation(string Title, string Body, Func<Task> Proceed);

/// What the manager arranges its tiles by. The names are what the picker shows.
public enum ModOrder
{
    Installed,
    Item,
}

/// Names the orders for the picker. A converter rather than a wrapper record, so the property the
/// picker is bound to is the value itself and nothing has to be matched back to it afterwards.
public sealed class ModOrderName : Avalonia.Data.Converters.IValueConverter
{
    public static ModOrderName Instance { get; } = new();

    public object Convert(object? value, Type type, object? parameter, System.Globalization.CultureInfo culture)
        => value is ModOrder.Item ? "By item" : "Newest last";

    public object ConvertBack(object? value, Type type, object? parameter, System.Globalization.CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}

/// One level of the shelf the installed mods are arranged on: a weapon, or one of its looks.
///
/// The arrangement is the one on disk — a folder per weapon, a folder per skin inside it — because
/// two views of the same thing that disagree are worse than either. What it holds is ids rather
/// than rows: the rows are rebuilt on every refresh and an id is what survives that.
public sealed class ModFolder(string label, string detail, IReadOnlyCollection<string> holds)
{
    public string Label => label;
    public string Detail => detail;
    public ObservableCollection<ModFolder> Children { get; } = [];

    /// Where this sits, as folder names from the top. What a selection is remembered by, since the
    /// nodes themselves are made anew each time the list is read.
    public IReadOnlyList<string> At { get; init; } = [];

    public bool Holds(InstalledMod mod) => holds.Contains(mod.Id);
}

/// The game side: what is installed, what state the bundles are in, and turning things on and off.
///
/// Everything here writes to the game, which is the one thing the rest of the tool never does. So
/// it says what it is about to do first, and it always checks whether the game is running —
/// writing a bundle out from under it is what leaves the half-written files that make people
/// distrust modding tools.
public sealed partial class ManagerViewModel : ObservableObject
{
    private readonly Func<GameInstallation?> _game;

    public ManagerViewModel(Func<GameInstallation?> game) => _game = game;

    /// Size or speed, when this rewrites the game. Set from the settings, which is where the
    /// choice is made; the manager itself only passes it on.
    public BundlePacking Packing { get; set; }

    /// How many bundles are rebuilt at once. See ModApplier.AtOnce.
    public int AtOnce { get; set; } = 4;

    /// Everything installed, whatever folder it is filed in.
    public ObservableCollection<InstalledRow> Mods { get; } = [];

    /// The tiles on show: everything, or what the selected folder holds.
    public ObservableCollection<InstalledRow> Shown { get; } = [];

    /// The shelf itself, as one root that holds the lot.
    public ObservableCollection<ModFolder> Folders { get; } = [];

    [ObservableProperty] private ModFolder? _folder;

    partial void OnFolderChanged(ModFolder? value) => Show();

    /// Narrows the shelf to what is being looked for. Empty shows everything.
    [ObservableProperty] private string _folderSearch = "";

    partial void OnFolderSearchChanged(string value) => Arrange();

    /// Which weapons a search hits, answered by the same code the weapon list searches with.
    ///
    /// Handed in rather than worked out here: a player knows one weapon by one name and it is not
    /// necessarily the one on screen, so searching properly means every language's table — which
    /// the shell has open and this does not. Null when the game is not open, and then only what a
    /// mod says about itself is searched.
    public Func<string, IReadOnlySet<int>?>? FindWeapons { get; set; }

    /// What the game calls this weapon and this skin now, in the language the tool is set to.
    ///
    /// Asked of the catalogues rather than read off the pack. A pack records the names it was built
    /// with, which is what it needs to be able to say for itself when it is handed to somebody —
    /// but on this screen the question is what these things are called *here*, and the answer moves
    /// when the language does. Looking them up by number and by skin id also reaches packs built
    /// before any of this existed, which carry no key to translate.
    public Func<Core.Pack.PackSubject, (string? Name, string? Variant)>? Names { get; set; }

    /// Redraws the headings for a language that has just changed, without going back to the game.
    public void Relabel() => Arrange();

    /// The mods a search leaves. Everything, when nothing is being searched for.
    private List<InstalledRow> Searched()
    {
        var text = FolderSearch.Trim();
        if (text.Length == 0) return Mods.ToList();

        var weapons = FindWeapons?.Invoke(text);
        return Mods.Where(row => Hit(row.Mod, text, weapons)).ToList();
    }

    private bool Hit(InstalledMod mod, string text, IReadOnlySet<int>? weapons)
    {
        if (mod.Subject is { IsKnown: true } subject)
        {
            if (subject.Number > 0 && weapons?.Contains(subject.Number) == true) return true;
            if (subject.Id.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
            if (subject.Variant.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
            if (subject.VariantName.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
            if (Varied(subject)?.Contains(text, StringComparison.OrdinalIgnoreCase) == true) return true;
            if (Named(subject)?.Contains(text, StringComparison.OrdinalIgnoreCase) == true) return true;
        }

        // What the mod says about itself, which is all there is to go on for one that was not made
        // by extracting a weapon.
        return mod.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || mod.Author.Contains(text, StringComparison.OrdinalIgnoreCase)
            || mod.Id.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    /// Fills the tiles from whichever folder is open.
    ///
    /// The selection goes with the folder rather than surviving it: a mod that is no longer on
    /// screen is not something a person means to be acting on, and Remove acting on a tile nobody
    /// can see is the kind of surprise this tab must not have.
    private void Show()
    {
        var was = Selected?.Mod.Id;

        Selection.Clear();
        Shown.Clear();

        var rows = Mods.Where(r => Folder is null || Folder.Holds(r.Mod));

        // Sorted here rather than where the rows are built, so changing the order is a rearranging
        // of what is already read and not a trip back to the ledger and the bundles.
        rows = Order == ModOrder.Item
            ? rows.OrderBy(r => r.Mod.Subject?.Number ?? int.MaxValue)
                .ThenBy(r => r.Mod.Subject?.Id ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Mod.InstalledAt)
            : rows.OrderBy(r => r.Mod.InstalledAt);

        foreach (var row in rows) Shown.Add(row);

        // Followed by id rather than by row. Every refresh builds new rows around new records — a
        // mod that has just been turned off is a different value from the one that was selected —
        // so holding the row itself emptied the details pane under whoever was reading it.
        Selected = was is null ? null : Shown.FirstOrDefault(r => r.Mod.Id == was);
    }

    /// Builds the shelf from what is installed, and reopens the folder that was open before.
    private void Arrange()
    {
        var was = Folder?.At ?? [];
        Folders.Clear();

        var searched = Searched();
        var all = new ModFolder(
            FolderSearch.Trim().Length == 0 ? "All mods" : "Matching",
            $"{searched.Count}", searched.Select(r => r.Mod.Id).ToHashSet());
        Folders.Add(all);

        // The folders on disk, level for level.
        //
        // The top level is the kind of thing, and it is skipped while everything installed is the
        // same kind: a row saying "weapon" over nothing but weapons is a click that tells nobody
        // anything. It appears of its own accord the first time something that is not a weapon is
        // installed beside one.
        var filed = searched.Where(r => r.Mod.Subject is { IsKnown: true }).ToList();
        var kinds = filed.Select(r => r.Mod.Subject!.Path[0])
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

        Build(all, filed, kinds > 1 ? 0 : 1);

        // Anything that does not say what it is for. Its own folder rather than mixed in, because
        // "we do not know" is a different answer from "the plain weapon".
        var unfiled = searched.Where(r => r.Mod.Subject is not { IsKnown: true }).ToList();
        if (unfiled.Count > 0)
            all.Children.Add(new ModFolder(
                "Unfiled", $"{unfiled.Count}", unfiled.Select(r => r.Mod.Id).ToHashSet())
            {
                At = [Core.Mods.ModStore.Unfiled],
            });

        Folder = Find(all, was) ?? all;
        Show();
    }

    /// Adds a folder per distinct name at this level of the path, and recurses.
    ///
    /// Grouped on the folder name rather than on the heading, so what is on screen is in the order
    /// the folders on disk are in — the same names, sorted the same way, whatever language the
    /// headings are being read in.
    private void Build(ModFolder into, IReadOnlyList<InstalledRow> rows, int level)
    {
        if (rows.Count == 0 || level >= rows[0].Mod.Subject!.Path.Count) return;

        foreach (var group in rows
                     .GroupBy(r => r.Mod.Subject!.Path[level], StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var subject = group.First().Mod.Subject!;
            var node = new ModFolder(
                Heading(subject, level), $"{group.Count()}", group.Select(r => r.Mod.Id).ToHashSet())
            {
                At = [.. subject.Path.Take(level + 1)],
            };

            into.Children.Add(node);
            Build(node, group.ToList(), level + 1);
        }
    }

    /// What a folder at this level is called, in the language the tool is set to.
    private string Heading(Core.Pack.PackSubject subject, int level) => level switch
    {
        // The kind as the picker in Browse names it, rather than the folder name it is filed under.
        0 => Core.Catalog.ItemKinds.ByName(subject.Kind)?.Name ?? subject.Kind,
        1 => Named(subject) is { } name
            ? (subject.Number > 0 ? $"#{subject.Number} {name}" : name)
            : subject.Describe,
        _ => Varied(subject) ?? subject.DescribeVariant,
    };

    private string? Named(Core.Pack.PackSubject subject)
        => Names?.Invoke(subject).Name is { Length: > 0 } name ? name : null;

    private string? Varied(Core.Pack.PackSubject subject)
        => subject.Variant.Length == 0
            ? null
            : Names?.Invoke(subject).Variant is { Length: > 0 } name ? name : null;

    private static ModFolder? Find(ModFolder among, IReadOnlyList<string> at)
    {
        if (at.Count == 0) return among;

        foreach (var child in among.Children)
            if (child.At.SequenceEqual(at, StringComparer.OrdinalIgnoreCase)) return child;
            else if (Find(child, at) is { } deeper) return deeper;

        return null;
    }
    public ObservableCollection<BundleReport> Bundles { get; } = [];

    [ObservableProperty] private InstalledRow? _selected;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _backups = "";
    [ObservableProperty] private bool _gameIsRunning;

    /// Set while the game is being rewritten. Nothing else here is slow enough to matter.
    [ObservableProperty] private bool _busy;

    /// Set when something is waiting on an answer. The view shows it; nothing happens until it is
    /// resolved either way.
    [ObservableProperty] private Confirmation? _asking;

    /// Skips the confirmation for routine changes. Never skips the warning about the game running.
    [ObservableProperty] private bool _confirmChanges = true;

    /// What the tiles are arranged by.
    ///
    /// Installed order is where somebody left off; item order is where a mod is in the game. Which
    /// one is wanted depends on whether the question is "what did I just do" or "what have I got
    /// for this weapon", and both get asked.
    [ObservableProperty] private ModOrder _order;

    partial void OnOrderChanged(ModOrder value) => Show();

    public IReadOnlyList<ModOrder> Orders { get; } = [ModOrder.Installed, ModOrder.Item];

    /// What the selected pack says about itself, read from the pack rather than from the ledger.
    [ObservableProperty] private ModDetails? _details;

    partial void OnSelectedChanged(InstalledRow? value) => Details = value is null ? null : ModDetails.Read(value.Mod);

    /// How big a tile is drawn, in pixels down one side.
    ///
    /// Worked with Ctrl and the wheel, because how many mods fit on the page against how well each
    /// one can be made out is a judgement that changes with how many are installed — and with how
    /// close somebody is looking.
    [ObservableProperty] private int _tileSize = 112;

    /// The tile itself is a little wider than its picture, to leave the name somewhere to sit.
    public int TileWidth => TileSize + 12;

    partial void OnTileSizeChanged(int value) => OnPropertyChanged(nameof(TileWidth));

    public const int SmallestTile = 56;
    public const int LargestTile = 224;

    /// One notch of the wheel. Proportional rather than fixed: the same step feels like a lot at
    /// the small end and like nothing at the large one.
    public void ResizeTiles(int notches)
        => TileSize = Math.Clamp(
            (int)Math.Round(TileSize * Math.Pow(1.15, notches)), SmallestTile, LargestTile);

    /// Runs the work with the reader put down and picks it up again afterwards.
    ///
    /// The shell holds the bundles open for browsing, and everything here rewrites those same
    /// files — a write attempted around a live reader fails with the file in use, which is exactly
    /// what happened as soon as the confirmation started working.
    public Func<Func<Task>, Task>? Around { get; set; }


    /// Reads only the bundles a mod claims. Finding one changed by something else means hashing all
    /// 2300 of them, which is a deliberate act rather than the cost of opening a tab.
    public void Refresh() => Load(everything: false);

    /// Notices the game being started or closed while the tool is open.
    ///
    /// The buttons on this tab, and what the editor will let through, both turn on whether the game
    /// holds its bundles open — and that was read once, when the tab last did a full refresh. Start
    /// the game afterwards and everything went on offering to write to it; close it and everything
    /// went on refusing.
    ///
    /// The cheap question is asked on a timer and the expensive one only when the answer moves: a
    /// refresh re-reads the ledger and every bundle a mod claims, which is not a thing to do every
    /// couple of seconds, and leaving the status line disagreeing with the buttons would be its own
    /// small lie.
    public void NoticeTheGame()
    {
        if (Busy || _game() is not { } game) return;

        var running = GameProcess.IsRunning(game);
        if (running == GameIsRunning) return;

        GameIsRunning = running;
        Refresh();
    }

    [RelayCommand]
    private void CheckEverything() => Load(everything: true);

    /// Reads the ledger and the bundles every mod claims.
    ///
    /// Guarded because of where it is called from: a timer notices the game starting or closing and
    /// refreshes, and an exception out of a timer tick is an exception nobody is waiting for — it
    /// takes the window down rather than reaching a status line. The ledger being unreadable is the
    /// case this is for, and it is a thing to be told about, not to be closed over.
    private void Load(bool everything)
    {
        try { Read(everything); }
        catch (Exception ex) { Status = ErrorLog.Said(ex, "reading what is installed"); }
    }

    private void Read(bool everything)
    {
        if (_game() is not { } game) { Status = "The game is not open."; return; }

        var store = new ModStore(game);
        var installed = store.Read();

        // Rebuilt rows are new instances; anything held from the previous set is stale.
        Selection.Clear();
        Mods.Clear();
        foreach (var mod in installed.OrderBy(m => m.InstalledAt)) Mods.Add(new InstalledRow(mod));
        Arrange();

        Bundles.Clear();
        foreach (var report in InstallationReport.Read(game, store, everything)) Bundles.Add(report);

        GameIsRunning = GameProcess.IsRunning(game);
        Backups = store.BackupRoot;

        var elsewhere = Bundles.Count(b => b.State == BundleState.ChangedBySomethingElse);
        Status = $"{installed.Count} installed, {installed.Count(m => m.Enabled)} on"
            + (elsewhere > 0 ? $" — {elsewhere} bundle(s) changed by something outside this tool" : "")
            + (GameIsRunning ? " — the game is running" : "")
            + (everything ? "" : " — only the bundles mods claim were checked");
    }


    /// Turning one on turns off whatever else was writing the same assets, and says which.
    ///
    /// Without that the two both apply and the later install silently wins, so a player had no way
    /// to know which of two skins for one weapon they were actually running.
    [RelayCommand]
    private void Enable()
    {
        var applier = Applier();
        Ask("Turn on", ids => applier.SetEnabled(ids, true),
            afterwards: () => applier.Displaced.Count == 0
                ? ""
                : $"  Turned off {string.Join(", ", applier.Displaced)}, which wrote the same assets.");
    }

    [RelayCommand]
    private void Disable() => Ask("Turn off", ids => Applier().SetEnabled(ids, false));

    /// Whether the shift key is down, which is what turns removing into deleting.
    ///
    /// Kept here rather than read at the moment of the click, because the button says which of the
    /// two it is about to do. A hidden second meaning on a button is a trap; one that announces
    /// itself while the key is held is a shortcut.
    [ObservableProperty] private bool _shiftHeld;

    partial void OnShiftHeldChanged(bool value) => OnPropertyChanged(nameof(RemoveLabel));

    public string RemoveLabel => ShiftHeld ? "Remove and delete" : "Remove";

    /// Takes a mod out of the game, and with shift held throws its pack away as well.
    ///
    /// Remove on its own puts the bundles back and forgets the mod, and the copy of the pack stays
    /// in the mods directory — which is what makes reinstalling it a click rather than a rebuild,
    /// and what made the button's name a half-truth. The other half is here, behind a modifier and
    /// behind its own confirmation, because a deleted pack that was never anywhere else is gone.
    [RelayCommand]
    private void Remove()
    {
        if (!ShiftHeld) { Ask("Remove", ids => Applier().Remove(ids)); return; }

        // Read before the confirmation: the rows are rebuilt by the refresh that follows the work,
        // and the ones this was asked about would be gone by the time it mattered.
        var mods = Chosen;

        Ask("Remove and delete", ids => Applier().Remove(ids),
            note: "The pack files are deleted as well, and installing these again would mean "
                + "building them anew. Only this tool's own copies go; a pack of your own, "
                + "anywhere else, is left where it is.",
            alwaysConfirm: true,
            afterwards: () =>
            {
                var store = new ModStore(_game()!);
                var (gone, kept) = (0, 0);
                foreach (var mod in mods)
                {
                    try { if (store.DiscardKeptPack(mod)) gone++; else kept++; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        kept++;
                    }
                }

                return kept == 0 ? $", {gone} pack file(s) deleted"
                    : $", {gone} pack file(s) deleted, {kept} left in place";
            });
    }

    /// Rebuilds every bundle, including the ones already holding what they should.
    ///
    /// Every other route leaves those alone, which is what makes a toggle cost one bundle instead of
    /// seventeen. This is the request that means the opposite: it is what somebody reaches for when
    /// they think the game is not in the state the tool believes it is, and answering it by deciding
    /// there was nothing to do would be no answer at all.
    [RelayCommand]
    private void Reapply() => Ask("Reapply everything",
        _ => new ModApplier(_game()!, new ModStore(_game()!)) { Packing = Packing, AtOnce = AtOnce, Rebuild = true }.Reconcile(),
        needsSelection: false);

    private ModApplier Applier() => new(_game()!, new ModStore(_game()!)) { Packing = Packing, AtOnce = AtOnce };

    /// The rows a command acts on: everything highlighted, or the one current row.
    ///
    /// Turning six mods off one at a time means six confirmations and six whole-game rebuilds, and
    /// leaves the game in five intermediate states nobody asked for.
    public IReadOnlyList<InstalledMod> Chosen =>
        Selection.Count > 0 ? Selection.Select(r => r.Mod).ToList()
        : Selected is { } row ? [row.Mod]
        : [];

    /// Bound to the list's own selection, which is where multiple rows live; Selected stays the
    /// anchor the details pane follows.
    public ObservableCollection<InstalledRow> Selection { get; } = [];

    /// Puts the request behind a confirmation, unless the setting says otherwise — and always
    /// behind the running-game check, which the setting does not cover.
    /// <param name="note">
    /// Said in the confirmation on top of what every one of these says, for a request that does
    /// something the ordinary explanation does not cover.
    /// </param>
    /// <param name="alwaysConfirm">
    /// Asks even when the setting says not to. The setting is about the rebuild — which is undone
    /// by doing the opposite — and not about anything that cannot be taken back.
    /// </param>
    /// <param name="afterwards">
    /// Run once the work is done, on the thread that owns the view models; what it answers is added
    /// to what the status line says happened.
    /// </param>
    private void Ask(string what, Func<IReadOnlyList<string>, ReconcileResult> work,
        bool needsSelection = true, string? note = null, bool alwaysConfirm = false,
        Func<string>? afterwards = null)
    {
        if (_game() is not { } game) { Status = "The game is not open."; return; }

        var mods = needsSelection ? Chosen : [];
        if (needsSelection && mods.Count == 0) { Status = "Nothing selected."; return; }

        var subject = mods.Count switch
        {
            0 => "everything installed",
            1 => $"'{mods[0].Name}'",
            _ => $"{mods.Count} mods",
        };

        var running = GameProcess.IsRunning(game);
        if (!running && !ConfirmChanges && !alwaysConfirm)
        {
            _ = Run(what, mods, work, afterwards);
            return;
        }

        Asking = new Confirmation(
            $"{what} {subject}?",
            running
                ? "The game is running. Its bundles are open, and rewriting them now can leave a "
                  + "half-written file behind. Close it first."
                : (mods.Count > 1 ? string.Join(", ", mods.Select(m => m.Name)) + "\n\n" : "")
                  + "Every bundle these mods touch is restored from its backup and the enabled ones "
                  + "are applied again, so the result is the same however this was reached."
                  + (note is null ? "" : "\n\n" + note),
            () => running ? Task.CompletedTask : Run(what, mods, work, afterwards));
    }

    private async Task Run(
        string what, IReadOnlyList<InstalledMod> mods, Func<IReadOnlyList<string>, ReconcileResult> work,
        Func<string>? afterwards = null)
    {
        Asking = null;
        Busy = true;

        var ids = mods.Select(m => m.Id).ToList();

        async Task Apply()
        {
            var result = await Task.Run(() => work(ids));

            // After the game has been put back, never instead of it: a pack thrown away while the
            // mod it installed was still in place would leave something that cannot be undone.
            var also = afterwards?.Invoke() ?? "";

            Status = $"{what}: {result.Applied.Count} applied, {result.Restored.Count} restored"
                + (result.Unchanged.Count > 0 ? $", {result.Unchanged.Count} left alone" : "")
                // Worth saying: the pack named one bundle and the asset was in another, which is
                // what a game update does, and the author may want to rebuild the pack.
                + (result.Moved.Count > 0
                    ? $", {result.Moved.Count} followed into another bundle — {string.Join("; ", result.Moved)}"
                    : "")
                + (result.Failed.Count > 0
                    ? $", {result.Failed.Count} failed — {string.Join("; ", result.Failed)}"
                    : "")
                + also;
        }

        try
        {
            if (Around is not null) await Around(Apply); else await Apply();
            Refresh();
        }
        catch (Exception ex)
        {
            Status = ErrorLog.Said(ex, "changing the game");
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Dismiss() => Asking = null;

    [RelayCommand]
    private async Task Proceed()
    {
        // Run owns the busy flag: it raises it before the work and lowers it in a finally. Raising
        // it again here — after the work had already finished and lowered it — left the manager
        // stuck showing itself as busy for the rest of the session, and every confirmed action
        // after the first one looked like it had hung.
        if (Asking is { } asking) await asking.Proceed();
        Asking = null;
    }

    [RelayCommand]
    private void CloseGame()
    {
        if (_game() is not { } game) return;

        Status = GameProcess.Close(game, TimeSpan.FromSeconds(10))
            ? "The game has been closed."
            : "The game did not close; end it yourself and try again.";
        Refresh();
    }

    [RelayCommand]
    private void LaunchGame()
    {
        if (_game() is not { } game) return;

        Status = GameProcess.Launch(game) ? "Starting the game…" : "The game could not be started.";
    }
}
