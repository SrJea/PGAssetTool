using System.Collections.ObjectModel;
using AssetsTools.NET.Extra;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PGAssetTool.Core.Animation;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;
using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Pack;
using PGAssetTool.Core.Preview;
using PGAssetTool.Core.Settings;
using PGAssetTool.Core.Weapons;

namespace PGAssetTool.Gui.ViewModels;

/// The editor's half of animations: showing one moving the model it moves, and putting another
/// item's in its place.
///
/// A `.anim` is a file like the others, edited by being replaced, so it shows the way they do — the
/// game's on one side and the workspace's on the other — except that what is shown is the item's
/// model playing it. Replacing one is done here rather than in another program, because the other
/// program would be this one: the animations worth having are the game's own, on other items.
public sealed partial class EditorViewModel
{
    /// The catalogues, for finding the item an animation is taken from. Null until the game is open.
    private Func<GameCatalogs?> _catalogs = () => null;

    public Func<GameCatalogs?> Catalogs { set => _catalogs = value; }

    /// Whether the file selected is an animation, which is when choosing another one is offered.
    [ObservableProperty] private bool _isAnimation;

    /// What the selected animation is now: the item's own, or whose it was and how well it fitted.
    [ObservableProperty] private string _animationSays = "";

    /// The item to take an animation from, as typed: a weapon's number, any name it goes by, an id.
    [ObservableProperty] private string _fromQuery = "";

    /// Which item that turned out to be, or why it is none.
    [ObservableProperty] private string _fromFound = "";

    /// That item's animations, by name.
    public ObservableCollection<string> FromClips { get; } = [];

    [ObservableProperty] private string? _fromClip;

    private WeaponRecord? _fromItem;

    /// Which lookup is the latest, so a slow one cannot land after a quicker one typed later.
    private int _fromAsked;

    private void ShowAnimationChoice(WorkspaceFile? file)
    {
        IsAnimation = file?.Target.Class == nameof(AssetClassID.AnimationClip);
        AnimationSays = IsAnimation && file is not null ? Describe(file) : "";
        if (IsAnimation && FromClips.Count > 0 && file is not null)
            FromClip = FromClips.FirstOrDefault(c => string.Equals(c, file.Target.Name, StringComparison.OrdinalIgnoreCase))
                ?? FromClip;
    }

    private static string Describe(WorkspaceFile file)
    {
        try
        {
            var clip = ClipFile.Read(file.FullPath);
            var motion = clip.ToMotion();
            var origin = !file.Edited ? "the item's own"
                : clip.From.Length > 0 ? $"from {clip.From}" : "edited";
            return $"{file.Target.Name}: {origin}, {motion.Length:0.00}s, {motion.Curves.Count} curves"
                + (clip.Unmatched.Count > 0 ? $", {clip.Unmatched.Count} of the original's left out with nothing here to move" : "");
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return $"{file.Target.Name}: unreadable — {e.Message}";
        }
    }

    partial void OnFromQueryChanged(string value) => _ = FindSourceAsync(value);

    /// Finds the item typed and lists what it plays.
    ///
    /// The game's own ids and numbers first, then any of a weapon's names in any language, then the
    /// name of anything else — the way the browse list finds things, so whatever finds an item there
    /// finds it here.
    private async Task FindSourceAsync(string query)
    {
        var asked = ++_fromAsked;
        _fromItem = null;
        FromClips.Clear();
        FromClip = null;

        var text = query.Trim();
        if (text.Length == 0) { FromFound = ""; return; }
        if (_catalogs() is not { } catalogs) { FromFound = "The game is not open."; return; }

        var item = catalogs.Find(text)
            ?? catalogs.Items.Search(text, catalogs.Names).FirstOrDefault()
            ?? catalogs.Kinds.Where(k => k != ItemKinds.Weapon).SelectMany(k => catalogs.Of(k))
                .FirstOrDefault(r => catalogs.Localization.Translate(r.LocalizationKey)?
                    .Contains(text, StringComparison.OrdinalIgnoreCase) == true);
        if (item is null) { FromFound = $"Nothing goes by '{text}'."; return; }

        IReadOnlyList<AnimationSwap.Choice> clips;
        await _reading.WaitAsync();
        try
        {
            if (asked != _fromAsked) return;
            if (_bundles() is not { } bundles) { FromFound = "The game is not open."; return; }
            clips = await Task.Run(() => AnimationSwap.ClipsOf(bundles, new WeaponResolver(bundles, catalogs).Resolve(item)));
        }
        catch (Exception e)
        {
            FromFound = ErrorLog.Said(e, "listing an item's animations");
            return;
        }
        finally
        {
            _reading.Release();
        }

        if (asked != _fromAsked) return;
        _fromItem = item;
        var names = clips.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        FromFound = names.Count == 0
            ? $"{AnimationSwap.Describe(catalogs, item)} plays no animation."
            : $"{AnimationSwap.Describe(catalogs, item)}: {names.Count} animations";
        foreach (var name in names) FromClips.Add(name);

        // The same name as the one being replaced, where it has one: a reload for a reload.
        FromClip = FromClips.FirstOrDefault(c => string.Equals(c, SelectedFile?.Target.Name, StringComparison.OrdinalIgnoreCase))
            ?? FromClips.FirstOrDefault();
    }

