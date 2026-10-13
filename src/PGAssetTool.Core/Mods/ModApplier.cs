using System.IO.Compression;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;
using PGAssetTool.Core.Game;
using PGAssetTool.Core.Import.Audio;
using PGAssetTool.Core.Import.Meshes;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.Mods;

public sealed record AppliedOperation(string Mod, string Op, AssetAddress Target, string Detail, bool ResolvedByPathId);

public sealed record ReconcileResult(
    IReadOnlyList<string> Restored,
    IReadOnlyList<AppliedOperation> Applied,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> PrunedBackups,
    IReadOnlyList<SharedAsset> Shared,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<string> Moved);

/// An operation whose asset was not in the bundle it was sent to, with the name the pack knows it by.
internal sealed record Misplaced(string ModId, PackOperation Operation, string Key);

/// Writes the enabled mods into the game's bundles.
///
/// Every change goes through a full reconcile: each affected bundle is restored from its backup and
/// the enabled mods are then applied in the order they were installed. Toggling one mod off by
/// restoring its bundles would also undo any other mod sharing them, and reapplying on top of
/// already-modified bundles would stack changes; rebuilding from the originals avoids both.
public sealed class ModApplier(GameInstallation game, ModStore store)
{
    /// Accept a bundle that is already modified as the baseline for its backup.
    public bool Force { get; init; }

    /// Size or speed, when a bundle is written back. See BundlePacking.
    public BundlePacking Packing { get; init; }

    /// Rebuild every bundle, whatever it already holds.
    ///
    /// A reconcile leaves alone any bundle that already holds exactly what this run would put into
    /// it, which is what makes turning one mod off cost one bundle instead of seventeen. Reapplying
    /// everything is the one request where that is the wrong answer: it is what somebody reaches for
    /// when they believe the game is not in the state the tool thinks it is.
    public bool Rebuild { get; init; }

    /// What a bundle being rebuilt is called until it is finished.
    private const string Unfinished = ".pgnew";

    private static void Discard(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public ReconcileResult Install(string packPath, string gameVersion)
        => Install([packPath], gameVersion);

    /// Installs several packs and rebuilds the game once, rather than once per pack.
    ///
    /// A reconcile restores every modified bundle and reapplies everything enabled, so running it
    /// per pack does the same whole-game work as many times as there are packs — and leaves the
    /// game briefly in a state that has some of them but not the rest.
    public ReconcileResult Install(IReadOnlyList<string> packPaths, string gameVersion)
    {
        var mods = store.Read();
        var displaced = new List<string>();

        // Each pack claims its assets as it goes in, so a later one in the same batch displaces an
        // earlier one the same way it displaces something installed last week.
        //
        // Installing is how a mod usually comes to be on, and it was the one route that skipped
        // this: turning a mod on stood the others down, and installing one enrolled it enabled and
        // said nothing. Two skins for #401 arrived both on, both writing the same texture, and only
        // one of them was in the game.
        var enrolled = packPaths.Select(p => Enrol(mods, p, gameVersion)).ToList();
        displaced.AddRange(StandAside(mods, enrolled));

        Displaced = displaced.Distinct().ToList();
        store.Write(mods);
        return Reconcile();
    }

    /// Puts a pack in the ledger, enabled, and answers its id.
    private string Enrol(List<InstalledMod> mods, string packPath, string gameVersion)
    {
        var manifest = PackBuilder.ReadManifest(packPath);
        mods.RemoveAll(m => m.Id == manifest.Id);
        mods.Add(new InstalledMod
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Author = manifest.Author,
            Version = manifest.Version,
            PackPath = store.Keep(packPath, manifest.Subject),
            InstalledAt = DateTimeOffset.Now,
            GameVersion = gameVersion,
            Enabled = true,
            Subject = manifest.Subject,
            TouchedBundles = manifest.Operations
                .Select(o => o.Target.Container)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(c => c, _ => "", StringComparer.OrdinalIgnoreCase),
        });

        return manifest.Id;
    }

    public ReconcileResult SetEnabled(string id, bool enabled) => SetEnabled([id], enabled);

    public ReconcileResult SetEnabled(IReadOnlyList<string> ids, bool enabled)
    {
        var mods = store.Read();
        foreach (var id in ids)
        {
            var index = mods.FindIndex(m => m.Id == id);
            if (index < 0) throw new KeyNotFoundException($"No mod with id '{id}' is installed.");
            mods[index] = mods[index] with { Enabled = enabled };
        }

        if (enabled) Displaced = StandAside(mods, ids);

        store.Write(mods);
        return Reconcile();
    }

