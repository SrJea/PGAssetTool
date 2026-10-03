using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;
using PGAssetTool.Core.Pack;
using PGAssetTool.Core.Preview;
using PGAssetTool.Core.Weapons;

namespace PGAssetTool.Core.Animation;

/// Puts one of the game's existing animations into a workspace, in place of one of its own.
///
/// A workspace holds a `.anim` for each clip its item plays. Choosing another item's clip for one of
/// them writes that clip's motion into the file, moved onto this item's own objects (see Retarget),
/// so packing and applying it are what they are for any other edited file — and the pack carries
/// the motion itself, not a reference to wherever it came from, so it still applies after an update
/// has moved or changed the clip it was taken from.
public static class AnimationSwap
{
    /// One clip a workspace can replace: the file that holds it and the clip it goes into.
    public sealed record Slot(string Source, AssetAddress Target)
    {
        public string Clip => Target.Name;
    }

    /// A clip an item plays, for choosing from.
    public sealed record Choice(string Bundle, long PathId, string Name, float Length, int Curves);

    /// What putting a clip in did.
    /// <param name="Moved">How many curves found something of this item's to move.</param>
    /// <param name="Unmatched">The paths the clip moved that nothing here answers to.</param>
    /// <param name="Notes">Anything else worth saying about how it was read.</param>
    /// <param name="Unchanged">Whether what was read is the animation the slot already had, so nothing was written.</param>
    public sealed record Outcome(
        string From, int Moved, IReadOnlyList<string> Unmatched, float Length, IReadOnlyList<string>? Notes = null,
        bool Unchanged = false);

    public static IReadOnlyList<Slot> Slots(PackManifest manifest)
        => manifest.Operations
            .Where(o => o.Op == PackOperations.ReplaceAnimation)
            .Select(o => new Slot(o.Source, o.Target))
            .ToList();

    /// The slot a name means: its clip's name or its file's, without minding case.
    public static Slot? Find(PackManifest manifest, string name)
        => Slots(manifest).FirstOrDefault(s => string.Equals(s.Clip, name, StringComparison.OrdinalIgnoreCase))
           ?? Slots(manifest).FirstOrDefault(s => string.Equals(
               Path.GetFileNameWithoutExtension(s.Source), name, StringComparison.OrdinalIgnoreCase));

    /// Every clip an item plays that moves anything, once each, by name.
    public static IReadOnlyList<Choice> ClipsOf(BundleSet bundles, WeaponTree tree)
    {
        var choices = new List<Choice>();
        foreach (var node in tree.PrefabAssets.Where(a => a.Class == AssetClassID.AnimationClip))
        {
            var bundle = node.Bundle.Length > 0 ? node.Bundle : tree.PrefabBundle;
            if (bundle is null || choices.Any(c => c.PathId == node.PathId
                    && string.Equals(c.Bundle, bundle, StringComparison.OrdinalIgnoreCase))) continue;
            if (Read(bundles, bundle, node.PathId) is not { } motion) continue;
            choices.Add(new Choice(bundle, node.PathId, motion.Name, motion.Length, motion.Curves.Count));
        }
        return choices;
    }

