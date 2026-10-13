using System.Diagnostics;
using System.Text;
using AssetsTools.NET.Extra;
using PGAssetTool.Cli;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;
using PGAssetTool.Core.Export;
using PGAssetTool.Core.Pack;
using PGAssetTool.Core.Game;
using PGAssetTool.Core.Mods;
using PGAssetTool.Core.Settings;
using PGAssetTool.Core.Weapons;

// Names come from twelve localization bundles; the Windows console defaults to a legacy code page
// that mangles all of them.
try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }

CommandLine line;
try
{
    line = CommandLine.Parse(args);
}
catch (CommandLineException wrong)
{
    Console.Error.WriteLine($"{wrong.Message} Try --help.");
    return 2;
}

// Every command runs inside one handler, so what reaches the person at the prompt is a sentence
// rather than a stack trace. Trouble with the world — a file that is not there, a pack that will not
// read — is said as its message. Anything else is a fault in the tool, and is written down where
// somebody can send it, as the window does with its own.
try
{
    return Run(line);
}
catch (CommandLineException wrong)
{
    Console.Error.WriteLine($"{wrong.Message} Try --help.");
    return 2;
}
catch (Exception e) when (CommandLine.IsExpected(e))
{
    ErrorLog.Record(e, $"running '{line.Command}' from the command line");
    Console.Error.WriteLine(e.Message);
    return 1;
}
catch (Exception e)
{
    var report = ErrorLog.Crash(e, $"pgassettool {line.Command}");
    Console.Error.WriteLine($"Something went wrong that the tool did not expect: {e.GetType().Name}: {e.Message}");
    if (report is not null) Console.Error.WriteLine($"The details are in {report}. Please send it with a report.");
    return 1;
}

static int Run(CommandLine line)
{
    if (line.Has("version"))
    {
        Console.WriteLine($"pgassettool {ForumPost.ToolVersion}");
        return 0;
    }

    if (line.WantsHelp)
    {
        Console.WriteLine($"""
            pgassettool {ForumPost.ToolVersion}
            pgassettool <command> [options]

              info                 Show the detected installation and version.
              weapons [<filter>]   List weapons, optionally filtered by name, slug, tag or prefab.
              items [<kind>]       List the kinds of item — {string.Join(", ", ItemKinds.All.Select(k => k.Pack))} —
                                   or the whole of one of them.
              show <item>          Show one item and everything it references. A weapon takes the
                                   in-game number (819), a prefab name (Weapon1257) or a slug; anything
                                   else takes the id `items` lists. Note that a weapon's in-game number
                                   and its prefab number are different sequences.

              extract <item>       Write out what can be changed of an item: images as PNG, audio as
                                   WAV or Ogg, meshes as glTF. With --workspace, also writes a
                                   pgmod.json naming every replaceable file.
              pack [<directory>]   Build a .pgmod from a workspace. Only files edited since the
                                   extract are included.
              animation <directory> [<clip>]
                                   List a workspace's animations, or change one: --from <item> puts
                                   in that item's animation of the same name (or --clip <name>),
                                   fitted to this item's bones; --glb <file> puts in one from a glTF
                                   made in Blender or anywhere else; --reset puts the original back.
                                   --export writes them all with the item's models into one .glb
                                   (animations/animations.glb, or --out) to edit in Blender.

              apply <pack>...      Install one or more .pgmod files into the game.
              verify               Check every bundle against the hash the game recorded for it.
              consolidate          Report what emptying the downloaded cache would change. --apply
                                   removes only the copies that change nothing.
              mods                 List installed mods.
              enable <id>...       Turn mods back on.
              disable <id>...      Turn mods off without uninstalling them.
              remove <id>...       Uninstall mods.
                                   A mod can be named by the start of its id, if only one begins so.

            Options:
              --game <directory>   Use this installation instead of the detected one.
              --language <bundle>  Localization bundle to read names from (default l_en-gb).
              --out <directory>    Where extract writes (default ./workspace), or where pack puts the
                                   .pgmod (a directory, or the file's own path).
              --author <name>      Recorded in the manifest by extract --workspace.
              --force              Let apply go ahead anyway: back up a bundle already modified, or
                                   install a signed pack altered after it was built.
              --fast               Squeeze rebuilt bundles less: that part is about four times quicker
                                   and the bundles come out about a sixth larger. The game reads both
                                   at the same speed.
              --rebuild            Rebuild every bundle, including the ones already holding what they
                                   should. Those are normally left where they are.
              --low-memory         Rebuild one bundle at a time rather than four: about a third slower,
                                   about a third less memory at the peak.
              --read-memory <MB>   How much of the game show and extract may keep unpacked in memory
                                   (default 1024). Reading is several times quicker for it; 0 reads
                                   everything from disk.
              --opaque             Write textures with no alpha channel. Most of them keep emission
                                   rather than transparency there, and an editor opens those as almost
                                   invisible. An image brought back without an alpha channel keeps the
                                   original one.
              --whole              Write the whole of every texture. By default the part no model
                                   samples is made transparent, so what is left is what an author can
                                   actually see on the item.
              --skin <id or name>  Write out one of a weapon's skins instead of the weapon as it comes:
                                   its own materials and textures, and the model it brings if it brings
                                   one. `show` lists what a weapon has. Hats, capes, masks and boots
                                   draw their skins with their own textures, so they need none.
              --all-skins          With extract: write the item as it comes and every one of its skins,
                                   each into a workspace of its own.
              --protect            Sign the built pack with a key kept beside the tool, and keep it from
                                   opening as a zip. Whoever alters one afterwards shows up as having
                                   done so. It stops a casual look inside and nothing more.
              --from <item>        For animation: the item whose animation to use.
              --clip <name>        For animation: which of its animations, when not the same name.
              --reset              For animation: put the item's own animation back.
              --glb <file>         For animation: the .glb whose animation to use.
              --anim <file>        For animation: a .anim to use, such as another workspace's, fitted
                                   to this item's bones.
              --take <name>        For animation: which of the .glb's animations, when it has several
                                   and none is called like the one being replaced.
              --export             For animation: write the workspace's animations into a .glb.
              --version            Say which version this is.

            Options may come before or after the command, as --name value or --name=value.
            """);
        return 0;
    }

    if (line.Command == "pack") return Pack(line);

    // Before the game is looked for: a typo is a typo whether or not there is a game to find.
    string[] commands =
        ["info", "consolidate", "verify", "mods", "apply", "enable", "disable", "remove", "items", "weapons", "show", "extract",
         "animation"];
    if (!commands.Contains(line.Command))
        throw new CommandLineException($"Unknown command '{line.Command}'.");

    var dir = line.Option("game");
    var game = dir is null ? GameInstallation.OpenDetected() : GameInstallation.Open(dir);

    // The game as shipped: show and extract are about the item, not about whatever mod is
    // installed over it. Anything that writes or verifies goes to the installation directly.
    // Unpacked into memory up to a limit, because reading them from disk a block at a time was most of
    // what resolving and extracting cost. See BundleUnpacker.
    using var bundles = new BundleSet(game, originals: new ModStore(game).OriginalOf)
    {
        UnpackBudget = line.Number("read-memory", 1024) * 1024L * 1024,
    };

    // Loaded on demand: only the commands that name items pay for it.
    var catalogs = new Lazy<GameCatalogs>(() => LoadCatalogs(bundles, line.Option("language")));

    return line.Command switch
    {
        "info" => Info(game, bundles),
        "consolidate" => Consolidate(game, line),
        "verify" => Verify(game),
        "mods" => Mods(game),
        "apply" or "enable" or "disable" or "remove" => Change(line, game, bundles, catalogs),
        "items" => Items(line, catalogs),
        "weapons" => Weapons(line, catalogs),
        "show" or "extract" => ShowOrExtract(line, game, bundles, catalogs),
        "animation" => Animations(line, bundles, catalogs),
        _ => throw new CommandLineException($"Unknown command '{line.Command}'."),
    };
}