    /// What the last turning-on turned off, so the caller can say so. Names, not ids.
    public IReadOnlyList<string> Displaced { get; private set; } = [];

    /// Turns off whatever else was writing the same assets as the mods just turned on.
    ///
    /// Two mods that write the same texture do not both take effect; the reconcile applies them in
    /// the order they were installed and the last one wins, silently. Which of two skins for one
    /// weapon a player is actually running was then a question the manager could not answer, and
    /// turning one on appeared to do nothing at all.
    ///
    /// Judged on the assets themselves rather than on the item they belong to. Two mods for one
    /// weapon that touch nothing in common — a new model and a new shop icon, say — are a
    /// combination worth having, and the whole point of this is that the ones which cannot stand
    /// together are exactly the ones that write over each other.
    ///
    /// Read out of the packs at the moment it is asked rather than kept in the ledger. It is a
    /// handful of small files, read once per turning-on, against a reconcile that rewrites every
    /// bundle the mods touch — and reading them is what makes the answer true for a mod installed
    /// before any of this existed.
    ///
    /// The mods being turned on are held to this as well as everyone else, which is the half that
    /// was missing: select every row and turn them on, and thirty-nine mods all went on together,
    /// rivals included. Whoever came last wins, because that is the order a reconcile applies them
    /// in — so the answer here and the file that ends up in the game agree.
    private List<string> StandAside(List<InstalledMod> mods, IReadOnlyList<string> ids)
    {
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var displaced = new List<string>();
        var writes = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        List<string> WrittenBy(InstalledMod mod)
        {
            if (writes.TryGetValue(mod.Id, out var known)) return known;
            return writes[mod.Id] = Writes(mod).ToList();
        }

        // Latest first: the last one applied is the one actually in the game, so it is the one that
        // keeps its claim and the earlier ones stand down.
        var turningOn = ids
            .Select(id => mods.FindIndex(m => m.Id == id))
            .Where(at => at >= 0)
            .OrderByDescending(at => mods[at].InstalledAt)
            .ThenByDescending(at => at)
            .ToList();

        foreach (var at in turningOn)
        {
            var mine = WrittenBy(mods[at]);
            if (mine.Any(claimed.Contains))
            {
                displaced.Add(mods[at].Name);
                mods[at] = mods[at] with { Enabled = false };
                continue;
            }

            foreach (var asset in mine) claimed.Add(asset);
        }

        if (claimed.Count == 0) return displaced;

        for (var i = 0; i < mods.Count; i++)
        {
            if (!mods[i].Enabled || ids.Contains(mods[i].Id)) continue;
            if (!WrittenBy(mods[i]).Any(claimed.Contains)) continue;

            displaced.Add(mods[i].Name);
            mods[i] = mods[i] with { Enabled = false };
        }

        return displaced;
    }

    /// Every asset a mod writes, as a name that means the same thing in two different packs.
    private static IEnumerable<string> Writes(InstalledMod? mod)
    {
        if (mod is null || !File.Exists(mod.PackPath)) return [];

        try
        {
            // Where each asset was last found, which is where the mod actually writes it: a pack made
            // before an update and one made after it write the same asset under two bundle names.
            return PackBuilder.ReadManifest(mod.PackPath).Operations
                .Select(o => Relocation.Follow(o, mod.Moved).Target)
                .Select(t => $"{t.Container}:{t.Class}:{t.Name}:{t.PathId}")
                .ToList();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            // A pack that will not open cannot be shown to overlap with anything, and refusing to
            // turn a mod on because another one is unreadable would be the wrong way round.
            return [];
        }
    }

    public ReconcileResult Remove(string id) => Remove([id]);

    public ReconcileResult Remove(IReadOnlyList<string> ids)
    {
        var mods = store.Read();
        foreach (var id in ids)
            if (mods.RemoveAll(m => m.Id == id) == 0)
                throw new KeyNotFoundException($"No mod with id '{id}' is installed.");
        store.Write(mods);
        return Reconcile();
    }