    /// Puts `from`'s clip called `clip` into this slot of the workspace.
    /// <exception cref="KeyNotFoundException">`from` plays no clip of that name.</exception>
    /// <exception cref="InvalidOperationException">Nothing in that clip finds anything here to move.</exception>
    public static Outcome Use(
        BundleSet bundles, GameCatalogs catalogs, string workspace, Slot slot, WeaponRecord from, string clip)
    {
        var resolver = new WeaponResolver(bundles, catalogs);
        var target = TargetRig(bundles, catalogs, resolver, workspace, slot);

        var sourceTree = resolver.Resolve(from);
        var choices = ClipsOf(bundles, sourceTree);
        // Where an item has two of a name — the gun's and a part's — the one that moves the most is
        // the one anybody means.
        var chosen = choices
            .Where(c => string.Equals(c.Name, clip, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Curves)
            .FirstOrDefault()
            ?? throw new KeyNotFoundException(
                $"{Describe(catalogs, from)} has no animation called '{clip}'. It has: "
                + string.Join(", ", choices.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase)) + ".");

        var motion = Read(bundles, chosen.Bundle, chosen.PathId)!;
        var sourceRig = AnimationRig.Playing(bundles, Holders(sourceTree, chosen.Bundle), chosen.Bundle, chosen.PathId);
        var (moved, unmatched) = sourceRig is not null
            ? Retarget.Apply(motion, target, sourceRig)
            : Retarget.Apply(motion, target.Paths);

        if (moved.Curves.Count == 0)
            throw new InvalidOperationException(
                $"Nothing {Describe(catalogs, from)}'s '{chosen.Name}' moves is here to be moved: "
                + $"{string.Join(", ", unmatched.Take(4))}{(unmatched.Count > 4 ? ", …" : "")}.");

        var name = $"{Describe(catalogs, from)}: {chosen.Name}";
        var (fitted, notes) = FitToSlot(bundles, slot, moved);
        ClipFile.FromMotion(fitted, name, unmatched).Write(Path.Combine(workspace, slot.Source));
        return new Outcome(name, fitted.Curves.Count, unmatched, fitted.Length, notes);
    }

    /// Puts the slot's own clip back, exactly as the extract wrote it — so the file is unedited
    /// again, and a pack leaves it out.
    public static Outcome Restore(BundleSet bundles, string workspace, Slot slot)
    {
        var bundle = slot.Target.Container;
        var file = bundles.Open(bundle);
        var info = new ContainerIndex(bundles.Context).Resolve(slot.Target, file, out _)
            ?? throw new KeyNotFoundException($"'{slot.Clip}' is no longer in '{bundle}'.");
        var motion = Read(bundles, bundle, info.PathId)
            ?? throw new InvalidOperationException($"'{slot.Clip}' moves nothing.");

        ClipFile.FromMotion(motion, from: "").Write(Path.Combine(workspace, slot.Source));
        return new Outcome("", motion.Curves.Count, [], motion.Length);
    }

    /// Puts an animation out of a glTF — one edited in Blender, or made anywhere else — into this
    /// slot of the workspace. See AnimationGlb.Read for which of the file's animations that is.
    /// <exception cref="KeyNotFoundException">The file has no animation that is the one asked for.</exception>
    /// <exception cref="InvalidOperationException">Nothing in it finds anything here to move.</exception>
    public static Outcome UseGlb(
        BundleSet bundles, GameCatalogs catalogs, string workspace, Slot slot, string glb, string? take = null)
    {
        var resolver = new WeaponResolver(bundles, catalogs);
        var target = TargetRig(bundles, catalogs, resolver, workspace, slot);
        var original = PathIdOf(bundles, slot) is { } id ? Read(bundles, slot.Target.Container, id) : null;

        var names = new[] { Path.GetFileNameWithoutExtension(slot.Source), slot.Clip }
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Motion? current = null;
        try { current = ClipFile.Read(Path.Combine(workspace, slot.Source)).ToMotion(); }
        catch (Exception e) when (e is IOException or InvalidDataException) { }
        var imported = AnimationGlb.Read(glb, target, names, take, original, current);
        if (imported.Motion.Curves.Count == 0)
            throw new InvalidOperationException(
                $"Nothing '{imported.Take}' in '{Path.GetFileName(glb)}' moves is here to be moved"
                + (imported.Unmatched.Count > 0
                    ? $": {string.Join(", ", imported.Unmatched.Take(4))}{(imported.Unmatched.Count > 4 ? ", …" : "")}."
                    : "."));

        var name = $"{Path.GetFileName(glb)}: {imported.Take}";
        // Left as it is, where it is what it was: rewritten, it would say it came from the file, and
        // an animation opened and saved without an edit would count as edited.
        var (fitted, fitting) = FitToSlot(bundles, slot, imported.Motion);
        if (!imported.Same)
            ClipFile.FromMotion(fitted, name, imported.Unmatched).Write(Path.Combine(workspace, slot.Source));
        return new Outcome(name, fitted.Curves.Count, imported.Unmatched, fitted.Length, [.. imported.Notes, .. fitting],
            imported.Same);
    }

