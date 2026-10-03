using AssetsTools.NET.Extra;
using Avalonia;
using Avalonia.Headless;
using Avalonia.VisualTree;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Export;
using PGAssetTool.Core.Mods;
using PGAssetTool.Core.Preview;
using PGAssetTool.Gui.ViewModels;

namespace PGAssetTool.Gui;

/// Exercises the view models against the real game without opening a window.
///
/// Everything the GUI does before a person touches it — find the installation, load the catalogs,
/// list the weapons, resolve one and build its tree — happens here too, so a break in that path
/// shows up as a failed run rather than a window that opens empty.
internal static class SelfTest
{
    private const int AttachParentProcess = -1;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);


    /// <param name="game">
    /// The installation to test against, or null for the one the tool would find on its own. A copy
    /// of the game is the safer thing to point this at: it installs and removes for real.
    /// </param>
    public static int Run(string? game = null)
    {
        // A WinExe starts with no console, so a published build writing to one would print into
        // nowhere. Borrowing the terminal that launched it is what makes --self-test usable on the
        // thing that actually ships, rather than only under `dotnet run`.
        // Only when nothing is already capturing the output. A pipe or a redirect means someone is,
        // and attaching replaces the handles underneath them — which swallows every line rather
        // than printing it.
        if (!Console.IsOutputRedirected) AttachConsole(AttachParentProcess);

        // The tree labels carry emoji; the Windows console defaults to a code page that mangles them.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }
        // Bitmaps need a rendering backend even when nothing is shown, so the headless one stands
        // in for a window. Everything below it is the same code the real app runs.
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();

        // Avalonia's dispatcher stays the synchronization context, as it is in the real app, and
        // WaitWhile runs it. Clearing it instead put every continuation on the thread pool, which
        // works right up until one of them touches a collection a control is bound to — and then
        // fails with "call from invalid thread" somewhere far from the cause. The whole point of
        // this test is to be the app, so it waits the way the app does.
        return Body(game);
    }


    /// What this test's own pack is called wherever it turns up: in the workspace list, in the
    /// manager, in the mods folder. Nothing a person would build is named this, so nothing this test
    /// removes can be theirs by accident.
    private const string PackName = "PGAssetTool self test";
    private const string PackIdentity = "pgassettool-selftest";
    private const string PackFolder = "pgassettool-selftest";

    /// A second, so batch operations have more than one thing to act on.
    private const string PackNameB = "PGAssetTool self test B";
    private const string PackIdentityB = "pgassettool-selftest-b";
    private const string PackFolderB = "pgassettool-selftest-b";

    /// Waits for something to settle while running the dispatcher, the way a live window does.
    ///
    /// Once a window has been created, Avalonia's dispatcher is the synchronization context that
    /// awaits are posted back to. A test that only sleeps never runs those, so anything that
    /// awaited while the window was up stops there for good — still holding whatever lock it took.
    /// That looked exactly like a deadlock in the tool, and was not one.
    /// Waits for one task, running the dispatcher meanwhile, and rethrows what it threw.
    ///
    /// Blocking on it instead would deadlock: its continuations are posted to this very thread.
    private static void Settle(Task task, string what)
    {
        if (!WaitWhile(() => !task.IsCompleted, 300_000))
            throw new TimeoutException($"{what} did not finish");
        task.GetAwaiter().GetResult();
    }

    private static bool WaitWhile(Func<bool> busy, int milliseconds)
    {
        for (var waited = 0; waited < milliseconds; waited += 25)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (!busy()) return true;
            Thread.Sleep(25);
        }
        return false;
    }

    private static int Body(string? game)
    {
        // Its own directory, thrown away afterwards. This test extracts, edits, packs and installs
        // for real; doing that in the author's workspace put its scratch work in the same list as
        // theirs and, when the selection drifted, packed one of theirs instead.
        var scratch = Directory.CreateTempSubdirectory("pgassettool-selftest").FullName;

        // The game asked for goes into this run's own preferences, which is where the window reads
        // the game folder from — so it is opened exactly as the Options setting would open it.
        if (game is not null)
            new PGAssetTool.Core.Settings.ToolSettings { GameDirectory = Path.GetFullPath(game) }.Save(scratch);
        var model = new MainViewModel { WorkspaceOverride = scratch, SettingsHome = scratch };

        // Held out here so the tidying up on the way out knows what the author's game looked like
        // before this run, however far the run got. Null until the ledger has been read once.
        HashSet<string>? wasInstalled = null;
        HashSet<string>? wasEnabled = null;

        // The author's log folder as it stands. This run reports errors on purpose — a refused pack,
        // a missing file — and every one of them is written to a log; they belong in its own.
        var theirLogs = Path.Combine(ModStore.DefaultHome(), PGAssetTool.Core.Settings.ErrorLog.FolderName);
        string LogsNow() => Directory.Exists(theirLogs)
            ? string.Join("\n", Directory.EnumerateFiles(theirLogs).Order().Select(f => $"{f} {new FileInfo(f).Length}"))
            : "";
        var theirLogsBefore = LogsNow();

        // A crash report waiting from a session that never came back, as a crash leaves it. Opening
        // must say so, once, and name it.
        Directory.CreateDirectory(Path.Combine(scratch, PGAssetTool.Core.Settings.ErrorLog.FolderName));
        var planted = Path.Combine(scratch, PGAssetTool.Core.Settings.ErrorLog.FolderName, "crash-20260101-000000.txt");
        File.WriteAllText(planted, "a crash that happened before this run");

        try
        {
            Settle(model.LoadAsync(), "opening the game");
            Console.WriteLine($"status   {model.Status}");
            Console.WriteLine($"game     {model.Game?.RootDirectory ?? "(none)"}");

            Console.WriteLine($"crash    notice: {model.CrashNotice ?? "(none)"}");
            if (model.CrashNotice is not { } notice || !notice.Contains(Path.GetFileName(planted)))
                return Fail("a crash report left by the last session was not announced");
            if (PGAssetTool.Core.Settings.ErrorLog.UntoldCrash() is not null)
                return Fail("the same crash report would be announced again at the next start");

            // Before anything, not after. This test writes to the game for real, and it used to ask
            // this question only once it reached the manager — by which point it had already
            // installed two mods into a game somebody was playing.
            if (model.Game is { } open && PGAssetTool.Core.Game.GameProcess.IsRunning(open))
                return Fail("the game is running; close it before running a test that writes to it");

            // The author's own preferences, as they stand. This test drives the filter, the language
            // and the alpha toggle, and each of those is written back the moment it moves — so runs
            // were quietly rewriting the settings file beside the executable, and one left the asset
            // tree filter switched off. Compared again at the end rather than trusted.
            var theirSettings = PGAssetTool.Core.Settings.ToolSettings.PathIn(ModStore.DefaultHome());
            var theirPreferences = File.Exists(theirSettings) ? File.ReadAllText(theirSettings) : null;
            Console.WriteLine($"status   preferences for this run: {model.SettingsPath}");

            if (ReaderReleasesItsBundles(model) is { } stillOpen) return Fail(stillOpen);

            if (ABundleSurvivesBeingWrittenBothWays(model, scratch) is { } packProblem) return Fail(packProblem);

            if (LeavingBundlesAloneArrivesWhereRebuildingDoes(model) is { } shortcutProblem)
                return Fail(shortcutProblem);
            Console.WriteLine($"weapons  {model.Weapons.Count}");
            if (model.Weapons.Count == 0) return Fail("the catalog produced no weapons");

            if (EveryKindOpens(model, scratch) is { } kindProblem) return Fail(kindProblem);

            if (WhichMeshIsTheWeapon(model) is { } wrongMesh) return Fail(wrongMesh);

            if (EveryWeaponHasAModelToShow(model) is { } modelProblem) return Fail(modelProblem);

            if (TheModelPlaysItsAnimations(model) is { } movingProblem) return Fail(movingProblem);
            if (SkinModelsMoveWithoutClipsOrBonesOfTheirOwn(model) is { } stillProblem) return Fail(stillProblem);
            if (HalfFloatNormalsAreReadAsNormals(model) is { } normalProblem) return Fail(normalProblem);
            if (ModelsAreShownAssembled(model) is { } piecesProblem) return Fail(piecesProblem);

            model.Search = "beretta";
            Console.WriteLine($"search   'beretta' -> {model.Weapons.Count}");

            model.Search = "";
            model.Search = "";
            if (!Select(model, 16)) return Fail($"#16 never resolved: {model.Status}");

            var detail = model.Detail!;
            Console.WriteLine($"selected {detail.Name} — {detail.Subtitle}");
            Console.WriteLine($"selected {detail.Name} — {detail.Subtitle}");

            foreach (var root in detail.Roots)
            {
                Console.WriteLine($"  {root.Icon} {root.Label}  {root.Detail}");
                foreach (var child in root.Children.Take(6))
                    Console.WriteLine($"      {child.Icon} {child.Label}  {child.Detail}");
                if (root.Children.Count > 6) Console.WriteLine($"      … {root.Children.Count - 6} more");
            }

            if (detail.Roots.Count == 0) return Fail("the tree came out empty");

            // The previews are the half of the GUI a self-test can still check: decoding a texture
            // and unpacking a mesh happen in the core, and only the drawing needs a window.
            foreach (var want in new[] { AssetClassID.Texture2D, AssetClassID.Mesh, AssetClassID.AudioClip })
            {
                var node = detail.Roots
                    .SelectMany(r => r.Children)
                    .SelectMany(g => g.Children)
                    .FirstOrDefault(n => n.Class == want && n.PathId != 0);

                if (node is null) { Console.WriteLine($"preview  no {want} to try"); continue; }

                // Cleared first, or the wait below would pass instantly on the previous asset.
                model.Preview.Clear();
                detail.SelectedNode = node;
                Arrived(model, node);

                if (model.Preview.Nothing is { } why) return Fail($"{want} '{node.Label}': {why}");
                Console.WriteLine($"preview  {model.Preview.Caption}");

                // A texture reached through a material carries something other than coverage in its
                // alpha — emission, usually — so honouring it would punch holes in the picture.
                if (want == AssetClassID.Texture2D && model.Preview.ShowAlpha)
                    return Fail($"'{node.Label}' is a model texture and was shown with alpha honoured");

                if (want == AssetClassID.AudioClip && model.Preview.Sound is { } clip)
                {
                    // The envelope is what the pane draws; a clip that decodes to silence would
                    // otherwise look like a working preview of a flat line.
                    var envelope = clip.Envelope(320);
                    var loudest = envelope.Max(c => Math.Max(Math.Abs(c.Low), Math.Abs(c.High)));
                    Console.WriteLine($"         {clip.Frames:N0} frames, peak {loudest:0.00}, "
                        + $"{clip.ToWave().Length:N0} bytes as a wave");

                    if (clip.Frames == 0) return Fail($"'{node.Label}' decoded to no audio at all");
                    if (loudest <= 0) return Fail($"'{node.Label}' decoded to silence");

                    // What the player is handed has to be a wave Windows will take; the exporter
                    // used to produce one that declared itself empty.
                    var wave = clip.ToWave();
                    var again = PGAssetTool.Core.Import.Audio.WaveFile.Parse(wave, "the preview");
                    if (again.Frames != clip.Frames)
                        return Fail($"the wave handed to the player holds {again.Frames} of {clip.Frames} frames");

                    // The waveform draws a line at where the clip has got to, which needs the
                    // speaker to say two things: which clip is sounding, and how far through it is.
                    // Two waveforms are on the page in the editor and one of them is silent.
                    model.Preview.PlayOrStop();
                    var sounding = ReferenceEquals(Audio.Speaker.Sounding, clip);
                    var start = Audio.Speaker.Through;
                    Thread.Sleep(60);
                    var later = Audio.Speaker.Through;
                    model.Preview.PlayOrStop();

                    Console.WriteLine($"         playing: this clip {sounding}, "
                        + $"through {start:0.000} -> {later:0.000}, stopped {!Audio.Speaker.IsPlaying}");

                    if (!sounding) return Fail("the speaker does not say which clip it is playing");
                    if (later <= start) return Fail("the playing position did not move");
                    if (Audio.Speaker.IsPlaying) return Fail("stopping did not stop it");
                    if (Audio.Speaker.Sounding is not null)
                        return Fail("a stopped speaker still names a clip");
                }

                if (want == AssetClassID.Mesh && model.Preview.Mesh is { } mesh)
                {
                    // Drawn the way the control would, so a rasterizer that throws or leaves an
                    // empty image is caught here rather than by a person looking at a blank pane.
                    var target = new RenderTarget();
                    target.Resize(320, 320);
                    MeshRenderer.Render(mesh, new Camera(), target, model.Preview.MeshTextures);

                    var drawn = 0;
                    var coloured = 0;
                    for (var i = 0; i < target.Bgra.Length; i += 4)
                    {
                        if (target.Bgra[i + 3] == 0) continue;
                        drawn++;
                        // Untextured shading writes equal channels; a texture almost never does.
                        if (target.Bgra[i] != target.Bgra[i + 1] || target.Bgra[i + 1] != target.Bgra[i + 2])
                            coloured++;
                    }

                    var slots = model.Preview.MeshTextures;
                    Console.WriteLine($"         rasterised {drawn:N0} of {320 * 320:N0} pixels, "
                        + $"{coloured:N0} of them coloured; textures "
                        + $"{(slots is null ? "none" : string.Join(", ", slots.Select(s => s is null ? "-" : $"{s.Width}x{s.Height}")))}");

                    if (drawn == 0) return Fail($"'{node.Label}' rendered to an empty image");
                    if (slots?.Any(s => s is not null) == true && coloured == 0)
                        return Fail($"'{node.Label}' has a texture but drew in flat grey");

                    if (TheMaskKeepsWhatTheModelShows(model) is { } maskProblem) return Fail(maskProblem);

                    // The list a weapon's own mesh is offered comes in three bands: what it is
                    // already drawn with, then the paint a skin would put on this same geometry —
                    // what the mesh will be seen wearing in the game — then everything else the
                    // weapon reaches. Ordering alone is a weak signal, so the worn ones are marked
                    // as well and the marks have to be the right ones.
                    var own = detail.Tree.MeshTextures
                        .FirstOrDefault(m => m.MeshPathId == node.PathId)?.BySubMesh
                        .Where(t => t is not null).Select(t => (t!.Bundle, t.PathId)).ToHashSet() ?? [];
                    // A named angle puts the model back somewhere exact, which turning by hand is a
                    // poor way to reach — and it leaves the framing alone, because how close in
                    // somebody has come is theirs and which way it faces is the question here.
                    model.Preview.Camera = (Camera.Facing(2.2f, -0.4f, 1.1f) with { Distance = 3f });
                    if (model.Preview.Views.FirstOrDefault(v => v.Name == "Top") is { } top)
                    {
                        model.Preview.LookCommand.Execute(top);
                        var looking = model.Preview.Camera;
                        var (yaw, pitch, roll) = looking.Angles;

                        Console.WriteLine($"         the Top preset: yaw {yaw:0.00} "
                            + $"pitch {pitch:0.00} roll {roll:0.00} distance {looking.Distance:0.00}");

                        // Compared as a frame rather than as three numbers. Straight up is a pole of
                        // that reading: forward is the vertical there, so the yaw is whatever the
                        // arithmetic of atan2(0, 0) lands on and the roll takes up the slack. The
                        // view is right — it comes back as yaw 3.14 with a roll of 3.14, which is
                        // the same view as yaw 0 with no roll — and asking the numbers to be tidy
                        // asks for something a pole cannot give.
                        var asked = Core.Preview.MeshRenderer.View(
                            Camera.Facing(top.Yaw, top.Pitch, top.Roll));
                        var got = Core.Preview.MeshRenderer.View(looking);

                        if (Math.Abs(asked.Rx - got.Rx) > 0.001f || Math.Abs(asked.Ry - got.Ry) > 0.001f
                            || Math.Abs(asked.Rz - got.Rz) > 0.001f || Math.Abs(asked.Ux - got.Ux) > 0.001f
                            || Math.Abs(asked.Uy - got.Uy) > 0.001f || Math.Abs(asked.Uz - got.Uz) > 0.001f
                            || Math.Abs(asked.Fx - got.Fx) > 0.001f || Math.Abs(asked.Fy - got.Fy) > 0.001f
                            || Math.Abs(asked.Fz - got.Fz) > 0.001f)
                            return Fail("the Top preset did not face the model the way it asked to");

                        if (Math.Abs(got.Fy - 1f) > 0.001f)
                            return Fail($"the Top preset is not looking down: forward is {got.Fy}");
                        if (looking.Distance != 3f) return Fail("a named angle threw away the framing");
                    }

                    var mains = detail.Tree.Skins
                        .Where(s => s.Model is null)
                        .SelectMany(s => s.Materials)
                        .Where(m => m.Main is not null)
                        .Select(m => m.Locate(m.Main!))
                        .ToHashSet();

                    // Every texture the tree files under the skins, read off the rows rather than
                    // out of the model the ordering is built from — so what the ordering brings up
                    // has to be something the tree agrees is there, at the same address.
                    var painted = new HashSet<(string, long)>();
                    void Paints(TreeNode n)
                    {
                        if (n.Class == AssetClassID.Texture2D) painted.Add((n.Bundle, n.PathId));
                        foreach (var child in n.Children) Paints(child);
                    }
                    if (detail.Roots.FirstOrDefault(r => r.Label == "Skins") is { } skinRoot)
                        Paints(skinRoot);

                    var choices = model.Preview.TextureChoices.Skip(1).ToList();
                    var bands = choices
                        .Select(c => c.Worn ? 0 : mains.Contains((c.Bundle, c.PathId)) ? 1 : 2)
                        .ToList();

                    Console.WriteLine($"         offered {choices.Count}: {bands.Count(b => b == 0)} worn, "
                        + $"{bands.Count(b => b == 1)} a skin, {bands.Count(b => b == 2)} other");

                    for (var i = 1; i < bands.Count; i++)
                        if (bands[i] < bands[i - 1])
                            return Fail($"'{choices[i].Name}' is offered below something less likely to be wanted");

                    if (choices.Any(c => c.Worn != own.Contains((c.Bundle, c.PathId))))
                        return Fail("what the list marks as worn is not what the mesh is drawn with");

                    // The marks are what the order is made of, so they have to say the same thing.
                    if (choices.Where((_, i) => bands[i] == 1).Any(c => !c.Skin)
                        || choices.Any(c => c.Skin && c.Worn))
                        return Fail("the list is ordered by one answer and lit by another");

                    if (own.Count > 0 && !bands.Contains(0))
                        return Fail($"'{node.Label}' is drawn with textures and none of them is marked");

                    // The address check, and the one the spelling bug failed: a texture in the same
                    // file as the material naming it carries no bundle of its own, and while the
                    // tree filled that in from the material the ordering did not, so every skin
                    // failed to match anything and the whole band stayed empty.
                    var brought = choices.Where((_, i) => bands[i] == 1).ToList();
                    if (brought.FirstOrDefault(c => !painted.Contains((c.Bundle, c.PathId))) is { } odd)
                        return Fail($"'{odd.Name}' was brought up as a skin and the tree files no such row");

                    if (mains.Count > 0 && brought.Count == 0 && !bands.Contains(0))
                        return Fail("this weapon's skins paint it and none of them was brought up");

                    // Wearing a texture chosen by hand is how a skin gets tried on a model the
                    // automatic answer knows nothing about. It silently did nothing: the choice
                    // carried its picture, filling that in made a different value, and the combo
                    // box dropped a selection it could no longer find in its own list.
                    if (model.Preview.TextureChoices.FirstOrDefault(c => c.PathId != 0) is { } wanted)
                    {
                        model.Preview.ChosenTexture = wanted;
                        WaitWhile(() => model.Preview.MeshTextures == slots, 30_000);

                        var worn = model.Preview.MeshTextures;
                        Console.WriteLine($"         wearing '{wanted.Name}': "
                            + $"{(worn is null ? "nothing" : string.Join(", ", worn.Select(s => s is null ? "-" : $"{s.Width}x{s.Height}")))}");

                        if (model.Preview.ChosenTexture != wanted)
                            return Fail("the chosen texture did not stay chosen");
                        if (worn is null || worn.Any(s => s is null))
                            return Fail($"'{wanted.Name}' was chosen and the model is still wearing the old one");
                        if (worn.Distinct().Count() != 1)
                            return Fail("a texture chosen by hand has to cover the whole model");

                        model.Preview.ChosenTexture = model.Preview.TextureChoices[0];
                        if (model.Preview.MeshTextures != slots)
                            return Fail("going back to the automatic answer did not restore it");
                    }
                }
            }

            // The icon is the one thing addressed by name rather than by path id — the lookup table
            // records no id for it — and for the newest weapons it lives in the game's own
            // resources.assets rather than a bundle. It was silently unpreviewable.
            var icon = detail.Roots
                .FirstOrDefault(r => r.Label == "Icon")?.Children.FirstOrDefault();

            if (icon is null) Console.WriteLine("preview  this weapon has no icon");
            else
            {
                model.Preview.Clear();
                detail.SelectedNode = icon;
                Arrived(model, icon);

                if (model.Preview.Nothing is { } why) return Fail($"icon '{icon.Label}': {why}");
                Console.WriteLine($"preview  {model.Preview.Caption}");
                Console.WriteLine($"         alpha honoured: {model.Preview.ShowAlpha} (an icon sits on nothing)");

                if (!model.Preview.ShowAlpha) return Fail("an icon was shown with its alpha ignored");

                // Automatic until somebody disagrees, and then their answer for everything after.
                // Re-deciding per asset meant an icon came back with its alpha honoured however
                // many times in a row it had just been turned off.
                model.ToggleAlphaCommand.Execute(null);
                if (model.Preview.ShowAlpha) return Fail("the alpha toggle did not turn off");

                detail.SelectedNode = detail.Roots[0];
                detail.SelectedNode = icon;
                Arrived(model, icon);

                Console.WriteLine($"         after turning it off, the next picture kept it off: "
                    + $"{!model.Preview.ShowAlpha}");
                if (model.Preview.ShowAlpha)
                    return Fail("the alpha choice was thrown away when the next picture loaded");

                model.ToggleAlphaCommand.Execute(null);
            }

            // The filter is what the tree shows by default, and it has to come from the import
            // registry rather than a list kept here, so a type gained later needs no edit.
            var everything = CountRows(detail.Roots);
            model.ReplaceableOnly = false;
            if (model.Detail is not { } unfiltered) return Fail("turning the filter off lost the tree");
            var all = CountRows(unfiltered.Roots);
            Console.WriteLine($"filter   {everything} rows replaceable-only, {all} rows unfiltered");

            // Withheld classes are not a filter and do not come back when one is turned off. The
            // check is here, with the filter at its widest, because that is the only setting where
            // failing to withhold them would show.
            var classes = unfiltered.Roots.SelectMany(r => r.Children).Select(c => c.Label).ToList();
            if (classes.Any(c => c is "MonoBehaviour" or "MonoScript"))
                return Fail($"the tree lists a withheld class with the filter off: "
                    + string.Join(", ", classes));

            if (all <= everything) return Fail("the filter hid nothing");

            model.ReplaceableOnly = true;
            if (model.Detail is not { } refiltered || CountRows(refiltered.Roots) != everything)
                return Fail("turning the filter back on did not restore the tree");

            if (RelatedPicturesSurviveTheFilter(model) is { } relatedProblem) return Fail(relatedProblem);

            // Everything above exercises the models. The window is where a binding can quietly
            // undo them, so it is built and read back too.
            var window = new Views.MainWindow { DataContext = model };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Console.WriteLine($"window   filter is {model.ReplaceableOnly} after the window bound to it");
            if (!model.ReplaceableOnly)
                return Fail("binding the window turned the replaceable-only filter off");

            var bound = model.Detail?.Roots.FirstOrDefault()?.Children.Select(c => c.Label).ToList() ?? [];
            Console.WriteLine($"window   classes shown: {string.Join(", ", bound)}");
            if (bound.Contains("Transform") || bound.Contains("GameObject"))
                return Fail("the filter is not being applied to the tree the window shows");

            // InputGesture only prints the shortcut beside the menu item; something has to register
            // it. The first build shipped the former alone, so every key did nothing while the menu
            // claimed otherwise. Then they were registered per menu item, which registers them when
            // that item is built — and a menu's items are not built until it has been opened once,
            // so every shortcut was still dead until its own menu had been pulled down.
            //
            // Asked of a window that has never been shown, let alone had a menu opened, and with
            // the commands checked rather than only the gestures: a binding that resolves to
            // nothing is registered and does nothing, which is the same failure wearing a hat.
            var untouched = new Views.MainWindow { DataContext = model };
            var registered = untouched.KeyBindings.ToList();
            Console.WriteLine($"keys     {string.Join(", ", registered.Select(b => b.Gesture?.ToString()))}"
                + $" (on a window nobody has opened)");

            foreach (var gesture in new[]
                     { "Ctrl+E", "Ctrl+Shift+E", "Ctrl+F", "Ctrl+O", "Ctrl+R", "F5", "Ctrl+Shift+R",
                       "Ctrl+D1", "Ctrl+D2", "Ctrl+D3" })
            {
                var binding = registered.FirstOrDefault(b => b.Gesture?.ToString() == gesture);
                if (binding is null) return Fail($"{gesture} is shown in the menu but not bound to anything");
                if (binding.Command is null) return Fail($"{gesture} is bound to nothing that can run");
            }

            // Alpha is deliberately not one of them. A key binding on the window is taken before
            // the focused control sees it, so Ctrl+A as a binding took select-all away from every
            // text box; it is handled with a look at what has the keyboard instead.
            if (registered.Any(b => b.Gesture?.ToString() is "Ctrl+A"))
                return Fail("Ctrl+A is a window binding again, which takes it from the search box");

            untouched.Close();

            // The toggle answers for whichever pane is in front. Bound to the browse one wherever
            // you were, it did nothing visible in the editor — which is where a texture is worked
            // on, and so where the question comes up.
            model.Workspace = MainViewModel.EditorTab;
            if (model.Showing is not { } inEditor)
                return Fail("the editor tab is in front and nothing is showing");
            model.Workspace = MainViewModel.BrowseTab;
            if (ReferenceEquals(inEditor, model.Showing))
                return Fail("the browse and editor tabs are showing the same pane");

            // And the toggle has to survive being driven, since a two-way binding on a checkable
            // menu item was what turned the filter off as soon as the menu was opened.
            model.ToggleReplaceableOnlyCommand.Execute(null);
            if (model.ReplaceableOnly) return Fail("the filter command did not turn it off");
            model.ToggleReplaceableOnlyCommand.Execute(null);
            if (!model.ReplaceableOnly) return Fail("the filter command did not turn it back on");

            model.ShowManagerCommand.Execute(null);
            if (model.Workspace != MainViewModel.ManagerTab) return Fail("the workspace command did nothing");
            model.ShowBrowseCommand.Execute(null);

            Console.WriteLine($"options  {model.Languages.Count} languages, settings at {model.SettingsPath}");
            if (model.Languages.Count < 2) return Fail("the game offers more than one language");

            // Switching reads the game again, so this is also the reload path.
            // A weapon is known by one name, and not necessarily the one on screen: searching the
            // Japanese name has to find it while the list is in English.
            model.Search = "究極点";
            Console.WriteLine($"search   '究極点' while displaying English -> "
                + $"{string.Join(", ", model.Weapons.Select(w => $"#{w.Record.GameNumber} {w.Name}"))}");
            if (model.Weapons.All(w => w.Record.GameNumber != 416))
                return Fail("searching a Japanese name did not find the weapon");
            model.Search = "";

            // Switching the language re-reads one bundle, not the whole game.
            var before = model.Weapons.FirstOrDefault(w => w.Record.GameNumber == 16)?.Name;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            model.Language = "l_ja";
            WaitWhile(() => model.Busy, 120_000);
            Console.WriteLine($"options  switching language took {clock.ElapsedMilliseconds}ms");
            if (clock.ElapsedMilliseconds > 3000)
                return Fail($"switching language took {clock.ElapsedMilliseconds}ms, which is a reload");

            var after = model.Weapons.FirstOrDefault(w => w.Record.GameNumber == 16)?.Name;
            Console.WriteLine($"options  #16 reads '{before}' in English, '{after}' in Japanese");
            if (before == after) return Fail("switching language changed no names");

            model.Language = "l_en-gb";
            WaitWhile(() => model.Busy, 120_000);

            // Ctrl+R throws the reader away and starts over, which is the slowest thing the GUI
            // does on purpose. It used to take fifteen seconds, nearly all of it rebuilding the
            // CAB index through a class database loaded once per bundle.
            // The game can be named instead of found. Only Steam can be asked where it put the
            // game and it is not the only shop selling it, so a path that was given is taken as
            // given — pointed here at the very installation that was just detected, which is the
            // one thing that can be checked without a second copy of the game to hand.
            var found = model.Game!.RootDirectory;
            model.GameDirectory = found;
            WaitWhile(() => model.Busy, 120_000);

            Console.WriteLine($"game     named instead of found: {model.GameDescribed}");
            if (model.Game is not { } asNamed
                || !string.Equals(Path.GetFullPath(asNamed.RootDirectory), Path.GetFullPath(found),
                    StringComparison.OrdinalIgnoreCase))
                return Fail($"naming the game opened '{model.Game?.RootDirectory}' instead");
            if (model.Weapons.Count == 0) return Fail("naming the game left no weapons");

            // Back to finding it — unless this run was pointed at a game of its own, in which case
            // back to that. Finding it means Steam's copy, the one being played, and everything after
            // this installs into whichever game is open: a run pointed at a spare copy carried on
            // into the real one from here, and was stopped only by a bundle another tool had modded.
            model.GameDirectory = game is null ? "" : Path.GetFullPath(game);
            WaitWhile(() => model.Busy, 120_000);
            if (model.Weapons.Count == 0) return Fail("going back to finding it left no weapons");
            if (game is not null && !string.Equals(Path.GetFullPath(model.Game?.RootDirectory ?? ""),
                    Path.GetFullPath(game).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return Fail($"the run was pointed at '{game}' and is now on '{model.Game?.RootDirectory}'");

            // Searched, so the reload has something to put back wrongly. A reload happens under
            // somebody in the middle of something — building a pack causes one — and it used to
            // hand back the whole list while the search box still said what they had typed.
            model.Search = "ultimatum";
            var narrowed = model.Weapons.Count;

            clock.Restart();
            // Blocking rather than awaiting: an await here would resume on a pool thread, and the
            // headless window can only be closed from the one that made it.
            Settle(model.ReloadAsync(), "reloading");
            WaitWhile(() => model.Busy, 120_000);
            Console.WriteLine($"reload   {clock.ElapsedMilliseconds}ms for the whole game, "
                + $"'{model.Search}' still showing {model.Weapons.Count} of {narrowed}");
            if (clock.ElapsedMilliseconds > 8000)
                return Fail($"reloading took {clock.ElapsedMilliseconds}ms");

            if (narrowed == 0) return Fail("searching for 'ultimatum' found nothing to reload with");
            if (model.Weapons.Count != narrowed)
                return Fail($"reloading put {model.Weapons.Count} weapons back where a search left {narrowed}");

            model.Search = "";

            // With a window open the list box owns the selection, and rebuilding the collection —
            // which searching and reloading both do — clears it. So pick one again before asking
            // for it to be written out.
            if (!Select(model, 16)) return Fail("selecting #16 resolved nothing");

            // A skin is worth a row only if what it changes can be reached from it.
            if (!Select(model, 416)) return Fail("selecting #416 resolved nothing");

            // Landing on the weapon, not on the tree. Nobody opens a weapon to read a list of
            // seventy-five objects, and which of its meshes is the weapon is not a question they
            // should have to answer by clicking.
            if (model.Detail?.SelectedNode is not { Class: AssetsTools.NET.Extra.AssetClassID.Mesh } landed
                || landed.PathId != model.Detail.Tree.MainMesh?.PathId)
                return Fail($"selecting #416 came up on '{model.Detail?.SelectedNode?.Label ?? "nothing"}'");

            // And it does not unfold the tree to do it. Seventy-odd rows across four groups is a
            // list to go looking in, not a thing to be handed; what somebody came for is in the
            // pane. Whatever the tree opens to reach the selected row is the tree's own business.
            var opened = CountRows(model.Detail.Roots.Where(r => r.IsExpanded));
            Console.WriteLine($"tree     selecting #416 leaves {opened} of "
                + $"{CountRows(model.Detail.Roots)} rows unfolded");
            if (opened > 8)
                return Fail($"selecting #416 unfolded {opened} rows");

            if (SkinsAreOffered(model, scratch) is { } skinProblem) return Fail(skinProblem);

            var skins = model.Detail?.Roots.FirstOrDefault(r => r.Label == "Skins");
            if (skins is null) return Fail("#416 has skins and the tree showed none");

            Console.WriteLine($"skins    {skins.Children.Count} on #416, "
                + $"{skins.Children.Sum(s => CountRows(s.Children))} rows beneath them");
            foreach (var skin in skins.Children.Take(3))
                Console.WriteLine($"         {skin.Label} -> "
                    + string.Join(", ", skin.Children.Select(c => c.Label)));

            if (skins.Children.All(s => s.Children.Count == 0 && s.Unread is null))
                return Fail("no skin reached anything at all");

            // The weapon's own mesh, on a weapon whose skins are a mixture: some only repaint it,
            // some bring a model of their own, and several do both. Everything any of them paints
            // with lands on this geometry, so the question is not whether the skin has other
            // geometry as well — asking it that way left four of this weapon's skins in the tail.
            var ownMesh = model.Detail!.Roots
                .SelectMany(r => r.Children).SelectMany(g => g.Children)
                .FirstOrDefault(n => n.Class == AssetsTools.NET.Extra.AssetClassID.Mesh
                    && n.Label.StartsWith("ultimatum", StringComparison.OrdinalIgnoreCase));
            if (ownMesh is null) return Fail("#416's own mesh is not in the tree");

            var looks = model.Detail.Tree.Skins
                .Where(s => s.Model is null)
                .SelectMany(s => s.Materials)
                .Where(m => m.Main is not null)
                .Select(m => m.Locate(m.Main!))
                .ToHashSet();

            model.Preview.Clear();
            model.Detail.SelectedNode = ownMesh;
            Arrived(model, ownMesh);

            var order = model.Preview.TextureChoices.Skip(1).ToList();
            bool Likely(TextureChoice c) => c.Worn || looks.Contains((c.Bundle, c.PathId));

            Console.WriteLine($"skins    on '{ownMesh.Label}' the first offered are "
                + string.Join(", ", order.Where(Likely).Select(c => c.Worn ? $"[{c.Name}]" : c.Name)));

            // Four of this weapon's eight skins repaint it and one of those is what it already
            // wears, so four rows come up. The other four bring a model of their own and stay
            // down, and so do the gloss, noise and mask maps the repainting four bind beside their
            // own paint — between them that is eleven rows this list does not lead with.
            var stray = order.FindIndex(c => !Likely(c));
            if (stray >= 0 && order.FindLastIndex(Likely) > stray)
                return Fail($"'{order[stray].Name}' is offered above a skin");

            if (order.Count(Likely) != 4)
                return Fail($"#416 has four skins that repaint it and {order.Count(Likely)} came up");

            var plain = order.Where(c => c.Worn).Select(c => c.Name).ToHashSet();

            // Opening a skin shows the weapon wearing it. One that only repaints has no model to
            // open, so what there is to see is the weapon's own geometry in this skin's paint —
            // and it has to survive the model landing, which puts back what that mesh last wore.
            if (skins.Children.FirstOrDefault(s => s.Unread is null && s.Children.Count > 0) is { } repaint)
            {
                // Selecting it, not opening it. Going down a weapon's eight skins to see what each
                // looks like is the ordinary thing to do here, and it used to unfold every one of
                // them into its materials and textures on the way past.
                model.Preview.Clear();
                model.Detail.SelectedNode = repaint;
                Arrived(model, ownMesh);

                var put = model.Preview.ChosenTexture;
                Console.WriteLine($"skins    picking '{repaint.Label}' put "
                    + $"{put?.Name ?? "nothing"} on '{ownMesh.Label}'");

                if (put is null || !looks.Contains((put.Bundle, put.PathId)))
                    return Fail($"picking '{repaint.Label}' did not put a skin on the weapon");
                if (repaint.IsExpanded)
                    return Fail($"picking '{repaint.Label}' unfolded it as well");
            }

            // A skin that replaces the weapon rather than repainting it has nothing under it until
            // its row is opened, because reading every such model on every click in the weapon list
            // is a walk per skin nobody asked for. Opening one asks for that one.
            if (skins.Children.FirstOrDefault(s => s.Unread is not null) is not { } withModel)
                return Fail("#416 has skins that bring their own model and none is marked unread");

            // Its model, that is. The row can already hold the skin's own materials — Corrupted
            // Ultimatum's is found now under the name its model gives it — and those cost nothing to
            // show; it is the walk of the model that waits to be asked for.
            bool HoldsAModel() => withModel.Children.SelectMany(c => c.Children)
                .Any(c => c.Class == AssetsTools.NET.Extra.AssetClassID.Mesh);

            if (withModel.ModelRead || HoldsAModel())
                return Fail($"'{withModel.Label}' was read before anybody opened it");

            withModel.IsExpanded = true;
            WaitWhile(() => !HoldsAModel(), 60_000);

            var inside = withModel.Children.SelectMany(c => c.Children).ToList();
            Console.WriteLine($"skins    opening '{withModel.Label}' read "
                + $"{string.Join(", ", withModel.Children.Select(c => $"{c.Label} {c.Children.Count}"))}");

            if (inside.Count == 0) return Fail($"opening '{withModel.Label}' read nothing");
            if (inside.All(c => c.Class != AssetsTools.NET.Extra.AssetClassID.Mesh))
                return Fail($"'{withModel.Label}' brings its own model and no mesh came back");

            // The mesh is no use without something to put on it, and what somebody wants on a skin's
            // own model is that skin's own texture. The picker is built from the tree, so it has to
            // be built again now the tree has grown.
            var offered = model.Preview.TextureChoices.Select(t => t.Name).ToHashSet();
            var unoffered = inside
                .Where(c => c.Class == AssetsTools.NET.Extra.AssetClassID.Texture2D)
                .Select(c => c.Label)
                .Where(n => !offered.Contains(n))
                .ToList();

            Console.WriteLine($"skins    {model.Preview.TextureChoices.Count} textures offered for it");
            if (unoffered.Count > 0)
                return Fail($"the model's own textures are not offered for it: {string.Join(", ", unoffered)}");

            // And it should not need choosing. Which texture goes on which part is decided by the
            // renderers in the model, which are read along with everything else — so selecting the
            // skin's mesh dresses it, the same as selecting the weapon's own does.
            if (inside.FirstOrDefault(c => c.Class == AssetsTools.NET.Extra.AssetClassID.Mesh
                    && c.Label.Contains(withModel.Detail ?? "", StringComparison.OrdinalIgnoreCase))
                is not { } skinMesh)
                skinMesh = inside.First(c => c.Class == AssetsTools.NET.Extra.AssetClassID.Mesh);

            // Emptied first, or the wait below is satisfied by the model already in the pane and
            // everything after it reads the last mesh's answers while this one is still arriving.
            model.Preview.Clear();
            model.Detail!.SelectedNode = skinMesh;
            Arrived(model, skinMesh);

            var dressed = model.Preview.MeshTextures?.Count(t => t is not null) ?? 0;
            Console.WriteLine($"skins    '{skinMesh.Label}' came up wearing {dressed} texture(s)");
            if (dressed == 0)
                return Fail($"'{skinMesh.Label}' came up grey, with nothing worked out to put on it");

            // And it moves, by the clips its own model carries. The weapon's name the weapon's own
            // hierarchy, which three of #416's four skin models are not built under — those three
            // stood still, and across the game 77 of 130 skin models did.
            Console.WriteLine($"skins    '{skinMesh.Label}': {model.Preview.Clips.Count} clip(s) move it"
                + (model.Preview.Clips.Count == 0 ? "" : $" — {string.Join(", ", model.Preview.Clips.Select(c => c.Name))}"));
            if (model.Preview.Clips.Count == 0)
                return Fail($"'{skinMesh.Label}' is a skin's own model and none of its own clips play on it");

            // What this mesh is actually drawn with comes first. Everything else stays on the list,
            // because putting another skin's paint on a mesh is what the list is for — but a weapon
            // reaches thirty textures and two of them answer the question somebody has.
            Console.WriteLine("skins    first offered: " + string.Join(", ",
                model.Preview.TextureChoices.Skip(1).Take(3).Select(t => t.Name)));

            // The first entry is "(automatic)"; the ones this mesh's own renderers name follow it.
            var head = model.Preview.TextureChoices.Skip(1).Take(dressed).ToList();
            if (head.Any(c => !inside.Any(n => n.Label == c.Name && n.PathId == c.PathId)))
                return Fail("the textures at the top of the list are not the ones on the mesh: "
                    + string.Join(", ", head.Select(c => c.Name)));

            // And nothing else is promoted here. A repainting skin's paint is cut for the weapon's
            // own geometry, so on a mesh that arrived with a skin of its own it is no better an
            // answer than anything else on the list — only the marked ones come first.
            if (model.Preview.TextureChoices.Any(
                    c => c.Worn && !inside.Any(n => n.Label == c.Name && n.PathId == c.PathId)))
                return Fail("something outside the skin's own model is marked as worn on it");

            // Read once. Opening and closing the row again must not pile the same rows up under it.
            var read = withModel.Children.Count;
            withModel.IsExpanded = false;
            withModel.IsExpanded = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (withModel.Children.Count != read)
                return Fail($"opening it again read it again: {read} -> {withModel.Children.Count}");

            // A skin can paint the weapon and bring a model of its own, and such a row arrives with
            // the paint already beneath it. Whether the model had been read was inferred from
            // whether the row had anything under it at all, so exactly these skins — the ones with
            // the most to show — never had their model read, and opening the row showed the paint
            // and nothing else.
            if (skins.Children.FirstOrDefault(s => s.Unread is not null && !ReferenceEquals(s, withModel)
                    && s.Children.Count > 0) is { } both)
            {
                var painted = CountRows(both.Children);
                both.IsExpanded = true;
                WaitWhile(() => CountRows(both.Children) == painted, 60_000);

                Console.WriteLine($"skins    '{both.Label}' paints the weapon and brings a model: "
                    + $"{painted} -> {CountRows(both.Children)} rows");

                if (!both.Children.SelectMany(c => c.Children)
                        .Any(g => g.Class == AssetsTools.NET.Extra.AssetClassID.Mesh))
                    return Fail($"'{both.Label}' brings its own model and opening it read no mesh");
            }

            // A skin's model can point straight at the weapon's own mesh rather than carrying a
            // copy of it, so the same asset stands in the tree twice under two sets of materials.
            // Asked by path id, the answer found was whichever came first — the weapon's — and the
            // skin's row came up wearing the paint it was made to replace.
            TreeNode? twice = null;
            foreach (var skin in skins.Children.Where(s => s.Unread is not null))
            {
                if (!skin.ModelRead)
                {
                    var had = CountRows(skin.Children);
                    skin.IsExpanded = true;
                    WaitWhile(() => CountRows(skin.Children) == had, 60_000);
                }

                twice = skin.Children.SelectMany(c => c.Children).FirstOrDefault(
                    c => c.Class == AssetsTools.NET.Extra.AssetClassID.Mesh && c.PathId == ownMesh.PathId);
                if (twice is not null) break;
            }

            if (twice is null) Console.WriteLine("skins    no skin of #416 shares the weapon's own mesh");
            else
            {
                model.Preview.Clear();
                model.Detail.SelectedNode = twice;
                Arrived(model, twice);

                var instead = model.Preview.TextureChoices.Where(c => c.Worn).Select(c => c.Name).ToList();
                Console.WriteLine($"skins    the weapon's own mesh under a skin of its own wears "
                    + $"{(instead.Count == 0 ? "nothing" : string.Join(", ", instead))}, "
                    + $"not {string.Join(", ", plain)}");

                if (instead.Count == 0)
                    return Fail("the same mesh under a skin's own model came up with nothing on it");
                if (instead.Any(plain.Contains))
                    return Fail("the same mesh under a skin's own model came up in the weapon's paint");

                // And the weapon's own row is untouched by having gone there. Keyed by path id,
                // opening that skin marked this mesh as one that arrived with a skin of its own,
                // and the weapon's own row stopped offering the weapon's own skins.
                model.Preview.Clear();
                model.Detail.SelectedNode = ownMesh;
                Arrived(model, ownMesh);

                var back = model.Preview.TextureChoices.Skip(1).ToList();
                if (back.Count(Likely) != 4)
                    return Fail($"after a trip to the skin's row, '{ownMesh.Label}' leads with "
                        + $"{back.Count(Likely)} of its four skins");
                if (!back.Any(c => c.Worn && plain.Contains(c.Name)))
                    return Fail($"after a trip to the skin's row, '{ownMesh.Label}' is not in its own paint");
            }

            if (!Select(model, 16)) return Fail("selecting #16 resolved nothing");

            // Closed before the writing starts: an async command raises CanExecuteChanged when it
            // finishes, and with no synchronization context here that lands on a pool thread while
            // a menu item is listening. The real app resumes on the UI thread and does not care.
            window.Close();

            // Extraction has to land somewhere sensible whatever directory the exe was launched
            // from, which is why it does not use the current one the way the CLI does.
            Console.WriteLine($"extract  writing to {model.WorkspaceRoot}");
            if (!Path.IsPathRooted(model.WorkspaceRoot))
                return Fail("the workspace root has to be absolute; a window has no current directory");

            model.ExtractWeaponCommand.Execute(null);
            WaitWhile(() => model.Busy, 120_000);
            Console.WriteLine($"extract  {model.Status}");

            if (model.LastExport is not { } exported || !Directory.Exists(exported))
                return Fail($"nothing was written: {model.Status}");

            if (!File.Exists(Path.Combine(exported, PGAssetTool.Core.Pack.PackManifest.FileName)))
                return Fail($"no manifest in {exported}");

            // Renamed before anything is built from it, folder and manifest both. Left alone this
            // pack would be called "Hitman Pistol", built into 0016_Beretta.pgmod and filed in the
            // mods folder beside a real mod of the same weapon — twenty of them accumulated there
            // before anyone looked. Under its own name it is obvious, and it can only remove its own.
            //
            // Through the editor rather than around it: renaming the directory underneath a live
            // file watcher is refused by Windows, and the editor is what knows to put the watcher
            // down first. So this is also the test of that.
            var written = Rename(model, exported, PackFolder, PackName, PackIdentity);
            if (written is null) return Fail($"the workspace could not be renamed: {model.Editor.Status}");

            var identified = PGAssetTool.Core.Pack.Workspace.Read(written);
            var operations = identified.Operations;
            Console.WriteLine($"extract  {operations.Count} replaceable files across "
                + $"{operations.Select(o => o.Target.Container).Distinct().Count()} bundles");
            if (operations.Count == 0) return Fail("the manifest named nothing replaceable");

            if (TheMaskReachesTheFileAndTheManifest(written, operations) is { } writtenMask)
                return Fail(writtenMask);


            // The editor reads the workspace that was just written, and has to notice an edit made
            // to it from outside — which is the whole point of the round trip.
            model.Editor.Rescan(model.WorkspaceRoot);
            Console.WriteLine($"editor   {model.Editor.Workspaces.Count} workspaces, "
                + $"{model.Editor.Files.Count} files in '{model.Editor.SelectedWorkspace?.Name}'");

            // The one just written, found by name: rescanning keeps whatever was selected before,
            // and the root also holds earlier extracts whose edits are not this test's business.
            var fresh = model.Editor.Workspaces.FirstOrDefault(w => string.Equals(Path.GetFullPath(w.Directory), written, StringComparison.OrdinalIgnoreCase));
            if (fresh is null) return Fail("the freshly extracted workspace is not listed");
            if (fresh.Edited != 0) return Fail($"'{fresh.Name}' was just extracted and shows {fresh.Edited} edited");

            model.Editor.SelectedWorkspace = fresh;
            if (model.Editor.Files.Count == 0) return Fail("the editor saw no files in a fresh extract");

            // The pack's descriptive half is editable from the editor. Saving it must not disturb
            // the operations underneath: they carry the baseline hashes that decide what gets
            // packed, and losing those would make every untouched file look edited.
            if (model.Editor.PackName != PackName)
                return Fail($"the form shows '{model.Editor.PackName}', not what the manifest says");

            model.Editor.PackAuthor = "self test";
            model.Editor.PackVersion = "9.9.9";
            model.Editor.ApplyDetailsCommand.Execute(null);

            var saved = PGAssetTool.Core.Pack.Workspace.Read(written);
            Console.WriteLine($"editor   pack details saved: {saved.Name} {saved.Version} by {saved.Author}");
            if (saved.Author != "self test" || saved.Version != "9.9.9")
                return Fail($"the pack details did not reach the manifest: {model.Editor.Status}");
            if (PGAssetTool.Core.Pack.Workspace.Changed(written, saved).Count != 0)
                return Fail("saving the details made untouched files look edited");

            if (ChoicesSaveAtOnceAndLeaveTypingAlone(model, written) is { } choiceProblem)
                return Fail(choiceProblem);

            if (PackIconIsTheModel(model, written) is { } iconProblem) return Fail(iconProblem);

            // Built protected once, so the whole loop is exercised on a signed pack rather than on
            // the plain one: what the manager reads, what the applier unpacks, and what the seal
            // says about it are three different code paths through the same file.
            model.ProtectPacks = true;
            Core.Pack.Workspace.Save(written, Core.Pack.Workspace.Read(written) with { Protect = null });

            if (TheEditorPutsTheModelInItsPaint(model) is { } paintProblem) return Fail(paintProblem);
            if (AnimationsCanBeTakenFromAnotherWeapon(model) is { } animationProblem) return Fail(animationProblem);
            if (AnimationsGoOutAsAGlbAndComeBack(model) is { } glbProblem) return Fail(glbProblem);
            if (OneMeshCanWearTwoPictures(model, model.WorkspaceRoot) is { } twoPaints) return Fail(twoPaints);

            var texture = model.Editor.Files.FirstOrDefault(f => f.Name.EndsWith(".png"));
            if (texture is null) return Fail("no texture in the extracted workspace");

            model.Editor.SelectedFile = texture;
            Shows(model, texture);
            Console.WriteLine($"editor   original: {model.Editor.Original.Caption}");
            Console.WriteLine($"editor   edited:   {model.Editor.Edited.Caption}");

            if (model.Editor.Original.Nothing is { } gameSide) return Fail($"the game side did not load: {gameSide}");
            if (model.Editor.Edited.Nothing is { } fileSide) return Fail($"the file side did not load: {fileSide}");

            // Paint over it the way an image editor would, and ask again.
            var bytes = File.ReadAllBytes(texture.FullPath);
            File.WriteAllBytes(texture.FullPath, [.. bytes, .. new byte[16]]);
            model.Editor.Refresh();

            var marked = model.Editor.Files.Single(f => f.RelativePath == texture.RelativePath).Edited;
            Console.WriteLine($"editor   after an outside edit, marked as edited: {marked}");

            // And it comes to the top. A workspace holds thirty files and the mod is three of
            // them; finding those three again after every save was most of the work.
            var listed = model.Editor.Files.Select(f => f.Edited).ToList();
            Console.WriteLine($"editor   {listed.Count(e => e)} edited of {listed.Count}, "
                + $"first is '{model.Editor.Files[0].Name}'");
            if (listed.LastIndexOf(true) > listed.IndexOf(false))
                return Fail("an edited file is listed below an untouched one");
            if (!marked) return Fail("an edit made outside the tool was not noticed");

            if (TheForumPostSaysWhatChanged(model) is { } postProblem) return Fail(postProblem);

            // Alpha in these textures is usually emission rather than coverage, so an author can
            // ask for the colours alone. What comes back without an alpha channel has to be given
            // the original one, or turning that option on would flatten every mask in the game.
            if (RoundTripWithoutAlpha(model) is { } alphaProblem) return Fail(alphaProblem);

            if (TheViewOutlivesAReading(model, texture) is { } viewProblem) return Fail(viewProblem);

            if (TwoModelsKeepTheirOwnViews(model, texture) is { } swapProblem) return Fail(swapProblem);

            if (TwoWorkspacesKeepTheirOwnViews(model, texture) is { } twiceProblem) return Fail(twiceProblem);

            if (DroppedFilesLand(model, texture) is { } dropProblem) return Fail(dropProblem);

            // What was installed before this run touched anything. A test that writes to the game
            // has to leave it as it found it, and the only way to know that is to have looked
            // first — an earlier version of this left a mod behind every time the workspace it
            // happened to pick changed, and nothing noticed for days.
            var theirs = new PGAssetTool.Core.Mods.ModStore(model.Game!).Read();
            wasInstalled = theirs.Select(m => m.Id).ToHashSet();

            // Their on-or-off state as well as their presence. Turning a mod on now turns off
            // whatever else writes the same assets, and the author's own mods are within reach of
            // that — so being left installed is no longer the whole of leaving them alone.
            wasEnabled = theirs.Where(m => m.Enabled).Select(m => m.Id).ToHashSet();

            // A second workspace, so what gets tested is the batch. Building and applying six packs
            // one at a time is six trips through the same controls, and — because applying is a
            // whole-game reconcile — six rebuilds of the game with five half-finished states along
            // the way. One pack proves nothing about any of that.
            var secondary = SecondWorkspace(model, weapon: 2, PackFolderB, PackIdentityB, PackNameB);
            if (secondary is null) return Fail("the second workspace could not be prepared");

            model.Editor.Rescan(model.WorkspaceRoot);
            foreach (var w in model.Editor.Workspaces) model.Editor.Selection.Add(w);

            var chosen = model.Editor.Chosen;
            Console.WriteLine($"batch    {chosen.Count} workspaces chosen: "
                + string.Join(", ", chosen.Select(w => w.Name)));
            if (chosen.Count != 2) return Fail($"two workspaces were selected and {chosen.Count} are being acted on");

            // Exactly one file was touched in each, so exactly one belongs in each pack. Printed
            // because a pack that quietly grew to everything in its workspace is indistinguishable
            // from a working one until it is applied and writes five bundles instead of one.
            foreach (var w in chosen)
            {
                // One edited file each.
                const int packing = 1;

                // Counted by file: one picture can be written to two assets, and it is still one
                // edited file however many operations follow from it.
                var differ = PGAssetTool.Core.Pack.Workspace.Changed(
                        w.Directory, PGAssetTool.Core.Pack.Workspace.Read(w.Directory))
                    .Select(o => o.Source)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Console.WriteLine($"batch    {w.Name}: {differ.Count} of {w.Files} files differ"
                    + (differ.Count > 0 ? $" ({string.Join(", ", differ)})" : ""));
                if (differ.Count != packing)
                    return Fail($"'{w.Name}' should be packing {packing} and is packing {differ.Count}");
            }

            // The whole loop, for both at once: build, install, and come back with the game read
            // again. Only files that differ are packed, so this is also what says the edits landed.
            var applying = System.Diagnostics.Stopwatch.StartNew();
            model.Editor.PackAndApplyCommand.Execute(null);
            WaitWhile(() => model.Busy, 300_000);
            Console.WriteLine($"batch    {model.Status}");
            Console.WriteLine($"timing   build and apply took {applying.ElapsedMilliseconds}ms");

            if (model.Status.Contains("failed") && !model.Status.Contains("0 failed"))
                return Fail($"applying reported failures: {model.Status}");
            if (!model.Status.Contains("applied")) return Fail($"the packs were not applied: {model.Status}");

            // Only what this run installed: someone else's mods are not this test's to touch.
            var mine = new[] { PackIdentity, PackIdentityB };
            var installed = new PGAssetTool.Core.Mods.ModStore(model.Game!).Read();
            var strays = installed.Select(m => m.Id)
                .Where(id => !mine.Contains(id) && !wasInstalled.Contains(id)).ToList();
            if (strays.Count > 0)
                return Fail($"this run installed {string.Join(", ", strays)} and has no plan to remove them");

            Console.WriteLine($"batch    installed: {string.Join(", ", installed.Select(m => m.Id))}");

            if (BrowsingReadsTheGameAsShipped(model) is { } shippedProblem) return Fail(shippedProblem);

            if (OneModStandsAsideForAnother(model, written) is { } rivalProblem) return Fail(rivalProblem);

            if (AnAssetTheGameMovedIsFollowed(model, written) is { } movedProblem) return Fail(movedProblem);

            // The same install a pack dropped on the window takes: a file, straight to the applier,
            // with no workspace behind it. That is how somebody else's mod gets in, and it is the
            // one install path with nothing in front of it to catch a mistake.
            if (installed.FirstOrDefault(m => m.Id == PackIdentity)?.PackPath is not { } dropped)
                return Fail("the kept pack has no path to install from");

            // A signed pack that no longer matches its signature stops and asks first. It used to
            // be read only when a row was selected in the manager — after the game had been
            // rewritten — so the one fact worth having before deciding arrived after the decision.
            var meddled = Path.Combine(scratch, "meddled-" + Path.GetFileName(dropped));
            var sealed_ = File.ReadAllBytes(dropped);
            if (sealed_.AsSpan().IndexOf("self test"u8) is var at and >= 0)
            {
                "self tost"u8.CopyTo(sealed_.AsSpan(at));
                File.WriteAllBytes(meddled, sealed_);

                var ledger = new PGAssetTool.Core.Mods.ModStore(model.Game!).Read().Count;
                model.Manager.Asking = null;
                _ = model.InstallPacks([meddled]);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                Console.WriteLine($"seal     a pack changed after signing: "
                    + $"{model.Manager.Asking?.Title ?? "nothing was asked"}");

                if (model.Manager.Asking is null)
                    return Fail("a pack that no longer matches its signature installed without a word");
                if (new PGAssetTool.Core.Mods.ModStore(model.Game!).Read().Count != ledger)
                    return Fail("it was installed before anybody answered");

                model.Manager.Asking = null;
            }
            else Console.WriteLine("seal     the built pack carries no author name to meddle with");

            _ = model.InstallPacks([dropped]);
            WaitWhile(() => model.Busy, 300_000);
            Console.WriteLine($"drop     installing '{Path.GetFileName(dropped)}' on its own: {model.Status}");

            if (!model.Status.Contains("applied") || model.Status.Contains("1 failed"))
                return Fail($"installing a pack by itself did not apply it: {model.Status}");
            if (new PGAssetTool.Core.Mods.ModStore(model.Game!).Read().All(m => m.Id != PackIdentity))
                return Fail("a pack installed on its own is not in the ledger");

            var missing = mine.Where(id => installed.All(m => m.Id != id)).ToList();
            if (missing.Count > 0)
                return Fail($"{string.Join(", ", missing)} did not reach the ledger — a batch that installed some");

            // The manager reads the game rather than the ledger alone, so a bundle changed outside
            // this tool is visible before anyone installs over it.
            model.Manager.Refresh();
            Console.WriteLine($"manager  {model.Manager.Status}");
            Console.WriteLine($"manager  bundles differing: "
                + string.Join(", ", model.Manager.Bundles.Select(b => $"{b.Bundle} ({b.Explanation})")));

            if (model.Manager.Mods.Count == 0) return Fail("the manager saw nothing installed");

            if (AnotherCopysModsCannotBeExtracted(model, installed) is { } borrowed) return Fail(borrowed);

            // The pack's picture has to survive the whole way: chosen at extraction, written into
            // the pack even though it is not one of the files being replaced, and read back out
            // where the mod is listed. Every step of that is somewhere it could quietly go missing.
            foreach (var row in model.Manager.Mods.Where(m => mine.Contains(m.Mod.Id)))
            {
                var named = PGAssetTool.Core.Pack.PackBuilder.ReadManifest(row.Mod.PackPath).Icon;
                var picture = PGAssetTool.Core.Pack.PackBuilder.ReadIcon(row.Mod.PackPath);

                // Measured from the bytes, not from the Bitmap: without a window there is nothing to
                // decode an image into, so its size reads as one pixel however good the file is.
                var size = picture is null
                    ? null
                    : StbImageSharp.ImageInfo.FromStream(new MemoryStream(picture));

                Console.WriteLine($"manager  '{row.Name}' shows '{named}': "
                    + $"{(size is { } s ? $"{s.Width}x{s.Height}, {picture!.Length:N0} bytes" : "nothing")}");

                // A protected pack has to stay openable by this tool and stay shut to everything
                // else, and the manager has to say who built it. Checked on the pack the manager is
                // actually pointing at, not on one made for the occasion.
                var seal = Core.Pack.PackFile.Inspect(row.Mod.PackPath);
                Console.WriteLine($"manager  '{row.Name}' is {seal.Describe}");

                if (!seal.Protected) return Fail($"'{row.Name}' was built protected and is not");
                if (seal.State != Core.Pack.SealState.Signed)
                    return Fail($"'{row.Name}' does not verify against the key that signed it");

                try
                {
                    System.IO.Compression.ZipFile.OpenRead(row.Mod.PackPath).Dispose();
                    return Fail($"'{row.Name}' is protected and still opens as a zip");
                }
                catch (InvalidDataException) { }

                if (named.Length == 0) return Fail($"'{row.Name}' was built with no icon named");
                if (!row.HasIcon) return Fail($"'{row.Name}' names '{named}' and the pack has no such picture");
                if (size is not { Width: > 1, Height: > 1 })
                    return Fail($"'{row.Name}' carries '{named}' and it is not a picture");
            }
            if (model.Manager.Bundles.All(b => b.State != PGAssetTool.Core.Mods.BundleState.ChangedByThisTool))
                return Fail("the bundle just written was not attributed to the mod that wrote it");
            if (model.Manager.GameIsRunning) return Fail("the game should not be running during a self-test");

            if (TilesArrangeTwoWays(model) is { } orderProblem) return Fail(orderProblem);

            // Before the window the tiles are counted in exists. Finding switches tabs and back, and
            // a tab switched back to is built again from nothing: counted straight afterwards, its
            // tiles had not been laid out and every picture read as missing.
            if (OptionsCanBeClosedOnAShortScreen(model) is { } stuck) return Fail(stuck);
            if (FindingStaysInTheWorkspaceItIsAskedIn(model) is { } wandered) return Fail(wandered);
            if (AnEditToTheWeaponReachesItsDefaultSkin(model) is { } unpaired) return Fail(unpaired);

            // One first. Turning it off restores its bundles; the confirmation is what stands
            // between a click and the game being rewritten.
            model.Manager.Selected = model.Manager.Mods.FirstOrDefault(m => m.Mod.Id == PackIdentity);
            if (model.Manager.Selected is null) return Fail($"the manager does not list '{PackIdentity}'");
            model.Manager.DisableCommand.Execute(null);
            if (model.Manager.Asking is null) return Fail("turning a mod off asked for no confirmation");

            // The buttons in that dialog have to reach the commands. Bound through the wrong
            // ancestor they resolve to nothing, Avalonia disables them, and it looks like the tool
            // refusing rather than a binding being wrong — which is exactly what happened.
            var dialog = new Views.MainWindow { DataContext = model };
            dialog.Show();
            model.Workspace = MainViewModel.ManagerTab;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var buttons = dialog.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
                .Where(b => b.Content is "Cancel" or "Go ahead")
                .ToList();

            if (TheFormFitsASmallWindow(model) is { } tooTall) return Fail(tooTall);
            if (TheScaleReachesTheLayout(model) is { } scaleProblem) return Fail(scaleProblem);

            // What is installed is shown as tiles with the picture on top, so a picture that never
            // reaches a control is the whole point of the tab going missing. Counted from the
            // window rather than the model: the model held its icons correctly the entire time the
            // combo box was dropping the texture chosen for a mesh.
            var showing = dialog.GetVisualDescendants().OfType<Avalonia.Controls.Image>()
                .Count(i => i.Source is not null);
            var expected = model.Manager.Mods.Count(m => m.HasIcon);
            Console.WriteLine($"manager  {expected} of {model.Manager.Mods.Count} mods carry a picture, "
                + $"{showing} reached a tile");
            if (showing < expected)
                return Fail($"{expected} mods have a picture and only {showing} of them are being shown");

            // Ctrl and the wheel resize the tiles, and the tiles have to follow. Measured off the
            // controls: the size is a number on the model whatever happens, and a binding that
            // resolves to nothing leaves the tiles their default size while it changes.
            var was = model.Manager.TileSize;
            var tileWas = Tile(dialog);
            for (var i = 0; i < 4; i++) model.Manager.ResizeTiles(1);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var tileNow = Tile(dialog);
            Console.WriteLine($"manager  tiles {was} -> {model.Manager.TileSize} px, "
                + $"drawn {tileWas?.ToString() ?? "?"} -> {tileNow?.ToString() ?? "?"}");

            if (model.Manager.TileSize <= was) return Fail("the tiles did not grow");
            if (tileWas is null || tileNow is null) return Fail("no tile to measure");
            if (tileNow <= tileWas) return Fail("the tiles were resized and the drawing did not follow");

            for (var i = 0; i < 40; i++) model.Manager.ResizeTiles(-1);
            if (model.Manager.TileSize != ManagerViewModel.SmallestTile)
                return Fail($"shrinking without end reached {model.Manager.TileSize}");
            for (var i = 0; i < 80; i++) model.Manager.ResizeTiles(1);
            if (model.Manager.TileSize != ManagerViewModel.LargestTile)
                return Fail($"growing without end reached {model.Manager.TileSize}");
            model.Manager.TileSize = was;

            // And the pack's own words, read out of the pack rather than out of the ledger.
            if (model.Manager.Details is not { } about) return Fail("the selected mod shows no details");
            Console.WriteLine($"manager  '{about.Name}' {about.Summary}, {about.Changes.ToLowerInvariant()}, "
                + $"{about.Built}");
            if (about.Id != PackIdentity) return Fail($"the details are for '{about.Id}', not the selected mod");
            if (about.Replaces.Count == 0) return Fail("the pack replaces something and the details say nothing");

            // The lists themselves have to accept more than one row, or none of the above is
            // reachable by anyone actually using the window.
            var lists = dialog.GetVisualDescendants().OfType<Avalonia.Controls.ListBox>()
                .Select(l => l.SelectionMode).ToList();
            Console.WriteLine($"manager  dialog buttons: {buttons.Count}, "
                + $"enabled {buttons.Count(b => b.IsEffectivelyEnabled)}; "
                + $"lists accepting several rows: {lists.Count(m => m.HasFlag(Avalonia.Controls.SelectionMode.Multiple))}");

            var usable = buttons.Count == 2 && buttons.All(b => b.IsEffectivelyEnabled);
            var multi = lists.Count(m => m.HasFlag(Avalonia.Controls.SelectionMode.Multiple));
            dialog.Close();
            if (!usable) return Fail("the confirmation buttons are not usable");
            if (multi < 1) return Fail("no list in the manager accepts more than one row");
            Console.WriteLine($"manager  asked: {model.Manager.Asking.Title}");

            // The bar in the status line is the only thing on screen that says the tool is doing
            // something, and the window binds the shell's answer rather than the manager's — so
            // turning a mod on or off left it still for the whole of the rewrite. Watched rather
            // than sampled: the work is over in a second or two, and a look afterwards always finds
            // it finished.
            var moved = false;
            void Moving(object? _, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(MainViewModel.Working) && model.Working && model.Manager.Busy)
                    moved = true;
            }
            model.PropertyChanged += Moving;

            var turning = System.Diagnostics.Stopwatch.StartNew();
            model.Manager.ProceedCommand.Execute(null);
            WaitWhile(() => model.Manager.Busy || model.Manager.Asking is not null, 180_000);
            model.PropertyChanged -= Moving;
            Console.WriteLine($"manager  {model.Manager.Status}");
            Console.WriteLine($"manager  the window said it was working while the manager was: {moved}");
            if (!moved) return Fail("the manager rewrote the game and the window showed nothing happening");
            Console.WriteLine($"timing   turning one mod off took {turning.ElapsedMilliseconds}ms");

            var mineNow = model.Manager.Mods.First(m => m.Mod.Id == PackIdentity);
            if (mineNow.Enabled) return Fail("the mod is still on after being turned off");
            if (model.Manager.Mods.Single(m => m.Mod.Id == PackIdentityB).Enabled is false)
                return Fail("turning one mod off also turned off the other");

            // Only the bundles that were this mod's alone. Anything else installed and still on is
            // supposed to be left written, and a bundle two mods share is still written after one
            // of them goes — asserting the whole game went vanilla passed only for as long as this
            // test was the only thing installed, and asserting on every bundle it touched passed
            // only for as long as nothing else touched the same ones. The author now has three
            // dozen mods on, several of them in the bundle this test writes to.
            var alsoWritten = model.Manager.Mods
                .Where(m => m.Mod.Enabled && m.Mod.Id != PackIdentity)
                .SelectMany(m => m.Mod.TouchedBundles.Keys)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var mineAlone = mineNow.Mod.TouchedBundles.Keys
                .Where(b => !alsoWritten.Contains(b))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Console.WriteLine($"manager  {mineAlone.Count} of {mineNow.Mod.TouchedBundles.Count} "
                + "of its bundles are its alone and have to go back");

            if (model.Manager.Bundles.FirstOrDefault(b =>
                    mineAlone.Contains(b.Bundle)
                    && b.State == PGAssetTool.Core.Mods.BundleState.ChangedByThisTool) is { } stillChanged)
                return Fail($"turning it off left '{stillChanged.Bundle}', which nothing else writes, changed");

            // The packs are kept beside the tool, so deleting a workspace cannot strand one.
            var kept = model.Manager.Mods.Where(m => mine.Contains(m.Mod.Id))
                .Select(m => m.Mod.PackPath).ToList();
            Console.WriteLine($"manager  packs kept at {string.Join(", ", kept.Select(Path.GetFileName))}");
            if (kept.Any(p => !p.Contains("PGAssetTool-data")))
                return Fail("a pack was not copied into the store");

            if (ModsAreFiledByWeaponAndLook(model) is { } filingProblem) return Fail(filingProblem);

            // Now both, in one gesture. Removed through the manager rather than around it, so the
            // path a person actually takes is the one under test. This also puts the game back: it
            // is a test, not a change anyone asked for.
            foreach (var row in model.Manager.Mods.Where(m => mine.Contains(m.Mod.Id)))
                model.Manager.Selection.Add(row);
            if (model.Manager.Chosen.Count != 2)
                return Fail($"two mods were selected and {model.Manager.Chosen.Count} are being acted on");

            // With shift held, which is what turns Remove into Remove and delete. Removing on its
            // own deliberately leaves the kept pack so reinstalling does not depend on the
            // workspace — right for a person, and wrong for a test that runs on every change:
            // twenty copies of one had piled up in the mods folder before this existed.
            model.Manager.ShiftHeld = true;
            if (model.Manager.RemoveLabel != "Remove and delete")
                return Fail($"with shift held the button still says '{model.Manager.RemoveLabel}'");

            model.Manager.RemoveCommand.Execute(null);
            if (model.Manager.Asking is null) return Fail("removing asked for no confirmation");
            Console.WriteLine($"manager  asked: {model.Manager.Asking.Title}");
            if (!model.Manager.Asking.Title.Contains("2 mods"))
                return Fail($"the confirmation does not say how many: {model.Manager.Asking.Title}");

            model.Manager.ProceedCommand.Execute(null);
            WaitWhile(() => model.Manager.Busy || model.Manager.Asking is not null, 180_000);

            Console.WriteLine($"manager  after removing both: {model.Manager.Status}");
            if (model.Manager.Mods.Any(m => mine.Contains(m.Mod.Id)))
                return Fail("a mod is still installed after both were removed");

            model.Manager.ShiftHeld = false;
            if (kept.Where(File.Exists).ToList() is { Count: > 0 } still)
                return Fail($"shift-removing left {string.Join(", ", still.Select(Path.GetFileName))} behind");
            Console.WriteLine($"manager  and the {kept.Count} kept pack file(s) went with them");

            // Before the check below rather than instead of it. This run's second pack replaces the
            // first shotgun's texture, the author has a mod of their own that replaces it too, and
            // one of the two has to stand down — so the exclusion turning theirs off is the tool
            // working, not a fault. Putting it back on afterwards is this test's job; the check is
            // then what says the putting back worked.
            TidyUpAfterItself(model.Game, wasInstalled, wasEnabled);

            var now = new PGAssetTool.Core.Mods.ModStore(model.Game!).Read();
            var switched = now
                .Where(m => wasInstalled.Contains(m.Id) && m.Enabled != wasEnabled.Contains(m.Id))
                .Select(m => m.Name)
                .ToList();

            if (switched.Count > 0)
                return Fail($"this run turned {string.Join(", ", switched)} on or off, and they are not its own");

            var left = model.Manager.Mods.Select(m => m.Mod.Id).Where(id => !wasInstalled.Contains(id)).ToList();
            Console.WriteLine($"manager  installed before this run: {wasInstalled.Count}, left behind by it: {left.Count}");
            if (left.Count > 0) return Fail($"this run left {string.Join(", ", left)} in the game");
            if (model.Status.Contains("Value cannot be null"))
                return Fail($"the shell reported an error during removal: {model.Status}");

            // Back to the workspace the edit was made in, which the batch moved off.
            model.Editor.SelectedWorkspace = model.Editor.Workspaces
                .FirstOrDefault(w => string.Equals(Path.GetFullPath(w.Directory), written,
                    StringComparison.OrdinalIgnoreCase));

            File.WriteAllBytes(texture.FullPath, bytes);
            model.Editor.Refresh();
            if (model.Editor.Files.Single(f => f.RelativePath == texture.RelativePath).Edited)
                return Fail("putting the file back should clear the mark");

            // The workspace this run made a second time round is no longer wanted, which is the
            // case the button exists for. Deleted through it rather than around it, so the path a
            // person takes is the one under test — and it leaves this run's scratch behind it.
            // Left until here, because it installs and removes once more of its own accord and
            // everything above wants the game in a state it put it in.
            if (AComponentIsRefusedAtBothEnds(model, written) is { } refusedProblem)
                return Fail(refusedProblem);

            if (WorkspacesCanBeDeleted(model, secondary) is { } deleteProblem) return Fail(deleteProblem);

            var nowPreferences = File.Exists(theirSettings) ? File.ReadAllText(theirSettings) : null;
            Console.WriteLine("status   the author's settings file: "
                + (theirPreferences == nowPreferences ? "untouched" : "REWRITTEN"));
            if (theirPreferences != nowPreferences) return Fail($"this run rewrote {theirSettings}");

            var ownErrors = Path.Combine(scratch, PGAssetTool.Core.Settings.ErrorLog.FolderName,
                PGAssetTool.Core.Settings.ErrorLog.ErrorsFileName);
            Console.WriteLine($"status   errors this run logged: {(File.Exists(ownErrors) ? File.ReadAllLines(ownErrors).Count(l => l.Contains("  while ")) : 0)}, in its own folder");
            if (LogsNow() != theirLogsBefore) return Fail($"this run wrote into {theirLogs}");

            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex.ToString());
        }
        finally
        {
            var installation = model.Game;

            // Nothing reading before the reader goes. Deleting a workspace re-reads the folder,
            // which selects whatever is left and starts loading it — and putting the bundles down
            // from under that walks the list it is adding to, which surfaces as a crash inside the
            // library on the way out of a run that had otherwise passed.
            WaitWhile(() => model.Reading, 30_000);
            model.Dispose();
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { }

            // Also on the way out of a failed run, which is the whole reason this is here: a run
            // that stops half way leaves its own mods installed and its own packs kept, and the
            // next run then starts from a game this one made rather than from the author's.
            TidyUpAfterItself(installation, wasInstalled, wasEnabled);
        }
    }

    /// Takes this test's own mods and packs back out, and puts the author's back as they were.
    ///
    /// Three things pile up. The ledger keeps whatever a failed run had installed, and that is not
    /// just clutter — a later run asserts on what the author had before it started, and one of
    /// these left behind makes an unrelated check fail in a way that says nothing about the cause.
    /// The store keeps a copy of every pack it installs, filed under the weapon and look the pack
    /// names, under a file name taken from the directory it was built in — so each run kept its own
    /// copy, in a folder the old sweep of the mods directory itself never looked in. Twenty-four of
    /// them had collected under one weapon. And this run's own packs write assets the author's mods
    /// write, so installing them turns theirs off, which is the tool working correctly and still
    /// has to be undone.
    ///
    /// The packs are found by the id in the manifest rather than by the file's name. The ones this
    /// test installs are also called rival and refused, and the id is the only thing all of them
    /// share and nothing of the author's can be called.
    ///
    /// Safe to call twice, and called twice: once where the run can still check the result, and
    /// again on the way out for the runs that never get there.
    private static void TidyUpAfterItself(
        Core.Game.GameInstallation? installation,
        IReadOnlySet<string>? wasInstalled, IReadOnlySet<string>? wasEnabled)
    {
        if (installation is not null && !Core.Game.GameProcess.IsRunning(installation))
            try
            {
                var store = new ModStore(installation);
                var ledger = store.Read();

                var mine = ledger
                    .Where(m => m.Id.StartsWith(PackIdentity, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // Put back exactly what was there, rather than turning them on through the applier:
                // turning a mod on stands its rivals down, and the author having two that write the
                // same asset is theirs to have. This is undoing a change, not making one.
                List<InstalledMod> switched = wasInstalled is null || wasEnabled is null
                    ? []
                    : ledger.Where(m => wasInstalled.Contains(m.Id)
                                        && m.Enabled != wasEnabled.Contains(m.Id)).ToList();

                if (mine.Count > 0 || switched.Count > 0)
                {
                    if (mine.Count > 0)
                        Console.WriteLine($"cleanup  taking back out: {string.Join(", ", mine.Select(m => m.Id))}");
                    if (switched.Count > 0)
                        Console.WriteLine("cleanup  putting back as they were: "
                            + string.Join(", ", switched.Select(m => $"{m.Name} {(m.Enabled ? "off" : "on")}")));

                    store.Write(ledger
                        .Where(m => !mine.Contains(m))
                        .Select(m => switched.Contains(m)
                            ? m with { Enabled = wasEnabled!.Contains(m.Id) }
                            : m));

                    new Core.Mods.ModApplier(installation, store).Reconcile();
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                Console.WriteLine($"cleanup  could not put the game back: {e.Message}");
            }

        var mods = Path.Combine(ModStore.DefaultHome(), "mods");
        if (!Directory.Exists(mods)) return;

        foreach (var pack in Directory.GetFiles(mods, "*" + Core.Pack.PackBuilder.Extension,
                     SearchOption.AllDirectories))
        {
            string id;
            try { id = Core.Pack.PackBuilder.ReadManifest(pack).Id; }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                continue;
            }

            if (!id.StartsWith(PackIdentity, StringComparison.OrdinalIgnoreCase)) continue;

            try { File.Delete(pack); } catch (IOException) { continue; }
            Prune(Path.GetDirectoryName(pack), mods);
        }
    }

    /// Takes away the folders this test's packs were the last thing in, up to the mods root.
    private static void Prune(string? directory, string root)
    {
        for (var at = directory; at is not null; at = Path.GetDirectoryName(at))
        {
            if (Path.GetFullPath(at).Length <= Path.GetFullPath(root).Length) return;
            if (!Directory.Exists(at) || Directory.EnumerateFileSystemEntries(at).Any()) return;

            try { Directory.Delete(at); } catch (IOException) { return; }
        }
    }

    /// A pack that names a component is refused at both ends: built here, and arriving from
    /// somewhere else.
    ///
    /// Both are checked because they answer different questions. Refusing to build one stops this
    /// tool being what makes it. Refusing to apply one is what actually holds, because a pack built
    /// by something else never went past the first check — so the second is made to face exactly
    /// that: a legitimate pack, opened afterwards, with the operation written into its manifest by
    /// hand the way another tool would have written it.
    ///
    /// The rest of that pack still applies. A refusal that took everything down with it would make
    /// the honest answer cost more than no answer, and authors would route around it.
    private static string? AComponentIsRefusedAtBothEnds(MainViewModel model, string workspace)
    {
        var manifest = Core.Pack.Workspace.Read(workspace);
        var bundle = manifest.Operations[0].Target.Container;
        const string source = "selftest-refused.png";

        Core.Pack.PackOperation refused;

        // Scoped, and put down before anything installs. A reader holds the bundle it has opened,
        // and the install below writes to that same bundle.
        using (var bundles = new Core.Assets.BundleSet(model.Game!))
        {
            var file = bundles.Open(bundle);
            if (file.file.AssetInfos.FirstOrDefault(
                    i => i.TypeId == (int)AssetsTools.NET.Extra.AssetClassID.MonoBehaviour) is not { } info)
                return $"'{bundle}' holds no component to try this with";

            // Any picture will do: what is refused is the class the operation names, and a texture
            // operation is as good a way as any left to point at a component.
            File.Copy(Path.Combine(workspace, manifest.Operations
                    .First(o => o.Op == Core.Pack.PackOperations.ReplaceTexture).Source),
                Path.Combine(workspace, source), overwrite: true);

            refused = new Core.Pack.PackOperation
            {
                Op = Core.Pack.PackOperations.ReplaceTexture,
                Target = new Core.Assets.AssetAddress(
                    bundle, nameof(AssetsTools.NET.Extra.AssetClassID.MonoBehaviour),
                    bundles.Context.Deserialize(file, info)?["m_Name"]?.AsString ?? "", 0, info.PathId),
                Source = source,
            };
        }

        // Built here: refused, and the pack is not written at all.
        var output = Path.Combine(workspace, "refused.pgmod");
        Core.Pack.Workspace.Save(workspace, manifest with
        {
            Operations = [.. manifest.Operations, refused],
        });

        try
        {
            Core.Pack.PackBuilder.Build(workspace, output);
            return "a pack naming a component was built anyway";
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"refused  building: {ex.Message}");
            if (File.Exists(output)) return "the build was refused and still left a pack behind";
        }
        finally
        {
            Core.Pack.Workspace.Save(workspace, manifest);
        }

        // Arriving from somewhere else: a pack this tool did build, with the operation put into its
        // manifest afterwards. Nothing here writes the container; it is opened as what it is.
        //
        // It carries one of the workspace's own pictures, unchanged but counted as edited: by the
        // time this runs the workspace's edits have been put back, and a pack with nothing in it is
        // not built at all. The picture is the game's own, so applying it changes nothing.
        var carried = manifest.Operations.First(o => o.Op == Core.Pack.PackOperations.ReplaceTexture);
        Core.Pack.Workspace.Save(workspace, manifest with
        {
            Operations = [.. manifest.Operations.Select(o =>
                ReferenceEquals(o, carried) ? o with { BaselineSha256 = null } : o)],
        });
        try
        {
            Core.Pack.PackBuilder.Build(workspace, output);
        }
        finally
        {
            Core.Pack.Workspace.Save(workspace, manifest);
        }
        using (var pack = System.IO.Compression.ZipFile.Open(
                   output, System.IO.Compression.ZipArchiveMode.Update))
        {
            var entry = pack.GetEntry(Core.Pack.PackManifest.FileName)!;

            string json;
            using (var reading = new StreamReader(entry.Open())) json = reading.ReadToEnd();
            entry.Delete();

            var inside = Core.Pack.PackManifest.Parse(json);
            using (var writing = new StreamWriter(
                       pack.CreateEntry(Core.Pack.PackManifest.FileName).Open()))
                writing.Write((inside with { Operations = [.. inside.Operations, refused] }).ToJson());

            pack.CreateEntry(source);
        }

        // Installed the way anything is, so the reader is put down and picked up around it.
        _ = model.InstallPacks([output]);
        WaitWhile(() => model.Busy, 300_000);
        Console.WriteLine($"refused  applying: {model.Status}");

        var said = model.Status;

        // Removed through the manager, which is what closes the reader for a write.
        model.Manager.Refresh();
        model.Manager.Selection.Clear();
        foreach (var row in model.Manager.Mods.Where(m => m.Mod.Id == manifest.Id))
            model.Manager.Selection.Add(row);

        if (model.Manager.Chosen.Count > 0)
        {
            model.Manager.RemoveCommand.Execute(null);
            if (model.Manager.Asking is not null) model.Manager.ProceedCommand.Execute(null);
            WaitWhile(() => model.Manager.Busy || model.Manager.Asking is not null, 180_000);
        }

        File.Delete(output);
        File.Delete(Path.Combine(workspace, source));

        if (!said.Contains("behave", StringComparison.OrdinalIgnoreCase))
            return $"a component operation was not turned away on its way in: {said}";
        if (said.Contains("0 applied", StringComparison.Ordinal))
            return $"one refused operation took the rest of the pack down with it: {said}";

        return null;
    }

    /// An angle and a texture stay on the model when the same file is read again, and the two
    /// halves of the comparison move together.
    ///
    /// The workspace is re-read whenever anything in it is written — saving the pack's own icon
    /// counts, and so does anything an image editor does — and each reading empties the lists the
    /// panes are bound to on its way past, which used to take the view with it. That put the angle
    /// back to square at the exact moment somebody had turned the model to the one they wanted.
    private static string? TheViewOutlivesAReading(MainViewModel model, Core.Pack.WorkspaceFile back)
    {
        if (model.Editor.Files.FirstOrDefault(f => f.Name.EndsWith(".glb")) is not { } model3d)
            return "no model in the extracted workspace to look at";

        model.Editor.SelectedFile = model3d;
        Shows(model, model3d);
        if (model.Editor.Edited.Mesh is null) return "the file side of the comparison shows no model";

        if (model.Editor.Edited.TextureChoices.FirstOrDefault(c => c.PathId == -1) is not { } wearing)
            return "the editor offered none of the workspace's own textures to put on the model";

        var turned = (Camera.Facing(2.1f, -0.4f) with { Distance = 0.9f });
        model.Editor.Edited.Camera = turned;
        model.Editor.Edited.ChosenTexture = wearing;

        if (model.Editor.Original.Camera != turned)
            return "the halves are linked, and the game side did not follow the view";
        if (model.Editor.Original.ChosenTexture != wearing)
            return "the halves are linked, and the game side did not take the same texture";

        // The reading a save sets off, which is where the view used to be lost.
        model.Editor.Refresh();
        Shows(model, model3d);

        if (model.Editor.Edited.Camera != turned)
            return $"reading the model again threw the view away: {model.Editor.Edited.Camera}";
        if (model.Editor.Edited.ChosenTexture != wearing)
            return "reading the model again took the texture off the model";
        if (model.Editor.Edited.MeshTextures is null)
            return "the texture was still the chosen one but no longer on the model";

        // Away and back is the same asset as far as the view is concerned, and a different asset
        // is not: an angle chosen for one model says nothing about the next.
        model.Editor.SelectedFile = back;
        Shows(model, back);
        model.Editor.SelectedFile = model3d;
        Shows(model, model3d);

        if (model.Editor.Edited.Camera != turned)
            return "looking at something else and coming back lost the view";

        // Unlinked they are free to differ, which is what a replaced mesh needs: the two are then
        // different models, and each is worth looking at on its own terms.
        model.Editor.Linked = false;
        model.Editor.Edited.Camera = turned.Zoomed(1.5f);
        if (model.Editor.Original.Camera != turned)
            return "with the link off, one half still dragged the other along";

        model.Editor.Linked = true;
        if (model.Editor.Original.Camera != model.Editor.Edited.Camera)
            return "linking them again left them looking from different places";

        Console.WriteLine("editor   the view survives a reading, and the halves move together");
        model.Editor.SelectedFile = back;
        Shows(model, back);
        return null;
    }

    /// A kept pack goes in the folder its weapon and its look name, and the manager shows the same
    /// arrangement.
    ///
    /// Two views of one thing that disagree are worse than either, so the check is that they do
    /// not: the folder the file is in, and the folder the tree opens, are the same two names.
    /// The folder standing at a path, wherever in the shelf it ended up.
    private static ModFolder? Folder(MainViewModel model, IReadOnlyList<string> at)
    {
        var found = new List<ModFolder>();
        void Walk(ModFolder node)
        {
            found.Add(node);
            foreach (var child in node.Children) Walk(child);
        }

        foreach (var root in model.Manager.Folders) Walk(root);

        return found.FirstOrDefault(f => f.At.SequenceEqual(at, StringComparer.OrdinalIgnoreCase));
    }

    private static string? ModsAreFiledByWeaponAndLook(MainViewModel model)
    {
        if (model.Manager.Mods.FirstOrDefault(m => m.Mod.Id == PackIdentity) is not { } row)
            return $"the manager does not list '{PackIdentity}'";
        if (row.Mod.Subject is not { } subject)
            return "the ledger did not record what the mod is for";

        var under = Path.Combine([.. subject.Path]);
        Console.WriteLine($"manager  '{row.Name}' is filed under {under.Replace('\\', '/')}");

        if (!row.Mod.PackPath.Contains(under, StringComparison.OrdinalIgnoreCase))
            return $"it is kept at {row.Mod.PackPath}, which is not under {under}";
        if (!File.Exists(row.Mod.PackPath))
            return "the ledger names a kept pack that is not there";

        // Found by the path rather than by depth. The kind is the top level of the arrangement and
        // the shelf leaves it out while everything installed is the same kind, so how deep any of
        // these sit depends on what else is installed.
        if (Folder(model, subject.Path.Take(2).ToList()) is not { } weapon)
            return "the manager's shelf has no folder for the weapon this mod is for";
        if (Folder(model, subject.Path) is not { } look)
            return $"'{weapon.Label}' has no folder for the look this mod changes";

        // Opening a folder narrows the tiles to what it holds, and to nothing else.
        model.Manager.Folder = look;
        Console.WriteLine($"manager  {weapon.Label} / {look.Label} shows {model.Manager.Shown.Count} "
            + $"of {model.Manager.Mods.Count} installed");

        if (model.Manager.Shown.All(r => r.Mod.Id != PackIdentity))
            return "opening the folder hid the mod filed in it";
        if (model.Manager.Shown.Any(r => r.Mod.Id == PackIdentityB))
            return "the folder shows a mod filed under another weapon";

        // Searched the way the weapon list is searched, which is what makes a weapon findable by
        // any of its names — the number is the plainest proof that it is the same search.
        model.Manager.FolderSearch = subject.Number.ToString();
        if (model.Manager.Shown.All(r => r.Mod.Id != PackIdentity))
            return $"searching for #{subject.Number} hid the mod that is for it";
        if (model.Manager.Shown.Any(r => r.Mod.Id == PackIdentityB))
            return $"searching for #{subject.Number} showed a mod for another weapon";

        model.Manager.FolderSearch = "nothing-installed-is-called-this";
        if (model.Manager.Shown.Count > 0)
            return "a search that matches nothing still showed something";

        model.Manager.FolderSearch = "";

        // Back to everything, which is what the rest of this run acts on.
        model.Manager.Folder = model.Manager.Folders.FirstOrDefault();
        if (model.Manager.Shown.Count != model.Manager.Mods.Count)
            return "the top of the shelf does not hold everything installed";

        // The headings say what the game calls these things now, not what they were called when
        // the pack was built. A pack made in English must not go on reading as English to somebody
        // working in Japanese — and a pack made before any of this existed carries no name to
        // translate at all, so the lookup is by number and by skin id.
        string? Heading() => Folder(model, subject.Path.Take(2).ToList())?.Label;

        var english = Heading();
        var was = model.Language;
        model.Language = "l_ja";
        var japanese = Heading();
        model.Language = was;

        Console.WriteLine($"manager  the heading reads '{english}' and '{japanese}' in Japanese");
        if (japanese is null) return "the shelf lost the weapon when the language changed";
        if (japanese == english)
            return $"the heading stayed '{english}' when the language changed";
        if (Heading() != english) return "the heading did not come back when the language did";

        return null;
    }

    /// A workspace can be thrown away from the editor, folder and all, and it asks first.
    private static string? WorkspacesCanBeDeleted(MainViewModel model, string directory)
    {
        model.Editor.Rescan(model.WorkspaceRoot);
        model.Editor.Selection.Clear();

        model.Editor.SelectedWorkspace = model.Editor.Workspaces.FirstOrDefault(w =>
            string.Equals(Path.GetFullPath(w.Directory), Path.GetFullPath(directory),
                StringComparison.OrdinalIgnoreCase));
        if (model.Editor.SelectedWorkspace is null)
            return $"the editor no longer lists {Path.GetFileName(directory)} to delete";

        model.Editor.DeleteCommand.Execute(null);
        if (model.Editor.Asking is null) return "deleting a workspace asked for no confirmation";
        Console.WriteLine($"editor   asked: {model.Editor.Asking.Title}");

        model.Editor.ProceedCommand.Execute(null);
        WaitWhile(() => model.Editor.Asking is not null, 30_000);
        Console.WriteLine($"editor   {model.Editor.Status}");

        if (Directory.Exists(directory)) return $"{Path.GetFileName(directory)} is still on disk";
        if (model.Editor.Workspaces.Any(w =>
                string.Equals(Path.GetFullPath(w.Directory), Path.GetFullPath(directory),
                    StringComparison.OrdinalIgnoreCase)))
            return "the deleted workspace is still in the list";

        return null;
    }

    /// Going back and forth between two models leaves each one at its own angle.
    ///
    /// Each pane remembers a view per model, and the two panes hand their view to each other while
    /// they are linked — which they must not do while one of them still holds the model before
    /// last. Doing it anyway wrote one model's angle into the other's memory, so walking between
    /// two meshes carried a view across, back, and across again, and everything came round to
    /// where it started every few passes. Three passes, because one shows nothing.
    private static string? TwoModelsKeepTheirOwnViews(MainViewModel model, Core.Pack.WorkspaceFile back)
    {
        var meshes = model.Editor.Files.Where(f => f.Name.EndsWith(".glb")).Take(2).ToList();
        if (meshes.Count < 2) return "the workspace has only one model, and this needs two";

        Camera[] views =
        [
            Camera.Facing(1.1f, 0.2f) with { Distance = 1.3f },
            Camera.Facing(-0.6f, -0.9f) with { Distance = 2.2f },
        ];

        for (var i = 0; i < 2; i++)
        {
            model.Editor.SelectedFile = meshes[i];
            Shows(model, meshes[i]);
            if (model.Editor.Edited.Mesh is null) return $"'{meshes[i].Name}' would not show";

            model.Editor.Edited.Camera = views[i];
        }

        for (var pass = 1; pass <= 3; pass++)
            for (var i = 0; i < 2; i++)
            {
                model.Editor.SelectedFile = meshes[i];
                Shows(model, meshes[i]);

                if (model.Editor.Edited.Camera != views[i])
                    return $"on pass {pass} '{meshes[i].Name}' came back at "
                        + $"{model.Editor.Edited.Camera}, not {views[i]}";
                if (model.Editor.Linked && model.Editor.Original.Camera != views[i])
                    return $"on pass {pass} the two halves of '{meshes[i].Name}' disagree";
            }

        Console.WriteLine($"editor   '{meshes[0].Name}' and '{meshes[1].Name}' kept their own views "
            + "across three passes");

        model.Editor.SelectedFile = back;
        Shows(model, back);
        return null;
    }

    /// Two extractions of the same weapon are two workspaces, not one.
    ///
    /// The view a model was last left at was remembered against the file's path inside its
    /// workspace — and every extraction of one weapon has the same paths inside it. So two
    /// workspaces of the same weapon were one model as far as the memory was concerned: the angle
    /// found in the first turned up in the second, and going to a workspace of anything else threw
    /// it away. Which of the two an author saw depended on which they had opened last.
    private static string? TwoWorkspacesKeepTheirOwnViews(MainViewModel model, Core.Pack.WorkspaceFile back)
    {
        var first = model.Editor.SelectedWorkspace;
        if (first is null) return "nothing is selected to extract a second copy of";

        // A second extraction of the weapon already open, which is what an author doing two takes
        // on one gun ends up with. Named whatever the extractor names it — the first one has been
        // renamed by this point — so it is found by being the one that was not there before.
        var before = model.Editor.Workspaces.Select(w => w.Directory).ToHashSet();

        model.ExtractWeaponCommand.Execute(null);
        WaitWhile(() => model.Busy, 300_000);
        model.Editor.Rescan(model.WorkspaceRoot);

        var second = model.Editor.Workspaces.FirstOrDefault(w => !before.Contains(w.Directory));
        if (second is null)
            return $"extracting the same weapon again made no second workspace: {model.Status}";

        try
        {
            var turned = (Camera.Facing(0.77f, 0.33f) with { Distance = 1.9f });

            if (Model(model, first) is not { } mine) return $"'{first.Name}' has no model to look at";
            model.Editor.Edited.Camera = turned;

            if (Model(model, second) is not { } theirs)
                return $"'{second.Name}' has no model to look at";

            Console.WriteLine($"editor   two takes on one weapon: '{mine.Name}' in {first.Name} at "
                + $"{turned.Angles.Yaw:0.00}, the same file in {second.Name} at "
                + $"{model.Editor.Edited.Camera.Angles.Yaw:0.00}");

            if (model.Editor.Edited.Camera == turned)
                return "a second workspace of the same weapon opened at the first one's angle";

            // And back, to be sure the first one kept its own rather than merely not lending it.
            if (Model(model, first) is null) return "the first workspace stopped showing its model";
            if (model.Editor.Edited.Camera != turned)
                return $"the first workspace came back at {model.Editor.Edited.Camera}, not {turned}";
        }
        finally
        {
            model.Editor.SelectedWorkspace = first;
            model.Editor.SelectedFile = model.Editor.Files.FirstOrDefault(f => f.RelativePath == back.RelativePath);
            if (model.Editor.SelectedFile is { } went) Shows(model, went);

            try { Directory.Delete(second.Directory, recursive: true); } catch (IOException) { }
            model.Editor.Rescan(model.WorkspaceRoot);
        }

        return null;
    }

    /// Selects a workspace's first model and waits for it to arrive. Null if it has none.
    private static Core.Pack.WorkspaceFile? Model(MainViewModel model, WorkspaceItem workspace)
    {
        model.Editor.SelectedWorkspace = workspace;
        if (model.Editor.Files.FirstOrDefault(f => f.Name.EndsWith(".glb")) is not { } mesh) return null;

        model.Editor.Edited.Clear();
        model.Editor.SelectedFile = mesh;
        Shows(model, mesh);
        return model.Editor.Edited.Mesh is null ? null : mesh;
    }

    /// Files dropped on the window reach the file in the workspace they were meant for.
    ///
    /// Three answers to "which one did you mean": the name says so, the selection says so, or
    /// nothing does — and the third has to be said out loud rather than guessed at, because every
    /// one of these overwrites a file somebody is working on.
    private static string? DroppedFilesLand(MainViewModel model, Core.Pack.WorkspaceFile texture)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "pgassettool-selftest-drop");
        Directory.CreateDirectory(scratch);

        try
        {
            // Named the way the workspace names it, which is how a file comes back from an editor
            // that was pointed straight at it.
            var painted = File.ReadAllBytes(texture.FullPath).Concat(new byte[32]).ToArray();
            var byName = Path.Combine(scratch, texture.Name);
            File.WriteAllBytes(byName, painted);

            model.Editor.Import([byName]);
            if (!File.ReadAllBytes(texture.FullPath).SequenceEqual(painted))
                return $"a file dropped under the workspace's own name did not land: {model.Editor.Status}";

            // Named whatever the editor felt like, with the file it is meant for selected.
            var again = painted.Concat(new byte[8]).ToArray();
            var byChoice = Path.Combine(scratch, "whatever-my-editor-called-it.png");
            File.WriteAllBytes(byChoice, again);

            model.Editor.SelectedFile = model.Editor.Files.Single(f => f.RelativePath == texture.RelativePath);
            Shows(model, model.Editor.SelectedFile);
            model.Editor.Import([byChoice]);
            if (!File.ReadAllBytes(texture.FullPath).SequenceEqual(again))
                return $"a file dropped under another name did not reach the selected one: {model.Editor.Status}";

            // A kind of thing this workspace has nothing of, so there is no honest answer.
            var stranger = Path.Combine(scratch, "nothing-here-is-called-this.glb");
            File.WriteAllBytes(stranger, painted);

            model.Editor.Import([stranger]);
            if (!model.Editor.Status.Contains("not taken"))
                return $"a file with nothing to replace was not refused: {model.Editor.Status}";

            Console.WriteLine($"drop     '{texture.Name}' taken by name and by selection; "
                + $"a .glb with nothing to replace refused ({model.Editor.Status.Trim()})");
            return null;
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// Selecting resolves off the UI thread, and Detail holds the previous weapon while it does —
    /// so waiting for it to be non-null passes immediately on the wrong tree.
    /// Writes one texture with no alpha channel, reads it back through the importer and checks the
    /// original alpha survived. Answers null when it did.
    private static string? RoundTripWithoutAlpha(MainViewModel model)
    {
        if (model.Game is null) return "the game is not open";
        using var bundles = new BundleSet(model.Game);

        var file = bundles.Open("d_w");
        var info = ReferenceWalker.FindByName(bundles.Context, file, AssetClassID.Texture2D,
            "eco_rifle_map", StringComparison.OrdinalIgnoreCase);
        if (info is null) return "eco_rifle_map is not in d_w any more";

        var field = bundles.Context.Deserialize(file, info)!;
        var before = AssetPreview.Texture(bundles, "d_w", field);
        if (before is null) return "the original could not be decoded";

        long alphaBefore = 0;
        for (var i = 3; i < before.Bgra.Length; i += 4) alphaBefore += before.Bgra[i];

        var directory = Directory.CreateTempSubdirectory("pgassettool-alpha").FullName;
        try
        {
            var written = new AssetExporter(bundles) { Opaque = true }
                .Export("d_w", file, info, directory, fileNameOverride: "rgb");

            using (var stream = File.OpenRead(written[0].Path))
            {
                var read = StbImageSharp.ImageResult.FromStream(
                    stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                if (read?.SourceComp != StbImageSharp.ColorComponents.RedGreenBlue)
                    return $"the opaque export still carries {read?.SourceComp}";
            }

            var stream2 = field["m_StreamData"];
            var payload = bundles.ReadResource("d_w", stream2["path"].AsString,
                stream2["offset"].AsLong, stream2["size"].AsLong);
            var pixels = AssetsTools.NET.Texture.TextureFile.ReadTextureFile(field)
                .DecodeTextureRaw(payload, useBgra: true);

            var again = bundles.Context.Deserialize(file, info)!;
            var change = TextureImporter.Replace(again, written[0].Path, pixels);
            if (!change.AlphaKept) return "the importer did not reuse the original alpha";

            var after = AssetsTools.NET.Texture.TextureFile.ReadTextureFile(again);
            var decoded = after.DecodeTextureRaw(after.pictureData, useBgra: true);
            long alphaAfter = 0;
            for (var i = 3; i < decoded.Length; i += 4) alphaAfter += decoded[i];

            Console.WriteLine($"alpha    exported without it, reimported with the original back: "
                + $"mean {alphaBefore / (before.Bgra.Length / 4.0):F1} -> {alphaAfter / (decoded.Length / 4.0):F1}");

            if (alphaBefore != alphaAfter) return "the alpha came back different";

            // And the same promise for a masked export, whose alpha channel is the tool's own work:
            // solid where a model reads, empty everywhere else. Applying one of those has to keep
            // what the game had in that channel, or every emissive part of a masked texture goes out.
            var masked = new AssetExporter(bundles)
            {
                Coverage = texture => Enumerable
                    .Range(0, texture.Width * texture.Height)
                    .Select(at => at % texture.Width < texture.Width / 2)
                    .ToArray(),
            }.Export("d_w", file, info, directory, fileNameOverride: "masked");

            if (!masked[0].AlphaIsMask) return "a masked export did not record that it was masked";

            var third = bundles.Context.Deserialize(file, info)!;
            var kept = TextureImporter.Replace(third, masked[0].Path, pixels, alphaIsMask: true);
            if (!kept.AlphaKept) return "a masked texture came back without the game's own alpha";

            var back = AssetsTools.NET.Texture.TextureFile.ReadTextureFile(third);
            var afterMask = back.DecodeTextureRaw(back.pictureData, useBgra: true);
            long alphaMasked = 0;
            for (var i = 3; i < afterMask.Length; i += 4) alphaMasked += afterMask[i];

            Console.WriteLine("alpha    a masked export keeps the game's own alpha as well");
            return alphaMasked == alphaBefore
                ? null
                : $"a masked texture's alpha came back as {alphaMasked}, not {alphaBefore}";
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// Reads a bundle, puts the reader down, and asks whether the file is free.
    ///
    /// The whole design of the window rests on this: bundles are held open for browsing, and every
    /// write to the game closes the reader first and assumes that is enough. If disposing does not
    /// actually release the handle, an install works only until somebody has looked at the bundle it
    /// is about to write — which is intermittent, unattributable, and exactly what was happening.
    private static string? ReaderReleasesItsBundles(MainViewModel model)
    {
        foreach (var (what, pixels) in new[] { ("reading the objects", false), ("reading the pixels", true) })
        {
            const string name = "dw";
            string path;

            // Both halves, because they are different code. The pixels of a texture live in a
            // sibling stream inside the bundle, reached through the bundle's own data reader rather
            // than through the serialized file — which is the path a person takes by clicking a
            // texture, and the one nothing had ever checked released.
            using (var bundles = new BundleSet(model.Game!))
            {
                path = bundles.PathOf(name);
                var file = bundles.Open(name);
                var info = file.file.AssetInfos.First(i => i.TypeId == (int)AssetClassID.Texture2D);
                var field = bundles.Context.Deserialize(file, info)!;
                if (pixels && AssetPreview.Texture(bundles, name, field) is null)
                    return $"a texture in '{name}' could not be decoded";
            }

            try
            {
                using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Console.WriteLine($"status   '{name}' released after {what}: yes");
            }
            catch (IOException ex)
            {
                return $"'{name}' is still held open after {what} — {ex.Message}";
            }
        }
        return null;
    }

    /// Playing moves the clip on by the time that really passed between the display's frames, and
    /// stopping and starting again leaves exactly one run of frames going.
    ///
    /// It was a timer at thirty a second stepping a fixed thirtieth: on a 120Hz screen something new
    /// showed one frame in four, and a slow frame put the clip behind the clock for good. The
    /// window's frame clock does not run without a window, so one is stood in for here.
    private static string? PlayingFollowsTheFrames(PreviewViewModel preview, MotionChoice clip)
    {
        var frames = new Queue<Action<TimeSpan>>();
        preview.RequestFrame = frames.Enqueue;

        try
        {
            var span = Math.Min(0.5, clip.Motion.Length * 0.4);
            var late = 0.1;

            preview.Time = 0;
            preview.Playing = true;

            // A 120Hz screen for that long, with one frame arriving a tenth of a second late.
            var now = TimeSpan.Zero;
            var steps = (int)Math.Round(span * 120);
            for (var frame = 0; frame <= steps && frames.TryDequeue(out var next); frame++)
            {
                next(now);
                now += TimeSpan.FromSeconds(1 / 120.0) + (frame == steps / 2 ? TimeSpan.FromSeconds(late) : TimeSpan.Zero);
            }

            var elapsed = steps / 120.0 + late;
            var expected = elapsed % Math.Max(clip.Motion.Length, 0.001);
            var played = preview.Time;

            // Stopped and started twice over before a frame arrives: every run still going queues
            // itself again once a frame, and only the last should be.
            preview.Playing = false;
            preview.Playing = true;
            preview.Playing = false;
            preview.Playing = true;

            for (var round = 0; round < 8 && frames.Count > 0; round++)
            {
                var pending = frames.Count;
                for (var i = 0; i < pending; i++) frames.Dequeue()(now);
                now += TimeSpan.FromSeconds(1 / 120.0);
            }

            var running = frames.Count;

            Console.WriteLine($"anim     {steps + 1} frames at 120Hz over {elapsed:0.###}s moved '{clip.Name}' to "
                + $"{played:0.###}s (the clock says {expected:0.###}s); after stopping and starting twice, "
                + $"{running} run(s) of frames going");

            if (Math.Abs(played - expected) > 0.002)
                return $"{elapsed:0.###}s of frames moved the clip {played:0.###}s rather than {expected:0.###}s";
            if (running != 1)
                return $"stopping and starting again left {running} runs of frames driving the clip";

            return null;
        }
        finally
        {
            preview.Stop();
            preview.RequestFrame = null;
        }
    }

    /// A weapon's model plays its own animations, and comes back where it started.
    ///
    /// The game's weapons are one skinned mesh on three or four bones — the slide, the magazine, the
    /// hands — and their clips are legacy curves bound to those bones by name. So a reload can be
    /// shown without a scene or a second mesh: evaluate the curves, walk the hierarchy, and the
    /// vertices follow.
    ///
    /// Three things have to hold, and each of them was wrong at some point on the way here. The
    /// model at rest has to be the model as it was read, which is what says the bind poses are being
    /// composed the right way round — it was out by a metre and a half until the space was taken
    /// from the model itself rather than from the renderer's transform. Something has to actually
    /// move, which it did not while the one-bone-per-vertex rigging these weapons use was read as
    /// having no weights at all. And it has to come back: a clip that ends somewhere else has been
    /// evaluated past its end.
    private static string? TheModelPlaysItsAnimations(MainViewModel model)
    {
        if (!Select(model, 16)) return "#16 never resolved, and it is the one with the clips";
        if (model.Detail?.SelectedNode is not { } row) return "#16 selected nothing to show";
        if (!Arrived(model, row)) return $"'{row.Label}' never appeared";

        var preview = model.Preview;
        if (preview.Mesh is null) return $"'{row.Label}' is not a model";

        Console.WriteLine($"anim     '{row.Label}': {preview.Clips.Count} clip(s) move it"
            + (preview.Clips.Count == 0 ? "" : $" — {string.Join(", ", preview.Clips.Select(c => c.Name))}"));

        if (preview.Clips.Count == 0) return $"'{row.Label}' is skinned and nothing plays on it";

        var rest = preview.Rest ?? preview.Mesh;
        var reload = preview.Clips.FirstOrDefault(c => c.Name.Contains("Reload", StringComparison.OrdinalIgnoreCase))
            ?? preview.Clips[0];

        preview.Clip = reload;
        if (!preview.Playing) return $"choosing '{reload.Name}' did not start it";

        // A frame at a time rather than through the timer: the clock on screen is the window's own,
        // and a test with no window has nothing driving it. What the tick does is all here.
        var wrapped = 0;
        for (var frame = 0; frame < 90; frame++)
        {
            var was = preview.Time;
            preview.Advance(1 / 30.0);
            if (preview.Time < was) wrapped++;
        }

        Console.WriteLine($"anim     three seconds of it came round {wrapped} time(s)"
            + $", stopping at {preview.Time:0.###}s");

        if (wrapped == 0) return $"'{reload.Name}' is {reload.Motion.Length:0.00}s and three seconds of it never came round";

        preview.Stop();
        if (preview.Playing) return "stopping did not stop it";

        if (PlayingFollowsTheFrames(preview, reload) is { } frames) return frames;

        // At the beginning, the model is the model.
        preview.Time = 0;
        if (Apart(rest, preview.Mesh) is { } atRest && atRest > 0.0001f)
            return $"at rest '{reload.Name}' has already moved the model by {atRest:0.###}";

        var furthest = 0f;
        for (var step = 1; step <= 12; step++)
        {
            preview.Time = reload.Motion.Length * step / 12.0;
            if (Apart(rest, preview.Mesh) is { } moved) furthest = Math.Max(furthest, moved);
        }

        Console.WriteLine($"anim     '{reload.Name}' {reload.Motion.Length:0.00}s: "
            + $"furthest a vertex moves is {furthest:0.###}");

        if (furthest < 0.001f) return $"'{reload.Name}' moved nothing at all";

        preview.Time = reload.Motion.Length;
        if (Apart(rest, preview.Mesh) is { } atEnd && atEnd > furthest)
            return $"'{reload.Name}' ends further from rest ({atEnd:0.###}) than it ever gets ({furthest:0.###})";

        preview.Clip = null;
        return Apart(rest, preview.Mesh) is { } after && after > 0.0001f
            ? "putting the clip down left the model where it had got to"
            : null;
    }

    /// How far the furthest vertex of one model is from the same vertex of another.
    private static float? Apart(Core.Export.Meshes.UnityMesh? a, Core.Export.Meshes.UnityMesh? b)
    {
        if (a?.Get(Core.Export.Meshes.VertexAttribute.Position) is not { } one) return null;
        if (b?.Get(Core.Export.Meshes.VertexAttribute.Position) is not { } other) return null;
        if (one.Length != other.Length) return null;

        var furthest = 0f;
        for (var at = 0; at + 2 < one.Length; at += 3)
        {
            var dx = one[at] - other[at];
            var dy = one[at + 1] - other[at + 1];
            var dz = one[at + 2] - other[at + 2];
            furthest = MathF.Max(furthest, MathF.Sqrt(dx * dx + dy * dy + dz * dz));
        }

        return furthest;
    }

    /// Every kind the game has opens in the browser, and one that is not a weapon goes all the way
    /// to a workspace.
    ///
    /// Weapons were the only kind for the tool's first year, and everything that reads one turned
    /// out to be general already: a hat is a prefab under a root of its own with a skin beside it
    /// and an offer icon named after its id, which is a weapon's arrangement with the names changed.
    /// What was missing was a catalogue per kind, and those are all in `generated_data`.
    ///
    /// So this drives the picker rather than the catalogue: select each kind, take the first thing
    /// in it, and see a tree with a model in it come back. A kind whose registry reads but whose
    /// prefabs do not resolve looks perfectly healthy from the catalogue's side and shows an empty
    /// pane, which is the failure worth catching. A model and not merely something: 50 of the 112
    /// pets resolved to a GameObject and its Transform — a description of the pet that shares its
    /// name — and an assets-at-all check let every one of them through.
    private static string? EveryKindOpens(MainViewModel model, string scratch)
    {
        if (model.Kinds.Count < 2) return "only one kind of thing was found in the game";
        if (!model.OffersKinds) return "the kind picker is hidden with kinds to choose between";

        var was = model.Kind;

        try
        {
            foreach (var kind in model.Kinds)
            {
                model.Kind = kind;
                if (model.Weapons.Count == 0) return $"the {kind.Name} list came up empty";

                var first = model.Weapons[0];
                model.Selected = first;

                if (!WaitWhile(() => model.Detail?.Tree.Record.Slug != first.Record.Slug, 60_000))
                    return $"'{first.Record.Slug}' ({kind.Name}) never resolved: {model.Status}";

                var tree = model.Detail!.Tree;
                Console.WriteLine($"kinds    {kind.Name,-10} {model.Weapons.Count,5} listed   "
                    + $"first '{tree.DisplayName}' in {tree.PrefabBundle ?? "-"}: "
                    + $"{tree.PrefabAssets.Count} assets, {tree.Related.Count} related, "
                    + $"mesh {tree.MainMesh?.Name ?? "-"}, icon {(tree.Icon is null ? "no" : "yes")}");

                if (tree.PrefabBundle is null)
                    return $"'{first.Record.Slug}' ({kind.Name}) is in no bundle: "
                        + string.Join("; ", tree.UnresolvedReasons);
                if (tree.PrefabAssets.Count == 0)
                    return $"'{first.Record.Slug}' ({kind.Name}) resolved to nothing";
                if (tree.MainMesh is null)
                    return $"'{first.Record.Slug}' ({kind.Name}) resolved to {tree.PrefabAssets.Count} objects and no model";
            }
        }
        finally
        {
            model.Kind = was;
        }

        return HatsExtractLikeWeapons(model, scratch);
    }

    /// A hat written out as a workspace is a workspace like any other, and says it is a hat.
    ///
    /// The whole point of the kinds being the same shape is that nothing downstream has to change,
    /// and the one thing that does is the label: a pack is filed by what it is for, so a hat that
    /// called itself a weapon would go in the wrong folder and show up under the wrong heading in
    /// the manager.
    ///
    /// Read through the originals the way the window reads, so a hat whose bundles a mod has been
    /// installed over is still written out as the game ships it.
    private static string? HatsExtractLikeWeapons(MainViewModel model, string scratch)
    {
        if (model.Game is null || Core.Catalog.ItemKinds.ByName("Hat") is not { } hats) return null;

        using var bundles = new BundleSet(model.Game, originals: new ModStore(model.Game).OriginalOf);
        var catalogs = Core.Catalog.GameCatalogs.Load(bundles);
        if (catalogs.Of(hats).FirstOrDefault() is not { } hat) return "the game lists no hats";

        var tree = new Core.Weapons.WeaponResolver(bundles, catalogs).Resolve(hat);
        var into = Path.Combine(scratch, "kinds");

        var export = new Core.Export.WeaponExporter(bundles) { MaskUnused = true }
            .ExportAsWorkspace(tree, into, "self test", null);

        var manifest = Core.Pack.Workspace.Read(export.Directory);

        Console.WriteLine($"kinds    '{hat.Slug}' written out as {manifest.Operations.Count} "
            + $"replaceable file(s), filed as {manifest.Subject?.Kind}/{manifest.Subject?.Id}");

        if (manifest.Subject?.Kind != hats.Pack)
            return $"a hat's workspace is filed as '{manifest.Subject?.Kind}'";
        if (manifest.Subject.Number != 0)
            return $"a hat was given the number {manifest.Subject.Number}, and hats are not numbered";
        if (manifest.Operations.Count == 0) return "a hat's workspace named nothing replaceable";

        // Its own texture, not the particle sheet it shares with everything else: a kind whose
        // prefab resolves but whose own art does not is a kind that looks fine and is not.
        if (!manifest.Operations.Any(o => o.Source.Contains(hat.Slug, StringComparison.OrdinalIgnoreCase)))
            return $"nothing in '{hat.Slug}'s workspace is named after it";

        // Every asset once. The shop icon is reached as the icon and again among the related assets,
        // and was written out twice as two operations on the same texture.
        if (manifest.Operations
                .GroupBy(o => (o.Target.Container.ToLowerInvariant(), o.Target.PathId))
                .FirstOrDefault(g => g.Select(o => o.Source).Distinct().Count() > 1) is { } twice)
            return $"'{hat.Slug}'s workspace writes {twice.First().Target} from {twice.Count()} files";

        try { Directory.Delete(into, recursive: true); } catch (IOException) { }
        return null;
    }

    /// A masked texture carries the mask in its alpha, and the manifest says that it does.
    ///
    /// Both halves matter and they are written in different places. The file has to be solid where
    /// the model reads and empty everywhere else, which is what makes it worth opening. The manifest
    /// has to say so, because that is the only thing standing between the mask and the game's own
    /// alpha channel — apply one of these without it and every emissive part stops glowing.
    private static string? TheMaskReachesTheFileAndTheManifest(
        string workspace, IReadOnlyList<Core.Pack.PackOperation> operations)
    {
        var masked = operations.Where(o => o.AlphaIsMask).ToList();

        Console.WriteLine($"mask     {masked.Count} of "
            + $"{operations.Count(o => o.Source.EndsWith(".png", StringComparison.OrdinalIgnoreCase))} "
            + "image(s) were written masked");

        if (masked.Count == 0) return "nothing was masked, and this weapon's model has UVs to mask by";

        foreach (var operation in masked)
        {
            var path = Path.Combine(workspace, operation.Source);
            if (!File.Exists(path)) return $"'{operation.Source}' is named as masked and is not there";

            using var stream = File.OpenRead(path);
            var image = StbImageSharp.ImageResult.FromStream(
                stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);

            var (clear, solid, between) = (0, 0, 0);
            for (var at = 3; at < image.Data.Length; at += 4)
                switch (image.Data[at])
                {
                    case 0: clear++; break;
                    case 255: solid++; break;
                    default: between++; break;
                }

            if (between > 0)
                return $"'{operation.Source}' is masked and has {between} half-transparent pixels";
            if (clear == 0) return $"'{operation.Source}' is marked masked and nothing was cleared";
            if (solid == 0) return $"'{operation.Source}' is masked to nothing at all";

            Console.WriteLine($"mask     '{operation.Source}': {solid} kept, {clear} cleared");
        }

        return null;
    }

    /// The pictures filed beside a weapon are in the tree when the tree is filtered to what can be
    /// replaced — because they can be.
    ///
    /// A weapon's chat icon and each skin's shop icon are registered against the weapon's number
    /// rather than pointed at by anything in its prefab, so they arrive as paths and the whole group
    /// of them used to go when the filter went on. They are textures an extract writes out and a
    /// pack replaces, which is exactly what the filter promises to show; two per weapon were missing
    /// from it. The row has to carry the object as well as the path, or it can be read and not
    /// looked at.
    private static string? RelatedPicturesSurviveTheFilter(MainViewModel model)
    {
        static List<TreeNode> Rows(WeaponDetailViewModel detail)
            => detail.Roots.FirstOrDefault(r => r.Label == "Related")?.Children
                .SelectMany(space => space.Children).ToList() ?? [];

        if (model.Detail is not { } filtered) return "there is no tree to look at";
        var kept = Rows(filtered);

        var was = model.ReplaceableOnly;
        model.ReplaceableOnly = false;
        var all = model.Detail is { } unfiltered ? Rows(unfiltered) : [];
        model.ReplaceableOnly = was;

        Console.WriteLine($"related  {kept.Count} of {all.Count} rows kept by the filter: "
            + string.Join(", ", kept.Select(r => $"{r.Label} ({r.Class})")));

        if (all.Count == 0) return "this weapon has no related assets and it has a chat icon";
        if (kept.Count == 0) return "the filter dropped every related asset";
        if (kept.Count >= all.Count) return "the filter kept every related asset, replaceable or not";

        var chat = kept.FirstOrDefault(r => r.Label.EndsWith("_chaticon", StringComparison.OrdinalIgnoreCase));
        if (chat is null) return "the chat icon is not in the filtered tree";
        if (chat.Class != AssetClassID.Texture2D) return $"the chat icon is listed as a {chat.Class}";
        if (chat.PathId == 0) return "the chat icon row carries no path id, so it cannot be looked at";

        // And it shows. The path id comes from the bundle's own container table rather than from a
        // search by name, so this is where a wrong answer from that table would appear.
        model.Preview.Clear();
        if (model.Detail is not { } showing) return "the tree went away while the filter was put back";
        var row = Rows(showing).FirstOrDefault(r => r.Label == chat.Label);
        if (row is null) return "the chat icon left the tree when the filter was put back";

        showing.SelectedNode = row;
        Arrived(model, row);

        Console.WriteLine($"related  {row.Label}: {model.Preview.Caption}");
        return model.Preview.Nothing is { } why ? $"the chat icon could not be shown: {why}" : null;
    }

    /// What an export clears out of a texture is never anything the model shows.
    ///
    /// The mask is worked out in UV space and written into an image that is stored the other way up,
    /// and a mistake in that — the flip, a submesh paired with the wrong material — would rub out
    /// part of the weapon and leave an author repainting something the game never reads. Neither
    /// shows up in the file size or in the mask's own arithmetic.
    ///
    /// So it is checked against the thing it has to agree with: everything outside the mask is
    /// painted a colour nothing in the game is, the model is drawn with it from six angles, and any
    /// one of that colour reaching the screen is a texel the mask should have kept.
    private static string? TheMaskKeepsWhatTheModelShows(MainViewModel model)
    {
        if (model.Preview.Mesh is not { } mesh) return "nothing is being shown to check the mask against";
        if (model.Preview.MeshTextures is not { } dressed) return null;

        var painted = new List<PreviewImage?>();
        var masked = 0;

        for (var part = 0; part < dressed.Count; part++)
        {
            if (dressed[part] is not { } picture) { painted.Add(null); continue; }

            // No margin: the tightest mask there is, and so the strictest thing to hold it to. A
            // mask that reaches further can only cover more of what the model shows, never less.
            if (Core.Export.Meshes.UvCoverage.Of([(mesh, part)], picture.Width, picture.Height, margin: 0)
                is not { } used)
            {
                painted.Add(picture);
                continue;
            }

            masked++;
            var bgra = (byte[])picture.Bgra.Clone();
            for (var at = 0; at < used.Length; at++)
            {
                if (used[at]) continue;

                // Magenta, opaque. The renderer reads the colour channels and ignores alpha, so the
                // paint has to be in the colour.
                bgra[at * 4] = 255;
                bgra[at * 4 + 1] = 0;
                bgra[at * 4 + 2] = 255;
                bgra[at * 4 + 3] = 255;
            }

            painted.Add(picture with { Bgra = bgra });
        }

        if (masked == 0)
        {
            Console.WriteLine("mask     nothing of this model's textures could be masked");
            return null;
        }

        var target = new RenderTarget();
        target.Resize(320, 320);

        var showing = 0;
        var drawn = 0;

        foreach (var camera in new[]
                 {
                     new Camera(), Camera.Facing(1.6f, 0), Camera.Facing(3.1f, 0), Camera.Facing(-1.6f, 0),
                     Camera.Facing(0, 1.5f), Camera.Facing(0, -1.5f),
                 })
        {
            MeshRenderer.Render(mesh, camera, target, painted);

            for (var at = 0; at < target.Bgra.Length; at += 4)
            {
                if (target.Bgra[at + 3] == 0) continue;
                drawn++;

                // Shaded, so the magenta arrives dimmed — what marks it is red and blue together
                // with no green, which nothing in these textures is.
                if (target.Bgra[at] > 60 && target.Bgra[at + 2] > 60 && target.Bgra[at + 1] < 20) showing++;
            }
        }

        Console.WriteLine($"mask     {masked} of {dressed.Count} texture(s) masked; "
            + $"{showing} of {drawn:N0} pixels drawn from what it cleared");

        return showing > 0
            ? $"the mask cleared {showing} pixels' worth of texture the model actually shows"
            : null;
    }

    /// The Discord post counts what is edited, names the game open now, and follows the form rather
    /// than the manifest — what is on screen is what the author means to post.
    private static string? TheForumPostSaysWhatChanged(MainViewModel model)
    {
        var editor = model.Editor;
        var typed = editor.PackDescription;
        editor.PackDescription = "Typed and not saved.";
        try
        {
            var task = editor.ForumPostAsync();
            if (!WaitWhile(() => !task.IsCompleted, 30_000)) return "the Discord post never came";
            if (task.Result is not { } post) return "there was no Discord post for the selected workspace";

            Console.WriteLine("editor   discord post:\n  " + post.Replace("\n", "\n  "));

            var edited = editor.Files.Count(f => f.Edited && f.Operation == Core.Pack.PackOperations.ReplaceTexture);
            var lines = post.Split('\n');
            using var context = new AssetsContext();
            var live = GameVersion.Read(context, model.Game!);

            if (!lines[0].StartsWith(editor.PackName + " / ")) return $"the post opens '{lines[0]}'";
            if (!lines.Contains("Typed and not saved.")) return "the post did not take the description as typed";
            if (!lines.Any(l => l.StartsWith("Changes: ") && l.Contains($"{edited} texture")))
                return $"the post does not count {edited} edited texture(s)";
            if (!lines.Contains("GameVersion: " + live)) return $"the post does not name the game open now, {live}";
            if (!lines.Contains("ToolVersion: " + Core.Pack.ForumPost.ToolVersion))
                return "the post does not name the tool's version";
            return null;
        }
        finally
        {
            editor.PackDescription = typed;
        }
    }

    /// A bundle modded by another copy of the tool cannot be extracted through this one.
    ///
    /// Two copies in two folders keep two sets of backups. From the second, a bundle the first
    /// modded is simply what the game holds now — so an extract there would write somebody else's
    /// work out as the game's own, and a pack built from it would carry it under another name. A
    /// reader with no backups of its own is exactly that second copy, and this run has just
    /// installed a mod to give it something to find.
    private static string? AnotherCopysModsCannotBeExtracted(
        MainViewModel model, IReadOnlyList<Core.Mods.InstalledMod> installed)
    {
        var touched = installed.SelectMany(m => m.TouchedBundles.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (touched.Count == 0) return "nothing installed by this run names a bundle";

        // This copy kept the originals, so it answers for every one of them.
        if (model.Reader is not { } reader) return "there is no reader to ask";
        if (reader.Altered(touched) is { Count: > 0 } mine)
            return $"this copy cannot read the game as shipped: {string.Join(", ", mine)}";

        using var elsewhere = new BundleSet(model.Game!);
        var altered = elsewhere.Altered(touched);
        Console.WriteLine($"shipped  {touched.Count} bundle(s) written to; a copy without the backups "
            + $"calls {altered.Count} of them altered");
        if (altered.Count == 0) return "a reader with no backups saw nothing altered in a modded game";

        var weapon = model.Weapons.FirstOrDefault(w =>
            installed.Any(m => m.Subject?.Number == w.Record.GameNumber));
        if (weapon is null) return "no installed mod's weapon is in the list";

        var temp = Directory.CreateTempSubdirectory("pgassettool-selftest-shipped").FullName;
        try
        {
            var catalogs = Core.Catalog.GameCatalogs.Load(elsewhere);
            var tree = new Core.Weapons.WeaponResolver(elsewhere, catalogs).Resolve(weapon.Record);
            new Core.Export.WeaponExporter(elsewhere) { MaskUnused = true }.Export(tree, temp);
            return $"#{weapon.Record.GameNumber} extracted through a reader that cannot tell the game's own bytes";
        }
        catch (InvalidOperationException refused)
        {
            Console.WriteLine($"shipped  #{weapon.Record.GameNumber} refused: {refused.Message[..Math.Min(90, refused.Message.Length)]}…");
            return null;
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    /// A model whose one mesh wears two pictures wears both of them in the editor.
    ///
    /// #145's mesh is the gun in submesh 0 and its flashlight in submesh 1, painted `mp5Gold_map`
    /// and `mp5Light_map`. The editor put one picture over the whole model, so the flashlight was
    /// drawn in the gun's paint — 1,869 of the model's 21,912 drawn pixels, and the author found it
    /// by looking. Four of the 308 meshes in the first 150 weapons are like this, so it is not the
    /// common case and not a rare one either.
    ///
    /// Its own workspace, extracted and thrown away here, because neither of the weapons this test
    /// works with has such a mesh and swapping one of them for this would move what everything
    /// after it is about.
    private static string? OneMeshCanWearTwoPictures(MainViewModel model, string root)
    {
        const int weapon = 145;
        var was = model.Editor.SelectedWorkspace?.Directory;

        if (!Select(model, weapon)) return $"#{weapon} could not be selected";

        model.ExtractWeaponCommand.Execute(null);
        WaitWhile(() => model.Busy, 120_000);
        if (model.LastExport is not { } directory || !Directory.Exists(directory))
            return $"extracting #{weapon} wrote nothing: {model.Status}";

        try
        {
            model.Editor.Rescan(root);
            if (model.Editor.Workspaces.FirstOrDefault(w => w.Directory == directory) is not { } opened)
                return $"the workspace extracted for #{weapon} did not appear in the editor";

            model.Editor.SelectedWorkspace = opened;

            // The one with two submeshes. The arms are a mesh of this workspace too and wear one
            // picture, like almost everything else.
            var meshes = model.Editor.Files
                .Where(f => f.Name.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var mesh in meshes)
            {
                model.Editor.SelectedFile = mesh;
                if (!Shows(model, mesh))
                    return $"'{mesh.Name}' never loaded in the editor: {model.Editor.Edited.Nothing}";

                if (model.Editor.Edited.Mesh is not { SubMeshes.Count: > 1 }) continue;

                var worn = model.Editor.Edited.MeshTextures;
                var sizes = worn is null
                    ? "nothing"
                    : string.Join(", ", worn.Select(t => t is null ? "-" : $"{t.Width}x{t.Height}"));

                Console.WriteLine($"editor   '{mesh.Name}' has {model.Editor.Edited.Mesh.SubMeshes.Count} "
                    + $"submeshes and is dressed with {sizes}");

                if (worn is null || worn.Any(t => t is null))
                    return $"'{mesh.Name}' has a submesh drawn in no picture at all";

                // Two pictures, not the same one twice — compared by their pixels, because two
                // readings of one file are two objects and the bug being checked for is exactly
                // one picture read twice. Both sides of the comparison, since both dress the model
                // and only one of them was ever looked at while this was wrong.
                if (Alike(worn))
                    return $"'{mesh.Name}' wears two different pictures in the game and one here";

                var game = model.Editor.Original.MeshTextures;
                if (game is null || game.Count != worn.Count || game.Any(t => t is null) || Alike(game))
                    return $"'{mesh.Name}' is dressed with two pictures as edited and not in the game";

                return null;
            }

            return $"#{weapon} has a mesh with two submeshes and none was found in its workspace";
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            model.Editor.Rescan(root);
            model.Editor.SelectedWorkspace = was is null
                ? model.Editor.Workspaces.FirstOrDefault()
                : model.Editor.Workspaces.FirstOrDefault(w => w.Directory == was);
        }
    }

    /// Whether every submesh of a model is drawn in the same picture, by the pixels rather than by
    /// the objects: reading one file twice makes two of them.
    private static bool Alike(IReadOnlyList<PreviewImage?> worn)
        => worn.Count > 1 && worn.Skip(1).All(t =>
            t is not null && worn[0] is not null && t.Bgra.AsSpan().SequenceEqual(worn[0]!.Bgra));

    /// A model opened in the editor comes up wearing its own paint, and says which of the pictures
    /// that is.
    ///
    /// The workspace is a `.glb` and a folder of `.png` and holds nothing that says which goes on
    /// which — no renderers, no materials — so a model drew grey until somebody picked a picture out
    /// of a list of a dozen, every time, for every mesh. The answer is in the game: the renderer
    /// that draws this mesh names a material, the material names a texture, and the workspace files
    /// record which of the game's textures each of them stands for.
    private static string? TheEditorPutsTheModelInItsPaint(MainViewModel model)
    {
        var models = model.Editor.Files
            .Where(f => f.Name.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (models.Count == 0) return "the extracted workspace holds no model to dress";

        var dressed = 0;

        foreach (var mesh in models)
        {
            model.Editor.SelectedFile = mesh;
            if (!Shows(model, mesh))
                return $"'{mesh.Name}' never loaded in the editor: {model.Editor.Edited.Nothing}";

            var offered = model.Editor.Edited.TextureChoices;
            var lit = offered.Count(c => c.Worn);
            var chosen = model.Editor.Edited.ChosenTexture;
            var slots = model.Editor.Edited.MeshTextures;

            Console.WriteLine($"editor   '{mesh.Name}': {lit} of {Math.Max(offered.Count - 1, 0)} "
                + $"pictures are its own, on {slots?.Count(t => t is not null) ?? 0} of "
                + $"{model.Editor.Edited.Mesh?.SubMeshes.Count ?? 0} submesh(es), "
                + $"list on '{chosen?.Name ?? "nothing"}'");

            if (lit == 0) continue;
            dressed++;

            // The list opens on its own paint rather than on one of the pictures, because the model
            // is already wearing it — submesh by submesh, which is not something one entry in a
            // list of names can stand for.
            if (chosen is not { PathId: 0 })
                return $"'{mesh.Name}' came up dressed and its list says it is wearing "
                    + $"'{chosen?.Name ?? "nothing"}'";

            if (slots is not { } on || on.All(t => t is null))
                return $"'{mesh.Name}' knows which pictures it wears and is still drawn grey";

            if (model.Editor.Edited.Mesh is { SubMeshes.Count: > 1 } several && on.Count != several.SubMeshes.Count)
                return $"'{mesh.Name}' has {several.SubMeshes.Count} submeshes and was dressed with {on.Count}";

            // Marked and listed first, both: an order only says something once the whole list has
            // been read, and the mark says it at a glance.
            var order = offered.Skip(1).Select(c => c.Worn).ToList();
            if (order.LastIndexOf(true) > order.IndexOf(false))
                return $"'{mesh.Name}' lists a picture it wears below one it does not";
        }

        if (dressed == 0) return "no model in the workspace knew which of its pictures it wears";
        return null;
    }

    /// A reconcile that leaves bundles alone leaves the game where rebuilding it would.
    ///
    /// A reconcile restores every modified bundle and applies everything enabled again, which is the
    /// same work whether one mod changed or none did — so turning one mod off rebuilt seventeen
    /// bundles to change one. It now leaves alone any bundle already holding exactly what this run
    /// would put into it, judged on the mods and packs behind it and on the file not having moved
    /// since.
    ///
    /// That is a shortcut past the tool's most load-bearing property, so what is checked here is not
    /// that it is quicker but that it is invisible: settle the game, leave it alone, then rebuild it
    /// from scratch, and every bundle has to come out with the hash it already had. If it does not,
    /// the shortcut is wrong — or something in the writing is not deterministic, which would be
    /// worth knowing about just as much.
    ///
    /// Run before this test installs anything, so what it settles is the author's own game and the
    /// answer is about their mods rather than this run's.
    private static string? LeavingBundlesAloneArrivesWhereRebuildingDoes(MainViewModel model)
    {
        var game = model.Game!;
        var store = new ModStore(game);

        // Nothing enabled means nothing is written into any bundle, so a reconcile restores and
        // leaves nothing alone — correctly. Asked of the ledger rather than of the backups: a
        // backup outlives the mod that caused it, so counting those says a game is modded long
        // after every mod on it has been turned off, and the check then fails on a game that is
        // behaving exactly as it should.
        if (!store.Read().Any(m => m.Enabled))
        {
            Console.WriteLine("shortcut no mod is enabled, so there is nothing to leave alone");
            return null;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var settling = new Core.Mods.ModApplier(game, store).Reconcile();
        var settled = clock.ElapsedMilliseconds;

        var before = LiveBundles(game, store);
        if (before.Count == 0)
        {
            Console.WriteLine("shortcut nothing is installed, so there is nothing to leave alone");
            return null;
        }

        clock.Restart();
        var again = new Core.Mods.ModApplier(game, store).Reconcile();
        var quickly = clock.ElapsedMilliseconds;

        Console.WriteLine($"shortcut settling {before.Count} bundle(s) took {settled}ms, "
            + $"asking again took {quickly}ms and left {again.Unchanged.Count} alone");

        if (again.Unchanged.Count == 0)
            return "a reconcile with nothing to do left no bundle alone";
        if (again.Restored.Count > 0)
            return $"a reconcile with nothing to do put back {string.Join(", ", again.Restored)}";
        if (again.Applied.Count > 0 && settling.Applied.Count > 0)
            return $"a reconcile with nothing to do applied {again.Applied.Count} operation(s)";

        var between = LiveBundles(game, store);
        if (Differ(before, between) is { } moved)
            return $"a reconcile that did nothing changed {moved}";

        clock.Restart();
        var forced = new Core.Mods.ModApplier(game, store) { Rebuild = true }.Reconcile();
        Console.WriteLine($"shortcut rebuilding the lot took {clock.ElapsedMilliseconds}ms, "
            + $"{forced.Applied.Count} applied, {forced.Unchanged.Count} left alone");

        if (forced.Unchanged.Count > 0)
            return $"rebuilding everything still left {forced.Unchanged.Count} bundle(s) alone";

        var after = LiveBundles(game, store);
        if (Differ(before, after) is { } elsewhere)
            return $"rebuilding arrived somewhere else than leaving things alone: {elsewhere}";

        return null;
    }

    /// What every bundle this store has a backup of currently holds, by the copy the game loads.
    private static Dictionary<string, string> LiveBundles(Core.Game.GameInstallation game, ModStore store)
    {
        var holds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, bundle, hash) in store.BackedUp())
            if (game.Resolve(bundle, hash) is { } resolved && File.Exists(resolved.Path))
                holds[bundle] = Core.Mods.BundleIntegrity.Md5(resolved.Path);

        return holds;
    }

    private static string? Differ(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        var moved = before
            .Where(b => !after.TryGetValue(b.Key, out var now) || now != b.Value)
            .Select(b => b.Key)
            .ToList();

        return moved.Count == 0 ? null : string.Join(", ", moved);
    }

    /// A bundle written back comes back the same bundle, at either setting.
    ///
    /// The file the game loads is assembled here rather than by the library that knows the format —
    /// the library's compressor is twenty-five times slower than the one used instead, and was
    /// seventeen of the nineteen seconds an apply took. That makes the layout this tool's own
    /// problem: a header, a table of blocks, and a hundred-odd compressed blocks, in a format
    /// nothing in this repository documents. Reading one back and asking it what it holds is the
    /// cheapest way to know it was assembled right, and the first attempt at it was wrong in a way
    /// that would have left every modded bundle unreadable.
    ///
    /// Both settings, because they differ in more than effort: a block compressed the quick way is
    /// flagged differently from one compressed thoroughly, and a block that would have grown is
    /// stored as it is and flagged as neither.
    ///
    /// Nothing of the game is written. The bundle is read from the installation and the copies go
    /// to this run's own scratch directory.
    private static string? ABundleSurvivesBeingWrittenBothWays(MainViewModel model, string scratch)
    {
        const string name = "bw";
        string path;
        int assets;
        List<string> textures;

        using (var bundles = new BundleSet(model.Game!))
        {
            path = bundles.PathOf(name);
            var file = bundles.Open(name);
            assets = file.file.AssetInfos.Count;
            textures = Textures(bundles.Context, file);
        }

        foreach (var packing in new[] { BundlePacking.Smaller, BundlePacking.Faster })
        {
            var output = Path.Combine(scratch, $"{name}.{packing}".ToLowerInvariant());
            var clock = System.Diagnostics.Stopwatch.StartNew();

            using (var editor = new BundleEditor(path)) editor.Save(output, packing);
            var took = clock.ElapsedMilliseconds;

            int back;
            List<string> theirs;

            try
            {
                using var editor = new BundleEditor(output);
                back = editor.File.file.AssetInfos.Count;

                // Deserialized rather than counted, so this reads the blocks themselves and not
                // only the table in front of them.
                theirs = Textures(editor.Context, editor.File);
            }
            catch (Exception e)
            {
                // Anything at all: a file assembled wrongly fails in whatever way the reader
                // happens to fail, and none of those ways is one to carry on from.
                return $"a bundle written {packing} could not be read back — {e.GetType().Name}: {e.Message}";
            }

            if (back != assets) return $"'{name}' holds {assets} assets and {back} written {packing}";
            if (!theirs.SequenceEqual(textures))
                return $"'{name}' holds {textures.Count} textures and {theirs.Count} written {packing}";

            Console.WriteLine($"pack     '{name}' written {packing.ToString().ToLowerInvariant()}: "
                + $"{new FileInfo(output).Length / 1024}KB in {took}ms, {back} assets, {theirs.Count} textures");

            File.Delete(output);
        }

        return null;
    }

    private static List<string> Textures(AssetsContext context, AssetsFileInstance file)
        => file.file.AssetInfos
            .Where(i => i.TypeId == (int)AssetClassID.Texture2D)
            .Select(i => context.Deserialize(file, i)?["m_Name"].AsString ?? "")
            .Order(StringComparer.Ordinal)
            .ToList();

    /// Renames a workspace and re-labels its manifest, the way the editor's form does.
    ///
    /// The id is set here rather than through the form, which deliberately does not offer it: the
    /// ledger keys an installed mod by it, so changing it in the window would turn an update into a
    /// second copy. This test needs its own, which is a different thing from an author needing one.
    private static string? Rename(
        MainViewModel model, string directory, string folder, string name, string id)
    {
        model.Editor.Rescan(model.WorkspaceRoot);

        var full = Path.GetFullPath(directory);
        model.Editor.SelectedWorkspace = model.Editor.Workspaces.FirstOrDefault(
            w => string.Equals(Path.GetFullPath(w.Directory), full, StringComparison.OrdinalIgnoreCase));
        if (model.Editor.SelectedWorkspace is null) return null;

        model.Editor.FolderName = folder;
        model.Editor.PackName = name;
        model.Editor.ApplyDetailsCommand.Execute(null);

        if (model.Editor.SelectedWorkspace is not { } moved) return null;
        if (!string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(moved.Directory)), folder,
                StringComparison.Ordinal))
            return null;

        var renamed = Path.GetFullPath(Path.TrimEndingDirectorySeparator(moved.Directory));
        PGAssetTool.Core.Pack.Workspace.Save(
            renamed, PGAssetTool.Core.Pack.Workspace.Read(renamed) with { Id = id });
        return renamed;
    }

    /// An edit to a weapon's own paint reaches its default skin as well, where the two are copies.
    ///
    /// A weapon with skins has two default looks, and which a player sees depends on whether they
    /// have ever changed skins. #507's default skin paints with a separate texture whose pixels are
    /// the weapon's own, so a pack that replaced only the weapon's showed to some players and not
    /// to others. #416's paints with the very same asset, and pairing it would be a second write of
    /// one texture.
    private static string? AnEditToTheWeaponReachesItsDefaultSkin(MainViewModel model)
    {
        if (model.Reader is not { } reader) return "the game is not open to extract from";

        var catalogs = PGAssetTool.Core.Catalog.GameCatalogs.Load(reader);
        var resolver = new PGAssetTool.Core.Weapons.WeaponResolver(reader, catalogs);
        var weapons = catalogs.Of(PGAssetTool.Core.Catalog.ItemKinds.Weapon);
        var root = Path.Combine(Path.GetTempPath(), $"pgassettool-selftest-skins-{Guid.NewGuid():N}");

        try
        {
            (int Shared, int Rows, int Files, string Names, int Notes) Extract(int number)
            {
                var tree = resolver.Resolve(weapons.First(w => w.GameNumber == number));
                var export = new PGAssetTool.Core.Export.WeaponExporter(reader) { MaskUnused = true }
                    .ExportAsWorkspace(tree, root, "self test", null);

                var pairs = PGAssetTool.Core.Pack.Workspace.Read(export.Directory).Operations
                    .Where(o => o.Op == PGAssetTool.Core.Pack.PackOperations.ReplaceTexture)
                    .GroupBy(o => o.Source, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .ToList();

                var view = PGAssetTool.Core.Pack.WorkspaceView.Open(export.Directory)!;
                return (pairs.Count, view.Files.Count,
                    view.Files.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    string.Join("; ", pairs.Select(g => $"{g.Key} -> {string.Join(" + ", g.Select(o => o.Target.Name))}")),
                    export.Notes?.Count ?? 0);
            }

            var knife = Extract(507);
            var ultimatum = Extract(416);

            Console.WriteLine($"skins    #507: {knife.Shared} picture(s) written to two assets ({knife.Names}), "
                + $"{knife.Notes} note(s); #416: {ultimatum.Shared}; each file listed once: "
                + $"{knife.Rows == knife.Files && ultimatum.Rows == ultimatum.Files}");

            if (knife.Shared == 0 || !knife.Names.Contains("Weapon928_default", StringComparison.OrdinalIgnoreCase))
                return "#507's default skin paints with a copy of its texture, and an edit to the weapon's does not reach it";
            if (knife.Notes == 0) return "#507's paint was written to two assets and the author was not told";
            if (ultimatum.Shared != 0)
                return $"#416's default skin paints with the weapon's own texture and was paired anyway: {ultimatum.Names}";
            if (knife.Rows != knife.Files || ultimatum.Rows != ultimatum.Files)
                return "a picture written to two assets is listed twice in the editor";

            return null;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    /// Browse and extraction read the game as shipped, not the mods installed over it.
    ///
    /// They read the live bundles, so an installed mod was what Browse showed and what an extract
    /// wrote out as the weapon's own — and a pack takes only what changed after the extract, so it
    /// could carry somebody else's installed work without anybody seeing it. Checked against the
    /// bundles this run's own pack has just been written into, which are exactly the ones where the
    /// two answers differ.
    private static string? BrowsingReadsTheGameAsShipped(MainViewModel model)
    {
        if (model.Reader is not { } reader) return "the game was not read again after installing";

        var game = model.Game!;
        var store = new PGAssetTool.Core.Mods.ModStore(game);
        var mine = store.Read().FirstOrDefault(m => m.Id == PackIdentity);
        if (mine is null || mine.TouchedBundles.Count == 0) return "this run's pack wrote into no bundle to read back";

        var looked = 0;
        foreach (var (bundle, hash) in mine.TouchedBundles)
        {
            if (game.Resolve(bundle, hash) is not { } live) continue;
            if (store.OriginalOf(live.Cache, bundle, hash) is not { } original)
                return $"'{bundle}' was written into and no original of it is kept";

            var read = reader.PathOf(bundle);
            if (!string.Equals(Path.GetFullPath(read), Path.GetFullPath(original), StringComparison.OrdinalIgnoreCase))
                return $"Browse reads '{bundle}' from {read}, the modded copy, rather than from its original";

            if (Core.Mods.BundleIntegrity.Md5(live.Path) == Core.Mods.BundleIntegrity.Md5(original))
                return $"'{bundle}' is no different from its original, so reading it proves nothing";

            looked++;
        }

        Console.WriteLine($"shipped  Browse reads the {looked} bundle(s) this run's pack is in from their originals");
        return looked == 0 ? "none of the bundles this run's pack is in could be found to check" : null;
    }

    /// Protection is picked from a list and saved the moment it is picked, and the re-read that the
    /// save sets off leaves whatever is half-typed in the form where it was.
    ///
    /// Both halves failed once. A three-state checkbox turned protection off on the first click, and
    /// the only way to make it stick was Save; and every re-read of the workspace refilled the form
    /// from disk, so a description being typed vanished whenever anything in the folder was written.
    private static string? ChoicesSaveAtOnceAndLeaveTypingAlone(MainViewModel model, string written)
    {
        const string typed = "half-typed, never saved";

        model.ProtectPacks = true;
        var followingLabel = model.Editor.ProtectChoices[0].Label;
        model.ProtectPacks = false;
        var followingLater = model.Editor.ProtectChoices[0].Label;

        model.Editor.PackDescription = typed;
        model.Editor.SelectedProtect = model.Editor.ProtectChoices.First(c => c.Value == true);

        var onDisk = PGAssetTool.Core.Pack.Workspace.Read(written);

        // What the watcher would do a moment later. Asked directly: its timer does not run here.
        model.Editor.Refresh();
        var survived = model.Editor.PackDescription == typed;
        var stillChosen = model.Editor.SelectedProtect?.Value;

        Console.WriteLine($"editor   protection: '{followingLabel}', then '{followingLater}'; "
            + $"picking 'protect' wrote {onDisk.Protect?.ToString() ?? "nothing"} at once, the form still says "
            + $"{stillChosen?.ToString() ?? "follow"}, and the typed description {(survived ? "survived" : "was lost in")} the re-read");

        model.Editor.RevertDetailsCommand.Execute(null);

        if (!followingLabel.Contains("— protected") || !followingLater.Contains("not protected"))
            return "the first protection choice does not say what Options currently answers";
        if (onDisk.Protect != true) return "picking 'protect this pack' did not reach the manifest until Save";
        if (onDisk.Description == typed) return "picking protection saved the half-typed description along with it";
        if (stillChosen != true) return "the re-read after picking protection put the old choice back";
        if (!survived) return "re-reading the workspace threw away text typed into the form";
        if (model.Editor.PackDescription == typed) return "Revert kept the typing it was asked to throw away";

        return null;
    }

    /// Options keeps its Close button on screen however little screen there is and however much
    /// of it is open, and Escape closes it.
    ///
    /// It was one stack sized to its content and not resizable, with Close at the bottom: at
    /// 1920x1080 half the button was below the edge of the screen and nothing could reach it.
    private static string? OptionsCanBeClosedOnAShortScreen(MainViewModel model)
    {
        const double tall = 420;

        // Every moment the language is emptied or changing it fails, from opening the window to after
        // it is closed. Watched rather than sampled: a picker that passes on nothing and then picks
        // again leaves the model looking right a moment later.
        var emptied = new List<string>();
        void Watch(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.Language) && string.IsNullOrEmpty(model.Language))
                emptied.Add("the language was set to nothing");
            if (e.PropertyName == nameof(MainViewModel.Status)
                && (model.Status ?? "").Contains("changing the language", StringComparison.Ordinal))
                emptied.Add(model.Status!);
        }
        model.PropertyChanged += Watch;

        var window = new Views.OptionsWindow { DataContext = model };
        window.Show();

        // After showing, which is when the window caps itself to the real screen.
        window.MaxHeight = tall;

        var explanations = window.GetVisualDescendants()
            .OfType<Avalonia.Controls.Primitives.ToggleButton>()
            .Where(t => t.Content is "?")
            .ToList();
        foreach (var why in explanations) why.IsChecked = true;

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.Measure(new Avalonia.Size(460, tall));
        window.Arrange(new Avalonia.Rect(0, 0, 460, tall));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var close = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .FirstOrDefault(b => b.Name == "CloseButton");
        var bottom = close?.TranslatePoint(new Avalonia.Point(0, close.Bounds.Height), window);

        Console.WriteLine($"options  at 460x{tall} with all {explanations.Count} explanations open, "
            + $"Close ends at {bottom?.Y:0} and Escape {(close?.IsCancel == true ? "closes it" : "does nothing")}");

        // Reloaded with it open, as building and applying a pack does. The language list is filled
        // again, and the picker bound to it can pass the emptied list on as no language at all —
        // which read a translation table called nothing and said so in the status bar.
        var language = model.Language;
        Settle(model.ReloadAsync(), "reloading under the options window");
        WaitWhile(() => model.Busy, 120_000);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // The scale picker holds plain numbers and writes the percent sign in with a template, so
        // what it shows and what it is bound to are two things that can fail apart.
        var scales = window.GetVisualDescendants().OfType<Avalonia.Controls.ComboBox>()
            .FirstOrDefault(c => ReferenceEquals(c.ItemsSource, model.UiScales));
        var shownScale = window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>()
            .Select(t => t.Text)
            .FirstOrDefault(t => t == $"{model.UiScale}%");

        Console.WriteLine($"options  the scale is {model.UiScale}, the picker holds "
            + $"{scales?.SelectedItem?.ToString() ?? "nothing"} and shows '{shownScale ?? "nothing"}'");

        if (scales is null) return "the options window offers no UI scale";
        if (scales.SelectedItem as int? != model.UiScale)
            return $"the scale is {model.UiScale} and its picker holds {scales.SelectedItem}";
        if (shownScale is null) return $"the scale picker shows no '{model.UiScale}%' anywhere";

        var picker = window.GetVisualDescendants().OfType<Avalonia.Controls.ComboBox>()
            .FirstOrDefault(c => ReferenceEquals(c.ItemsSource, model.Languages));
        var shows = picker?.SelectedValue as string;
        var saved = Core.Settings.ToolSettings.Load(model.SettingsHome).Language;

        Console.WriteLine($"options  reloaded under it: language '{model.Language}' (was '{language}'), "
            + $"the picker shows '{shows}', saved as '{saved}'");

        window.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        model.PropertyChanged -= Watch;

        Console.WriteLine($"options  from opening to closing, the language was emptied or failed {emptied.Count} time(s)"
            + (emptied.Count == 0 ? "" : ": " + string.Join("; ", emptied.Distinct())));

        // Emptied for a moment is what the picker does while its list is refilled, and is let pass:
        // the model takes no notice of an empty choice. Failing because of it is what must not happen.
        if (emptied.FirstOrDefault(e => e.Contains("changing the language", StringComparison.Ordinal)) is { } failed)
            return $"the options window made changing the language fail: {failed}";

        if (model.Language != language)
            return $"reloading under the options window changed the language from '{language}' to '{model.Language}'";
        if (saved != language) return $"reloading under the options window saved the language as '{saved}'";
        if (picker is not null && shows != language)
            return $"after reloading, the options window's picker shows '{shows}' for '{language}'";

        if (close is null) return "the options window has no Close button";
        if (explanations.Count == 0) return "the options window has no explanations to open";
        if (bottom is null || bottom.Value.Y > tall)
            return $"with every explanation open the Close button ends at {bottom?.Y:0}, below a {tall}px screen";
        if (!close.IsCancel) return "Escape does not close the options window";

        return null;
    }

    /// Ctrl+F looks in the workspace it was pressed in and never takes you somewhere else.
    ///
    /// It switched to Browse first, so pressing it in the manager — which has a search of its own —
    /// threw away the shelf on screen to search the weapon list instead.
    private static string? FindingStaysInTheWorkspaceItIsAskedIn(MainViewModel model)
    {
        var showing = model.Workspace;

        try
        {
            foreach (var tab in new[] { MainViewModel.ManagerTab, MainViewModel.EditorTab, MainViewModel.BrowseTab })
            {
                model.Workspace = tab;
                model.FocusSearchCommand.Execute(null);

                if (model.Workspace != tab)
                    return $"Ctrl+F pressed in workspace {tab} moved the window to {model.Workspace}";
            }

            Console.WriteLine("find     Ctrl+F stayed in the manager, the editor and browse");
            return null;
        }
        finally
        {
            model.Workspace = showing;
        }
    }

    /// The pack details form keeps its buttons on the page in a window squeezed to its smallest.
    ///
    /// It did not: the form is docked to the bottom of a column, and a window short enough simply
    /// clipped it — Save and Revert went off the bottom with no way to reach them. Measured at the
    /// smallest size the window will go to, because that is the case that failed.
    private static string? TheFormFitsASmallWindow(MainViewModel model)
    {
        var showing = model.Workspace;
        var window = new Views.MainWindow { DataContext = model, Width = 900, Height = 560 };
        window.Show();
        model.Workspace = MainViewModel.EditorTab;

        // The form is behind an expander, which builds nothing until it is opened.
        foreach (var expander in window.GetVisualDescendants().OfType<Avalonia.Controls.Expander>())
            expander.IsExpanded = true;

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.Measure(new Avalonia.Size(900, 560));
        window.Arrange(new Avalonia.Rect(0, 0, 900, 560));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var buttons = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .Where(b => b.Content is "Save" or "Revert")
            .ToList();

        var offscreen = buttons
            .Select(b => b.TranslatePoint(new Avalonia.Point(0, b.Bounds.Height), window))
            .Count(p => p is null || p.Value.Y > 560);

        Console.WriteLine($"editor   at 900x560 the form's {buttons.Count} buttons are on the page: "
            + $"{buttons.Count - offscreen} of {buttons.Count}");

        window.Close();

        // Put the tab back: the caller is part way through the manager, and this borrowed the model
        // to look at the editor.
        model.Workspace = showing;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        if (buttons.Count != 2) return $"the pack details form shows {buttons.Count} buttons, not two";
        return offscreen > 0 ? $"{offscreen} of the form's buttons fall off a 900x560 window" : null;
    }

    /// The scale chosen in Options is what the window's content is laid out at.
    ///
    /// Measured rather than assumed, because there are two ways to get it wrong that both look right
    /// in the model: a transform over the top of the layout leaves every size where it was and
    /// clips, and a binding that resolves to nothing leaves the setting perfectly correct and the
    /// window exactly as it was. What the content actually gets is the room the window has divided
    /// by the scale — drawn back out at that scale, that is the whole of it made larger.
    private static string? TheScaleReachesTheLayout(MainViewModel model)
    {
        const double wide = 1200, high = 800;
        var was = model.UiScale;
        var window = new Views.MainWindow { DataContext = model, Width = wide, Height = high };
        window.Show();

        double? Content(int scale)
        {
            model.UiScale = scale;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.Measure(new Avalonia.Size(wide, high));
            window.Arrange(new Avalonia.Rect(0, 0, wide, high));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            return window.GetVisualDescendants().OfType<Avalonia.Controls.LayoutTransformControl>()
                .FirstOrDefault()?.Child?.Bounds.Width;
        }

        try
        {
            var plain = Content(100);
            var larger = Content(150);

            Console.WriteLine($"scale    in a {wide:0} window the content is laid out {plain:0} wide "
                + $"at 100% and {larger:0} at 150%");

            if (plain is null || larger is null) return "the window has no scaled content to measure";
            if (Math.Abs(plain.Value - wide) > 2)
                return $"at 100% the content is {plain:0} wide in a {wide:0} window, not the whole of it";

            // Two thirds of the room, so that everything in it comes out half again as large.
            var wanted = wide / 1.5;
            return Math.Abs(larger.Value - wanted) > 2
                ? $"at 150% the content was given {larger:0} of {wide:0}, and {wanted:0} is what 150% means"
                : null;
        }
        finally
        {
            model.UiScale = was;
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    /// How wide the first tile in the manager is actually being drawn, or null when there is none.
    private static double? Tile(Views.MainWindow window)
        => window.GetVisualDescendants().OfType<Avalonia.Controls.Image>()
            .Select(i => i.FindAncestorOfType<Avalonia.Controls.Border>()?.Width)
            .FirstOrDefault(w => w is > 0);

    /// A skin can be written out alongside the weapon, models and all.
    ///
    /// Skin assets were left out of every export until now: the tree resolved them and the exporter
    /// was never taught to walk them, so the one thing most weapon mods change could not be reached.
    /// Both kinds are checked, because they are found by different means — most skins repaint the
    /// same geometry, and a few bring a model of their own filed under the skin's id.
    private static string? SkinsAreOffered(MainViewModel model, string scratch)
    {
        var offered = model.SkinChoices.Where(s => s.Id is not null).ToList();
        Console.WriteLine($"skins    {offered.Count} offered, "
            + $"{offered.Count(s => s.HasModel)} of them with a model of their own");

        if (offered.Count == 0) return "#416 has skins and none was offered for extraction";
        if (!model.HasSkins) return "skins were offered and the picker is hidden";
        if (model.ChosenSkin?.Id is not null) return "a skin was chosen before anybody asked for one";

        var withModel = offered.FirstOrDefault(s => s.HasModel);
        if (withModel is null) return "#416 has a skin with its own model and none was marked as having one";

        // Through the shell's own command, so the picker, the exporter and the folder naming are
        // all under test rather than only the last of them.
        var written = new List<string>();
        foreach (var skin in new[] { offered.First(s => !s.HasModel), withModel })
        {
            model.ChosenSkin = skin;
            model.ExtractWeaponCommand.Execute(null);
            WaitWhile(() => model.Busy, 300_000);

            if (model.LastExport is not { } directory || !Directory.Exists(directory))
                return $"extracting '{skin.Name}' wrote nothing: {model.Status}";
            written.Add(directory);

            var mine = Path.Combine(directory, "skin");
            if (!Directory.Exists(mine)) return $"'{skin.Name}' was asked for and nothing of it was written";

            var files = Directory.GetFiles(mine, "*", SearchOption.AllDirectories);
            Console.WriteLine($"skins    '{skin.Name}' -> {Path.GetFileName(directory)}, "
                + $"{files.Length} files of its own ("
                + string.Join(", ", files.GroupBy(f => Path.GetExtension(f))
                    .Select(g => $"{g.Count()}{g.Key}")) + ")");

            if (!files.Any(f => f.EndsWith(".png")))
                return $"'{skin.Name}' wrote no texture, which is the whole of what a skin is";
            if (skin.HasModel && !files.Any(f => f.EndsWith(".glb")))
                return $"'{skin.Name}' brings its own model and no mesh was written";
            if (!skin.HasModel && files.Any(f => f.EndsWith(".glb")))
                return $"'{skin.Name}' only repaints and a mesh was written for it";

            // A skin that brings a model is masked by that model's UVs. It was masked by nothing:
            // no mesh the weapon has draws its paint, so every texture of its own came out whole.
            if (skin.HasModel && model.MaskUnusedTextures)
            {
                var paint = Core.Pack.Workspace.Read(directory).Operations
                    .Where(o => o.Op == Core.Pack.PackOperations.ReplaceTexture
                        && o.Target.Name.StartsWith(skin.Id!, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                Console.WriteLine($"skins    '{skin.Name}': {paint.Count(o => o.AlphaIsMask)} of {paint.Count} "
                    + "textures named after it masked by its own model");

                if (paint.Count == 0) return $"'{skin.Name}' wrote no texture named after itself";
                if (!paint.Any(o => o.AlphaIsMask))
                    return $"'{skin.Name}' brings a model and none of its own textures was masked by it";
            }

            // Nothing of the other skins. Every skin's offer icon, profile and definition is filed
            // against the same weapon, so all of them used to come out whichever one was asked for
            // — seven sets of files an author has no reason to touch to change the eighth.
            var alongside = Path.Combine(directory, "related");
            var others = (Directory.Exists(alongside)
                    ? Directory.GetFiles(alongside, "*", SearchOption.AllDirectories)
                    : [])
                .Where(f => !Path.GetFileName(f).StartsWith(skin.Id!, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (others.Count > 0)
                return $"'{skin.Name}' brought {others.Count} file(s) belonging to other skins: "
                    + string.Join(", ", others.Select(Path.GetFileName));

            // A skin with its own model replaces the weapon rather than repainting it, so the
            // weapon's own geometry is not what is being changed and has no business being here.
            var weaponsOwn = Path.Combine(directory, "meshes");
            if (skin.HasModel && Directory.Exists(weaponsOwn))
                return $"'{skin.Name}' has a model of its own and the weapon's was written too";
            if (!skin.HasModel && !Directory.Exists(weaponsOwn))
                return $"'{skin.Name}' only repaints and the model it repaints was not written";
        }

        // Asked for again, the same skin gets a workspace of its own rather than writing over the
        // one that is already there — which used to take an author's edits with it, and the
        // manifest that said which mod their work was.
        model.ChosenSkin = withModel;
        model.ExtractWeaponCommand.Execute(null);
        WaitWhile(() => model.Busy, 300_000);

        if (model.LastExport is not { } again || !Directory.Exists(again))
            return $"extracting '{withModel.Name}' a second time wrote nothing: {model.Status}";
        if (string.Equals(Path.GetFullPath(again), Path.GetFullPath(written[1]),
                StringComparison.OrdinalIgnoreCase))
            return $"extracting '{withModel.Name}' twice wrote over the first at {again}";

        Console.WriteLine($"skins    asked for '{withModel.Name}' again -> {Path.GetFileName(again)}");
        written.Add(again);

        // Two looks of one weapon are two mods.
        //
        // The id used to be the weapon's slug and nothing else, so every mod of a weapon was the
        // same mod as far as the ledger went: installing a skin replaced the plain one, and
        // installing somebody else's replaced yours — quietly, since replacing by id is how a mod
        // is updated. Each extraction mints its own now, and says what it was made from.
        var manifests = written.Select(Core.Pack.Workspace.Read).ToList();
        Console.WriteLine($"skins    filed as {string.Join(" and ", manifests.Select(m => m.Id))}");

        if (manifests[0].Id == manifests[1].Id)
            return $"two skins of one weapon were both called '{manifests[0].Id}'";

        // Including the two of the same skin: two extractions are two mods whatever they were made
        // from, which is what "mints its own" has to mean to be worth anything.
        if (manifests.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifests.Count)
            return $"{manifests.Count} extractions produced fewer ids than that";

        foreach (var manifest in manifests)
        {
            if (manifest.Subject is not { } subject)
                return $"'{manifest.Id}' does not say which weapon it is for";
            if (subject.Number != 416)
                return $"'{manifest.Id}' says it is for #{subject.Number}, not the weapon it came from";
            if (subject.Variant.Length == 0)
                return $"'{manifest.Id}' was extracted with a skin and records none";
        }

        // Same kind, same weapon, different look: the first two levels of the path agree and the
        // last one does not.
        if (!manifests[0].Subject!.Path.Take(2).SequenceEqual(manifests[1].Subject!.Path.Take(2)))
            return "two skins of one weapon are filed under two different weapons";
        if (manifests[0].Subject!.Path[2] == manifests[1].Subject!.Path[2])
            return "two different skins are filed under the same look";

        // Put it back, or extracting the next weapon would carry this one's skin along with it.
        model.ChosenSkin = SkinChoice.None;
        foreach (var directory in written) Directory.Delete(directory, recursive: true);
        model.Editor.Rescan(model.WorkspaceRoot);
        return null;
    }

    /// The pack shows a drawing of the model, and can be given another one from a different angle.
    ///
    /// The game's own weapon icon says which weapon a pack is for and nothing about what the pack
    /// does to it, which for a texture or mesh mod is the only interesting part. Both halves are
    /// checked: the one the export draws, and the one the editor's button takes from whatever the
    /// preview is showing.
    private static string? PackIconIsTheModel(MainViewModel model, string workspace)
    {
        var drawn = Path.Combine(workspace, PGAssetTool.Core.Preview.PackIcon.FileName);
        var manifest = PGAssetTool.Core.Pack.Workspace.Read(workspace);

        if (manifest.Icon != PGAssetTool.Core.Preview.PackIcon.FileName)
            return $"the pack shows '{manifest.Icon}' rather than a drawing of the model";
        if (!File.Exists(drawn)) return "the manifest names a drawing that was never made";

        // Disposed: leaving it open made the capture below fail to write over it, which is the file
        // being locked by the test rather than anything wrong with the tool.
        StbImageSharp.ImageInfo? size;
        using (var reading = File.OpenRead(drawn)) size = StbImageSharp.ImageInfo.FromStream(reading);
        Console.WriteLine($"editor   the export drew the model as {size?.Width}x{size?.Height}, "
            + $"{new FileInfo(drawn).Length:N0} bytes");
        if (size is not { Width: PGAssetTool.Core.Preview.PackIcon.Size })
            return "the drawing is not the size an icon is meant to be";

        // Drawn again from somewhere else, the way the button does it. A mesh read back off disk,
        // because that is what the editor is showing when somebody presses it.
        var glb = Directory.EnumerateFiles(workspace, "*.glb", SearchOption.AllDirectories).FirstOrDefault();
        if (glb is null) return null;

        if (AssetPreview.FromFile(glb) is not PGAssetTool.Core.Export.Meshes.UnityMesh mesh) return $"'{Path.GetFileName(glb)}' would not read back";

        var turned = PGAssetTool.Core.Preview.PackIcon.Render(
            mesh, null, PGAssetTool.Core.Preview.Camera.Facing(2.1f, -0.4f));
        if (PGAssetTool.Core.Preview.PackIcon.IsBlank(turned)) return "drawing it from another angle came out empty";

        var before = File.ReadAllBytes(drawn);
        model.Editor.CaptureIcon(turned);
        Console.WriteLine($"editor   {model.Editor.Status}");

        if (PGAssetTool.Core.Pack.Workspace.Read(workspace).Icon != PGAssetTool.Core.Preview.PackIcon.FileName)
            return $"capturing a view did not become the pack's icon: {model.Editor.Status}";
        if (File.ReadAllBytes(drawn).SequenceEqual(before))
            return "capturing a different angle left the old picture in place";

        return null;
    }

    /// Extracts another weapon under this test's own name, with one file edited so there is
    /// something to pack. Answers where it landed, or null when a step of that fell short.
    private static string? SecondWorkspace(
        MainViewModel model, int weapon, string folder, string id, string name)
    {
        // Every step says which one it was. Answering "it could not be prepared" for five different
        // failures meant a run that fell over here left nothing to go on but a guess.
        if (!Select(model, weapon))
        {
            Console.WriteLine($"batch    #{weapon} could not be selected");
            return null;
        }

        model.ExtractWeaponCommand.Execute(null);
        WaitWhile(() => model.Busy, 120_000);
        if (model.LastExport is not { } exported || !Directory.Exists(exported))
        {
            Console.WriteLine($"batch    extracting #{weapon} wrote nothing: {model.Status}");
            return null;
        }

        if (Rename(model, exported, folder, name, id) is not { } directory)
        {
            Console.WriteLine($"batch    '{Path.GetFileName(exported)}' could not be renamed to '{folder}': {model.Editor.Status}");
            return null;
        }

        var manifest = PGAssetTool.Core.Pack.Workspace.Read(directory);

        // Packing keeps only what differs from the export, so an untouched workspace packs nothing.
        var texture = manifest.Operations.FirstOrDefault(o => o.Source.EndsWith(".png"));
        if (texture is null) { Console.WriteLine($"batch    '{folder}' has no texture to edit"); return null; }

        var path = Path.Combine(directory, texture.Source);
        File.WriteAllBytes(path, [.. File.ReadAllBytes(path), .. new byte[16]]);

        Console.WriteLine($"batch    a second workspace at {Path.GetFileName(directory)}, "
            + $"editing {texture.Source}");
        return directory;
    }

    /// The manager's tiles come in the order they were installed, or in the order the items are.
    ///
    /// One answers "what did I just do" and the other "what have I got for this weapon", and both
    /// get asked. Sorted where the tiles are chosen rather than where the rows are read, so changing
    /// it rearranges what is already in hand.
    private static string? TilesArrangeTwoWays(MainViewModel model)
    {
        if (model.Manager.Shown.Count < 2) return null;

        model.Manager.Order = ModOrder.Installed;
        var byTime = model.Manager.Shown.Select(r => r.Mod.Id).ToList();

        model.Manager.Order = ModOrder.Item;
        var byItem = model.Manager.Shown.Select(r => r.Mod.Id).ToList();

        Console.WriteLine($"manager  {byItem.Count} tiles, by item: "
            + string.Join(", ", model.Manager.Shown.Select(r => r.Mod.Subject?.Number.ToString() ?? "-")));

        if (byTime.Count != byItem.Count || byTime.ToHashSet().Count != byItem.ToHashSet().Count)
            return "rearranging the tiles changed which of them there are";

        var numbers = model.Manager.Shown.Select(r => r.Mod.Subject?.Number ?? int.MaxValue).ToList();
        for (var i = 1; i < numbers.Count; i++)
            if (numbers[i] < numbers[i - 1])
                return $"by item, #{numbers[i]} is listed after #{numbers[i - 1]}";

        model.Manager.Order = ModOrder.Installed;
        return null;
    }

    /// Turning one mod on turns off whatever else writes the same assets, and says which.
    ///
    /// Two of those do not both take effect: the reconcile applies them in the order they were
    /// installed and the last one wins without a word, so which of two skins for a weapon was
    /// actually running was a question the manager could not answer.
    private static string? OneModStandsAsideForAnother(MainViewModel model, string workspace)
    {
        var store = new PGAssetTool.Core.Mods.ModStore(model.Game!);
        var mine = store.Read().FirstOrDefault(m => m.Id == PackIdentity);
        if (mine is null) return $"'{PackIdentity}' is not installed to stand aside";

        // A second pack out of the same workspace: the same operations under another name, which
        // is the shape two skins for one weapon have.
        var manifest = Core.Pack.Workspace.Read(workspace);
        var rival = Path.Combine(workspace, "rival.pgmod");

        Core.Pack.Workspace.Save(workspace, manifest with { Id = PackIdentity + "-rival", Name = "Self test rival" });
        try
        {
            Core.Pack.PackBuilder.Build(workspace, rival);
        }
        finally
        {
            Core.Pack.Workspace.Save(workspace, manifest);
        }

        var applier = new PGAssetTool.Core.Mods.ModApplier(model.Game!, store);

        try
        {
            // Installing is the usual way a mod comes to be on, and it used to be the one route
            // that claimed nothing: two skins for one weapon arrived both enabled, both writing the
            // same texture, and only one of them was in the game.
            applier.Install(rival, "self-test");

            Console.WriteLine("exclude  installing the rival stood down: "
                + (applier.Displaced.Count == 0 ? "nothing" : string.Join(", ", applier.Displaced)));

            var after = store.Read();
            if (after.FirstOrDefault(m => m.Id == PackIdentity) is not { Enabled: false })
                return "a mod writing the same assets was left on beside the one just installed";
            if (applier.Displaced.Count == 0)
                return "it was turned off without saying so";

            // And a mod that shares nothing is left alone: the point is the assets, not the weapon.
            if (after.FirstOrDefault(m => m.Id == PackIdentityB) is { Enabled: false })
                return "a mod writing different assets was turned off as well";

            // The other way round, through the toggle rather than the install.
            applier.SetEnabled(PackIdentity, true);

            Console.WriteLine("exclude  turning the first one back on stood down: "
                + (applier.Displaced.Count == 0 ? "nothing" : string.Join(", ", applier.Displaced)));

            if (store.Read().FirstOrDefault(m => m.Id == PackIdentity + "-rival") is not { Enabled: false })
                return "turning a mod on left the rival that writes the same assets on as well";

            // And both at once, which is what select-everything and turn on does. The rivals were
            // held against what was already installed and not against each other, so one gesture
            // turned on thirty-nine mods and every pair of them that could not stand together.
            //
            // The later of the two keeps its claim, because a reconcile applies them in the order
            // they were installed and the last one is the one actually in the game.
            applier.SetEnabled([PackIdentity, PackIdentity + "-rival"], true);

            Console.WriteLine("exclude  turning both on at once stood down: "
                + (applier.Displaced.Count == 0 ? "nothing" : string.Join(", ", applier.Displaced)));

            var both = store.Read();
            if (both.FirstOrDefault(m => m.Id == PackIdentity) is not { Enabled: false })
                return "turning both on at once left the earlier of two rivals on";
            if (both.FirstOrDefault(m => m.Id == PackIdentity + "-rival") is not { Enabled: true })
                return "turning both on at once left the later of two rivals off";
            if (both.FirstOrDefault(m => m.Id == PackIdentityB) is not { Enabled: true })
                return "turning both on at once turned off a mod writing different assets";
        }
        finally
        {
            applier.Remove(PackIdentity + "-rival");
            applier.SetEnabled(PackIdentity, true);
            try { File.Delete(rival); } catch (IOException) { }
        }

        return null;
    }

    /// An asset that is not in the bundle a pack names is followed into the one it is in.
    ///
    /// What a game update does to a pack: against 24.3.7, nineteen of the twenty operations kept
    /// here that did not apply named an asset the game had filed in another bundle under the same
    /// path id. Imitated here by sending the pack's textures somewhere they are not, and asking that
    /// they land where they are, that the mod is not recorded against the wrong bundle, and that the
    /// next reconcile goes straight there rather than looking all over again.
    ///
    /// The wrong bundle is one the self-test's own mods already write, or failing that one this
    /// installation does not have: either way nothing of the user's is backed up or written for it.
    private static string? AnAssetTheGameMovedIsFollowed(MainViewModel model, string workspace)
    {
        const string id = PackIdentity + "-moved";
        var store = new PGAssetTool.Core.Mods.ModStore(model.Game!);
        var manifest = Core.Pack.Workspace.Read(workspace);

        if (manifest.Subject is not { IsKnown: true })
            return "the self-test's workspace records no item, so there is nothing to look for a moved asset by";

        var pack = Path.Combine(workspace, "moved.pgmod");

        // Only what packing keeps: a file nobody changed is not an operation, and sending one of
        // those astray tests nothing. So the pack is built as it is first, to see which survive.
        Core.Pack.PackBuilder.Build(workspace, pack);
        var textures = Core.Pack.PackBuilder.ReadManifest(pack).Operations
            .Where(o => o.Op == Core.Pack.PackOperations.ReplaceTexture)
            .Select(o => Core.Mods.Relocation.Key(o.Target))
            .ToHashSet(StringComparer.Ordinal);
        if (textures.Count == 0) return "the self-test's pack replaces no texture to send astray";

        var sending = manifest.Operations
            .Where(o => o.Op == Core.Pack.PackOperations.ReplaceTexture && textures.Contains(Core.Mods.Relocation.Key(o.Target)))
            .ToList();
        var homes = sending.Select(o => o.Target.Container).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Somewhere nothing else in this pack goes, so that "nothing of it went there" can be asked:
        // a bundle the pack's other operations do write to is rightly counted as written to.
        var named = manifest.Operations.Select(o => o.Target.Container).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wrong = store.Read()
            .Where(m => m.Id is PackIdentity or PackIdentityB)
            .SelectMany(m => m.TouchedBundles.Keys)
            .FirstOrDefault(b => !named.Contains(b) && !homes.Contains(b))
            ?? "pgassettool-selftest-no-such-bundle";

        Core.Pack.Workspace.Save(workspace, manifest with
        {
            Id = id,
            Name = "Self test moved",
            Operations = [.. manifest.Operations.Select(o => sending.Contains(o)
                ? o with { Target = o.Target with { Container = wrong } }
                : o)],
        });
        try
        {
            Core.Pack.PackBuilder.Build(workspace, pack);
        }
        finally
        {
            Core.Pack.Workspace.Save(workspace, manifest);
        }

        var astray = Core.Pack.PackBuilder.ReadManifest(pack).Operations
            .Count(o => o.Op == Core.Pack.PackOperations.ReplaceTexture
                && string.Equals(o.Target.Container, wrong, StringComparison.OrdinalIgnoreCase));
        if (astray != sending.Count) return $"{sending.Count} texture(s) sent to '{wrong}' and {astray} arrived in the pack";
        var home = homes.First();

        var applier = new PGAssetTool.Core.Mods.ModApplier(model.Game!, store);

        try
        {
            var result = applier.Install(pack, "self-test");
            var mine = result.Applied.Where(a => a.Mod == id).ToList();

            Console.WriteLine($"moved    {astray} texture(s) sent to '{wrong}', {result.Moved.Count} followed: "
                + string.Join("; ", result.Moved));

            var trouble = result.Failed.Where(f => f.StartsWith(id + ":", StringComparison.Ordinal)).ToList();
            if (trouble.Count > 0) return "a moved asset was not followed: " + string.Join("; ", trouble);
            if (result.Moved.Count != astray) return $"{astray} sent astray and {result.Moved.Count} said to be followed";
            if (mine.Count(a => string.Equals(a.Target.Container, home, StringComparison.OrdinalIgnoreCase)) < astray)
                return $"what was sent astray did not land in '{home}'";

            var ledger = store.Read().First(m => m.Id == id);
            if (ledger.Moved is not { Count: > 0 } moved || moved.Values.Any(r => r.Bundle is null))
                return "where the asset was found was not written down";
            if (ledger.TouchedBundles.ContainsKey(wrong))
                return $"the mod is recorded as having written to '{wrong}', where nothing of it went";

            // Turned off and on: the second reconcile knows where to go and loses nothing on the way.
            applier.SetEnabled(id, false);
            var again = applier.SetEnabled(id, true);

            Console.WriteLine($"moved    turned off and on: {again.Moved.Count} looked for again, "
                + $"{again.Failed.Count(f => f.StartsWith(id + ":", StringComparison.Ordinal))} failed");

            if (again.Moved.Count > 0) return "the next reconcile looked for the moved asset all over again";
            if (again.Failed.Any(f => f.StartsWith(id + ":", StringComparison.Ordinal)))
                return "the next reconcile did not find its way to the moved asset: " + string.Join("; ", again.Failed);
        }
        finally
        {
            applier.Remove(id);
            applier.SetEnabled(PackIdentity, true);
            try { File.Delete(pack); } catch (IOException) { }
        }

        return null;
    }

    /// Normals kept as half floats come out as normals.
    ///
    /// Unity pads a half-float normal to four components. It was kept four wide while the preview
    /// read three a vertex, so every vertex after the first took its neighbours' components, and #14
    /// Battle Shovel — with 54 other main meshes kept the same way — shaded in alternating light and
    /// dark triangles. Nothing about that looks like a decoding fault from the outside: the shovel
    /// was put down as a model built with its normals one way and its triangles the other.
    /// A normal read right is unit length and faces the way its triangle is wound.
    private static string? HalfFloatNormalsAreReadAsNormals(MainViewModel model)
    {
        if (!Select(model, 14)) return "#14 never resolved";
        if (model.Detail?.SelectedNode is not { } row) return "#14 selected nothing to show";
        if (!Arrived(model, row)) return $"'{row.Label}' never appeared";
        if (model.Preview.Mesh is not { } mesh) return $"'{row.Label}' is not a model";

        var width = mesh.Dimensions.GetValueOrDefault(Core.Export.Meshes.VertexAttribute.Normal);
        if (mesh.Get(Core.Export.Meshes.VertexAttribute.Normal) is not { } normals)
            return $"'{row.Label}' came up with no normals";
        if (mesh.Get(Core.Export.Meshes.VertexAttribute.Position) is not { } positions)
            return $"'{row.Label}' came up with no positions";
        if (width != 3 || normals.Length != mesh.VertexCount * 3)
            return $"'{row.Label}' has its normals {width} wide, {normals.Length} values for {mesh.VertexCount} vertices";

        var unit = 0;
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var length = MathF.Sqrt(normals[v * 3] * normals[v * 3] + normals[v * 3 + 1] * normals[v * 3 + 1]
                + normals[v * 3 + 2] * normals[v * 3 + 2]);
            if (MathF.Abs(length - 1) < 0.05f) unit++;
        }

        var (agree, all) = (0, 0);
        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            var (a, b, c) = (mesh.Indices[i], mesh.Indices[i + 1], mesh.Indices[i + 2]);
            float P(int vertex, int axis) => positions[vertex * 3 + axis];
            float N(int axis) => normals[a * 3 + axis] + normals[b * 3 + axis] + normals[c * 3 + axis];

            var (ux, uy, uz) = (P(b, 0) - P(a, 0), P(b, 1) - P(a, 1), P(b, 2) - P(a, 2));
            var (vx, vy, vz) = (P(c, 0) - P(a, 0), P(c, 1) - P(a, 1), P(c, 2) - P(a, 2));
            var (fx, fy, fz) = (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);

            all++;
            if (fx * N(0) + fy * N(1) + fz * N(2) >= 0) agree++;
        }

        Console.WriteLine($"normals  '{row.Label}': {unit} of {mesh.VertexCount} unit length, "
            + $"{agree} of {all} triangles facing the way they are wound");

        if (unit < mesh.VertexCount * 0.95) return $"only {unit} of '{row.Label}'s {mesh.VertexCount} normals are unit length";
        if (agree < all * 0.9) return $"only {agree} of '{row.Label}'s {all} triangles face the way they are wound";

        return null;
    }

    /// A model is shown put together the way its own bones put it together.
    ///
    /// The preview drew the mesh as it is written, which is the assembled model only when every
    /// bone carries the same skinning matrix at rest. 518 of the game's 1517 weapons break that:
    /// #1496's barrel, its skull plate and its wooden mount are each written where their own bone
    /// would put them and nowhere near each other, so the preview showed the pieces scattered — and
    /// tiny, because the framing is worked out from a bounding box the far-flung pieces are in.
    ///
    /// Checked by the box rather than by the picture: assembling a model that needs it pulls the
    /// pieces together, so the radius the preview frames by comes down sharply. #16 moves by
    /// nothing at all through the same code, which is the other half of the check.
    private static string? ModelsAreShownAssembled(MainViewModel model)
    {
        foreach (var (number, apart) in new[] { (1496, true), (16, false) })
        {
            if (!Select(model, number)) return $"#{number} never resolved";
            if (model.Detail?.SelectedNode is not { } row) return $"#{number} selected nothing to show";
            if (!Arrived(model, row)) return $"'{row.Label}' never appeared";
            if (model.Preview.Rest is not { } shown) return $"'{row.Label}' is not a model";

            // The same mesh as the tool reads it, with nothing done to it.
            if (model.Game is not { } game) return "the game is not open";
            using var bundles = new Core.Assets.BundleSet(game);
            var file = bundles.Open(row.Bundle);
            if (file.file.GetAssetInfo(row.PathId) is not { } info) return $"'{row.Label}' is not in {row.Bundle}";
            if (bundles.Context.Deserialize(file, info) is not { } field) return $"'{row.Label}' would not read";
            if (Core.Preview.AssetPreview.Mesh(field, bundles, row.Bundle) is not { } written)
                return $"'{row.Label}' would not unpack";

            var wasRadius = Core.Export.Meshes.MeshBounds.Of(written).Radius;
            var nowRadius = Core.Export.Meshes.MeshBounds.Of(shown).Radius;
            var shrunk = wasRadius > 0 ? 1 - nowRadius / wasRadius : 0;

            Console.WriteLine($"whole    #{number} '{row.Label}': radius {wasRadius:N2} as written, "
                + $"{nowRadius:N2} as shown ({shrunk:P0} smaller)");

            if (apart && shrunk < 0.25f)
                return $"#{number} is written in pieces and was shown {shrunk:P0} smaller, not gathered up";
            if (!apart && MathF.Abs(wasRadius - nowRadius) > 0.001f)
                return $"#{number} is already whole and was changed: {wasRadius:N3} -> {nowRadius:N3}";
        }

        return null;
    }

    /// A skin's own model moves, whether or not it carries clips of its own and whether or not it is
    /// skinned.
    ///
    /// Eleven of 130 skin models carry no clips and stood still. Seven of them are built on the
    /// weapon's rig under a name of their own, and it is the weapon's clips that play on them —
    /// #1496's Bonebreaker drives all eight of its bones that way. And one, #8's Old Combat Knife, is
    /// a plain mesh its own clips swing by name, which could not be posed because it has no bones.
    private static string? SkinModelsMoveWithoutClipsOrBonesOfTheirOwn(MainViewModel model)
    {
        foreach (var (number, id) in new[] { (1496, "Weapon1936_bonebreaker"), (8, "Weapon9_old_combat_knife") })
        {
            if (!Select(model, number)) return $"#{number} never resolved";

            var skins = model.Detail?.Roots.FirstOrDefault(r => r.Label == "Skins");
            if (skins?.Children.FirstOrDefault(s =>
                    string.Equals(s.Skin?.Record.Id, id, StringComparison.OrdinalIgnoreCase)) is not { } skin)
                return $"#{number} has no skin '{id}' to open";
            if (skin.Unread is null) return $"'{id}' should bring a model of its own and does not";

            // Waiting on the row rather than on the model being named: the model is named before its
            // clips are read, and the rows go in after that.
            TreeNode? Row() => skin.Body is { } named
                ? skin.Children.SelectMany(c => c.Children).FirstOrDefault(c =>
                    c.Class == AssetsTools.NET.Extra.AssetClassID.Mesh && c.PathId == named.PathId)
                : null;

            skin.IsExpanded = true;
            WaitWhile(() => Row() is null, 60_000);
            if (skin.Body is not { } body) return $"opening '{id}' found no model in it";

            var row = Row();
            if (row is null) return $"'{id}' names '{body.Name}' as its model and no row stands for it";

            model.Preview.Clear();
            model.Detail!.SelectedNode = row;
            if (!Arrived(model, row)) return $"'{row.Label}' never appeared";

            var preview = model.Preview;
            if (preview.Mesh is null) return $"'{row.Label}' is not a model";

            Console.WriteLine($"skinanim #{number} '{row.Label}': {preview.Clips.Count} clip(s)"
                + (preview.Clips.Count == 0 ? "" : $" — {string.Join(", ", preview.Clips.Select(c => c.Name))}"));
            if (preview.Clips.Count == 0) return $"#{number}'s '{id}' stands still: nothing plays on '{row.Label}'";

            var rest = preview.Rest ?? preview.Mesh;
            var furthest = 0f;
            foreach (var clip in preview.Clips.ToList())
            {
                preview.Clip = clip;
                preview.Stop();
                for (var step = 1; step <= 12; step++)
                {
                    preview.Time = clip.Motion.Length * step / 12.0;
                    if (Apart(rest, preview.Mesh) is { } moved) furthest = Math.Max(furthest, moved);
                }
            }
            preview.Clip = null;

            Console.WriteLine($"skinanim #{number} '{row.Label}': furthest a vertex moves is {furthest:0.###}");
            if (furthest < 0.001f) return $"#{number}'s '{id}' has clips and none of them moves '{row.Label}'";
        }

        return null;
    }

    /// Whether the weapon a selection lands on can actually be decoded.
    ///
    /// Some meshes keep their vertices in the bundle's .resS rather than in the object, exactly as
    /// most textures keep their pixels — and those were refused outright, so the weapon had no
    /// model anywhere: no preview, and an export that wrote a field dump where a .glb belonged.
    /// #14 Battle Shovel is one. Nothing in the object says so from the outside, which is why this
    /// sweeps rather than asks.
    private static string? EveryWeaponHasAModelToShow(MainViewModel model)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (looked, drawn, empty) = (0, 0, new List<string>());

        foreach (var item in model.Weapons.Where((_, i) => i % 37 == 0))
        {
            model.Selected = item;
            if (!WaitWhile(() => model.Detail?.Tree.Record.GameNumber != item.Record.GameNumber, 60_000))
                return $"#{item.Record.GameNumber} never resolved";

            if (model.Detail!.SelectedNode is not { } row) continue;
            looked++;

            if (!Arrived(model, row)) { empty.Add($"#{item.Record.GameNumber} {row.Label}"); continue; }
            if (model.Preview.Mesh is null) empty.Add($"#{item.Record.GameNumber} {row.Label}");
            else drawn++;
        }

        Console.WriteLine($"model    {drawn} of {looked} swept weapons came up with a model "
            + $"in {clock.ElapsedMilliseconds}ms");

        if (looked < 15) return $"only {looked} weapons offered a model row at all";
        return empty.Count == 0 ? null : $"no model could be read for {string.Join(", ", empty)}";
    }

    /// Whether selecting a weapon lands on the weapon rather than on the player's hands.
    ///
    /// Swept across the catalogue rather than asked of one, because what makes this hard is how
    /// little the weapons have in common: the early ones are not named after their own meshes
    /// (#30 Guerilla Rifle is SVD_2_mesh), some keep their art in a shared bundle rather than
    /// their own, and a few are called one thing and painted with another. A rule can be made to
    /// fit any handful of them and still be wrong about the rest.
    ///
    /// The name of the arms is the oracle here and deliberately not the answer: what is being
    /// checked is that the tool arrives at the same place without being told it. It is allowed one
    /// last-resort use of that name for the weapons nothing else separates, so a sweep that came
    /// back perfect would be checking nothing — hence the count, which says how much of the answer
    /// is coming from the data.
    private static string? WhichMeshIsTheWeapon(MainViewModel model)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (looked, arms, byData, missing) = (0, 0, 0, new List<string>());
        TreeNode? last = null;

        // Every twenty-third, which is a spread across the whole catalogue rather than a run of
        // neighbours — weapons near each other in the list were made at the same time and share
        // whatever convention was in fashion then.
        foreach (var item in model.Weapons.Where((_, i) => i % 23 == 0))
        {
            model.Selected = item;
            if (!WaitWhile(() => model.Detail?.Tree.Record.GameNumber != item.Record.GameNumber, 60_000))
                return $"#{item.Record.GameNumber} never resolved";

            var tree = model.Detail!.Tree;
            var meshes = tree.PrefabAssets.Count(a => a.Class == AssetsTools.NET.Extra.AssetClassID.Mesh);
            if (meshes == 0) continue;

            looked++;
            if (tree.MainMesh is not { } body) { missing.Add($"#{item.Record.GameNumber}"); continue; }

            if (string.Equals(body.Name, PGAssetTool.Core.Weapons.WeaponResolver.Arms, StringComparison.OrdinalIgnoreCase))
                arms++;
            else if (meshes > 1 && tree.PrefabAssets.Any(
                         a => string.Equals(a.Name, PGAssetTool.Core.Weapons.WeaponResolver.Arms, StringComparison.OrdinalIgnoreCase)))
                byData++;

            last = model.Detail.SelectedNode;
        }

        // Each selection puts a decode on the queue, and the queue is served in order — so waiting
        // for the last one is waiting for all of them. Left running, they land under whatever the
        // next check is doing and it reads them instead of its own.
        if (last is not null) Arrived(model, last);

        Console.WriteLine($"model    {looked} weapons swept in {clock.ElapsedMilliseconds}ms: "
            + $"{byData} picked out from beside the arms, {arms} landed on them");

        if (looked < 20) return $"only {looked} weapons had a mesh to choose between";
        if (missing.Count > 0) return $"no model was worked out for {string.Join(", ", missing)}";
        if (arms > 0) return $"{arms} of {looked} weapons come up showing the player's hands";
        if (byData < looked / 2) return $"only {byData} of {looked} were told apart from their arms";

        return null;
    }

    /// Waits for the preview to be showing this row, rather than merely showing something.
    ///
    /// A selection is read and decoded on a queue, so at any moment the pane may still be holding
    /// what was selected before it. Waiting for "anything at all" was satisfied by the previous
    /// row, and every check after it read the wrong asset's answers — which only started happening
    /// when the tool began showing a weapon's model the moment the weapon is picked, putting one
    /// more of those in flight. The caption is the one thing that says which row landed.
    /// Waits until the editor's comparison is showing the file it was asked for.
    ///
    /// Not "until something is loaded": the panes hold the last file until the new one lands, so a
    /// test that waits for a mesh to be there is answered instantly by the mesh already there, and
    /// everything after it runs against the wrong model. That has now cost two afternoons in two
    /// different places — the browse tree was the first — so both wait on the caption, which names
    /// what is actually in front of you.
    /// An animation in a workspace plays on the model it moves, and can be swapped for another
    /// weapon's and put back.
    ///
    /// Through the editor's own controls: the item typed into the box, its clips listed, Use
    /// pressed. What comes out has to be the file changed — marked edited, saying where its motion
    /// came from — and both halves of the comparison playing a clip, the game's and the new one.
    /// Put back, it has to be the file the extract wrote to the byte, or a pack would carry an
    /// animation nobody changed.
    private static string? AnimationsCanBeTakenFromAnotherWeapon(MainViewModel model)
    {
        var editor = model.Editor;
        var reload = editor.Files.FirstOrDefault(f =>
            f.Target.Class == nameof(AssetClassID.AnimationClip) && f.Target.Name == "Reload");
        if (reload is null)
            return $"the workspace lists no Reload animation among {editor.Files.Count(f => f.Name.EndsWith(".anim"))} .anim files";

        var before = File.ReadAllBytes(reload.FullPath);
        editor.SelectedFile = reload;
        if (!Shows(model, reload)) return "the Reload animation never showed";
        Console.WriteLine($"anim     {editor.AnimationSays}");
        Console.WriteLine($"anim     game side: {editor.Original.Caption}, {editor.Original.Clips.Count} clip(s)");
        if (!editor.IsAnimation) return "an animation was selected and the editor did not offer to change it";
        if (!editor.Original.HasClips || !editor.Edited.HasClips)
            return $"the Reload animation is not played on the model: {editor.Original.Nothing ?? editor.Edited.Nothing ?? "no clips"}";

        // #15 Pig Hammer: its clips are among the ones the game keeps compressed.
        editor.FromQuery = "15";
        if (!WaitWhile(() => !editor.FromClips.Contains("Reload"), 60_000))
            return $"typing 15 listed no Reload to take: {editor.FromFound}";
        editor.FromClip = "Reload";
        Settle(editor.UseAnimationCommand.ExecuteAsync(null), "putting in #15's Reload");
        var swapped = editor.Files.FirstOrDefault(f => f.RelativePath == reload.RelativePath);
        Console.WriteLine($"anim     {editor.Status}");
        if (swapped is not { Edited: true }) return $"taking #15's Reload left the file unedited: {editor.Status}";
        if (!editor.Status.Contains("fitted", StringComparison.Ordinal))
            return $"after taking #15's Reload the window says '{editor.Status}' rather than what was done";
        if (!editor.AnimationSays.Contains("#15", StringComparison.Ordinal))
            return $"the editor does not say where the animation came from: '{editor.AnimationSays}'";

        editor.SelectedFile = swapped;
        if (!Shows(model, swapped)) return "the swapped animation never showed";
        var lengths = (Game: editor.Original.Clip?.Motion.Length, Edited: editor.Edited.Clip?.Motion.Length);
        Console.WriteLine($"anim     playing {lengths.Game:0.00}s in the game against {lengths.Edited:0.00}s as edited");
        if (editor.Edited.Clip is null) return "the swapped animation does not play on the model";
        if (lengths.Game == lengths.Edited) return "the edited side plays the same animation as the game";

        Settle(editor.RestoreAnimationCommand.ExecuteAsync(null), "putting the Reload back");
        if (!before.AsSpan().SequenceEqual(File.ReadAllBytes(reload.FullPath)))
            return "putting the Reload back did not write the file the extract wrote";
        if (editor.Files.FirstOrDefault(f => f.RelativePath == reload.RelativePath) is not { Edited: false })
            return "the Reload put back is still marked edited";

        return null;
    }

    /// A workspace's animations go out as one glTF to edit in Blender, and an animation of one comes
    /// back into a slot.
    ///
    /// Blender itself is not here, so what comes back is the file as it went out: read into the
    /// slot it was written from, it is the animation that slot already plays and has to leave the
    /// file exactly as it was — anything else marks an animation nobody touched as edited. Read
    /// with another of its animations chosen, it has to change the slot, and say from where.
    private static string? AnimationsGoOutAsAGlbAndComeBack(MainViewModel model)
    {
        var editor = model.Editor;
        var reload = editor.Files.FirstOrDefault(f =>
            f.Target.Class == nameof(AssetClassID.AnimationClip) && f.Target.Name == "Reload");
        if (reload is null) return "the workspace lists no Reload animation to write out";
        editor.SelectedFile = reload;
        if (!Shows(model, reload)) return "the Reload animation never showed";

        string? shown = null;
        editor.ShowFile = path => shown = path;
        Settle(editor.ExportAnimationsCommand.ExecuteAsync(null), "writing the animations to a glb");
        Console.WriteLine($"glb      {editor.Status}");
        if (shown is null || !File.Exists(shown)) return $"writing the animations to edit wrote nothing: {editor.Status}";
        var takes = Core.Animation.AnimationGlb.Takes(shown);
        if (takes.Count != editor.Files.Count(f => f.Target.Class == nameof(AssetClassID.AnimationClip)))
            return $"the glb holds {takes.Count} animations for {editor.Files.Count(f => f.Name.EndsWith(".anim"))} in the workspace";

        var before = File.ReadAllBytes(reload.FullPath);
        Settle(editor.UseGlbFile(shown, take: null), "reading the Reload back");
        Console.WriteLine($"glb      {editor.Status}");
        if (!before.AsSpan().SequenceEqual(File.ReadAllBytes(reload.FullPath)))
            return "reading back the Reload that went out unedited changed the file";
        if (!editor.Status.Contains("unchanged", StringComparison.Ordinal))
            return $"reading back an unedited Reload says '{editor.Status}'";
        if (!editor.HasGlbTakes || editor.GlbTakes.Count != takes.Count)
            return $"the glb's {takes.Count} animations are not offered to choose from ({editor.GlbTakes.Count})";

        Settle(editor.UseGlbFile(shown, take: "Walk"), "putting the glb's Walk in the Reload");
        Console.WriteLine($"glb      {editor.Status}");
        if (editor.Files.FirstOrDefault(f => f.RelativePath == reload.RelativePath) is not { Edited: true })
            return $"the glb's Walk left the Reload unedited: {editor.Status}";
        if (!editor.AnimationSays.Contains($"{Core.Animation.AnimationGlb.FileName}: Walk", StringComparison.Ordinal))
            return $"the editor does not say the Reload came from the glb's Walk: '{editor.AnimationSays}'";
        if (editor.GlbTake != "Walk") return $"the animation chosen shows as '{editor.GlbTake}' rather than Walk";

        Settle(editor.RestoreAnimationCommand.ExecuteAsync(null), "putting the Reload back after the glb");
        File.Delete(shown);
        return before.AsSpan().SequenceEqual(File.ReadAllBytes(reload.FullPath))
            ? null
            : "putting the Reload back after the glb did not write the file the extract wrote";
    }

    private static bool Shows(MainViewModel model, Core.Pack.WorkspaceFile file)
        => WaitWhile(
            () => !Showing(model.Editor.Edited, file) || !Showing(model.Editor.Original, file),
            60_000);

    /// Whether one pane has finished with this file, whether or not it could show it.
    ///
    /// Both panes are emptied the moment a file is selected and both say so in the same words, so
    /// that message is the one state that means "not yet" — anything else is an answer about the
    /// file asked for.
    private static bool Showing(PreviewViewModel pane, Core.Pack.WorkspaceFile file)
        => pane.Caption.StartsWith(file.Name, StringComparison.Ordinal)
            || pane.Nothing is { } why && why != "Nothing selected.";

    private static bool Arrived(MainViewModel model, TreeNode node)
        => WaitWhile(
            () => !model.Preview.Caption.StartsWith($"{node.Label}   @ ", StringComparison.Ordinal),
            60_000);

    private static bool Select(MainViewModel model, int number)
    {
        model.Selected = model.Weapons.First(w => w.Record.GameNumber == number);
        return WaitWhile(() => model.Detail?.Tree.Record.GameNumber != number, 60_000);
    }

    private static int CountRows(IEnumerable<TreeNode> nodes)
        => nodes.Sum(n => 1 + CountRows(n.Children));

    private static int Fail(string why)
    {
        Console.Error.WriteLine($"SELF TEST FAILED: {why}");
        return 1;
    }
}
