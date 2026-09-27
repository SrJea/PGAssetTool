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
using PGAssetTool.Core.Weapons;

// Names come from twelve localization bundles; the Windows console defaults to a legacy code page
// that mangles all of them.
try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }

var command = args.FirstOrDefault() ?? "help";
var positional = args.Skip(1).TakeWhile(a => !a.StartsWith("--")).ToArray();
string? Option(string name) => args.SkipWhile(a => a != "--" + name).Skip(1).FirstOrDefault();

if (command is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        pgassettool <command> [options]

          info                 Show the detected installation and version.
          weapons [<filter>]   List weapons, optionally filtered by name, slug, tag or prefab.
          items [<kind>]       List the game's other kinds — hats, capes, masks, boots, pets,
                               gliders, transports, avatars — or the whole of one of them.
          show <item>          Show one item and everything it references. A weapon takes the
                               in-game number (819), a prefab name (Weapon1257) or a slug; anything
                               else takes the id `items` lists. Note that a weapon's in-game number
                               and its prefab number are different sequences.

          extract <item>       Write out what can be changed of an item: images as PNG, audio as
                               WAV or Ogg, meshes as glTF. With --workspace, also writes a
                               pgmod.json naming every replaceable file.
          pack [<directory>]   Build a .pgmod from a workspace. Only files edited since the
                               extract are included.

          apply <pack>         Install a .pgmod into the game.
          verify               Check every bundle against the hash the game recorded for it.
          consolidate          Report what emptying the downloaded cache would change. --apply
                               removes only the copies that change nothing.
          mods                 List installed mods.
          enable <id>          Turn a mod back on.
          disable <id>         Turn a mod off without uninstalling it.
          remove <id>          Uninstall a mod.

        Options:
          --game <directory>   Use this installation instead of the detected one.
          --language <bundle>  Localization bundle to read names from (default l_en-gb).
          --out <directory>    Where extract writes (default ./workspace).
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
                               actually see on the weapon.
          --skin <id or name>  Also write out one of the weapon's skins: its own materials and
                               textures, and the model it brings if it brings one. Off by default,
                               because a weapon carries up to a dozen and writing all of them would
                               multiply the workspace for the sake of the one being worked on.
                               `show` lists what a weapon has.
          --protect            Sign the built pack with a key kept beside the tool, and keep it from
                               opening as a zip. Whoever alters one afterwards shows up as having
                               done so. It stops a casual look inside and nothing more.
        """);
    return 0;
}

if (command == "pack")
{
    var workspace = positional.FirstOrDefault() ?? Directory.GetCurrentDirectory();
    // Named after the mod, not after the directory it was built in. See PackBuilder.FileNameFor.
    var output = Option("out") ?? PackBuilder.OutputFor(workspace, Workspace.Read(workspace));
    try
    {
        // Signed when the workspace asks for it. The CLI has no settings of its own, so a pack that
        // says nothing is built plain — --protect is how to ask from here.
        var manifest = Workspace.Read(workspace);
        using var signer = (manifest.Protect ?? args.Contains("--protect")) ? PackAuthor.Mine() : null;

        var result = PackBuilder.Build(workspace, output, signer);
        Console.WriteLine($"{result.Path}");
        Console.WriteLine($"  {result.Operations} operations, {result.Bytes:N0} bytes");
        foreach (var operation in PackBuilder.ReadManifest(result.Path).Operations)
            Console.WriteLine($"    {operation.Op}  {operation.Target}  <- {operation.Source}");
        if (result.Unchanged.Count > 0)
            Console.WriteLine($"  {result.Unchanged.Count} unedited files left out.");

        // Said here as well as on apply, because the author is the one who can decide whether
        // reaching the other weapons is what they meant. Building the pack itself reads only the
        // workspace, so an installation the tool cannot find costs this warning and nothing else.
        try
        {
            var installation = Option("game") is { } dir
                ? GameInstallation.Open(dir)
                : GameInstallation.OpenDetected();
            using var opened = new BundleSet(installation);
            var named = GameCatalogs.Load(opened, Option("language") ?? GameCatalogs.DefaultLanguage);

            foreach (var also in SharedInPack(opened, PackBuilder.ReadManifest(result.Path)))
                Console.WriteLine($"  shared: {Describe(also, named)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  (could not check what else uses these: {ex.Message})");
        }

        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

GameInstallation game;
try
{
    var dir = Option("game");
    game = dir is null ? GameInstallation.OpenDetected() : GameInstallation.Open(dir);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

// The game as shipped: show and extract are about the item, not about whatever mod is
// installed over it. Anything that writes or verifies goes to the installation directly.
// Unpacked into memory up to a limit, because reading them from disk a block at a time was most of
// what resolving and extracting cost. See BundleUnpacker.
using var bundles = new BundleSet(game, originals: new ModStore(game).OriginalOf)
{
    UnpackBudget = (int.TryParse(Option("read-memory"), out var megabytes) ? megabytes : 1024) * 1024L * 1024,
};

// Loaded on demand: only the commands that name weapons pay for it.
var weaponNames = new Lazy<GameCatalogs>(
    () => GameCatalogs.Load(bundles, Option("language") ?? GameCatalogs.DefaultLanguage));


if (command == "info")
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
    return 0;
}

if (command == "consolidate")
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

    if (!args.Contains("--apply"))
    {
        Console.WriteLine("\nNothing was changed. Pass --apply to remove the safe ones.");
        Console.WriteLine("Whether the game refills the cache afterwards is not known, so check before relying on it.");
        return 0;
    }

    var removed = CacheConsolidation.Apply(game, plan);
    Console.WriteLine($"\nRemoved {removed.Count}; the ledger now claims {game.Downloaded.Claimed.Count - removed.Count}.");
    return 0;
}

if (command == "verify")
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

if (command is "apply" or "mods" or "enable" or "disable" or "remove")
{
    var store = new ModStore(game);
    var applier = new ModApplier(game, store)
    {
        Force = args.Contains("--force"),
        Rebuild = args.Contains("--rebuild"),
        Packing = args.Contains("--fast") ? BundlePacking.Faster : BundlePacking.Smaller,
        AtOnce = args.Contains("--low-memory") ? 1 : 4,
    };

    if (command == "mods")
    {
        var installed = store.Read();
        if (installed.Count == 0) Console.WriteLine("Nothing installed.");
        foreach (var mod in installed.OrderBy(m => m.InstalledAt))
            Console.WriteLine($"{(mod.Enabled ? "[on ]" : "[off]")} {TextColumn.Pad(mod.Id, 28)} "
                + $"{TextColumn.Pad(mod.Name, 30)} {mod.Version,-8} "
                + $"{mod.TouchedBundles.Count} bundle(s), installed {mod.InstalledAt:yyyy-MM-dd} for {mod.GameVersion}");
        Console.WriteLine($"\nBackups: {store.BackupRoot}");
        return 0;
    }

    var target = positional.FirstOrDefault();
    if (target is null)
    {
        Console.Error.WriteLine($"{command} requires " + (command == "apply" ? "a .pgmod path." : "a mod id."));
        return 2;
    }

    // Everything past here rewrites the game's bundles, and the game holds them open while it runs:
    // a write that lands in the middle of that leaves a half-written file where a bundle was. The
    // manager and the editor have always refused; this had not, and it is the same game.
    if (GameProcess.IsRunning(game))
    {
        Console.Error.WriteLine(
            "The game is running, and rewriting its bundles now can leave a half-written file "
            + "behind. Close it, then run this again.");
        return 1;
    }

    // Said before the game is written to rather than after, and said whichever way it goes: a
    // signature is only worth carrying if somebody hears about it while there is still a decision
    // to make. An altered pack still installs — with --force, so it takes saying so.
    if (command == "apply" && PackFile.Inspect(target) is { } seal && seal.State != SealState.Unsigned)
    {
        Console.WriteLine($"Seal       {seal.Describe}");
        if (seal.Wrong && !args.Contains("--force"))
        {
            Console.Error.WriteLine(
                $"'{Path.GetFileName(target)}' carries a signature it no longer matches, so it has been "
                + "changed since it was built. Pass --force to install it anyway.");
            return 1;
        }
    }

    try
    {
        var version = GameVersion.Of(bundles.Context, game) ?? GameVersion.Unknown;
        var result = command switch
        {
            "apply" => applier.Install(target, version),
            "enable" => applier.SetEnabled(target, true),
            "disable" => applier.SetEnabled(target, false),
            "remove" => applier.Remove(target),
            _ => throw new UnreachableException(),
        };

        foreach (var operation in result.Applied)
            Console.WriteLine($"  {operation.Mod}  {operation.Op}  {operation.Target}  "
                + $"[{operation.Detail}{(operation.ResolvedByPathId ? "" : ", matched by name")}]");
        foreach (var moved in result.Moved) Console.WriteLine($"  followed: {moved}");
        if (result.Restored.Count > 0)
            Console.WriteLine($"  restored from backup: {string.Join(", ", result.Restored)}");
        foreach (var also in result.Shared)
            Console.WriteLine($"  shared: {Describe(also, weaponNames.Value)}");
        if (result.PrunedBackups.Count > 0)
            Console.WriteLine($"  removed stale backups: {string.Join(", ", result.PrunedBackups)}");
        foreach (var failure in result.Failed) Console.Error.WriteLine($"  FAILED {failure}");

        if (result.Unchanged.Count > 0)
            Console.WriteLine($"  already holding what they should: {string.Join(", ", result.Unchanged)}");

        Console.WriteLine($"\n{result.Applied.Count} operation(s) applied, {result.Failed.Count} failed"
            + (result.Unchanged.Count > 0 ? $", {result.Unchanged.Count} bundle(s) left alone." : "."));
        return result.Failed.Count > 0 ? 1 : 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

if (command is not ("weapons" or "items" or "show" or "extract"))
{
    Console.Error.WriteLine($"Unknown command '{command}'. Try --help.");
    return 2;
}

var timer = Stopwatch.StartNew();
var catalogs = weaponNames.Value;
var catalogTime = timer.ElapsedMilliseconds;

if (command == "items")
{
    var wanted = positional.FirstOrDefault();

    foreach (var kind in catalogs.Kinds)
    {
        if (wanted is { Length: > 0 }
            && !string.Equals(kind.Name, wanted, StringComparison.OrdinalIgnoreCase)) continue;

        var of = catalogs.Of(kind);
        Console.WriteLine($"{kind.Name} ({of.Count})");

        // The whole of a kind when it was asked for by name, and a taste of each otherwise: there
        // are 596 avatars and nobody typing `items` wanted all of them.
        foreach (var item in wanted is { Length: > 0 } ? of : of.Take(5))
            Console.WriteLine($"    {TextColumn.Pad(item.Slug, 42)} "
                + $"{catalogs.Localization.Translate(item.LocalizationKey) ?? item.Tag}");

        if (wanted is null or "" && of.Count > 5) Console.WriteLine($"    … {of.Count - 5} more");
    }

    return 0;
}

if (command == "weapons")
{
    var filter = positional.FirstOrDefault();
    var matches = (filter is null ? catalogs.Items.Weapons : catalogs.Items.Search(filter, catalogs.Names)).ToList();
    foreach (var w in matches)
        Console.WriteLine($"{w.GameNumber,5}  "
            + $"{TextColumn.Pad(catalogs.Localization.Translate(w.LocalizationKey) ?? w.Slug, 34)} "
            + $"{TextColumn.Pad(w.Slug, 34)} {TextColumn.Pad(w.PrefabName, 12)}"
            + $"{(w.IsHidden ? "  (hidden)" : "")}");
    Console.Error.WriteLine($"\n{matches.Count} of {catalogs.Items.Count} weapons  (catalogs {catalogTime}ms)");
    return 0;
}

var query = positional.FirstOrDefault();
if (query is null)
{
    Console.Error.WriteLine("show requires a weapon number, prefab name or slug.");
    return 2;
}

var record = catalogs.Find(query);
if (record is null)
{
    Console.Error.WriteLine($"Nothing matches '{query}'.");
    return 1;
}

var tree = new WeaponResolver(bundles, catalogs).Resolve(record);

if (command == "extract")
{
    var outputRoot = Option("out") ?? Path.Combine(Directory.GetCurrentDirectory(), "workspace");
    var exporter = new WeaponExporter(bundles)
    {
        Opaque = args.Contains("--opaque"),
        Skin = Option("skin"),
        MaskUnused = !args.Contains("--whole"),
    };
    var asWorkspace = args.Contains("--workspace");
    WeaponExport export;
    try
    {
        export = asWorkspace
            ? exporter.ExportAsWorkspace(tree, outputRoot,
                Option("author") ?? "",
                GameVersion.Of(bundles.Context, game))
            : exporter.Export(tree, outputRoot);
    }
    catch (InvalidOperationException refused)
    {
        // A bundle something else modded, which this installation has no original of. See
        // WeaponExporter.Refuse.
        Console.Error.WriteLine(refused.Message);
        return 1;
    }

    Console.WriteLine($"#{record.GameNumber}  {tree.DisplayName}");
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
    if (export.Skipped.Count > 0)
    {
        Console.WriteLine($"\n  Skipped ({export.Skipped.Count})");
        foreach (var reason in export.Skipped.Take(10)) Console.WriteLine($"    {reason}");
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
    Console.Error.WriteLine($"\n{export.Assets.Count} files, total {timer.ElapsedMilliseconds}ms");
    return 0;
}

Console.WriteLine($"#{record.GameNumber}  {tree.DisplayName}{(record.IsHidden ? "   (hidden: no localization key, absent from the in-game list)" : "")}");
Console.WriteLine($"  prefab {record.PrefabName}  index {record.Index}  slug {record.Slug}  tag {record.Tag}");
Console.WriteLine();
if (tree.Icon is not null)
    Console.WriteLine($"  Icon     {tree.Icon.TextureName} @ {tree.Icon.Container}");
Console.WriteLine($"  Prefab   Weapons/{record.PrefabName} @ {tree.PrefabBundle ?? "?"}");
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

Console.WriteLine();
Console.WriteLine($"  Skins ({tree.Skins.Count})");
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

/// The replaceable objects in a pack that more than one weapon reaches.
///
/// Each bundle is indexed once, so a pack touching several costs one pass over each.
/// One of the weapon's own meshes, decoded, so `show` can say what it is made of.
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
    catch (Exception e) when (e is IOException or FileNotFoundException) { return null; }
}

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