    /// Puts a `.anim` from anywhere into this slot — another workspace's, dragged across — fitted to
    /// this item the way another item's clip is.
    ///
    /// Copied as it was, a file from another workspace names that item's objects, which this one
    /// does not have, and moves nothing here or in the game. Fitting it needs what it was written
    /// for, and a workspace says: the file's own workspace is looked for above it, and the slot that
    /// file is tells which of that item's components played it. A file with no workspace above it
    /// is fitted by its names alone.
    /// <exception cref="InvalidDataException">The file is not an animation this tool wrote.</exception>
    /// <exception cref="InvalidOperationException">Nothing in it finds anything here to move.</exception>
    public static Outcome UseFile(BundleSet bundles, GameCatalogs catalogs, string workspace, Slot slot, string file)
    {
        var full = Path.GetFullPath(file);
        var destination = Path.GetFullPath(Path.Combine(workspace, slot.Source));
        var clip = ClipFile.Read(full);
        var motion = clip.ToMotion();
        if (string.Equals(full, destination, StringComparison.OrdinalIgnoreCase))
            return new Outcome(clip.From, motion.Curves.Count, clip.Unmatched, motion.Length, Unchanged: true);

        var resolver = new WeaponResolver(bundles, catalogs);
        var target = TargetRig(bundles, catalogs, resolver, workspace, slot);

        var (sourceRig, name) = (default(AnimationRig), Path.GetFileName(full));
        if (Owner(full) is var (sourceWorkspace, sourceSlot))
        {
            try { sourceRig = TargetRig(bundles, catalogs, resolver, sourceWorkspace, sourceSlot); }
            catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or IOException) { }
            var subject = Workspace.Read(sourceWorkspace).Subject;
            var record = subject is { IsKnown: true } ? catalogs.Find(subject.Id) : null;
            name = $"{(record is not null ? Describe(catalogs, record) : Path.GetFileName(sourceWorkspace))}: {sourceSlot.Clip}";
        }

        // Already this item's, where every object it names is here: nothing to fit.
        var have = target.Paths.ToHashSet(StringComparer.Ordinal);
        var (moved, unmatched) = motion.Curves.All(c => have.Contains(c.Path))
            ? (motion, (IReadOnlyList<string>)[])
            : sourceRig is not null
                ? Retarget.Apply(motion, target, sourceRig)
                : Retarget.Apply(motion, target.Paths);

        if (moved.Curves.Count == 0)
            throw new InvalidOperationException(
                $"Nothing '{Path.GetFileName(full)}' moves is here to be moved: "
                + $"{string.Join(", ", unmatched.Take(4))}{(unmatched.Count > 4 ? ", …" : "")}.");