    /// Rebuilds every affected bundle from its backup, then applies the enabled mods in order.
    /// Also runs after a game update, which is when backups for superseded bundle versions go stale.
    ///
    /// Bundles already holding exactly what this run would put into them are left where they are.
    /// The restoring and reapplying is the same work whether one mod changed or none did, so a
    /// toggle used to rebuild every bundle any mod anywhere had touched: seventeen of them, to
    /// change one.
    ///
    /// An operation whose asset is not where the pack says is looked for in the rest of the item's
    /// bundles, and if it is found there the whole thing runs once more with that written down. See
    /// Relocation. A second run rather than a patch to the first, because the first has already put
    /// every bundle in order without it — and the order mods are applied in, what each one touched and
    /// what each bundle was built from are all worked out in one place, which is here.
    public ReconcileResult Reconcile()
    {
        var hashes = game.ReadManifest().ToDictionary(e => e.Name, e => e.Hash, StringComparer.OrdinalIgnoreCase);
        var (first, lost) = Pass(hashes);
        if (lost.Count == 0) return first;

        var moved = Relocate(lost, hashes);
        if (moved.Count == 0) return first with { Failed = [.. first.Failed, .. NotFound(lost)] };

        var (second, still) = Pass(hashes);
        return second with
        {
            Restored = [.. first.Restored.Union(second.Restored, StringComparer.OrdinalIgnoreCase)],
            PrunedBackups = [.. first.PrunedBackups, .. second.PrunedBackups],
            Failed = [.. second.Failed, .. NotFound(still)],
            Moved = moved,
        };
    }

    private static IEnumerable<string> NotFound(IEnumerable<Misplaced> lost)
        => lost.Select(l => $"{l.ModId}: {l.Operation.Target} not found");