static int Pack(CommandLine line)
{
    var workspace = Path.GetFullPath(line.Optional("a workspace directory") ?? Directory.GetCurrentDirectory());
    if (!File.Exists(Path.Combine(workspace, PackManifest.FileName)))
        throw new FileNotFoundException(
            $"'{workspace}' is not a workspace: it has no {PackManifest.FileName}. "
            + "`pgassettool extract <item> --workspace` makes one.");

    var manifest = Workspace.Read(workspace);

    // Named after the mod, not after the directory it was built in. See PackBuilder.FileNameFor.
    // --out may name the file or the directory to put it in.
    var output = line.Option("out") is { } wanted
        ? Directory.Exists(wanted) || wanted.EndsWith('/') || wanted.EndsWith('\\')
            ? Path.Combine(Path.GetFullPath(wanted), PackBuilder.FileNameFor(manifest))
            : Path.GetFullPath(wanted)
        : PackBuilder.OutputFor(workspace, manifest);

    // Signed when the workspace asks for it or the command does. The CLI has no settings of its own,
    // so a pack that says nothing is built plain — and --protect is a request about this build, so
    // it wins over a workspace saved with protection off.
    using var signer = line.Has("protect") || manifest.Protect == true ? PackAuthor.Mine() : null;

    var result = PackBuilder.Build(workspace, output, signer);
    Console.WriteLine($"{result.Path}");
    Console.WriteLine($"  {result.Operations} operations, {result.Bytes:N0} bytes{(signer is null ? "" : ", signed")}");
    foreach (var operation in PackBuilder.ReadManifest(result.Path).Operations)
        Console.WriteLine($"    {operation.Op}  {operation.Target}  <- {operation.Source}");
    if (result.Unchanged.Count > 0)
        Console.WriteLine($"  {result.Unchanged.Count} unedited files left out.");

    // Said here as well as on apply, because the author is the one who can decide whether
    // reaching the other weapons is what they meant. Building the pack itself reads only the
    // workspace, so an installation the tool cannot find costs this warning and nothing else.
    try
    {
        var installation = line.Option("game") is { } dir
            ? GameInstallation.Open(dir)
            : GameInstallation.OpenDetected();
        using var opened = new BundleSet(installation);
        var named = LoadCatalogs(opened, line.Option("language"));

        foreach (var also in SharedInPack(opened, PackBuilder.ReadManifest(result.Path)))
            Console.WriteLine($"  shared: {Describe(also, named)}");
    }
    catch (Exception ex) when (CommandLine.IsExpected(ex) || ex is CommandLineException)
    {
        Console.Error.WriteLine($"  (could not check what else uses these: {ex.Message})");
    }

    return 0;
}