    /// Puts the chosen item's animation in place of the selected one.
    [RelayCommand]
    private async Task UseAnimation()
    {
        if (SelectedWorkspace is not { } workspace || SelectedFile is not { } file) return;
        if (_fromItem is not { } item || FromClip is not { } clip) { Status = "Choose an item and one of its animations first."; return; }

        var slot = new AnimationSwap.Slot(file.RelativePath, file.Target);
        await Swap(workspace, file, "putting in another animation", (bundles, catalogs) =>
        {
            var outcome = AnimationSwap.Use(bundles, catalogs, workspace.Directory, slot, item, clip);
            return $"{file.Target.Name} now plays {outcome.From}: {outcome.Moved} curves fitted to this item"
                + (outcome.Unmatched.Count > 0 ? $", {outcome.Unmatched.Count} left out with nothing here to move." : ".")
                + (outcome.Notes is { Count: > 0 } n ? " " + string.Join(" ", n) : "");
        });
    }

    /// Puts the selected animation back to the item's own.
    [RelayCommand]
    private async Task RestoreAnimation()
    {
        if (SelectedWorkspace is not { } workspace || SelectedFile is not { } file) return;

        var slot = new AnimationSwap.Slot(file.RelativePath, file.Target);
        await Swap(workspace, file, "putting an animation back", (bundles, _) =>
        {
            AnimationSwap.Restore(bundles, workspace.Directory, slot);
            return $"{file.Target.Name} is the item's own again.";
        });
    }

    /// The glTF an animation was last read from, and the animations in it, for choosing another.
    [ObservableProperty] private string? _glbPath;

    public ObservableCollection<string> GlbTakes { get; } = [];

    [ObservableProperty] private string? _glbTake;

    [ObservableProperty] private bool _hasGlbTakes;

    /// While the list is being filled, choosing in it is the list being filled, not a person.
    private bool _listingTakes;

    /// Asks for a .glb to read. Set by the window, which is what owns a file picker.
    public Func<Task<string?>>? PickGlb { get; set; }