    /// Restores and reapplies once, and answers what could not be found where it was sent.
    private (ReconcileResult Result, List<Misplaced> Lost) Pass(IReadOnlyDictionary<string, string> hashes)
    {
        var mods = store.Read();
        var restored = new List<string>();
        var applied = new List<AppliedOperation>();
        var failed = new List<string>();
        var shared = new List<SharedAsset>();
        var unchanged = new List<string>();
        var lost = new List<Misplaced>();
        var touchedByMod = new Dictionary<string, Dictionary<string, string>>();

        var archives = new List<ZipArchive>();
        var staging = Directory.CreateTempSubdirectory("pgassettool-apply");

        try
        {
            var byBundle = Gather(mods, archives, touchedByMod, failed);
            var recipes = byBundle.ToDictionary(b => b.Key, b => RecipeFor(b.Value),
                StringComparer.OrdinalIgnoreCase);

            // What the last run left in each bundle. Missing, unreadable or turned down by Rebuild
            // all mean the same thing: assume nothing, and do the lot.
            var written = Rebuild ? [] : store.ReadWritten();

            var settled = PutBack(recipes, written, restored);

            ApplyAll(byBundle, settled, recipes, written, hashes, touchedByMod,
                applied, failed, shared, unchanged, lost, staging.FullName);

            store.WriteWritten(written);
        }
        finally
        {
            foreach (var archive in archives) archive.Dispose();

            // Tidying up is not worth losing the reason for. This is a temporary directory, and
            // something holding one of its files open — a virus scanner reading what was just
            // written is the ordinary case — used to throw from inside the finally, which both
            // ended a successful apply as a failure and threw away whatever the real trouble had
            // been when there was one.
            try { staging.Delete(recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Settings.ErrorLog.Record(e, $"clearing '{staging.FullName}'");
            }
        }

        // Record which bundle version each mod actually wrote to, so a later update is detectable.
        store.Write(mods.Select(m => touchedByMod.TryGetValue(m.Id, out var touched)
            ? m with { TouchedBundles = touched }
            : m));

        var pruned = store.PruneStaleBackups(hashes);
        return (new ReconcileResult(restored, applied, failed, pruned, shared, unchanged, []), lost);
    }

    /// Looks for every asset that was not where it was sent, and writes down where each one is.
    ///
    /// Answers what was found somewhere new, as sentences. The reading is done with a reader of its
    /// own, closed before this returns: the second pass writes to the very bundles it opened.
    private List<string> Relocate(IReadOnlyList<Misplaced> lost, IReadOnlyDictionary<string, string> hashes)
    {
        var mods = store.Read();
        var said = new List<string>();
        var changed = false;

        BundleSet? reading = null;
        GameCatalogs? catalogs = null;

        try
        {
            foreach (var group in lost.GroupBy(l => l.ModId))
            {
                var at = mods.FindIndex(m => m.Id == group.Key);
                if (at < 0) continue;

                var mod = mods[at];
                var moved = new Dictionary<string, Relocated>(mod.Moved ?? [], StringComparer.Ordinal);
                IReadOnlyList<string>? candidates = null;

                foreach (var missing in group)
                {
                    var from = missing.Operation.Target.Container;
                    var hash = hashes.GetValueOrDefault(from, "");

                    // Already looked for, found nowhere, and nothing about where it was sent has
                    // changed since. Reapplying everything is the one request that looks again.
                    if (!Rebuild && moved.TryGetValue(missing.Key, out var before)
                        && before.Bundle is null && before.Hash == hash) continue;

                    string? found = null;
                    if (mod.Subject is { IsKnown: true } subject)
                    {
                        try
                        {
                            reading ??= new BundleSet(game, originals: store.OriginalOf);
                            catalogs ??= GameCatalogs.Load(reading);
                            candidates ??= Relocation.Candidates(reading, catalogs, subject);
                            found = Relocation.Find(reading, candidates, missing.Operation.Target, from);
                        }
                        catch (Exception e) when (e is IOException or KeyNotFoundException or InvalidDataException)
                        {
                            // Not being able to look is not the same as having looked, so nothing is
                            // written down and the next reconcile tries again.
                            continue;
                        }
                    }

                    moved[missing.Key] = new Relocated(found, hash);
                    changed = true;

                    if (found is not null)
                        said.Add($"{mod.Id}: {missing.Operation.Target.Class} '{missing.Operation.Target.Name}' "
                            + $"is in {found} now, not {from}");
                }

                mods[at] = mod with { Moved = moved.Count > 0 ? moved : null };
            }
        }
        finally
        {
            reading?.Dispose();
        }

        if (changed) store.Write(mods);
        return said;
    }

    /// Puts back every bundle that is not already what it should be, and answers which ones are.
    ///
    /// Every backup there is, not what the installed mods happen to name: uninstalling drops the mod
    /// before its bundles are put back, so by the time this runs the list no longer mentions them
    /// and the modified bundle would be left in place.
    ///
    /// The file is hashed once and the answer used twice. A bundle that already matches what the
    /// game shipped needs nothing doing — a backup outlives the mod that caused it, so this used to
    /// copy every bundle the tool had ever touched over an identical copy of itself. And a bundle
    /// that matches what this tool last wrote into it, from the same mods in the same order, needs
    /// nothing doing either: putting it back and building it again would arrive at the file already
    /// there.
    ///
    /// Both halves of that have to hold. The recipe says the answer would be the same; the hash says
    /// nobody has been at the file since. Anything else — a game update, an edit from outside, a
    /// record from a run that did not finish — falls through to the slow, certain path.
    private HashSet<string> PutBack(
        IReadOnlyDictionary<string, string> recipes,
        Dictionary<string, WrittenBundle> written,
        List<string> restored)
    {
        var settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (cache, bundle, hash) in store.BackedUp())
        {
            var live = LivePathIn(cache, bundle, hash);
            if (live is null || !File.Exists(live)) continue;

            // A rebuild that never got as far as its rename, from a run that died part way. It is
            // no use to anyone and the game's own folder is no place to leave one lying.
            Discard(live + Unfinished);

            var holds = BundleIntegrity.Md5(live);
            if (string.Equals(holds, hash, StringComparison.OrdinalIgnoreCase)
                || string.Equals(holds, store.BackupMd5(cache, bundle, hash), StringComparison.OrdinalIgnoreCase))
            {
                written.Remove(bundle);
                continue;
            }

            if (written.TryGetValue(bundle, out var before)
                && recipes.TryGetValue(bundle, out var recipe)
                && before.Recipe == recipe
                && string.Equals(before.Hash, holds, StringComparison.OrdinalIgnoreCase)
                // Only the copy the game would actually load. A bundle present in both caches has
                // one record between them, and the copy nothing loads still has to be put back.
                && game.Resolve(bundle, hash) is { } resolved
                && resolved.Cache == cache)
            {
                settled.Add(bundle);
                continue;
            }

            if (store.RestoreIfBackedUp(cache, bundle, hash, live))
                restored.Add($"{bundle} ({cache.ToString().ToLowerInvariant()})");

            written.Remove(bundle);
        }

        return settled;
    }