static int Animations(CommandLine line, BundleSet bundles, Lazy<GameCatalogs> catalogs)
{
    if (line.Positional.Count is 0 or > 2)
        throw new CommandLineException("animation takes a workspace directory, and then the animation to change.");

    var workspace = Path.GetFullPath(line.Positional[0]);
    if (!File.Exists(Path.Combine(workspace, PackManifest.FileName)))
        throw new FileNotFoundException(
            $"'{workspace}' is not a workspace: it has no {PackManifest.FileName}. "
            + "`pgassettool extract <item> --workspace` makes one.");

    var manifest = Workspace.Read(workspace);
    var slots = PGAssetTool.Core.Animation.AnimationSwap.Slots(manifest);
    if (slots.Count == 0)
    {
        Console.WriteLine("This workspace has no animations: either its item plays none, or it was extracted by a "
            + "version of the tool that did not write them. Extracting the item again writes them.");
        return 0;
    }

    var edited = Workspace.Changed(workspace, manifest).Select(o => o.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
    string Says(PGAssetTool.Core.Animation.AnimationSwap.Slot slot)
    {
        try
        {
            var file = PGAssetTool.Core.Animation.ClipFile.Read(Path.Combine(workspace, slot.Source));
            var motion = file.ToMotion();
            var origin = !edited.Contains(slot.Source) ? "its own"
                : file.From.Length > 0 ? $"from {file.From}" : "edited";
            return $"{motion.Length,5:0.00}s  {motion.Curves.Count,3} curves  {origin}"
                + (file.Unmatched.Count > 0 ? $"  ({file.Unmatched.Count} left out)" : "");
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return $"unreadable: {e.Message}";
        }
    }

    if (line.Has("export"))
    {
        var (written, outcome) = PGAssetTool.Core.Animation.AnimationSwap.ExportGlb(
            bundles, catalogs.Value, workspace, line.Option("out") is { } to ? Path.GetFullPath(to) : null);
        Console.WriteLine($"Wrote {written}");
        Console.WriteLine($"  {outcome.Animations} animations, {outcome.Objects} objects"
            + (outcome.Models.Count > 0 ? $", wearing {string.Join(", ", outcome.Models)}" : ", no model"));
        foreach (var why in outcome.LeftOut.Take(8)) Console.WriteLine($"  Left out: {why}");
        if (outcome.LeftOut.Count > 8) Console.WriteLine($"  and {outcome.LeftOut.Count - 8} more left out");
        Console.WriteLine();
        Console.WriteLine("  Edit it in Blender, export it as glTF Binary (.glb), then");
        Console.WriteLine("  `pgassettool animation <workspace> <clip> --glb <file>` puts an animation of it back.");
        return 0;
    }

    if (line.Positional.Count == 1)
    {
        var width = slots.Max(s => TextColumn.DisplayWidth(s.Clip));
        foreach (var slot in slots)
            Console.WriteLine($"  {TextColumn.Pad(slot.Clip, width)}  {Says(slot)}   {slot.Source}");
        Console.WriteLine();
        Console.WriteLine("`pgassettool animation <workspace> <clip> --from <item>` puts another item's animation in;");
        Console.WriteLine("`--glb <file>` one from a glTF; `pgassettool animation <workspace> --export` writes them all to edit.");
        return 0;
    }

    var named = line.Positional[1];
    var chosen = PGAssetTool.Core.Animation.AnimationSwap.Find(manifest, named)
        ?? throw new KeyNotFoundException($"This workspace has no animation called '{named}'. It has: "
            + string.Join(", ", slots.Select(s => s.Clip)) + ".");

    if (line.Has("reset"))
    {
        PGAssetTool.Core.Animation.AnimationSwap.Restore(bundles, workspace, chosen);
        Console.WriteLine($"{chosen.Clip} is its own again.");
        return 0;
    }

    if (line.Option("anim") is { } anim)
    {
        var outcome = PGAssetTool.Core.Animation.AnimationSwap.UseFile(
            bundles, catalogs.Value, workspace, chosen, Path.GetFullPath(anim));
        Report(chosen, outcome);
        return 0;
    }

    if (line.Option("glb") is { } glb)
    {
        var outcome = PGAssetTool.Core.Animation.AnimationSwap.UseGlb(
            bundles, catalogs.Value, workspace, chosen, Path.GetFullPath(glb), line.Option("take"));
        Report(chosen, outcome);
        return 0;
    }

    if (line.Option("from") is { } fromQuery)
    {
        var from = Find(catalogs.Value, fromQuery);
        var outcome = PGAssetTool.Core.Animation.AnimationSwap.Use(
            bundles, catalogs.Value, workspace, chosen, from, line.Option("clip") ?? chosen.Clip);
        Report(chosen, outcome);
        return 0;
    }

    Console.WriteLine($"  {chosen.Clip}  {Says(chosen)}   {chosen.Source}");
    return 0;

    static void Report(PGAssetTool.Core.Animation.AnimationSwap.Slot chosen, PGAssetTool.Core.Animation.AnimationSwap.Outcome outcome)
    {
        if (outcome.Unchanged)
        {
            Console.WriteLine($"{chosen.Clip} is unchanged: {outcome.From} is the animation it already plays.");
            return;
        }
        Console.WriteLine($"{chosen.Clip} now plays {outcome.From}: {outcome.Length:0.00}s, {outcome.Moved} curves fitted to this item.");
        if (outcome.Unmatched.Count > 0)
        {
            Console.WriteLine($"  Left out, with nothing here to move: {outcome.Unmatched.Count}");
            foreach (var path in outcome.Unmatched.Take(8)) Console.WriteLine($"    {path}");
            if (outcome.Unmatched.Count > 8) Console.WriteLine($"    and {outcome.Unmatched.Count - 8} more");
        }
        foreach (var note in outcome.Notes ?? []) Console.WriteLine($"  {note}");
        Console.WriteLine();
        Console.WriteLine($"  {chosen.Source} holds it. Pack the workspace to make it a mod.");
    }
}

static int Info(GameInstallation game, BundleSet bundles)
{
    Console.WriteLine($"Install    {game.RootDirectory}");
    Console.WriteLine($"Bundles    {bundles.BundleNames.Count} in the manifest");
    Console.WriteLine($"Shipped    {game.BundlesDirectory}");
    if (game.Downloaded is { } dl)
    {
        Console.WriteLine($"Downloaded {dl.Claimed.Count} claimed  {dl.BundlesDirectory}");
        var broken = dl.ClaimedButMissing().ToList();
        if (broken.Count > 0)
            Console.WriteLine($"           {broken.Count} claimed with no file; the game logs a read "
                + "failure for each and falls back to the shipped copy");
    }
    else Console.WriteLine("Downloaded none yet; everything loads from the shipped cache");
    Console.WriteLine($"Data files {game.EnumerateSerializedFiles().Count()}");
    Console.WriteLine($"Version    {GameVersion.Of(bundles.Context, game)
        ?? $"unavailable ({ClassPackage.FileName} not found)"}");
    Console.WriteLine($"Tool data  {new ModStore(game).Home}");
    return 0;
}

static int Consolidate(GameInstallation game, CommandLine line)
{
    if (game.Downloaded is null)
    {
        Console.WriteLine("No downloaded cache; everything already loads from the game's own folder.");
        return 0;
    }

    var plan = CacheConsolidation.Plan(game);
    if (plan.Count == 0)
    {
        Console.WriteLine("The downloaded cache claims nothing.");
        return 0;
    }

    foreach (var group in plan.GroupBy(i => i.Verdict))
    {
        Console.WriteLine($"\n{group.Key}  ({group.Count()})");
        Console.WriteLine("  " + group.Key switch
        {
            ConsolidationVerdict.Redundant => "identical to the shipped copy; removing changes nothing",
            ConsolidationVerdict.DownloadedIsDamaged => "differs from its own hash while the shipped copy matches; the shipped one is intact",
            ConsolidationVerdict.ShippedCarriesAMod => "the shipped copy is the edited one; removing these is what lets that edit load",
            ConsolidationVerdict.NewerVersion => "a different version from the one shipped; removing these rolls the game back",
            ConsolidationVerdict.OnlyHere => "nothing shipped under this name and version; removing these loses the content",
            ConsolidationVerdict.BrokenClaim => "claimed with no file; the game logs a read failure for each on every launch",
            _ => "",
        });
        foreach (var item in group.Take(10)) Console.WriteLine($"    {item.Bundle}");
        if (group.Count() > 10) Console.WriteLine($"    and {group.Count() - 10} more");
    }

    var safe = plan.Count(i => i.SafeToRemove);
    var keep = plan.Count - safe;
    Console.WriteLine($"\n{safe} of {plan.Count} can be removed without changing what the game loads.");
    if (keep > 0) Console.WriteLine($"{keep} would change it and are left alone.");

    if (!line.Has("apply"))
    {
        Console.WriteLine("\nNothing was changed. Pass --apply to remove the safe ones.");
        Console.WriteLine("Whether the game refills the cache afterwards is not known, so check before relying on it.");
        return 0;
    }

    if (GameProcess.IsRunning(game)) return GameIsRunning();

    var removed = CacheConsolidation.Apply(game, plan);
    Console.WriteLine($"\nRemoved {removed.Count}; the ledger now claims {game.Downloaded.Claimed.Count - removed.Count}.");
    return 0;
}

static int Verify(GameInstallation game)
{
    var store = new ModStore(game);
    var expected = store.Read().SelectMany(m => m.TouchedBundles.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var modified = new List<(string Bundle, bool Known)>();
    var missing = 0;

    var fromDownloaded = 0;
    foreach (var entry in game.ReadManifest())
    {
        if (game.Resolve(entry.Name, entry.Hash) is not { } resolved) { missing++; continue; }
        if (resolved.Cache == CacheKind.Downloaded) fromDownloaded++;
        if (!BundleIntegrity.IsPristine(resolved.Path, entry.Hash))
            modified.Add((entry.Name, expected.Contains(entry.Name)));
    }
    if (fromDownloaded > 0)
        Console.WriteLine($"{fromDownloaded} bundle(s) load from the downloaded cache rather than the shipped one.");

    Console.WriteLine($"{modified.Count} bundle(s) differ from the hash the game recorded for them"
        + (missing > 0 ? $", {missing} not downloaded" : "") + ".");
    foreach (var (bundle, known) in modified.OrderBy(m => m.Bundle))
        Console.WriteLine($"  {TextColumn.Pad(bundle, 40)} {(known ? "modified by an installed mod" : "modified by something else")}");
    if (modified.Any(m => !m.Known))
        Console.WriteLine("\nBundles in the second group have no backup here. Verify the game's files "
            + "through Steam before installing mods over them.");
    return 0;
}

static int Mods(GameInstallation game)
{
    var store = new ModStore(game);
    var installed = store.Read();
    if (installed.Count == 0) Console.WriteLine("Nothing installed.");

    // As wide as the longest there is, so the columns line up: an id is the item's id, its look and
    // eight characters more, and a fixed width ran most of them into the name beside them.
    var idWidth = installed.Select(m => TextColumn.DisplayWidth(m.Id)).DefaultIfEmpty(0).Max();
    var nameWidth = Math.Min(40, installed.Select(m => TextColumn.DisplayWidth(m.Name)).DefaultIfEmpty(0).Max());

    foreach (var mod in installed.OrderBy(m => m.InstalledAt))
        Console.WriteLine($"{(mod.Enabled ? "[on ]" : "[off]")} {TextColumn.Pad(mod.Id, idWidth)}  "
            + $"{TextColumn.Pad(mod.Name, nameWidth)}  {mod.Version,-8} "
            + $"{(mod.Subject is { IsKnown: true } subject ? TextColumn.Pad(subject.Kind, 9) + " " : "")}"
            + $"{mod.TouchedBundles.Count} bundle(s), installed {mod.InstalledAt:yyyy-MM-dd} for {mod.GameVersion}");
    Console.WriteLine($"\nBackups: {store.BackupRoot}");
    return 0;
}

static int Change(CommandLine line, GameInstallation game, BundleSet bundles, Lazy<GameCatalogs> catalogs)
{
    var command = line.Command;
    if (line.Positional.Count == 0)
        throw new CommandLineException($"{command} needs " + (command == "apply" ? "a .pgmod path." : "a mod id."));

    var store = new ModStore(game);
    var applier = new ModApplier(game, store)
    {
        Force = line.Has("force"),
        Rebuild = line.Has("rebuild"),
        Packing = line.Has("fast") ? BundlePacking.Faster : BundlePacking.Smaller,
        AtOnce = line.Has("low-memory") ? 1 : 4,
    };

    // Worked out before anything is written, so a typo in the third name stops the first two from
    // going in on their own.
    var targets = command == "apply"
        ? line.Positional.Select(Path.GetFullPath).ToList()
        : line.Positional.Select(id => ModNamed(store.Read(), id)).Distinct().ToList();

    if (command == "apply")
        foreach (var pack in targets.Where(p => !File.Exists(p)))
            throw new FileNotFoundException($"There is no pack at '{pack}'.");

    // Everything past here rewrites the game's bundles, and the game holds them open while it runs:
    // a write that lands in the middle of that leaves a half-written file where a bundle was. The
    // manager and the editor have always refused; this had not, and it is the same game.
    if (GameProcess.IsRunning(game)) return GameIsRunning();

    // Said before the game is written to rather than after, and said whichever way it goes: a
    // signature is only worth carrying if somebody hears about it while there is still a decision
    // to make. An altered pack still installs — with --force, so it takes saying so.
    if (command == "apply")
        foreach (var pack in targets)
        {
            if (PackFile.Inspect(pack) is not { } seal || seal.State == SealState.Unsigned) continue;

            Console.WriteLine($"Seal       {Path.GetFileName(pack)}: {seal.Describe}");
            if (seal.Wrong && !line.Has("force"))
            {
                Console.Error.WriteLine(
                    $"'{Path.GetFileName(pack)}' carries a signature it no longer matches, so it has been "
                    + "changed since it was built. Pass --force to install it anyway.");
                return 1;
            }
        }

    var version = GameVersion.Of(bundles.Context, game) ?? GameVersion.Unknown;
    var result = command switch
    {
        "apply" => applier.Install(targets, version),
        "enable" => applier.SetEnabled(targets, true),
        "disable" => applier.SetEnabled(targets, false),
        "remove" => applier.Remove(targets),
        _ => throw new UnreachableException(),
    };

    foreach (var operation in result.Applied)
        Console.WriteLine($"  {operation.Mod}  {operation.Op}  {operation.Target}  "
            + $"[{operation.Detail}{(operation.ResolvedByPathId ? "" : ", matched by name")}]");
    foreach (var moved in result.Moved) Console.WriteLine($"  followed: {moved}");
    if (result.Restored.Count > 0)
        Console.WriteLine($"  restored from backup: {string.Join(", ", result.Restored)}");
    foreach (var also in result.Shared)
        Console.WriteLine($"  shared: {DescribeSafely(also, catalogs)}");
    if (result.PrunedBackups.Count > 0)
        Console.WriteLine($"  removed stale backups: {string.Join(", ", result.PrunedBackups)}");
    foreach (var name in applier.Displaced)
        Console.WriteLine($"  turned off: {name}, which wrote the same assets");
    foreach (var failure in result.Failed) Console.Error.WriteLine($"  FAILED {failure}");

    if (result.Unchanged.Count > 0)
        Console.WriteLine($"  already holding what they should: {string.Join(", ", result.Unchanged)}");

    Console.WriteLine($"\n{result.Applied.Count} operation(s) applied, {result.Failed.Count} failed"
        + (result.Unchanged.Count > 0 ? $", {result.Unchanged.Count} bundle(s) left alone." : "."));

    // A pack whose writes were refused is still in the ledger, switched on, and will be tried again
    // on every change: said, so it is not found later as a mod that is on and does nothing.
    if (command == "apply" && result.Failed.Count > 0)
        Console.Error.WriteLine("\nWhat failed is still listed as installed and is tried again on every change. "
            + "`pgassettool remove <id>` takes it out; `pgassettool mods` lists the ids.");

    return result.Failed.Count > 0 ? 1 : 0;
}

static int Items(CommandLine line, Lazy<GameCatalogs> catalogs)
{
    var wanted = line.Optional("a kind") is { } name ? KindNamed(name) : null;

    foreach (var kind in catalogs.Value.Kinds)
    {
        if (wanted is not null && kind != wanted) continue;

        var of = catalogs.Value.Of(kind);
        Console.WriteLine($"{kind.Name} ({of.Count})");

        // The whole of a kind when it was asked for by name, and a taste of each otherwise: there
        // are 596 avatars and nobody typing `items` wanted all of them.
        foreach (var item in wanted is not null ? of : of.Take(5))
            Console.WriteLine($"    {TextColumn.Pad(Listed(item), 42)} "
                + $"{catalogs.Value.Localization.Translate(item.LocalizationKey) ?? item.Tag}");

        if (wanted is null && of.Count > 5) Console.WriteLine($"    … {of.Count - 5} more");
    }

    if (wanted is not null && catalogs.Value.Of(wanted).Count == 0)
        Console.WriteLine($"{wanted.Name} (0)\n    The game this was read from has none.");

    return 0;
}

static int Weapons(CommandLine line, Lazy<GameCatalogs> lazy)
{
    var timer = Stopwatch.StartNew();
    var catalogs = lazy.Value;
    var filter = line.Optional("a filter");
    var matches = (filter is null ? catalogs.Items.Weapons : catalogs.Items.Search(filter, catalogs.Names)).ToList();
    foreach (var w in matches)
        Console.WriteLine($"{w.GameNumber,5}  "
            + $"{TextColumn.Pad(catalogs.Localization.Translate(w.LocalizationKey) ?? w.Slug, 34)} "
            + $"{TextColumn.Pad(w.Slug, 34)} {TextColumn.Pad(w.PrefabName, 12)}"
            + $"{(w.IsHidden ? "  (hidden)" : "")}");
    Console.Error.WriteLine($"\n{matches.Count} of {catalogs.Items.Count} weapons  (catalogs {timer.ElapsedMilliseconds}ms)");
    return 0;
}

static int ShowOrExtract(CommandLine line, GameInstallation game, BundleSet bundles, Lazy<GameCatalogs> lazy)
{
    var query = line.Single(line.Command == "show"
        ? "an item: a weapon's number, prefab name or slug, or the id `items` lists"
        : "an item to extract");

    var timer = Stopwatch.StartNew();
    var catalogs = lazy.Value;
    var catalogTime = timer.ElapsedMilliseconds;

    var record = Find(catalogs, query);
    var tree = new WeaponResolver(bundles, catalogs).Resolve(record);

    if (line.Command == "extract") return Extract(line, game, bundles, record, tree, timer);

    Console.WriteLine($"{Heading(record, tree)}{(record.IsHidden && record.IsNumbered ? "   (hidden: no localization key, absent from the in-game list)" : "")}");
    Console.WriteLine(record.IsNumbered
        ? $"  prefab {record.PrefabName}  index {record.Index}  slug {record.Slug}  tag {record.Tag}"
        : $"  {record.Kind.Name.ToLowerInvariant()}  id {record.Slug}{(record.Index > 0 ? $"  index {record.Index}" : "")}"
            + (record.LocalizationKey.Length > 0 ? $"  key {record.LocalizationKey}" : ""));
    Console.WriteLine();
    if (tree.Icon is not null)
        Console.WriteLine($"  Icon     {tree.Icon.TextureName} @ {tree.Icon.Container}");
    Console.WriteLine($"  Prefab   {tree.PrefabPath ?? record.AssetPath} @ {tree.PrefabBundle ?? "?"}");
    if (tree.MainMesh is { } body)
    {
        // What the renderers say goes on it, which is what the preview draws it in. Printed because
        // a model that comes up grey is the one failure with nothing to look at: the mesh is there,
        // the textures are there, and what is missing is the line between them.
        var worn = tree.MeshTextures.FirstOrDefault(m => m.MeshPathId == body.PathId);
        var dressed = worn is null
            ? "nothing found to draw it in"
            : string.Join(", ", worn.BySubMesh.Select(t => t?.Name ?? "-"));

        var shell = ReadMeshOf(bundles, tree, body)?.CarriesAnOutline == true
            ? ", with an outline shell"
            : "";

        Console.WriteLine($"  Model    {body.Name}   ({dressed}{shell})");
    }
    foreach (var group in tree.PrefabAssets.GroupBy(a => a.Class).OrderByDescending(g => g.Count()))
    {
        Console.WriteLine($"    {group.Key} ({group.Count()})");
        foreach (var node in group.Where(n => n.Name.Length > 0).Take(8))
            Console.WriteLine($"      {node.Name}");
    }

    // A weapon's skins are a registry of their own. Every other kind keeps its skin beside the item
    // as a related asset, drawn with the item's own textures, so it is listed there.
    if (record.Kind == ItemKinds.Weapon)
    {
        Console.WriteLine();
        Console.WriteLine($"  Skins ({tree.Skins.Count})");
    }
    foreach (var skin in tree.Skins)
    {
        Console.WriteLine($"    {skin.Record.Id}  {(skin.DisplayName is null ? "" : $"\"{skin.DisplayName}\"")}");
        // What the resolver found rather than what the lookup table alone says: a skin's model can hold
        // materials the table does not list, and the tree already knows where.
        // By the path the skin wrote, not by name: a material can be found under a name a little
        // different from the one the skin gives it, and is then shown as what it is really called.
        foreach (var path in skin.Record.MaterialPaths)
        {
            var leaf = path[(path.LastIndexOf('/') + 1)..];
            var found = skin.Materials.FirstOrDefault(m =>
                string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)
                || m.Path.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine(found is null
                ? $"      material  {path} @ ?"
                : $"      material  {found.Path} @ {found.Bundle}"
                    + (string.Equals(found.Name, leaf, StringComparison.OrdinalIgnoreCase) ? "" : $"  (as {found.Name})"));
        }
        if (skin.Model is { } brought)
            Console.WriteLine($"      model     {brought.AssetPath} @ {brought.Bundle}");
    }

    if (tree.Related.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"  Related ({tree.Related.Count})");
        foreach (var group in tree.Related.GroupBy(r => r.Namespace))
        {
            Console.WriteLine($"    {group.Key} ({group.Count()})");
            foreach (var asset in group)
                // The class only for the ones that can be written back, which is what saying it is for.
                Console.WriteLine($"      {TextColumn.Pad(asset.Path, 66)} @ {asset.Bundle ?? "?"}"
                    + (asset.Asset is { } resolved ? $"  {resolved.Class}" : ""));
        }
    }

    if (tree.UnresolvedReasons.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("  Unresolved");
        foreach (var reason in tree.UnresolvedReasons) Console.WriteLine($"    {reason}");
    }

    Console.Error.WriteLine($"\ncatalogs {catalogTime}ms, total {timer.ElapsedMilliseconds}ms");
    return 0;
}

