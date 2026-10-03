using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using PGAssetTool.Core.Settings;
using PGAssetTool.Core.Pack;
using PGAssetTool.Core.Preview;
using PGAssetTool.Gui.ViewModels;

namespace PGAssetTool.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Clicking a row and opening it are one gesture. Avalonia expands only on the chevron,
        // which is a second, smaller target for something this tool asks for constantly. Handled
        // here rather than on selection, because re-clicking an already selected row must still
        // fold it away.
        AddHandler(InputElement.TappedEvent, OnTapped, RoutingStrategies.Bubble);
        AddHandler(InputElement.DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble);

        // Space plays the clip in front of you. Not a menu shortcut: registering it as one would
        // take the space bar away from the search box and from every button that a keyboard user
        // presses with it. Tunnelled so it is seen before a focused control acts on it, and given
        // straight back whenever typing is what space means.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        // Ctrl and the wheel resize the tiles. Seen on the way down, because the list would
        // otherwise scroll on the same gesture and the tiles would resize under a moving view.
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);

        // Files dropped anywhere on the window. Where they land is decided by what they are and
        // which workspace is open rather than by which pixel was under the pointer: the panes are
        // large, the answer is the same everywhere in them, and a drop that lands two pixels
        // outside a target and does nothing is the worst version of this.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Shift changes what the manager's Remove button says it will do, so the window has to know
        // while it is held rather than at the moment of the click. Tunnelled and never handled:
        // this only watches. Released on losing focus as well, because a window that is not in
        // front does not see the key come back up, and the button would have stayed changed.
        AddHandler(KeyDownEvent, (_, e) => Shift(e.KeyModifiers), RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) => Shift(e.KeyModifiers), RoutingStrategies.Tunnel);
        Deactivated += (_, _) => Shift(KeyModifiers.None);

        // Something inside the window has to hold the keyboard for a key press to have anywhere to
        // travel from. Freshly opened, nothing did, and every shortcut stayed dead until a click
        // landed somewhere. The weapon list is the right thing to hand it to: the arrow keys then
        // walk the list, which is what a person reaches for first anyway.
        Opened += (_, _) => this.FindControl<ListBox>("Weapons")?.Focus();

        // What the view asks for, read before anything has had a chance to change it: the size the
        // layout below was drawn against, at a scale of 100.
        (_designedWidth, _designedHeight) = (Width, Height);

        // Everything else the menu does is a command on the model. Focus is the exception: the
        // control belongs to the view, so the model only reports that someone asked for it.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel model) return;

            model.SearchRequested += FocusSearch;

            // A file picker belongs to the window; the editor only asks for a file.
            model.Editor.PickGlb = async () =>
            {
                var chosen = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "An animation to use",
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("glTF Binary") { Patterns = ["*.glb"] }],
                });
                return chosen.Count > 0 ? chosen[0].TryGetLocalPath() : null;
            };

            // The scale is read out of the settings after the window is up, so this is also how it
            // is applied at startup rather than only when somebody changes it.
            model.PropertyChanged += (_, changed) =>
            {
                if (changed.PropertyName == nameof(MainViewModel.UiScale)) GrowToFit(model.Zoom);
            };
            GrowToFit(model.Zoom);

            // Animations play on this window's own frames, by the time that really passed between
            // them — so a 120Hz screen shows 120 steps a second rather than a fixed thirty.
            foreach (var preview in new[] { model.Preview, model.Editor.Original, model.Editor.Edited })
                preview.RequestFrame = frame => RequestAnimationFrame(frame);

            // A file watcher fires on its own thread; everything it leads to touches the UI.
            model.Editor.Settled += () => Avalonia.Threading.Dispatcher.UIThread.Post(model.Editor.Refresh);

            // Whether the game is running decides what half this window offers will do, and it was
            // read only when the manager last refreshed — so starting the game after that left
            // every button still offering to write to it.
            _watchingTheGame?.Stop();
            _watchingTheGame = new Avalonia.Threading.DispatcherTimer(
                TimeSpan.FromSeconds(2), Avalonia.Threading.DispatcherPriority.Background,
                (_, _) => model.Manager.NoticeTheGame());
            _watchingTheGame.Start();
        };
    }

    /// Polls whether the game is running. Stopped when the window goes, or a closed window would
    /// keep a model alive and keep asking Windows about processes on its behalf.
    private Avalonia.Threading.DispatcherTimer? _watchingTheGame;

    /// The size the view asks for, which is the size the layout was drawn at 100% against.
    private readonly double _designedWidth, _designedHeight;

    /// Gives a window drawn larger than 100% the room to draw it in.
    ///
    /// The content of a window at 150% is given two thirds of it, which is less than the layout was
    /// drawn for — so the same window at a larger scale shows a cramped version of itself rather
    /// than a larger one. Grown from the designed size rather than from the current one, so choosing
    /// 125 and then 150 does not multiply twice.
    ///
    /// Capped to the screen, since the reason for a larger scale is often a screen that cannot take
    /// a larger window; and never made smaller, because turning the scale back down while shrinking
    /// the window would be deciding for somebody how much they want to see at once.
    ///
    /// Only the window the application itself opened. The self-test builds windows at exact sizes
    /// and then measures what fits in them.
    private void GrowToFit(double zoom)
    {
        if (!ReferenceEquals(
                this,
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow))
            return;

        if (zoom <= 1 || Screens.ScreenFromWindow(this) is not { } screen) return;

        var room = screen.WorkingArea;
        Width = Math.Max(Width, Math.Min(_designedWidth * zoom, room.Width / screen.Scaling * 0.95));
        Height = Math.Max(Height, Math.Min(_designedHeight * zoom, room.Height / screen.Scaling * 0.9));
    }

    protected override void OnClosed(EventArgs e)
    {
        _watchingTheGame?.Stop();
        _watchingTheGame = null;
        base.OnClosed(e);
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// A pack is installed; anything else goes to the workspace the editor has open.
    ///
    /// Decided by what the file is rather than by which pane it landed on. A .pgmod is a mod
    /// wherever it is dropped, and there is only one thing to do with one; a texture is an edit to
    /// whatever is being worked on, and there is only one workspace that could mean.
    /// Nothing waits for a handler like this, so anything it throws goes past the window rather than
    /// into it. What lands here is whatever somebody dragged in — a pack from anywhere, a folder,
    /// a file that is none of those — which makes it one of the likelier places to be handed
    /// something unexpected.
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;

        try { await Dropped(model, e); }
        catch (Exception ex) { model.Status = ErrorLog.Said(ex, "opening what was dropped on the window"); }
    }

    private static async Task Dropped(MainViewModel model, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList()
            is not { Count: > 0 } paths)
            return;

        e.Handled = true;

        var packs = paths.Where(p => p.EndsWith(PackBuilder.Extension, StringComparison.OrdinalIgnoreCase)).ToList();
        var rest = paths.Except(packs).ToList();

        if (rest.Count > 0)
        {
            model.Workspace = MainViewModel.EditorTab;
            model.Editor.Import(rest);
        }

        if (packs.Count == 0) return;

        // Shown before it starts: installing is a whole-game rebuild, and watching the manager sit
        // there is better than watching a tab that says nothing about what is happening.
        model.Workspace = MainViewModel.ManagerTab;
        await model.InstallPacks(packs);
        model.Manager.Refresh();
    }

    private void Shift(KeyModifiers modifiers)
    {
        if (DataContext is MainViewModel model)
            model.Manager.ShiftHeld = modifiers.HasFlag(KeyModifiers.Shift);
    }

    private static void OnTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control control) return;
        if (control.FindAncestorOfType<TreeViewItem>() is not { } row) return;

        switch (row.DataContext)
        {
            // A skin is the one row where opening it and looking at it are different things.
            // Clicking one shows the weapon wearing it, which is what somebody going down the list
            // of skins wants from every one of them; unfolding it into its materials and textures
            // is a separate question, and asking it of every glance made the tree unusable as a
            // list. That one takes a double click.
            case TreeNode { Skin: not null }:
                break;

            // The asset tree keeps its own expansion, because rebuilding it must not fold up what
            // somebody had opened.
            case TreeNode { HasChildren: true } node:
                node.IsExpanded = !node.IsExpanded;
                break;

            // The manager's shelf is rebuilt from scratch on every refresh, so there is nothing on
            // it worth remembering an expansion in; the row itself lasts as long as the answer does.
            case ModFolder { Children.Count: > 0 }:
                row.IsExpanded = !row.IsExpanded;
                break;
        }
    }

    /// The other half of the rule above: the rows a single click does not open, a double click does.
    private static void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control control) return;
        if (control.FindAncestorOfType<TreeViewItem>()?.DataContext is TreeNode { Skin: not null } skin)
            skin.IsExpanded = !skin.IsExpanded;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;

        // The one shortcut that cannot be a key binding on the window: opening a dialog needs the
        // window to be its owner, which is the view's business rather than the model's.
        if (e is { Key: Key.OemComma, KeyModifiers: KeyModifiers.Control })
        {
            OnOptions(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Whatever a key means to something being typed into, it means that. This is a handler
        // rather than a key binding because a binding on the window runs before the focused
        // control sees the key at all, and the shortcuts here are all ones something else has a
        // better claim to first.
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        // Ctrl+A is left alone on purpose: it belongs to whatever is focused, which is a list to
        // select the whole of or a box to select the text of. Alpha sits on Shift+A instead.
        if (e is { Key: Key.A, KeyModifiers: KeyModifiers.Shift })
        {
            model.ToggleAlphaCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers != KeyModifiers.None) return;

        switch (e.Key)
        {
            case Key.Space when model.Showing is { HasSound: true } preview:
                preview.PlayOrStop();
                e.Handled = true;
                break;

            // Every model pane on the page, because both halves of the editor's comparison show
            // one and straightening only the half that happens to be in front would leave the two
            // at different angles — which is the one thing that comparison exists to avoid.
            case Key.R:
                foreach (var view in this.GetVisualDescendants().OfType<Controls.MeshView>())
                {
                    view.Recentre();
                    e.Handled = true;
                }
                break;

            // What Delete means depends on what is in front of you, and on both tabs it is the
            // thing the tab's own button says: the workspace, or the mod. Both ask first, so the
            // key reaches a question rather than a deletion.
            case Key.Delete when model.Workspace == MainViewModel.EditorTab:
                model.Editor.DeleteCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Delete when model.Workspace == MainViewModel.ManagerTab:
                model.Manager.RemoveCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (DataContext is not MainViewModel model) return;

        // Only over the tiles. Ctrl and the wheel mean nothing anywhere else in this window, and
        // swallowing the gesture over a list that scrolls would be worse than ignoring it.
        if (e.Source is not Control over
            || this.FindControl<ListBox>("Installed") is not { } tiles
            || (!ReferenceEquals(over, tiles) && !over.GetVisualAncestors().Contains(tiles)))
            return;

        model.Manager.ResizeTiles(e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
    }

    /// Hands the keyboard to whichever search the tab in front has.
    ///
    /// Two tabs have one and they are different searches — the weapons, and what is installed. The
    /// editor has none, and moving the keyboard somewhere that is not a search box would be worse
    /// than the shortcut doing nothing.
    private void FocusSearch()
    {
        if (DataContext is not MainViewModel model) return;

        var box = model.Workspace switch
        {
            MainViewModel.BrowseTab => this.FindControl<TextBox>("Search"),
            MainViewModel.ManagerTab => this.FindControl<TextBox>("ShelfSearch"),
            _ => null,
        };

        box?.Focus();
    }

    /// Turns whichever model pane the editor is showing into the pack's icon.
    ///
    /// The camera lives in the control, because a view angle is a property of looking rather than
    /// of the model — so the picture is taken here and handed to the editor, which knows where it
    /// belongs. The visible pane, not the first one: side by side puts two on the page and the one
    /// under the pointer is not necessarily the one in front.
    private void OnCaptureIcon(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;

        var pane = this.GetVisualDescendants().OfType<Controls.MeshView>()
            .FirstOrDefault(v => v.IsEffectivelyVisible && v.Mesh is not null);

        if (pane?.Snapshot(PackIcon.Size) is not { } picture)
        {
            model.Editor.Status = "There is no model on show to make an icon out of.";
            return;
        }

        model.Editor.CaptureIcon(picture);
    }

    /// Here rather than in the model because the clipboard belongs to the window.
    private async void OnCopyForumPost(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        var editor = model.Editor;

        try
        {
            if (await editor.ForumPostAsync() is not { } post) { editor.Status = "Nothing selected."; return; }
            if (Clipboard is not { } clipboard) { editor.Status = "There is no clipboard to copy to."; return; }

            await clipboard.SetTextAsync(post);
            editor.Status = post.Length > ForumPost.Limit
                ? $"Copied — but it is {post.Length} characters, and Discord takes {ForumPost.Limit}. Shorten the description."
                : "Copied the Discord post.";
        }
        catch (Exception ex)
        {
            editor.Status = ErrorLog.Said(ex, "copying the Discord post");
        }
    }

    private void OnOptions(object? sender, RoutedEventArgs e)
        => new OptionsWindow { DataContext = DataContext }.ShowDialog(this);

    private void OnExit(object? sender, RoutedEventArgs e) => Close();
}