    /// What one bundle is to be built from, as a line that changes whenever the answer would.
    ///
    /// The mods in the order they will be applied, each with the whole content of the pack it comes
    /// out of. The pack rather than its manifest, because an author can rebuild a pack under the
    /// same id with a different picture in it; all of them together are a megabyte, and hashing that
    /// is nothing beside rebuilding a bundle that did not need it.
    ///
    /// And how it is to be squeezed. The same mods packed for speed and packed for size are two
    /// different files, so a bundle written one way is not already what a run asked for the other way
    /// would write. Left out, a bundle applied from the window with speed on was left alone by the
    /// next reconcile asked for size, and rebuilding everything then arrived at different bytes —
    /// which the self-test caught the first time a mod of the author's was enabled while it ran.
    private string RecipeFor(IReadOnlyList<Contribution> parts)
    {
        var recipe = new System.Text.StringBuilder();
        recipe.Append("packed for ").Append(Packing).Append('\n');

        foreach (var part in parts)
        {
            recipe.Append(part.Mod.Id).Append(' ');

            try
            {
                using var pack = File.OpenRead(part.Mod.PackPath);
                recipe.Append(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(pack)));
            }
            catch (IOException)
            {
                // Unreadable now is a reason to rebuild, not a reason to claim it has not changed.
                recipe.Append(Guid.NewGuid().ToString("n"));
            }

            // Which of the pack's operations land here. The same for every run of an unchanged pack,
            // until an asset is followed out of one bundle and into another — and then both of those
            // bundles are to be built from something new.
            recipe.Append(' ').Append(string.Join('|', part.Keys));

            recipe.Append('\n');
        }

