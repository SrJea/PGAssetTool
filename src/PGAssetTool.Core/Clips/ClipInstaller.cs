using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Game;
using PGAssetTool.Core.Mods;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Core.Clips;

/// One clip as the reader found it, which is all the writer needs to go back to it.
public sealed record ClipAddress(string Bundle, long PathId, string Name, float Length);

/// What one clip became.
public sealed record ClipRetimed(string Bundle, string Name, float Was, float Now);

/// One clip to be replaced by another weapon's.
/// <param name="Root">
/// What the object carrying the target's own animation is called. A clip names what it moves from
/// that object downwards — `mp5/FPS_PLAYER_Arm_Right/joint_root` — and the first name is the one
/// thing two weapons' rigs never share, so it is the one thing rewritten. Everything below it has
/// to match, and whether it does is the caller's to check and the author's to judge.
/// </param>
public sealed record ClipMove(ClipAddress Into, ClipAddress From, string Root);

/// What one transplant did. `Paths` is every object the clip now drives, for a caller that wants to
/// say which of them the target's own rig has never heard of.
public sealed record ClipMoved(
    string Bundle, string Name, float Was, float Now, string From, IReadOnlyList<string> Paths);

/// Rewrites the timing of animation clips in the game's own bundles, and puts them back.
///
/// **This is a deliberate act and not something a `.pgmod` carries.** A pack carries the three types
/// in `Replaceable`, and those three change what a thing looks and sounds like. A clip's length
/// looks very much like something the game reads as behaviour — across 242 weapons the shots a
/// second its own numbers imply land near one over the Shoot clip's length, median 0.90 — so a
/// shorter clip is plausibly a faster weapon, and that reaches other players. Until somebody has
/// watched the game and settled it, the way to change a length is this: from the command line, on a
/// copy of the game, with the bundle backed up where the mod store keeps every other original.
///
/// It leaves no entry in the ledger, so `pgassettool verify` reports a bundle as modified by
/// something else while a clip is retimed. That is honest — it was. `Restore` puts it back.
///
/// Written against the shipped bundle rather than the one in place, so retiming twice is measured
/// from the game's own timing each time rather than from the last change.
public sealed class ClipInstaller(GameInstallation game, ModStore store)
{
    /// The copy of a bundle the game would actually load, and the hash it is recorded under.
    private (CacheKind Cache, string Path, string Hash) Live(string bundle)
    {
        var entry = game.ReadManifest().FirstOrDefault(e =>
            string.Equals(e.Name, bundle, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"This installation's manifest does not list '{bundle}'.");

        var resolved = game.Resolve(entry.Name, entry.Hash)
            ?? throw new FileNotFoundException($"No copy of '{entry.Name}' is present in any cache.");

        return (resolved.Cache, resolved.Path, entry.Hash);
    }

    /// What these clips last in the bundles the game would actually load, which is not what the
    /// reader answers: the reader is pointed at the originals on purpose, so that showing and
    /// extracting an item are about the item rather than about whatever has been written over it.
    /// For a clip's timing the live file is the question.
    public IReadOnlyList<ClipAddress> Read(IReadOnlyList<ClipAddress> clips)
    {
        var found = new List<ClipAddress>();

        foreach (var group in clips.GroupBy(c => c.Bundle, StringComparer.OrdinalIgnoreCase))
        {
            var (_, path, _) = Live(group.Key);
            using var editor = new BundleEditor(path);

            foreach (var clip in group)
            {
                var info = editor.File.file.GetAssetInfo(clip.PathId);
                var field = info is null ? null : editor.Read(info);
                var motion = field is null ? null : Motion.Read(field);
                found.Add(clip with { Length = motion?.Length ?? clip.Length });
            }
        }

        return found;
    }

    /// Multiplies the length of each of these clips by `factor`, in whichever bundles they live in.
    ///
    /// Grouped by bundle, because one weapon's clips are all in one file and a bundle is rebuilt
    /// whole: six clips must cost one rebuild, not six.
    public IReadOnlyList<ClipRetimed> Stretch(
        IReadOnlyList<ClipAddress> clips, float factor, BundlePacking packing = BundlePacking.Smaller)
    {
        if (factor <= 0 || !float.IsFinite(factor)) throw new ArgumentOutOfRangeException(nameof(factor));
        Refuse();

        var done = new List<ClipRetimed>();

        foreach (var group in clips.GroupBy(c => c.Bundle, StringComparer.OrdinalIgnoreCase))
        {
            var (cache, path, hash) = Live(group.Key);

            // Backing up a bundle something else has already written would record that as the
            // original. A bundle this has retimed before is the one exception: its backup is
            // already the shipped file.
            if (!store.HasBackup(cache, group.Key, hash) && !BundleIntegrity.IsPristine(path, hash))
                throw new InvalidOperationException(
                    $"'{group.Key}' has already been modified by something else and there is no backup "
                    + "of it. Verify the game's files before retiming a clip in it.");

            store.Backup(cache, group.Key, hash, path);
            var original = store.OriginalOf(cache, group.Key, hash) ?? path;

            var written = path + ".pgat-clips";
            using (var editor = new BundleEditor(original))
            {
                foreach (var clip in group)
                {
                    var info = editor.File.file.GetAssetInfo(clip.PathId);
                    if (info is null || info.TypeId != (int)AssetClassID.AnimationClip) continue;

                    var field = editor.Read(info);
                    if (field is null) continue;

                    var was = Motion.Read(field)?.Length ?? 0f;
                    var now = ClipTiming.Stretch(field, factor);
                    editor.Stage(info, field);
                    done.Add(new ClipRetimed(group.Key, clip.Name, was, now));
                }

                editor.Save(written, packing);
            }

            File.Move(written, path, overwrite: true);
        }

        return done;
    }

    /// Puts another weapon's motion onto these clips, at the length they already have.
    ///
    /// The whole object's contents are moved rather than its curves rebuilt, which is safe here and
    /// would not be in another engine: read across 318 of the game's clips, not one holds a pointer
    /// to anything, and none carries a binding constant. A clip is self-contained data, so it
    /// travels between two files as it stands.
    ///
    /// Three things are changed on the way. The curve paths get the target's own root name, since
    /// that first name is the one thing two weapons never share. The times are scaled so the clip
    /// lasts exactly what the target's own clip lasted — this is the point of the whole exercise,
    /// because the author halved a Machine Gun's clips and watched its rate of fire hit the game's
    /// cap. And the name is put back, because a clip is what the Animation component asks for by
    /// name as well as by pointer.
    public IReadOnlyList<ClipMoved> Transplant(
        IReadOnlyList<ClipMove> moves, BundlePacking packing = BundlePacking.Smaller)
    {
        Refuse();
        var done = new List<ClipMoved>();

        foreach (var group in moves.GroupBy(m => m.Into.Bundle, StringComparer.OrdinalIgnoreCase))
        {
            var (cache, path, hash) = Live(group.Key);

            if (!store.HasBackup(cache, group.Key, hash) && !BundleIntegrity.IsPristine(path, hash))
                throw new InvalidOperationException(
                    $"'{group.Key}' has already been modified by something else and there is no backup "
                    + "of it. Verify the game's files before writing a clip into it.");

            store.Backup(cache, group.Key, hash, path);
            var original = store.OriginalOf(cache, group.Key, hash) ?? path;

            var written = path + ".pgat-clips";
            using (var editor = new BundleEditor(original))
            {
                foreach (var move in group)
                {
                    var into = editor.File.file.GetAssetInfo(move.Into.PathId);
                    if (into is null || into.TypeId != (int)AssetClassID.AnimationClip) continue;

                    // The source read as the game shipped it, so that moving a motion twice starts
                    // from the same place each time.
                    var (fromCache, fromLive, fromHash) = Live(move.From.Bundle);
                    var fromPath = store.OriginalOf(fromCache, move.From.Bundle, fromHash) ?? fromLive;

                    AssetTypeValueField? source;
                    using (var reading = new BundleEditor(fromPath))
                    {
                        var fromInfo = reading.File.file.GetAssetInfo(move.From.PathId);
                        source = fromInfo is null ? null : reading.Read(fromInfo);
                    }
                    if (source is null) continue;

                    var was = move.Into.Length;
                    var length = Motion.Read(source)?.Length ?? 0f;
                    var paths = Rebind(source, move.Root);

                    if (length > 0 && was > 0) ClipTiming.Stretch(source, was / length);
                    if (!source["m_Name"].IsDummy) source["m_Name"].AsString = move.Into.Name;

                    editor.Stage(into, source);
                    done.Add(new ClipMoved(
                        group.Key, move.Into.Name, was, Motion.Read(source)?.Length ?? was,
                        $"{move.From.Name} of {move.From.Bundle}", paths));
                }

                editor.Save(written, packing);
            }

            File.Move(written, path, overwrite: true);
        }

        return done;
    }

    /// Hangs every curve in a clip off a different root object, and answers what it now drives.
    ///
    /// Only the first name changes. A curve on the root object itself names no path at all and
    /// keeps naming none.
    private static IReadOnlyList<string> Rebind(AssetTypeValueField clip, string root)
    {
        var driving = new List<string>();

        foreach (var name in new[] { "m_PositionCurves", "m_RotationCurves", "m_ScaleCurves" })
        {
            var array = clip[name];
            if (array.IsDummy) continue;

            foreach (var bound in array["Array"].Children)
            {
                var path = bound["path"];
                if (path.IsDummy) continue;

                var was = path.AsString;
                var slash = was.IndexOf('/');
                var now = was.Length == 0 ? was : slash < 0 ? root : root + was[slash..];

                path.AsString = now;
                if (!driving.Contains(now, StringComparer.Ordinal)) driving.Add(now);
            }
        }

        return driving;
    }

    /// Puts these bundles back as they shipped. Answers the ones there was something to put back for.
    public IReadOnlyList<string> Restore(IEnumerable<string> bundles)
    {
        Refuse();

        var back = new List<string>();
        foreach (var bundle in bundles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (cache, path, hash) = Live(bundle);
            if (store.RestoreIfBackedUp(cache, bundle, hash, path)) back.Add(bundle);
        }
        return back;
    }

    /// Writing a bundle the game has open leaves the game reading half a file.
    private void Refuse()
    {
        if (GameProcess.IsRunning(game))
            throw new InvalidOperationException("The game is running. Close it first.");
    }
}