    /// Shows a file where it is, in Explorer — or, under the self-test, only says which.
    public Action<string> ShowFile { get; set; } = path =>
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    };

    /// Writes every animation, with the item's models, into one glTF to edit in another program,
    /// and shows where it went.
    [RelayCommand]
    private async Task ExportAnimations()
    {
        if (SelectedWorkspace is not { } workspace) return;

        string? written = null;
        await Run("writing the animations to edit", (bundles, catalogs) =>
        {
            var (path, outcome) = AnimationSwap.ExportGlb(bundles, catalogs, workspace.Directory);
            written = path;
            return $"Wrote {Path.GetRelativePath(workspace.Directory, path)}: {outcome.Animations} animations on "
                + $"{outcome.Objects} objects" + (outcome.Models.Count > 0 ? $", wearing {string.Join(", ", outcome.Models)}" : "")
                + ". Edit it in Blender, export it as glTF Binary (.glb), and Use a .glb puts an animation of it back.";
        });

        if (written is not null) ShowFile(written);
    }

    /// Puts in an animation out of a .glb the person picks.
    [RelayCommand]
    private async Task UseGlb()
    {
        if (PickGlb is null || await PickGlb() is not { } path) return;
        await UseGlbFile(path, take: null);
    }

    partial void OnGlbTakeChanged(string? value)
    {
        if (_listingTakes || value is null || GlbPath is not { } path) return;
        _ = UseGlbFile(path, value);
    }

    /// Puts an animation of this file into the selected slot: the one named `take`, or by default
    /// the one named like the slot. Also what dropping a .glb on the window does while an animation
    /// is selected.
    public async Task UseGlbFile(string path, string? take)
    {
        if (SelectedWorkspace is not { } workspace || SelectedFile is not { } file || !IsAnimation) return;

        IReadOnlyList<AnimationGlb.Take> takes;
        try { takes = AnimationGlb.Takes(path); }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException
                                      or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Status = ErrorLog.Said(e, "reading a glTF");
            return;
        }

        _listingTakes = true;
        try
        {
            GlbPath = path;
            GlbTakes.Clear();
            foreach (var t in takes) GlbTakes.Add(t.Name);
            HasGlbTakes = GlbTakes.Count > 1;
            GlbTake = take;
        }
        finally
        {
            _listingTakes = false;
        }

        var slot = new AnimationSwap.Slot(file.RelativePath, file.Target);
        string? chosen = null;
        await Swap(workspace, file, "putting in an animation from a glTF", (bundles, catalogs) =>
        {
            var outcome = AnimationSwap.UseGlb(bundles, catalogs, workspace.Directory, slot, path, take);
            chosen = outcome.From[(outcome.From.IndexOf(": ", StringComparison.Ordinal) + 2)..];
            var notes = outcome.Notes is { Count: > 0 } n ? " " + string.Join(" ", n) : "";
            return outcome.Unchanged
                ? $"{file.Target.Name} is unchanged: {outcome.From} is the animation it already plays."
                : $"{file.Target.Name} now plays {outcome.From}: {outcome.Moved} curves"
                  + (outcome.Unmatched.Count > 0 ? $", {outcome.Unmatched.Count} left out with nothing here to move." : ".")
                  + notes;
        });

        _listingTakes = true;
        try { GlbTake = chosen ?? GlbTake; }
        finally { _listingTakes = false; }
    }

    /// Puts dropped `.anim` files into the animations they are named for, each fitted to this item.
    private async Task UseAnimFiles(WorkspaceItem workspace, IReadOnlyList<(string Path, WorkspaceFile Slot)> files)
    {
        var said = await Run("putting in a dropped animation", (bundles, catalogs) =>
        {
            var lines = new List<string>();
            foreach (var (path, file) in files)
            {
                try
                {
                    var outcome = AnimationSwap.UseFile(bundles, catalogs, workspace.Directory,
                        new AnimationSwap.Slot(file.RelativePath, file.Target), path);
                    lines.Add(outcome.Unchanged
                        ? $"{file.Target.Name} is already that file."
                        : $"{file.Target.Name} now plays {outcome.From}: {outcome.Moved} curves"
                          + (outcome.Unmatched.Count > 0 ? $", {outcome.Unmatched.Count} left out with nothing here to move." : ".")
                          + (outcome.Notes is { Count: > 0 } n ? " " + string.Join(" ", n) : ""));
                }
                catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException
                                              or KeyNotFoundException or UnauthorizedAccessException)
                {
                    lines.Add($"{Path.GetFileName(path)} not taken: {e.Message}");
                }
            }
            return string.Join(" ", lines);
        });
        if (said is null) return;

        Refresh();
        if (SelectedFile is { } again) ShowAnimationChoice(again);
        Status = said;
    }

    private async Task Swap(
        WorkspaceItem workspace, WorkspaceFile file, string what, Func<BundleSet, GameCatalogs, string> work)
    {
        if (await Run(what, work) is not { } said) return;

        // Read again, so the file is marked edited (or not) and both halves are drawn afresh. That
        // says how many workspaces there are, which is not news; what was done is said after it.
        Refresh();
        if (SelectedFile is { } again) ShowAnimationChoice(again);
        Status = said;
    }

    /// Does something with the game open and nothing else reading it, and says what came of it.
    /// Null when it could not be done, which is then what the status says.
    private async Task<string?> Run(string what, Func<BundleSet, GameCatalogs, string> work)
    {
        await _reading.WaitAsync();
        try
        {
            if (_bundles() is not { } bundles || _catalogs() is not { } catalogs)
            {
                Status = "The game is not open.";
                return null;
            }
            return Status = await Task.Run(() => work(bundles, catalogs));
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or IOException
                                      or InvalidDataException or UnauthorizedAccessException or NotSupportedException
                                      or System.Text.Json.JsonException)
        {
            Status = ErrorLog.Said(e, what);
            return null;
        }
        finally
        {
            _reading.Release();
        }
    }

    /// An animation, played by the model it moves: the game's clip on one side and the workspace's
    /// on the other.
    ///
    /// Which model is the one of this workspace's meshes whose bones the clip moves — the gun, not
    /// the muzzle flash beside it — and of several, the largest. It is dressed from the workspace's
    /// own pictures, as the model itself is when it is selected.
    private async Task CompareAnimationAsync(BundleSet bundles, WorkspaceFile file)
    {
        var meshes = Files.Where(f => f.Target.Class == nameof(AssetClassID.Mesh)).ToList();
        var directory = SelectedWorkspace?.Directory;

        var loaded = await Task.Run(() =>
        {
            Motion? game = null;
            if (AssetPreview.Locate(bundles, file.Target.Container, AssetClassID.AnimationClip,
                    file.Target.PathId ?? 0, file.Target.Name) is var (opened, info)
                && bundles.Context.Deserialize(opened, info) is { } field)
                game = Motion.Read(field);

            Motion? disk = null;
            string? unreadable = null;
            try { disk = ClipFile.Read(file.FullPath).ToMotion(); }
            catch (Exception e) when (e is IOException or InvalidDataException) { unreadable = e.Message; }

            (UnityMesh Mesh, Skeleton Skeleton, WorkspaceFile File, long PathId)? model = null;
            foreach (var candidate in meshes)
            {
                if (AssetPreview.Locate(bundles, candidate.Target.Container, AssetClassID.Mesh,
                        candidate.Target.PathId ?? 0, candidate.Target.Name) is not var (meshFile, meshInfo)) continue;
                if (bundles.Context.Deserialize(meshFile, meshInfo) is not { } meshField) continue;
                if (AssetPreview.Mesh(meshField, bundles, candidate.Target.Container) is not { } mesh) continue;
                if (Skeleton.For(bundles, candidate.Target.Container, meshInfo.PathId) is not { } skeleton) continue;
                if (!(game is not null && skeleton.Moves(game)) && !(disk is not null && skeleton.Moves(disk))) continue;
                if (model is null || mesh.VertexCount > model.Value.Mesh.VertexCount)
                    model = (mesh, skeleton, candidate, meshInfo.PathId);
            }

            var standing = model is { } m
                ? Facing.Standing(bundles, m.File.Target.Container, m.Skeleton.Assembled(m.Mesh), m.PathId)
                : (MeshRenderer.Basis?)null;

            return (Game: game, Disk: disk, Unreadable: unreadable, Model: model, Standing: standing);
        });

        if (SelectedFile != file) return;

        if (loaded.Model is not { } found)
        {
            Original.Clear("No model in this workspace is moved by this animation.");
            Edited.Clear(loaded.Unreadable ?? "No model in this workspace is moved by this animation.");
            return;
        }

        var worn = directory is null ? null : Dressed(found.File, directory);
        _pairing = true;
        try
        {
            // Captioned by the file, as every other comparison here is: the caption is how anything
            // waiting on a pane tells which file it has finished with.
            Play(Original, found, loaded.Game, worn, $"{file.Name} in the game", "game", loaded.Standing);
            if (loaded.Disk is null) Edited.Clear($"{file.Name} is unreadable: {loaded.Unreadable}");
            else Play(Edited, found, loaded.Disk, worn,
                file.Edited ? $"{file.Name} as edited" : $"{file.Name} unchanged", "disk", loaded.Standing);
        }
        finally
        {
            _pairing = false;
        }
        Pair();
    }

    private static void Play(
        PreviewViewModel preview, (UnityMesh Mesh, Skeleton Skeleton, WorkspaceFile File, long PathId) model,
        Motion? motion, IReadOnlyList<PreviewImage?>? worn, string caption, string side, MeshRenderer.Basis? standing)
    {
        preview.Show(model.Mesh, caption, worn, $"anim:{side}:{model.File.FullPath}",
            model.Skeleton, motion is null ? [] : [motion], standing);
        // Selecting a clip is what starts it, and an animation is shown to be watched.
        preview.Clip = preview.Clips.FirstOrDefault();
    }
}