        return recipe.ToString();
    }

    /// Where a backup taken from a given cache belongs, which is not necessarily the copy the game
    /// now loads: once the downloaded cache claims a bundle it wins, and the shipped copy beneath it
    /// still has to be put back.
    private string? LivePathIn(CacheKind cache, string bundle, string hash) => cache switch
    {
        CacheKind.Downloaded => game.Downloaded?.ClaimedPath(bundle),
        _ => Path.Combine(game.BundlesDirectory, bundle, hash, bundle),
    };

    /// One mod's operations for one bundle, with the pack they came out of held open.
    ///
    /// `Keys` runs beside `Operations`, one for one: what the pack itself calls each target. An
    /// operation here has already been sent wherever its asset was last found, so its own target no
    /// longer says what the pack named, and that is what a relocation is remembered by.
    private sealed record Contribution(
        InstalledMod Mod, ZipArchive Archive, List<PackOperation> Operations, List<string> Keys);

    /// Sorts every enabled mod's operations into the bundles they are for, in install order.
    ///
    /// Install order is what decides who wins where two mods write the same asset, and this is where
    /// it is fixed: the contributions are gathered in it and applied in it within each bundle.
    private Dictionary<string, List<Contribution>> Gather(
        List<InstalledMod> mods, List<ZipArchive> archives,
        Dictionary<string, Dictionary<string, string>> touchedByMod, List<string> failed)
    {
        var byBundle = new Dictionary<string, List<Contribution>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods.Where(m => m.Enabled).OrderBy(m => m.InstalledAt))
        {
            touchedByMod[mod.Id] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(mod.PackPath))
            {
                failed.Add($"{mod.Id}: pack file is gone ({mod.PackPath})");
                continue;
            }

            // Held open for the whole run rather than reopened at each bundle. A pack is a small
            // file read whole into memory, and a mod that writes to four bundles would otherwise
            // be read four times.
            //
            // A pack that will not open is this mod's failure and not the run's. It used to come
            // back out of here and end the reconcile before anything had been looked at, so one
            // damaged file among a hundred meant nothing in the manager worked at all — and the
            // way to find out which file it was was to read the message and go looking.
            ZipArchive archive;
            List<PackOperation> operations;
            try
            {
                archive = PackFile.Open(mod.PackPath);
                archives.Add(archive);
                operations = PackBuilder.ReadManifest(mod.PackPath).Operations;
            }
            catch (Exception e) when (e is IOException or InvalidDataException
                                          or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                failed.Add($"{mod.Id}: {e.Message}");
                continue;
            }

            // Each to wherever its asset was last found, which for nearly every operation is where
            // the pack says.
            foreach (var group in operations
                         .Select(o => (Key: Relocation.Key(o.Target), Operation: Relocation.Follow(o, mod.Moved)))
                         .GroupBy(o => o.Operation.Target.Container, StringComparer.OrdinalIgnoreCase))
            {
                if (!byBundle.TryGetValue(group.Key, out var parts))
                    byBundle[group.Key] = parts = [];

                parts.Add(new Contribution(mod, archive,
                    [.. group.Select(o => o.Operation)], [.. group.Select(o => o.Key)]));
            }
        }

        return byBundle;
    }

    /// One bundle to rebuild: where it is, who writes to it, and where its pack contents are put.
    private sealed record Job(
        string Bundle, string Hash, string Live, long Size,
        IReadOnlyList<Contribution> Parts, string Staging);

    /// What came of rebuilding one, kept apart until every job is done so the reporting stays in
    /// one order however the work was shared out.
    private sealed record Outcome(
        Job Job, string Holds, List<string> Wrote,
        List<AppliedOperation> Applied, List<string> Failed, List<SharedAsset> Shared, List<Misplaced> Lost);

    /// How many bundles are rebuilt at once.
    ///
    /// Each one holds its whole decompressed self in memory while it is worked on, and the largest
    /// in this game is 216MB unpacked, so this is a limit on memory rather than on cores — four of
    /// them at once is about a gigabyte at the worst moment, and the work is nearly all compression,
    /// which is already spread across every core inside each bundle.
    ///
    /// One at a time is offered for a machine short of memory. Measured on the eight largest bundles
    /// in the game rebuilt together: 2.9 seconds and a 1.25GB peak four at a time, 3.9 seconds and
    /// 800MB one at a time.
    public int AtOnce { get; init; } = 4;

    /// Writes every enabled mod into the game, a bundle at a time, several bundles at once.
    ///
    /// A bundle at a time and not a mod at a time, which is how this used to work. Each pass over a
    /// bundle decompresses it, edits it and compresses it again, and going mod by mod meant one such
    /// pass per mod — so a bundle that thirty mods write to was rebuilt thirty times over. The work
    /// was never in the edits: with thirty-one mods installed a toggle took thirty-eight seconds, of
    /// which thirty-seven were this, for forty operations in total.
    ///
    /// The bundles themselves have nothing to do with each other — different files, different
    /// backups, different packs — so they are rebuilt side by side, largest first so the longest job
    /// is not the one left at the end. What each mod touched is still recorded per mod, because that
    /// is what tells a later game update which mods need reapplying.
    private void ApplyAll(
        Dictionary<string, List<Contribution>> byBundle, HashSet<string> settled,
        IReadOnlyDictionary<string, string> recipes, Dictionary<string, WrittenBundle> written,
        IReadOnlyDictionary<string, string> hashes,
        Dictionary<string, Dictionary<string, string>> touchedByMod,
        List<AppliedOperation> applied, List<string> failed, List<SharedAsset> shared,
        List<string> unchanged, List<Misplaced> lost, string staging)
    {
        var jobs = new List<Job>();

        foreach (var (bundle, parts) in byBundle)
        {
            if (!hashes.TryGetValue(bundle, out var hash))
            {
                // A bundle an update did away with is the plainest case of an asset having moved, so
                // everything for it is looked for elsewhere.
                foreach (var part in parts)
                    for (var at = 0; at < part.Operations.Count; at++)
                        lost.Add(new Misplaced(part.Mod.Id, part.Operations[at], part.Keys[at]));
                continue;
            }

            // Only the copy the game would actually load is worth writing to. Editing the shipped
            // copy of a bundle the downloaded cache claims changes nothing and says nothing.
            if (game.Resolve(bundle, hash) is not { } resolved)
            {
                foreach (var part in parts)
                    failed.Add($"{part.Mod.Id}: no copy of '{bundle}' is present in any cache");
                continue;
            }
            var (cache, live) = (resolved.Cache, resolved.Path);

            // Already holding exactly this. The mods still have to be told they are in it, because
            // that record is what a game update is noticed against.
            if (settled.Contains(bundle))
            {
                unchanged.Add(bundle);
                foreach (var part in parts) touchedByMod[part.Mod.Id][bundle] = hash;
                continue;
            }

            // Backing up a bundle something else already edited would record that edit as the
            // original, leaving no way back to the shipped file.
            if (!store.HasBackup(cache, bundle, hash) && !BundleIntegrity.IsPristine(live, hash) && !Force)
            {
                foreach (var part in parts)
                    failed.Add($"{part.Mod.Id}: '{bundle}' in the {cache.ToString().ToLowerInvariant()} cache "
                        + "has already been modified by something else and there is no backup of it. "
                        + "Verify the game's files through its store to put it back. Installing from "
                        + "the command line with --force instead accepts what is in that bundle now "
                        + "as the original, which is what every uninstall here will then restore.");
                continue;
            }
            store.Backup(cache, bundle, hash, live);

            jobs.Add(new Job(bundle, hash, live, new FileInfo(live).Length, parts,
                Path.Combine(staging, jobs.Count.ToString())));
        }

        // Largest first, and each with its own corner of the staging directory: two mods can carry
        // a file of the same name, and the name is all that reaches disk.
        jobs = [.. jobs.OrderByDescending(j => j.Size)];

        var outcomes = new Outcome[jobs.Count];
        Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = AtOnce },
            at => outcomes[at] = Rebuilt(jobs[at]));

        foreach (var outcome in outcomes)
        {
            applied.AddRange(outcome.Applied);
            failed.AddRange(outcome.Failed);
            shared.AddRange(outcome.Shared);
            lost.AddRange(outcome.Lost);

            var bundle = outcome.Job.Bundle;
            foreach (var id in outcome.Wrote) touchedByMod[id][bundle] = outcome.Job.Hash;

            // Recorded as built only when everything meant for it went in. The recipe is what was
            // asked for, not what came out, so a bundle where one operation failed was written down
            // as holding that operation too — and once whatever stopped it was put right, every
            // later reconcile left the bundle alone as already correct. A glider's mesh that the
            // importer could not read stayed out of the game after the importer learned to.
            if (outcome.Wrote.Count > 0 && outcome.Failed.Count == 0)
                written[bundle] = new WrittenBundle(recipes[bundle], outcome.Holds);
            else written.Remove(bundle);
        }
    }

    /// One bundle, rebuilt and put in place, with nothing shared with whatever else is running.
    private Outcome Rebuilt(Job job)
    {
        var applied = new List<AppliedOperation>();
        var failed = new List<string>();
        var shared = new List<SharedAsset>();
        var lost = new List<Misplaced>();

        // Written beside the file it replaces rather than into the temporary directory, because the
        // last step is then a rename instead of a copy. The two are rarely on the same drive — the
        // game on one, the system's temporary directory on another — and a rebuild of every bundle
        // is a couple of hundred megabytes to carry across.
        //
        // Nothing but this ever looks at the half-written name, and a run that dies before the
        // rename leaves it for the next one to clear out. The game is not running while any of this
        // happens; that is checked before an apply starts.
        var rewritten = job.Live + Unfinished;
        List<string> wrote;

        // One bundle going wrong is that bundle's failure, not the whole install's. A disk that
        // fills up, a file the system has locked, a library that throws at something it has never
        // seen — any of those used to come back out of the parallel loop and end the reconcile
        // where it stood, with the bundles already renamed into place and nothing written down
        // about them. What is left behind here instead is the original: the rename is the last
        // step, and the half-written file is thrown away.
        try
        {
            wrote = EditBundle(job.Live, rewritten, job.Parts, job.Staging, applied, failed, shared, lost);
        }
        catch (Exception e)
        {
            Settings.ErrorLog.Record(e, $"rebuilding '{job.Bundle}'");
            Discard(rewritten);
            failed.Add($"'{job.Bundle}' could not be rebuilt and has been left as it was: {e.Message}");
            return new Outcome(job, "", [], applied, failed, shared, lost);
        }

        if (wrote.Count == 0)
        {
            Discard(rewritten);
            return new Outcome(job, "", wrote, applied, failed, shared, lost);
        }

        try
        {
            File.Move(rewritten, job.Live, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Settings.ErrorLog.Record(e, $"putting the rebuilt '{job.Bundle}' in place");
            Discard(rewritten);
            failed.Add($"'{job.Bundle}' was rebuilt but could not be put in place, and has been "
                + $"left as it was: {e.Message}");
            return new Outcome(job, "", [], applied, failed, shared, lost);
        }

        // Hashed as it goes in, so the next reconcile can tell this bundle apart from one somebody
        // has edited since. The file was written a moment ago and is still in the system's cache.
        return new Outcome(job, BundleIntegrity.Md5(job.Live), wrote, applied, failed, shared, lost);
    }

    /// The texture as the game holds it, decoded, so a replacement that arrives without an alpha
    /// channel can be given the original one back.
    ///
    /// Nearly every texture keeps its pixels in a sibling stream rather than on the object, so this
    /// has to go and fetch them. Failing is not worth stopping an apply over: all that is lost is
    /// the chance to reuse an alpha channel.
    private static byte[]? OriginalPixels(BundleEditor editor, AssetTypeValueField field)
    {
        try
        {
            var texture = AssetsTools.NET.Texture.TextureFile.ReadTextureFile(field);
            var payload = texture.pictureData;

            var stream = field["m_StreamData"];
            if (!stream.IsDummy && stream["path"].AsString.Length > 0)
                payload = editor.ReadStream(
                    stream["path"].AsString, stream["offset"].AsLong, stream["size"].AsLong);

            return payload is { Length: > 0 } ? texture.DecodeTextureRaw(payload, useBgra: true) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// Whether an operation names a class this tool will not write, from what the pack says about
    /// it rather than from the asset — the target is refused before anything is opened.
    private static bool Refuses(PackOperation operation, out string why)
    {
        why = "";
        if (!Enum.TryParse<AssetClassID>(operation.Target.Class, out var cls)) return false;
        if (Replaceable.Supports(cls)) return false;

        // The file it would have been written from, when the asset has no name of its own — which
        // components generally do not, so the target's name would be an empty pair of quotes.
        why = Replaceable.WhyRefused(
            cls, operation.Target.Name.Length > 0 ? operation.Target.Name : operation.Source);
        return true;
    }

    /// Takes one file out of a pack and puts it where the importer can read it.
    ///
    /// Locked on the pack: a pack is opened once for the whole run and a mod that writes to four
    /// bundles is read from while four bundles are being rebuilt at once, and a ZipArchive is not a
    /// thing to share. The lock is held for the whole read, because the entry's stream is only good
    /// while nothing else has moved the archive on.
    private static bool Stage(ZipArchive archive, string entryName, string destination)
    {
        lock (archive)
        {
            if (archive.GetEntry(entryName) is not { } entry) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            PackBuilder.WriteEntry(entry, destination, PackBuilder.MostPerFile, $"'{entryName}'");
            return true;
        }
    }

    /// Applies every mod's share of one bundle, in one pass, and answers which of them wrote to it.
    private List<string> EditBundle(
        string live, string output, IReadOnlyList<Contribution> parts,
        string staging, List<AppliedOperation> applied, List<string> failed, List<SharedAsset> shared,
        List<Misplaced> lost)
    {
        using var editor = new BundleEditor(live);
        var index = new ContainerIndex(editor.Context);
        var wrote = new List<string>();

        // Built once for the bundle, and only if something is actually replaced in it. Under half a
        // second on the largest bundle in the game, which an install can afford.
        var usage = new Lazy<BundleUsage>(() => BundleUsage.Build(editor.Context, editor.File));

        foreach (var part in parts)
        {
            var mod = part.Mod;
            var archive = part.Archive;
            var changed = false;

            var ordered = part.Operations;

            for (var at = 0; at < ordered.Count; at++)
            {
                var operation = ordered[at];

                // Asked before the asset is read, and asked here rather than only where packs are
                // built: a pack that arrives from somewhere else has never been past that check.
                if (Refuses(operation, out var why)) { failed.Add($"{mod.Id}: {why}"); continue; }

                // Not here is not yet a failure: the game may have filed it in another bundle, and
                // the reconcile looks there once every bundle has had its turn.
                var info = index.Resolve(operation.Target, editor.File, out var byPathId);
                if (info is null)
                {
                    lost.Add(new Misplaced(mod.Id, operation, part.Keys[at]));
                    continue;
                }

                var field = editor.Read(info);
                if (field is null) { failed.Add($"{mod.Id}: {operation.Target} could not be read"); continue; }

                var source = Path.Combine(staging, Path.GetFileName(operation.Source));
                if (!Stage(archive, operation.Source, source))
                { failed.Add($"{mod.Id}: pack has no '{operation.Source}'"); continue; }

                try
                {
                    object change = operation.Op switch
                    {
                        PackOperations.ReplaceTexture =>
                            TextureImporter.Replace(field, source, OriginalPixels(editor, field), operation.AlphaIsMask),
                        PackOperations.ReplaceMesh =>
                            MeshImporter.Replace(field, GltfMeshReader.Read(source), editor.ReadStream),
                        PackOperations.ReplaceAudio => AudioImporter.Replace(field, source, (into, bank) => editor.AppendToStream(into, bank)),
                        PackOperations.ReplaceAnimation =>
                            Animation.ClipImporter.Replace(field, Animation.ClipFile.Read(source).ToMotion()),
                        _ => throw new NotSupportedException($"unknown operation '{operation.Op}'"),
                    };
                    editor.Stage(info, field);
                    changed = true;
                    if (SharedAssets.Check(usage.Value, operation.Target, info.PathId) is { } also)
                        shared.Add(also);

                    applied.Add(new AppliedOperation(mod.Id, operation.Op, operation.Target,
                        change.ToString()!, byPathId));
                }
                catch (Exception ex)
                {
                    failed.Add($"{mod.Id}: {operation.Target} {ex.Message}");
                }
            }

            if (changed) wrote.Add(mod.Id);
        }

        // Written once, whatever went into it. Saving per mod would put the bundle back through a
        // whole rebuild for each of them, which is the cost this exists to avoid.
        if (wrote.Count > 0) editor.Save(output, Packing);
        return wrote;
    }

}