static int Extract(
    CommandLine line, GameInstallation game, BundleSet bundles, WeaponRecord record, WeaponTree tree, Stopwatch timer)
{
    // A skin nobody has is an error here rather than a line under Skipped: extracting the item as it
    // comes, into a directory named for the item, is not what somebody asking for a skin wanted.
    if (line.Option("skin") is { Length: > 0 } asked
        && !tree.Skins.Any(s => string.Equals(s.Record.Id, asked, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(s.DisplayName, asked, StringComparison.OrdinalIgnoreCase)))
    {
        Console.Error.WriteLine(tree.Skins.Count == 0
            ? $"{Heading(record, tree)} has no skins of its own to extract."
                + (record.Kind == ItemKinds.Weapon ? "" : " Its skin is drawn with its own textures, so extracting it as it comes covers both.")
            : $"{Heading(record, tree)} has no skin called '{asked}'. It has: "
                + string.Join(", ", tree.Skins.Select(s => s.DisplayName is null ? s.Record.Id : $"{s.Record.Id} (\"{s.DisplayName}\")")));
        return 1;
    }

    var outputRoot = Path.GetFullPath(line.Option("out") ?? Path.Combine(Directory.GetCurrentDirectory(), "workspace"));

    // Every look at once: the item as it comes and each skin, a workspace apiece, since one pack is
    // one look. Always as workspaces — a skin written out with no manifest could never be packed.
    if (line.Has("all-skins"))
    {
        if (line.Option("skin") is { Length: > 0 })
            throw new CommandLineException("--all-skins writes every skin; --skin names one. Ask for one or the other.");

        Console.WriteLine(Heading(record, tree));
        if (tree.Skins.Count == 0)
            Console.WriteLine("  It has no skins of its own"
                + (record.Kind == ItemKinds.Weapon ? "; writing it as it comes." : ": its skin is drawn with its own textures, so this covers both."));

        var looks = WeaponExporter.ExportEveryLook(
            bundles, tree, outputRoot, line.Option("author") ?? "", GameVersion.Of(bundles.Context, game),
            opaque: line.Has("opaque"), maskUnused: !line.Has("whole"),
            progress: (at, of, name) => Console.Error.WriteLine($"  [{at}/{of}] {name}"));

        foreach (var look in looks.Where(l => l.Export is not null))
        {
            var count = look.Export!.Assets.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Console.WriteLine($"  {TextColumn.Pad(look.Skin is null ? "as it comes" : look.Name, 40)} {count,4} files  -> {look.Export.Directory}");
        }
        // Said once for every look it stopped: a bundle they share stops all of them the same way.
        foreach (var reason in looks.Where(l => l.Export is null).GroupBy(l => l.Failed))
            Console.WriteLine($"  Not written — {string.Join(", ", reason.Select(l => l.Skin is null ? "as it comes" : l.Name))}: {reason.Key}");

        var failed = looks.Count(l => l.Export is null);
        Console.WriteLine();
        Console.WriteLine($"  {looks.Count - failed} of {looks.Count} written, each a workspace of its own: edit one, then "
            + "`pgassettool pack <its folder>`.");
        Console.Error.WriteLine();
        Console.Error.WriteLine($"total {timer.ElapsedMilliseconds}ms");
        return failed == looks.Count ? 1 : 0;
    }

    var exporter = new WeaponExporter(bundles)
    {
        Opaque = line.Has("opaque"),
        Skin = line.Option("skin"),
        MaskUnused = !line.Has("whole"),
    };
    var asWorkspace = line.Has("workspace");

    WeaponExport export;
    try
    {
        export = asWorkspace
            ? exporter.ExportAsWorkspace(tree, outputRoot,
                line.Option("author") ?? "",
                GameVersion.Of(bundles.Context, game))
            : exporter.Export(tree, outputRoot);
    }
    catch (AlteredBundlesException refused)
    {
        // A bundle something else modded, which this installation has no original of. See
        // WeaponExporter.Refuse.
        Console.Error.WriteLine(refused.Message);
        return 1;
    }

    Console.WriteLine(Heading(record, tree));
    Console.WriteLine($"  -> {export.Directory}");
    // By file: a picture written to two assets is one file on disk and is listed once.
    var files = export.Assets.DistinctBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToList();
    foreach (var group in files.GroupBy(a => Path.GetDirectoryName(a.Path)).OrderBy(g => g.Key))
    {
        var folder = Path.GetRelativePath(export.Directory, group.Key!).Replace('\\', '/');
        Console.WriteLine($"    {folder}/  ({group.Count()})");
        foreach (var asset in group.OrderBy(a => a.Path))
            Console.WriteLine($"      {TextColumn.Pad(Path.GetFileName(asset.Path), 52)} {asset.Bytes,10:N0}");
    }
    if (files.Count == 0)
        Console.WriteLine("    nothing this tool can write back was found");
    if (export.Skipped.Count > 0)
    {
        Console.WriteLine($"\n  Skipped ({export.Skipped.Count})");
        foreach (var reason in export.Skipped.Take(10)) Console.WriteLine($"    {reason}");
        if (export.Skipped.Count > 10) Console.WriteLine($"    and {export.Skipped.Count - 10} more");
    }
    if (export.Notes is { Count: > 0 } notes)
    {
        Console.WriteLine();
        foreach (var note in notes) Console.WriteLine($"  Note: {note}");
    }
    if (asWorkspace)
    {
        var manifest = Workspace.Read(export.Directory);
        var replaceable = manifest.Operations.Select(o => o.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Console.WriteLine();
        Console.WriteLine($"  {PackManifest.FileName} lists {replaceable} replaceable files.");
        Console.WriteLine("  Edit any of them, then run:");
        Console.WriteLine($"    pgassettool pack \"{export.Directory}\"");
    }
    Console.Error.WriteLine($"\n{files.Count} files, total {timer.ElapsedMilliseconds}ms");
    return 0;
}

/// The catalogues in the language asked for, or the default one.
///
/// A language the game does not ship is said by name, with the ones it does: the registry read fails
/// on it otherwise, as a missing MonoBehaviour that names nothing a person typed.
static GameCatalogs LoadCatalogs(BundleSet bundles, string? language)
{
    if (language is not null && !bundles.BundleNames.Contains(language))
        throw new CommandLineException($"There is no language bundle called '{language}'. The game has: "
            + string.Join(", ", GameCatalogs.Languages(bundles).Select(l => $"{l.Bundle} ({l.Name})")) + ".");

    return GameCatalogs.Load(bundles, language ?? GameCatalogs.DefaultLanguage);
}

/// A kind by the name a person would type it: `hat`, `Hat`, `hats`.
static ItemKind KindNamed(string name)
    => ItemKinds.ByName(name)
       ?? (name.EndsWith('s') ? ItemKinds.ByName(name[..^1]) : null)
       ?? throw new CommandLineException(
           $"There is no kind called '{name}'. The kinds are: {string.Join(", ", ItemKinds.All.Select(k => k.Pack))}.");

/// The item a query names, or an error that says what it might have meant.
///
/// The game's own ids first, by the catalogue's rules. Then a display name, typed out in full, when
/// exactly one item answers to it — `Aztec Power Hat` is how a player knows the thing, and the id
/// is not something the game ever shows them.
static WeaponRecord Find(GameCatalogs catalogs, string query)
{
    if (catalogs.Find(query) is { } found) return found;

    var everything = catalogs.Kinds.SelectMany(k => catalogs.Of(k)).ToList();
    var named = everything
        .Where(r => string.Equals(catalogs.Localization.Translate(r.LocalizationKey), query,
            StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (named.Count == 1) return named[0];

    var near = (named.Count > 1 ? named : everything.Where(r =>
            r.Slug.Contains(query, StringComparison.OrdinalIgnoreCase)
            || catalogs.Localization.Translate(r.LocalizationKey)?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
        .Take(12)
        .Select(r => $"    {TextColumn.Pad(Listed(r), 42)} {r.Kind.Name.ToLowerInvariant(),-10} "
            + (catalogs.Localization.Translate(r.LocalizationKey) ?? ""))
        .ToList();

    throw new KeyNotFoundException(named.Count > 1
        ? $"'{query}' is the name of {named.Count} things; name one by its id:\n{string.Join("\n", near)}"
        : near.Count == 0
            ? $"Nothing matches '{query}'. `pgassettool items` and `pgassettool weapons` list what there is."
            : $"Nothing is called exactly '{query}'. Perhaps one of these:\n{string.Join("\n", near)}");
}

/// How an item is named on a list: by the number a player reads for a weapon, and by its id for
/// everything else, quoted when it has a space in it so it can be typed back as it stands.
static string Listed(WeaponRecord item)
    => item.IsNumbered ? item.Slug : item.Slug.Contains(' ') ? $"\"{item.Slug}\"" : item.Slug;

/// The first line said about an item: its number where it has one, and its kind where it does not.
static string Heading(WeaponRecord record, WeaponTree tree)
    => record.IsNumbered ? $"#{record.GameNumber}  {tree.DisplayName}" : $"{record.Kind.Name}  {tree.DisplayName}";

/// A mod by its id, by the start of one, or by its name, whichever names exactly one: the ids end in
/// eight characters of noise that nobody wants to type, and the name is what `mods` shows beside it.
static string ModNamed(IReadOnlyList<InstalledMod> mods, string text)
{
    // No id begins or ends in white space, and a carriage return copied in with one from a list
    // made on Windows turned a mod that is plainly installed into one that is not.
    text = text.Trim();
    if (mods.FirstOrDefault(m => m.Id == text) is { } exact) return exact.Id;

    var starting = mods.Where(m => m.Id.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToList();
    if (starting.Count == 0)
        starting = mods.Where(m => string.Equals(m.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();

    return starting.Count switch
    {
        1 => starting[0].Id,
        0 => throw new KeyNotFoundException(
            $"No installed mod is called '{text}' or has an id beginning so. `pgassettool mods` lists them."),
        _ => throw new KeyNotFoundException(
            $"{starting.Count} installed mods answer to '{text}': "
            + string.Join(", ", starting.Select(m => m.Id)) + ". Give more of the id."),
    };
}

static int GameIsRunning()
{
    Console.Error.WriteLine(
        "The game is running, and rewriting its files now can leave a half-written one behind. "
        + "Close it, then run this again.");
    return 1;
}

/// One of the item's own meshes, decoded, so `show` can say what it is made of. Nothing when it
/// cannot be read, since this is a remark rather than the answer.
static PGAssetTool.Core.Export.Meshes.UnityMesh? ReadMeshOf(BundleSet bundles, WeaponTree tree, AssetNode mesh)
{
    var bundle = mesh.Bundle.Length > 0 ? mesh.Bundle : tree.PrefabBundle;
    if (bundle is null) return null;

    try
    {
        var file = bundles.Open(bundle);
        var info = file.file.GetAssetInfo(mesh.PathId);
        var field = info is null ? null : bundles.Context.Deserialize(file, info);
        return field is null ? null : PGAssetTool.Core.Preview.AssetPreview.Mesh(field, bundles, bundle);
    }
    catch (Exception e) when (e is IOException or NotSupportedException or InvalidDataException
                                  or IndexOutOfRangeException or ArgumentException)
    {
        return null;
    }
}

/// The replaceable objects in a pack that more than one weapon reaches.
///
/// Each bundle is indexed once, so a pack touching several costs one pass over each.
static IEnumerable<SharedAsset> SharedInPack(BundleSet bundles, PackManifest manifest)
{
    foreach (var group in manifest.Operations.GroupBy(o => o.Target.Container, StringComparer.OrdinalIgnoreCase))
    {
        AssetsFileInstance file;
        BundleUsage usage;
        try
        {
            file = bundles.Open(group.Key);
            usage = BundleUsage.Build(bundles.Context, file);
        }
        catch (Exception)
        {
            // A pack can name a bundle this installation does not have; apply reports that properly.
            continue;
        }

        var index = new ContainerIndex(bundles.Context);
        foreach (var operation in group)
        {
            if (index.Resolve(operation.Target, file, out _) is not { } info) continue;
            if (SharedAssets.Check(usage, operation.Target, info.PathId) is { } also) yield return also;
        }
    }
}

/// The same, after an install: the catalogues are read only to name the weapons, and an install that
/// has already gone into the game is not reported as failed because they could not be.
static string DescribeSafely(SharedAsset also, Lazy<GameCatalogs> catalogs)
{
    try
    {
        return Describe(also, catalogs.Value);
    }
    catch (Exception e) when (CommandLine.IsExpected(e) || e is CommandLineException)
    {
        return $"{also.Target} is used by {string.Join(", ", also.Prefabs)}. Replacing it changes all of them.";
    }
}

/// Names the weapons a shared object reaches.
///
/// The numbers in a prefab name are not the numbers players see — the two sequences agree for six
/// weapons out of 1517 — so reporting them raw would point at the wrong weapon almost every time.
static string Describe(SharedAsset also, GameCatalogs catalogs)
{
    var weapons = also.Weapons
        .Select(number => catalogs.Items.Weapons.FirstOrDefault(w => w.PrefabNumber == number))
        .Where(w => w is not null)
        .Select(w => $"#{w!.GameNumber} {catalogs.Localization.Translate(w.LocalizationKey) ?? w.Slug}")
        .ToList();

    // Anything the catalog does not know — a shared UI material, a prop — is named as it stands.
    var rest = also.Prefabs.Where(p => !p.StartsWith("Weapon", StringComparison.Ordinal)
                                    && !p.StartsWith("Ray", StringComparison.Ordinal));

    return $"{also.Target} is used by {string.Join(", ", weapons.Concat(rest))}. "
        + "Replacing it changes all of them.";
}