        var from = clip.From.Length > 0 ? clip.From : name;
        var (fitted, notes) = FitToSlot(bundles, slot, moved);
        ClipFile.FromMotion(fitted, from, unmatched).Write(destination);
        return new Outcome(from, fitted.Curves.Count, unmatched, fitted.Length, notes);
    }

    /// The motion made as long as the slot's own clip, and what to say about it. See ClipImporter:
    /// the game times the weapon by its clips, so a clip of another length changes how it fights.
    private static (Motion Motion, IReadOnlyList<string> Notes) FitToSlot(BundleSet bundles, Slot slot, Motion motion)
    {
        var own = PathIdOf(bundles, slot) is { } id ? Read(bundles, slot.Target.Container, id) : null;
        if (own is not { Length: > 0 } || MathF.Abs(own.Length - motion.Length) < 1e-3f) return (motion, []);
        return (ClipImporter.Fit(motion, own.Length),
        [
            $"Played in {own.Length:0.00}s rather than {motion.Length:0.00}s: the length of this item's own "
            + $"{slot.Clip}, which the game times the weapon by, so it shoots and reloads as it always has.",
        ]);
    }

    /// The workspace a `.anim` belongs to and the slot it is there, looked for a few folders up.
    private static (string Workspace, Slot Slot)? Owner(string file)
    {
        var directory = Path.GetDirectoryName(file);
        for (var step = 0; directory is not null && step < 4; step++, directory = Path.GetDirectoryName(directory))
        {
            if (!File.Exists(Path.Combine(directory, PackManifest.FileName))) continue;
            try
            {
                var slot = Slots(Workspace.Read(directory)).FirstOrDefault(s => string.Equals(
                    Path.GetFullPath(Path.Combine(directory, s.Source)), file, StringComparison.OrdinalIgnoreCase));
                return slot is null ? null : (directory, slot);
            }
            catch (Exception e) when (e is IOException or InvalidDataException) { return null; }
        }
        return null;
    }

    /// Writes every animation of the workspace, as it stands, into one glTF with the item's models on
    /// the objects they move — for editing in Blender or anything else that reads one.
    ///
    /// The animations are this workspace's own files, so one already taken from another item goes
    /// out as it is now. The models are the game's, which is what the editor shows moving. Where an
    /// item's clips are played by more than one component, the one that plays the most of them is
    /// the one written.
    /// <param name="path">Where to write it; by default `animations/animations.glb` in the workspace.</param>
    public static (string Path, AnimationGlb.Written Written) ExportGlb(
        BundleSet bundles, GameCatalogs catalogs, string workspace, string? path = null)
    {
        var manifest = Workspace.Read(workspace);
        var slots = Slots(manifest);
        if (slots.Count == 0) throw new InvalidOperationException("This workspace has no animations to write.");

        var resolver = new WeaponResolver(bundles, catalogs);
        var rigs = slots.Select(s => s.Target.Container)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(b => RigsFor(bundles, catalogs, resolver, workspace, b))
            .DistinctBy(r => (r.Bundle, r.PathId))
            .ToList();
        var playing = slots
            .Select(s => (Slot: s, Rig: PathIdOf(bundles, s) is { } id
                ? rigs.FirstOrDefault(r => r.Clips.Any(c => c.PathId == id
                    && string.Equals(c.Bundle, s.Target.Container, StringComparison.OrdinalIgnoreCase)))
                : null))
            .Where(p => p.Rig is not null)
            .GroupBy(p => (p.Rig!.Bundle, p.Rig.PathId))
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Nothing in this workspace's item plays its animations.");
        var rig = playing.First().Rig!;

        var motions = new List<Motion>();
        foreach (var (slot, _) in playing)
        {
            try
            {
                var motion = ClipFile.Read(Path.Combine(workspace, slot.Source)).ToMotion();
                motions.Add(motion with { Name = Path.GetFileNameWithoutExtension(slot.Source) });
            }
            catch (Exception e) when (e is IOException or InvalidDataException) { }
        }

        var models = new List<AnimationGlb.Model>();
        foreach (var operation in manifest.Operations.Where(o => o.Op == PackOperations.ReplaceMesh))
        {
            if (AssetPreview.Locate(bundles, operation.Target.Container, AssetClassID.Mesh,
                    operation.Target.PathId ?? 0, operation.Target.Name) is not var (file, info)) continue;
            if (bundles.Context.Deserialize(file, info) is not { } field) continue;
            if (AssetPreview.Mesh(field, bundles, operation.Target.Container) is not { } mesh) continue;
            // The renderer under the object the animations play on, where the mesh has several: the
            // weapon's own prefab draws it, and so does the shop's, arranged its own way.
            if ((Skeleton.For(bundles, operation.Target.Container, info.PathId, under: rig.Name)
                 ?? Skeleton.For(bundles, operation.Target.Container, info.PathId)) is not { } skeleton) continue;

            var pictures = Enumerable.Range(0, mesh.SubMeshes.Count)
                .Select(s => s < operation.Wears.Count && operation.Wears[s].Length > 0
                    && File.Exists(Path.Combine(workspace, operation.Wears[s]))
                        ? File.ReadAllBytes(Path.Combine(workspace, operation.Wears[s]))
                        : null)
                .ToList();
            models.Add(new AnimationGlb.Model(mesh, skeleton, pictures));
        }

        path ??= Path.Combine(workspace, "animations", AnimationGlb.FileName);
        return (path, AnimationGlb.Write(path, rig, models, motions));
    }

    /// The component that plays the slot's clip in the workspace's own item, and everything under it.
    private static AnimationRig TargetRig(
        BundleSet bundles, GameCatalogs catalogs, WeaponResolver resolver, string workspace, Slot slot)
    {
        return (PathIdOf(bundles, slot) is { } id
                   ? RigsFor(bundles, catalogs, resolver, workspace, slot.Target.Container)
                       .FirstOrDefault(r => r.Clips.Any(c => c.PathId == id
                           && string.Equals(c.Bundle, slot.Target.Container, StringComparison.OrdinalIgnoreCase)))
                   : null)
            ?? throw new InvalidOperationException(
                $"Nothing in this workspace's item plays '{slot.Clip}', so there is nothing to fit another animation to.");
    }

    /// Every Animation component the workspace's item can have: in the bundle its clip is in, its
    /// prefab's, wherever else its prefab reaches one, and its skins' models.
    private static IEnumerable<AnimationRig> RigsFor(
        BundleSet bundles, GameCatalogs catalogs, WeaponResolver resolver, string workspace, string clipBundle)
    {
        var subject = Workspace.Read(workspace).Subject;
        var record = subject is { IsKnown: true }
            ? catalogs.Find(subject.Id) ?? (subject.Prefab.Length > 0 ? catalogs.Find(subject.Prefab) : null)
            : null;

        var lookIn = new List<string> { clipBundle };
        if (record is not null)
        {
            var tree = resolver.Resolve(record);
            lookIn.AddRange(Holders(tree, clipBundle));
            foreach (var skin in tree.Skins)
                if (skin.Model is { } model) lookIn.Add(model.Bundle);
        }

        return lookIn.Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(b => AnimationRig.In(bundles, b));
    }

    private static long? PathIdOf(BundleSet bundles, Slot slot)
    {
        if (slot.Target.PathId is { } known) return known;
        var file = bundles.Open(slot.Target.Container);
        return new ContainerIndex(bundles.Context).Resolve(slot.Target, file, out _)?.PathId;
    }

    /// The bundles an item's Animation components can be in: where its clip is, its prefab's, and
    /// wherever else its prefab reaches one.
    private static IEnumerable<string> Holders(WeaponTree tree, string clipBundle)
    {
        yield return clipBundle;
        if (tree.PrefabBundle is { } prefab) yield return prefab;
        foreach (var node in tree.PrefabAssets.Where(a => a.Class == AssetClassID.Animation))
            if (node.Bundle.Length > 0) yield return node.Bundle;
    }

    private static Motion? Read(BundleSet bundles, string bundle, long pathId)
    {
        try
        {
            var file = bundles.Open(bundle);
            var info = file.file.GetAssetInfo(pathId);
            var field = info is null ? null : bundles.Context.Deserialize(file, info);
            return field is null ? null : Motion.Read(field);
        }
        catch (Exception e) when (e is IOException or KeyNotFoundException)
        {
            return null;
        }
    }

    public static string Describe(GameCatalogs catalogs, WeaponRecord item)
    {
        var name = catalogs.Localization.Translate(item.LocalizationKey) ?? item.Slug;
        return item.IsNumbered ? $"#{item.GameNumber} {name}" : name;
    }
}
