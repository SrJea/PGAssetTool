using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;
using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Weapons;

namespace PGAssetTool.Core.Export;

/// <param name="Notes">
/// Things the author should know about what was written, which are not failures: that one picture
/// is written to two assets, or that two pictures which look alike have to be edited apart.
/// </param>
public sealed record WeaponExport(
    string Directory,
    IReadOnlyList<ExportedAsset> Assets,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string>? Notes = null);

/// An extract refused because a bundle it would read has been changed by something this copy of the
/// tool has no original for. See WeaponExporter.Refuse.
///
/// A class of its own so a caller can tell this refusal from a bug. Both were an
/// InvalidOperationException, and the CLI reported an empty LINQ sequence inside the export as
/// though it were this message's verdict on the game. Still one of those, so anything that caught
/// the refusal by its old type goes on catching it.
public sealed class AlteredBundlesException(IReadOnlyList<string> bundles, string message)
    : InvalidOperationException(message)
{
    public IReadOnlyList<string> Bundles { get; } = bundles;
}

/// Writes out everything belonging to one weapon, arranged so the result is browsable: images in
/// one place, audio in another, and the object graph as a single readable document rather than a
/// file per Transform.
public sealed class WeaponExporter(BundleSet bundles)
{
    private AssetExporter? _writer;

    /// Resolves references that leave the bundle they were written in, which a skin's model does
    /// as readily as the weapon's own prefab.
    private readonly Dressing _dressing = new(bundles);

    /// Following a reference out of one bundle and into the next, shared with the dressing so the
    /// index of which bundle holds which file is built once.
    private BundleGraph Graph => _dressing.Graph;

    /// Write textures with no alpha channel; see AssetExporter for why.
    public bool Opaque { get; init; }

    /// Clear the part of every texture no model in this weapon samples. See UvCoverage.
    public bool MaskUnused { get; init; }

    private AssetExporter _exporter => _writer ??= new AssetExporter(bundles) { Opaque = Opaque };

    /// Where each class that can come back is written. Nothing else is written at all — see
    /// AssetExporter.Export.
    private static readonly Dictionary<AssetClassID, string> Folders = new()
    {
        [AssetClassID.Texture2D] = "textures",
        [AssetClassID.AudioClip] = "audio",
        [AssetClassID.Mesh] = "meshes",
        [AssetClassID.AnimationClip] = "animations",
    };

    /// One of the weapon's skins to write out as well as the default look, by id or display name.
    ///
    /// Off by default. A weapon carries up to a dozen skins and each brings its own materials,
    /// textures and sometimes a whole model; writing all of them would multiply a workspace several
    /// times over for the sake of the one an author is actually working on.
    public string? Skin { get; init; }

    public WeaponExport Export(WeaponTree tree, string outputRoot)
    {
        var chosen = Chosen(tree);
        Refuse(Altered(tree, chosen));
        var directory = Pack.Workspace.Free(Path.Combine(outputRoot,
            // Numbered where the kind is numbered, which is weapons and nothing else: the number is
            // what a player calls a weapon by, and a hat has only its id.
            (tree.Record.IsNumbered ? $"{tree.Record.GameNumber:D4}_" : "")
            + AssetExporter.Sanitize(tree.Record.Slug)
            + (chosen is null ? "" : $"_{AssetExporter.Sanitize(Suffix(tree, chosen))}")));
        Directory.CreateDirectory(directory);

        var assets = new List<ExportedAsset>();
        var skipped = new List<string>();
        var notes = new List<string>();

        // A skin's own model is walked once, here, because the mask needs it as much as the export
        // does: that model draws the skin's paint on its own UVs, and nothing in the weapon says so.
        var brought = chosen?.Model is { } model ? Walk(model, skipped) : null;

        _exporter.Coverage = MaskUnused ? Sampled(tree, brought) : null;

        // What the weapon itself contributes when a skin was asked for.
        //
        // Nothing, if the skin brings its own model: it replaces the weapon rather than repainting
        // it, and every one of the weapon's own textures, sounds and animations is then a file the
        // author has no reason to touch. Its geometry and nothing else, if the skin only repaints:
        // that is the thing being repainted, and the paint is the skin's own.
        var wanted = chosen is null ? Folders
            : chosen.Model is null
                ? Folders.Where(f => f.Key == AssetClassID.Mesh).ToDictionary(f => f.Key, f => f.Value)
                : [];

        if (tree.PrefabBundle is not null && wanted.Count > 0)
        {
            // A weapon spans bundles: the prefab in one, its materials and textures in another. Each
            // object is read and addressed in the bundle it actually lives in, or a pack built from
            // this workspace would name the wrong container and fail to apply.
            foreach (var group in tree.PrefabAssets.GroupBy(a => a.Bundle.Length > 0 ? a.Bundle : tree.PrefabBundle))
            {
                AssetsFileInstance file;
                try
                {
                    file = bundles.Open(group.Key);
                }
                catch (Exception ex) when (ex is IOException or FileNotFoundException)
                {
                    skipped.Add($"{group.Key}: {ex.Message}");
                    continue;
                }

                foreach (var node in group)
                {
                    var info = file.file.GetAssetInfo(node.PathId);
                    if (info is null) continue;

                    if (wanted.TryGetValue(node.Class, out var folder))
                        Once(assets, _exporter.Export(
                            group.Key, file, info, Path.Combine(directory, folder),
                            Distinct(assets, node, Path.Combine(directory, folder))));
                }
            }
        }

        // The weapon's own shop icon, and only when the weapon is what was asked for. A skin has one
        // of its own among the related assets, and the weapon's says nothing about the skin.
        if (chosen is null)
        {
            if (tree.Icon is { AssetPath: not null } icon)
                ExportByName(icon.Container, icon.TextureName, Path.Combine(directory, "icon"), assets, skipped);
            else if (tree.Icon is not null)
                skipped.Add($"{tree.Icon.TextureName}: lives in {tree.Icon.Container}, outside the bundle cache");
        }

        foreach (var related in tree.Related)
        {
            // The shop icon is written once, into icon/, whichever route reaches it. Everything but
            // a weapon is found by its own id, and so is its icon — `OfferIcons/hat_chest_icon1_big`
            // is among the related assets as well as being the icon — which wrote the one picture
            // twice, as two files and two operations on the same asset. An author who edited one
            // copy had the other put the original straight back over it, or not, by manifest order.
            if (tree.Icon?.AssetPath is { } icon
                && string.Equals(related.Path, icon, StringComparison.OrdinalIgnoreCase)) continue;

            if (!Wanted(tree, related, chosen)) continue;
            if (related.Bundle is null) { skipped.Add($"{related.Path}: bundle unknown"); continue; }
            var leaf = related.Path[(related.Path.LastIndexOf('/') + 1)..];
            ExportByName(related.Bundle, leaf,
                Path.Combine(directory, "related", AssetExporter.Sanitize(related.Namespace)), assets, skipped);
        }

        if (chosen is not null) ExportSkin(chosen, brought, directory, assets, skipped);
        else if (Skin is { Length: > 0 } asked)
            skipped.Add($"'{asked}': this {tree.Record.Kind.Name.ToLowerInvariant()} has no such skin");

        if (chosen is null) PairTheDefaultSkin(tree, directory, assets, notes, skipped);

        DrawPackIcon(tree, directory, skipped);
        Dress(assets, tree, chosen);

        skipped.AddRange(_exporter.Failed);
        _exporter.Failed.Clear();

        return new WeaponExport(directory, assets, skipped, notes);
    }

    /// Every bundle this export would read an asset out of.
    private static IEnumerable<string> Reads(WeaponTree tree, WeaponSkinView? chosen)
    {
        if (tree.PrefabBundle is { } prefab) yield return prefab;
        foreach (var node in tree.PrefabAssets) yield return node.Bundle;
        foreach (var related in tree.Related) yield return related.Bundle ?? "";
        if (tree.Icon?.Container is { } icon) yield return icon;

        // A skin's own bundles only when it is the skin being written out; the rest are listed in
        // the tree and never read.
        foreach (var skin in chosen is null ? [] : new[] { chosen })
        {
            if (skin.Model is { } model) yield return model.Bundle;
            foreach (var material in skin.Materials)
            {
                yield return material.Bundle;
                foreach (var texture in material.Textures) yield return material.Locate(texture).Bundle;
            }
        }
    }

    private IReadOnlyList<string> Altered(WeaponTree tree, WeaponSkinView? chosen)
        => bundles.Altered(Reads(tree, chosen));

    /// Refuses to write out a bundle this copy of the tool cannot show as the game's own.
    ///
    /// A second copy of the tool, in another folder, keeps its backups in its own data folder. From
    /// here a bundle it modded is simply what the game holds now, and an extract would write
    /// somebody else's work out as the game's own — which a pack built from it would then carry, to
    /// whoever installed it, under this author's name. The bundle's real bytes are still recorded by
    /// the game itself, so this is caught rather than guessed at.
    private static void Refuse(IReadOnlyList<string> altered)
    {
        if (altered.Count == 0) return;

        throw new AlteredBundlesException(altered,
            $"{(altered.Count == 1 ? "A bundle this item is in has" : $"{altered.Count} bundles this item is in have")} "
            + "been changed by something other than this copy of the tool, which has no original to read instead: "
            + string.Join(", ", altered.Take(4)) + (altered.Count > 4 ? ", …" : "")
            + ". Extracting would write that change out as the game's own. Take the mods out with "
            + "whichever copy of the tool installed them, or restore the game's files (Steam's integrity check), "
            + "and try again.");
    }

    /// The skin the game shows once a player has ever changed skins on this weapon.
    ///
    /// A weapon with skins has two default looks, and which one a player gets depends on their own
    /// history: the weapon's own paint until they first touch its skins, and this one ever after.
    /// It is filed among the skins under the weapon's own prefab name — Weapon834_default — and
    /// named in the shop exactly as the weapon is.
    private static WeaponSkinView? DefaultSkin(WeaponTree tree)
        => tree.Skins.FirstOrDefault(s => s.Model is null
               && string.Equals(s.Record.Id, tree.Record.PrefabName + "_default", StringComparison.OrdinalIgnoreCase))
           ?? tree.Skins.FirstOrDefault(s => s.Model is null
               && string.Equals(s.DisplayName, tree.DisplayName, StringComparison.OrdinalIgnoreCase));

    /// Makes an edit to the weapon's own paint reach its default skin as well, so a pack looks the
    /// same to every player.
    ///
    /// Without this, a re-skin showed only to players who had never touched the skins: the default
    /// skin can paint with a texture of its own, and nothing the author edited was in it. The game
    /// has it both ways. #416's default skin draws with the weapon's own texture — the same asset —
    /// and needs nothing. #507's draws with a separate asset whose pixels are identical to the
    /// weapon's, so the one picture the author edits is written to both. Where the two pictures
    /// really differ, both are written out and the author is told, because painting one over the
    /// other would be a guess about which the author meant.
    private void PairTheDefaultSkin(
        WeaponTree tree, string directory, List<ExportedAsset> assets, List<string> notes, List<string> skipped)
    {
        if (DefaultSkin(tree) is not { } skin || tree.MainMesh is not { } body) return;
        if (tree.MeshTextures.FirstOrDefault(m => m.MeshPathId == body.PathId) is not { } slots) return;

        var index = new ContainerIndex(bundles.Context);

        // Slot for slot: a skin names one material per material the renderer has, in its order.
        for (var slot = 0; slot < Math.Min(slots.BySubMesh.Count, skin.Materials.Count); slot++)
        {
            if (slots.BySubMesh[slot] is not { } own) continue;
            if (skin.Materials[slot].Main is not { } main) continue;

            var ownBundle = own.Bundle.Length > 0 ? own.Bundle : tree.PrefabBundle ?? "";
            var (bundle, pathId) = skin.Materials[slot].Locate(main);
            if (pathId == own.PathId && string.Equals(bundle, ownBundle, StringComparison.OrdinalIgnoreCase)) continue;

            var written = assets.FirstOrDefault(a => a.Class == AssetClassID.Texture2D
                && a.Address.PathId == own.PathId
                && string.Equals(a.Address.Container, ownBundle, StringComparison.OrdinalIgnoreCase));
            if (written is null) continue;

            var ours = TextureFor(own with { Bundle = ownBundle });
            var theirs = TextureFor(new AssetNode(pathId, AssetClassID.Texture2D, main.Name, bundle));
            if (ours is null || theirs is null)
            {
                skipped.Add($"{main.Name}: the default skin's texture could not be read to compare");
                continue;
            }

            if (ours.Width == theirs.Width && ours.Height == theirs.Height && ours.Bgra.AsSpan().SequenceEqual(theirs.Bgra))
            {
                AssetsFileInstance file;
                try { file = bundles.Open(bundle); }
                catch (Exception ex) when (ex is IOException or FileNotFoundException)
                {
                    skipped.Add($"{main.Name}: {ex.Message}");
                    continue;
                }
                if (file.file.GetAssetInfo(pathId) is not { } info) continue;

                // The same file, a second address. Everything downstream keys by file — what is
                // edited, what is packed — and by address only when it writes, so one edit writes both.
                assets.Add(written with { Address = index.AddressOf(bundle, file, info, main.Name), Wears = null });
                notes.Add($"{Path.GetFileName(written.Path)} is also what the default skin paints with "
                    + $"({main.Name}), so editing it changes both.");
            }
            else
            {
                ExportByName(bundle, main.Name, Path.Combine(directory, "textures", "default skin"),
                    assets, skipped, AssetClassID.Texture2D);
                notes.Add($"The default skin paints with {main.Name}, which is not the same picture as {own.Name}. "
                    + "Both are written out: a player who has ever changed skins sees the one in 'textures/default skin'.");
            }
        }
    }

    /// Records, against each mesh written out, the textures it is drawn with.
    ///
    /// The workspace itself cannot answer this later: it holds a model and a folder of pictures and
    /// nothing that pairs them. The renderers and materials that decide it are in the game, and a
    /// workspace made from a skin does not even agree with them — the geometry is the weapon's and
    /// the renderer names the weapon's paint, while every picture in the workspace is the skin's.
    /// Here is the one moment that knows both.
    private void Dress(List<ExportedAsset> assets, WeaponTree tree, WeaponSkinView? chosen)
    {
        var named = assets
            .Select(a => a.Address.Container)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pictures = assets
            .Where(a => a.Class == AssetClassID.Texture2D && a.Address.Container.Length > 0)
            .Select(a => a.Address)
            .ToList();

        for (var at = 0; at < assets.Count; at++)
        {
            if (assets[at].Class != AssetClassID.Mesh) continue;
            if (assets[at].Address.PathId is not { } mesh) continue;

            var offered = Wearing(tree, chosen, assets[at].Address.Container, mesh, named)
                .Where(c => c.Count > 0)
                .ToList();

            // The one whose paint is here. More than one renderer can draw the same mesh and they do
            // not agree — the tactical knife's geometry is drawn by its own prefab in one paint and
            // by a skin's prefab in another — and the one worth recording is the one this workspace
            // can actually show. Failing that, whichever was found first.
            var wears = offered.FirstOrDefault(c => c.All(t => Written(pictures, t)))
                ?? offered.FirstOrDefault()
                ?? [];

            if (wears.Count > 0)
                assets[at] = assets[at] with
                {
                    Wears = wears
                        .Select(t => new AssetAddress(
                            t.Bundle, nameof(AssetClassID.Texture2D), t.Name, PathId: t.PathId))
                        .ToList(),
                };
        }
    }

    private static bool Written(IReadOnlyList<AssetAddress> pictures, AssetNode texture)
        => pictures.Any(p => p.PathId == texture.PathId);

    /// Every account of what this mesh is drawn with, best first.
    private IEnumerable<IReadOnlyList<AssetNode>> Wearing(
        WeaponTree tree, WeaponSkinView? chosen, string bundle, long mesh, IReadOnlyList<string> lookIn)
    {
        // A skin that only repaints hands the weapon's own renderers its own materials, so this
        // geometry is to be seen in the skin's paint. What the renderer names is the weapon's, and
        // in a workspace made for the skin that file was deliberately not written.
        //
        // The weapon's own mesh and nothing else. A skin repaints the gun; the player's arms and
        // the muzzle flash beside it are drawn with what they were always drawn with, and neither
        // of those textures is in a workspace made for a skin.
        if (chosen is { Model: null } && tree.MainMesh?.PathId == mesh)
            yield return chosen.Materials
                .Where(m => m.Main is not null)
                .Select(m => m.Main! with { Bundle = m.Locate(m.Main!).Bundle })
                .ToList();

        // What the weapon's own prefab binds, taken from the prefab's own closure rather than from
        // whatever else in the bundle happens to draw this mesh.
        if (tree.MeshTextures.FirstOrDefault(m => m.MeshPathId == mesh) is { } dressed)
            yield return dressed.BySubMesh.Where(t => t is not null).Select(t => t!).ToList();

        // And everything else that draws it, for a model a skin brought with it: its renderer is its
        // own and is nowhere in the weapon's prefab.
        foreach (var slots in _dressing.Every(bundle, mesh, lookIn))
            yield return slots.Where(t => t is not null).Select(t => t!).ToList();
    }


    /// Which texels of each of this weapon's textures its own models actually sample.
    ///
    /// Built from the same pairing of submesh to material the preview draws with, so what an author
    /// opens in an image editor and what they see on the model agree about which island is which.
    ///
    /// A texture nothing here draws is left whole, and that is most of them: a shop icon, a gloss
    /// or mask map bound beside the main slot, anything reached through a component. Clearing those
    /// would mean guessing which mesh reads them, and a wrong guess erases work.
    private Func<TextureShape, bool[]?> Sampled(WeaponTree tree, Brought? brought)
    {
        var uses = new Dictionary<(string Bundle, long PathId), List<(long Mesh, int SubMesh)>>();

        void Add(string bundle, long pathId, (long, int) use)
        {
            var key = ((bundle.Length > 0 ? bundle : tree.PrefabBundle ?? "").ToLowerInvariant(), pathId);
            if (!uses.TryGetValue(key, out var drawn)) uses[key] = drawn = [];
            if (!drawn.Contains(use)) drawn.Add(use);
        }

        foreach (var dressed in tree.MeshTextures)
            for (var slot = 0; slot < dressed.BySubMesh.Count; slot++)
                if (dressed.BySubMesh[slot] is { } node)
                    Add(node.Bundle, node.PathId, (dressed.MeshPathId, slot));

        // A skin repaints the gun, so its paint is held against the gun's own mesh and nothing else.
        // Which of a skin's materials lands in which slot is the renderer's business rather than the
        // skin's, so every submesh of that one mesh counts.
        //
        // Held against every mesh at first, on the grounds that wide was the safe direction. It is
        // not: a skin's material often paints with the weapon's own texture — 32 of the first 700
        // weapons, #416 among them — and holding that texture against the arms as well kept the
        // arms' UV island inside the gun's atlas, a region the gun never reads and which an author
        // recognises on sight because it is arm-shaped.
        var gun = tree.MeshTextures.FirstOrDefault(m => m.MeshPathId == tree.MainMesh?.PathId);

        //
        // And only the skins that repaint. One that brings a model draws its paint on that model's
        // UVs, not the gun's, and holding it against the gun cleared whatever the two did not
        // share — which for a model of its own is most of the picture.
        if (gun is not null)
            foreach (var material in tree.Skins.Where(s => s.Model is null).SelectMany(s => s.Materials))
                if (material.Main is { } main)
                {
                    var (bundle, pathId) = material.Locate(main);
                    for (var slot = 0; slot < gun.BySubMesh.Count; slot++)
                        Add(bundle, pathId, (gun.MeshPathId, slot));
                }

        // The model a skin brings, held against its own meshes. Without this a skin export masked
        // nothing of the skin: no mesh the weapon has draws its paint, so every one of its textures
        // was left whole — Ecko's Best Teammate's among them.
        if (brought is not null)
            foreach (var dressed in _dressing.OfModel(brought.Model.Bundle, brought.Assets))
                for (var slot = 0; slot < dressed.BySubMesh.Count; slot++)
                    if (dressed.BySubMesh[slot] is { } node)
                        Add(node.Bundle, node.PathId, (dressed.MeshPathId, slot));

        // Every mesh a texture here can be held against: the weapon's own, and the model's.
        var meshes = tree.PrefabAssets
            .Select(a => (Node: a, Bundle: a.Bundle.Length > 0 ? a.Bundle : tree.PrefabBundle ?? ""))
            .Concat((brought?.Assets ?? []).Select(a =>
                (Node: a, Bundle: a.Bundle.Length > 0 ? a.Bundle : brought!.Model.Bundle)))
            .Where(m => m.Node.Class == AssetClassID.Mesh)
            .ToList();

        var read = new Dictionary<long, UnityMesh?>();

        return texture =>
        {
            if (!uses.TryGetValue((texture.Bundle.ToLowerInvariant(), texture.PathId), out var drawn))
                return null;

            var models = drawn
                .Select(u => (Mesh: MeshFor(meshes, read, u.Mesh), u.SubMesh))
                .Where(u => u.Mesh is not null)
                .Select(u => (u.Mesh!, u.SubMesh))
                .ToList();

            return models.Count == 0
                ? null
                : UvCoverage.Of(models, texture.Width, texture.Height, texture.Margin);
        };
    }

    /// One of the meshes a texture is held against, unpacked, read once however many ask about it.
    private UnityMesh? MeshFor(
        IReadOnlyList<(AssetNode Node, string Bundle)> meshes, Dictionary<long, UnityMesh?> read, long pathId)
    {
        if (read.TryGetValue(pathId, out var known)) return known;

        var found = meshes.FirstOrDefault(m => m.Node.PathId == pathId);
        if (found.Node is null) return read[pathId] = null;

        var bundle = found.Bundle;

        try
        {
            var file = bundles.Open(bundle);
            var info = file.file.GetAssetInfo(pathId);
            var field = info is null ? null : bundles.Context.Deserialize(file, info);

            return read[pathId] = field is null
                ? null
                : UnityMesh.Read(field, (path, offset, size) => bundles.ReadResource(bundle, path, offset, size));
        }
        catch (Exception e) when (e is IOException or FileNotFoundException or NotSupportedException)
        {
            return read[pathId] = null;
        }
    }

    /// A file name for an animation that no other one here already has, or null for the usual one.
    ///
    /// A weapon's clips are called after what they are for, and a prefab with two Animation
    /// components — the gun and a part that moves on its own — has two of each: two Shoots, two
    /// Idles. Written by name, the second went over the first and the first was then in the manifest
    /// under a file holding the other one's motion. Textures that share a name are left as they
    /// always were; this is about clips, where it is the rule rather than the exception.
    private static string? Distinct(List<ExportedAsset> written, AssetNode node, string folder)
    {
        if (node.Class != AssetClassID.AnimationClip) return null;

        // By another clip: the same clip reached a second way is the same file, which Once settles.
        bool Taken(string name) => written.Any(a => a.Address.PathId != node.PathId && string.Equals(
            a.Path, Path.Combine(folder, AssetExporter.Sanitize(name) + Animation.ClipFile.Extension),
            StringComparison.OrdinalIgnoreCase));

        if (!Taken(node.Name)) return null;
        for (var n = 2; ; n++)
            if (!Taken($"{node.Name} ({n})")) return $"{node.Name} ({n})";
    }

    /// Records what was written, without listing the same file twice.
    ///
    /// One asset is reached by several routes — a texture four materials name, a mesh that both the
    /// weapon and a skin's model use — and following each route wrote the same file to the same
    /// path and added another row for it. What came out was right; what was listed was the same
    /// picture four times over, in the editor and in the manifest behind it.
    private static void Once(List<ExportedAsset> into, IEnumerable<ExportedAsset> written)
    {
        foreach (var asset in written)
            if (!into.Any(a => string.Equals(a.Path, asset.Path, StringComparison.OrdinalIgnoreCase)))
                into.Add(asset);
    }

    /// Whether a related asset belongs to what is being written out.
    ///
    /// Everything the lookup table names for this weapon lands in Related, and most of it belongs
    /// to one skin: an offer icon, a profile animation and an info record per skin, plus each
    /// skin's own definition. Writing the lot meant that extracting one weapon produced eight
    /// offer icons and that asking for one skin produced the other seven as well — files an author
    /// has no business changing to change the thing they asked for.
    ///
    /// Ownership is read off the name. A skin's assets are named after the skin's own id, so an
    /// asset whose name begins with one is that skin's and an asset whose name begins with none of
    /// them is the weapon's.
    private static bool Wanted(WeaponTree tree, RelatedAsset related, WeaponSkinView? chosen)
    {
        // A skin's own model, which the skin export walks properly — everything it reaches, in the
        // folders its types belong in. Taken by name here instead it came out as whatever else in
        // that bundle happened to share the name, which for these was the skin's texture.
        if (related.Path.StartsWith("WeaponSkinsV2/CustomModels/", StringComparison.OrdinalIgnoreCase))
            return false;

        var leaf = related.Path[(related.Path.LastIndexOf('/') + 1)..];
        var owner = tree.Skins
            .Select(s => s.Record.Id)
            .Where(id => Named(leaf, id))
            .OrderByDescending(id => id.Length)   // Weapon834_snow_night over Weapon834_snow
            .FirstOrDefault();

        // The default skin is the weapon as it comes, so what is filed under it is the weapon's.
        var weaponsOwn = owner is null
            || owner.EndsWith("_default", StringComparison.OrdinalIgnoreCase);

        return chosen is null
            ? weaponsOwn
            : Named(leaf, chosen.Record.Id);
    }

    /// Whether `leaf` is `id` itself or something filed under it, rather than a longer name that
    /// merely starts with the same letters.
    private static bool Named(string leaf, string id)
        => leaf.StartsWith(id, StringComparison.OrdinalIgnoreCase)
            && (leaf.Length == id.Length || leaf[id.Length] == '_');

    /// The skin that was asked for, matched on its id or on the name a player would see.
    private WeaponSkinView? Chosen(WeaponTree tree)
        => Skin is not { Length: > 0 } asked
            ? null
            : tree.Skins.FirstOrDefault(s =>
                string.Equals(s.Record.Id, asked, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.DisplayName, asked, StringComparison.OrdinalIgnoreCase));

    /// What the workspace directory is called after the weapon's own name.
    ///
    /// The skin's id already begins with the prefab name — Weapon834_christmas — so the whole of it
    /// would read as the weapon twice. What is left is what tells the skins apart.
    private static string Suffix(WeaponTree tree, WeaponSkinView skin)
        => skin.Record.Id.StartsWith(tree.Record.PrefabName + "_", StringComparison.OrdinalIgnoreCase)
            ? skin.Record.Id[(tree.Record.PrefabName.Length + 1)..]
            : skin.Record.Id;

    /// A model a skin brings, and everything it reaches that the tree would show.
    private sealed record Brought(SkinModel Model, IReadOnlyList<AssetNode> Assets);

    /// Walks a skin's own model, or says in `skipped` why it could not.
    ///
    /// The model is a prefab like the weapon's own, so it is walked the same way.
    private Brought? Walk(SkinModel model, List<string> skipped)
    {
        var name = model.AssetPath[(model.AssetPath.LastIndexOf('/') + 1)..];
        AssetsFileInstance file;
        try { file = bundles.Open(model.Bundle); }
        catch (Exception ex) when (ex is IOException or FileNotFoundException)
        {
            skipped.Add($"{name}: {ex.Message}");
            return null;
        }

        var root = ReferenceWalker.FindByName(bundles.Context, file, AssetClassID.GameObject, name);
        if (root is null) { skipped.Add($"{name}: no such model in '{model.Bundle}'"); return null; }

        return new Brought(model, ReferenceWalker
            .Closure(bundles.Context, file, root.PathId, Graph.Resolve, skip: WeaponResolver.Opaque)
            .Where(n => Pack.Replaceable.CanShow(n.Class))
            .ToList());
    }

    /// Writes a skin's own materials and textures, and the model it brings if it brings one.
    ///
    /// Kept apart from the weapon's own files. The two overlap — a skin repaints the same geometry
    /// — and mixing them would leave an author unable to tell which texture belonged to the skin
    /// they meant to change.
    private void ExportSkin(
        WeaponSkinView skin, Brought? brought, string directory, List<ExportedAsset> assets, List<string> skipped)
    {
        var into = Path.Combine(directory, "skin");

        foreach (var material in skin.Materials)
        {
            foreach (var texture in material.Textures)
                ExportByName(
                    texture.Bundle.Length > 0 ? texture.Bundle : material.Bundle,
                    texture.Name, Path.Combine(into, "textures"), assets, skipped, AssetClassID.Texture2D);
        }

        // Everything the model reaches, written into the folders its types belong in.
        if (brought is null) return;
        var model = brought.Model;

        foreach (var node in brought.Assets)
        {
            var bundle = node.Bundle.Length > 0 ? node.Bundle : model.Bundle;
            AssetsFileInstance holder;
            try { holder = bundles.Open(bundle); }
            catch (Exception ex) when (ex is IOException or FileNotFoundException) { continue; }

            var info = holder.file.GetAssetInfo(node.PathId);
            if (info is null) continue;

            if (Folders.TryGetValue(node.Class, out var folder))
                Once(assets, _exporter.Export(bundle, holder, info, Path.Combine(into, folder),
                    Distinct(assets, node, Path.Combine(into, folder))));
        }
    }

    /// Draws the weapon and leaves the picture in the workspace, for the pack to show itself with.
    ///
    /// The game's own icon says which weapon a pack is for and nothing about what the pack does to
    /// it, which for a texture or mesh mod is the only interesting part. This is the vanilla model,
    /// since nothing has been edited yet at this point — the editor can draw it again from whatever
    /// angle the author likes once it has.
    private void DrawPackIcon(WeaponTree tree, string directory, List<string> skipped)
    {
        try
        {
            // The biggest mesh that has textures: a weapon carries arms and muzzle-flash geometry
            // as well, and neither of those is what anybody means by the weapon.
            //
            // Gathered before choosing, because MaxBy over a sequence of tuples throws on an empty
            // one rather than answering a default — and an item with no dressed mesh at all is
            // ordinary outside the weapons. It took the whole extract down with it.
            var dressed = tree.MeshTextures
                .Select(slots => (Slots: slots, Mesh: ReadMesh(tree, slots.MeshPathId)))
                .Where(m => m.Mesh is not null)
                .Select(m => (m.Slots, Mesh: m.Mesh!))
                .ToList();
            if (dressed.Count == 0) return;

            var subject = dressed.MaxBy(m => m.Mesh.VertexCount);

            var textures = subject.Slots.BySubMesh.Select(TextureFor).ToList();

            // Standing the way the preview stands it, so a pack's picture faces the same way as the
            // model the author built it from — and so a shelf of packs does not read as a shelf of
            // weapons pointing at each other.
            var node = tree.PrefabAssets.FirstOrDefault(a => a.PathId == subject.Slots.MeshPathId);
            var bundle = node?.Bundle is { Length: > 0 } named ? named : tree.PrefabBundle;
            var standing = bundle is null
                ? (Preview.MeshRenderer.Basis?)null
                : Preview.Facing.Standing(bundles, bundle, subject.Mesh, subject.Slots.MeshPathId);

            var picture = Preview.PackIcon.Render(subject.Mesh, textures, standing: standing);
            if (Preview.PackIcon.IsBlank(picture)) return;

            Preview.PackIcon.Write(picture, Path.Combine(directory, Preview.PackIcon.FileName));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidDataException)
        {
            // A pack without a picture is a small loss; an export that failed over one is not.
            skipped.Add($"{Preview.PackIcon.FileName}: {ex.Message}");
        }
    }

    private UnityMesh? ReadMesh(WeaponTree tree, long pathId)
    {
        var node = tree.PrefabAssets.FirstOrDefault(a => a.PathId == pathId);
        if (node is null) return null;

        var bundle = node.Bundle.Length > 0 ? node.Bundle : tree.PrefabBundle;
        if (bundle is null) return null;

        var file = bundles.Open(bundle);
        var info = file.file.AssetInfos.FirstOrDefault(i => i.PathId == pathId);
        if (info is null) return null;

        var field = bundles.Context.Deserialize(file, info);
        if (field is null || Preview.AssetPreview.Mesh(field, bundles, bundle) is not { } mesh) return null;

        // Put together the way its bones put it together, or the pack's picture is of a model in
        // pieces — one weapon in three. See Skeleton.Assembled.
        return Preview.Skeleton.For(bundles, bundle, pathId) is { } skeleton
            ? skeleton.Assembled(mesh)
            : mesh;
    }

    private Preview.PreviewImage? TextureFor(Assets.AssetNode? node)
    {
        if (node is null || node.Bundle.Length == 0) return null;

        try
        {
            if (Preview.AssetPreview.Locate(bundles, node.Bundle, AssetClassID.Texture2D, node.PathId, node.Name)
                is not var (file, info)) return null;

            var field = bundles.Context.Deserialize(file, info);
            return field is null ? null : Preview.AssetPreview.Texture(bundles, node.Bundle, field);
        }
        catch (Exception e) when (e is IOException or NotSupportedException)
        {
            return null;
        }
    }

    /// Exports, then writes a pack manifest naming every replaceable file. Editing a file and
    /// running pack turns it into a mod; untouched files are left out on their own.
    public WeaponExport ExportAsWorkspace(WeaponTree tree, string outputRoot, string author, string? gameVersion)
    {
        var export = Export(tree, outputRoot);
        var chosen = Chosen(tree);

        Pack.Workspace.Create(
            export.Directory,
            id: Identity(tree, chosen),
            name: chosen is null ? tree.DisplayName : $"{tree.DisplayName} — {chosen.DisplayName ?? chosen.Record.Id}",
            author: author,
            gameVersion: gameVersion,
            assets: export.Assets,
            subject: new Pack.PackSubject
            {
                Kind = tree.Record.Kind.Pack,
                Id = AssetExporter.Sanitize(tree.Record.Slug).ToLowerInvariant(),
                Number = tree.Record.GameNumber,
                Prefab = tree.Record.PrefabName,
                Name = tree.DisplayName,
                Variant = chosen?.Record.Id ?? "",
                VariantName = chosen?.DisplayName ?? "",
                NameKey = tree.Record.LocalizationKey ?? "",
                VariantKey = chosen?.Record.LocalizationKey ?? "",
            });
        return export;
    }

    /// One look of an item written out: the item as it comes (`Skin` null) or one of its skins.
    /// <param name="Failed">Why it was not written, where it was not; the others go on regardless.</param>
    public sealed record LookExport(string? Skin, string Name, WeaponExport? Export, string? Failed);

    /// The item as it comes and then every one of its skins, each into a workspace of its own —
    /// except the default skin, which the item as it comes already covers.
    ///
    /// One workspace per look, because one pack is one look: the mod a skin's workspace builds
    /// replaces that skin and nothing else, and is turned on and off apart from the others. Each
    /// gets the folder a single extraction of it would, so asking for all of them is the same as
    /// asking for each in turn. One that cannot be written — a bundle changed by something else, a
    /// skin whose model is missing — is said and skipped, and the rest are still written.
    /// <param name="opaque">As `Opaque`, for every look.</param>
    /// <param name="maskUnused">As `MaskUnused`, for every look.</param>
    /// <param name="progress">Told before each look: which one, of how many, and its name.</param>
    public static IReadOnlyList<LookExport> ExportEveryLook(
        Assets.BundleSet bundles, WeaponTree tree, string outputRoot, string author, string? gameVersion,
        bool opaque = false, bool maskUnused = true, Action<int, int, string>? progress = null)
    {
        // Not the default skin: the item as it comes already writes into it — the two default looks
        // are painted as one (see "A weapon's paint reaches its default skin" in DESIGN.md) — and a
        // second workspace for it would build a second mod of the same pictures.
        var looks = new List<(string? Id, string Name)> { (null, tree.DisplayName) };
        looks.AddRange(tree.Skins
            .Where(s => !string.Equals(s.Record.Id, tree.Record.PrefabName + "_default", StringComparison.OrdinalIgnoreCase))
            .Select(s => ((string?)s.Record.Id, s.DisplayName ?? s.Record.Id)));

        var written = new List<LookExport>();
        for (var at = 0; at < looks.Count; at++)
        {
            var (id, name) = looks[at];
            progress?.Invoke(at + 1, looks.Count, name);

            var exporter = new WeaponExporter(bundles) { Opaque = opaque, MaskUnused = maskUnused, Skin = id };
            try
            {
                written.Add(new LookExport(id, name, exporter.ExportAsWorkspace(tree, outputRoot, author, gameVersion), null));
            }
            catch (Exception e) when (e is AlteredBundlesException or IOException or InvalidOperationException
                                          or KeyNotFoundException or NotSupportedException or UnauthorizedAccessException)
            {
                written.Add(new LookExport(id, name, null, e.Message));
            }
        }
        return written;
    }

    /// What the tool will know this mod by, for as long as it exists.
    ///
    /// A name and a number would not do it. The id was the weapon's slug alone, so every mod of a
    /// weapon was the same mod: installing a skin replaced the plain one, and installing somebody
    /// else's replaced yours — silently, because replacing by id is exactly how a mod is updated.
    /// Two mods of one weapon are two mods, whoever made them and whichever look they change.
    ///
    /// So each extraction mints its own. The readable half says what it is for at a glance; the
    /// last eight characters are what make it this one. It is written into the workspace once and
    /// never changes after — rebuilding the same workspace updates the mod it built before, which
    /// is the one case where replacing is what was meant.
    private static string Identity(WeaponTree tree, WeaponSkinView? chosen)
    {
        var slug = AssetExporter.Sanitize(tree.Record.Slug).ToLowerInvariant();
        var look = chosen is null
            ? "default"
            : AssetExporter.Sanitize(Suffix(tree, chosen)).ToLowerInvariant();
        var token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));

        return $"{slug}-{look}-{token}";
    }

    /// <param name="only">
    /// Which class to take, when the name alone is not enough. A skin's material and the texture it
    /// paints with are routinely called the same thing, and without this the material was written
    /// into the textures folder as well as its own.
    /// </param>
    private void ExportByName(
        string bundle, string name, string directory, List<ExportedAsset> into, List<string> skipped,
        AssetClassID? only = null)
    {
        AssetsFileInstance file;
        try { file = bundles.Open(bundle); }
        catch (Exception ex) { skipped.Add($"{name}: {ex.Message}"); return; }

        bool Named(AssetFileInfo i) => string.Equals(NameOf(file, i), name, StringComparison.OrdinalIgnoreCase);

        // Only the classes an export writes are worth asking the name of: a sprite or a material of
        // the same name writes nothing, and asking every object in a bundle was most of what
        // following a weapon's related assets cost. The rest are asked only to tell "not found"
        // apart from "found, and nothing to write".
        var candidates = file.file.AssetInfos
            .Where(i => only is null || i.TypeId == (int)only)
            .ToList();
        var matches = candidates
            .Where(i => Pack.Replaceable.Supports((AssetClassID)i.TypeId) && Named(i))
            .ToList();

        if (matches.Count == 0)
        {
            if (!candidates.Any(i => !Pack.Replaceable.Supports((AssetClassID)i.TypeId) && Named(i)))
                skipped.Add($"{name}: not found in '{bundle}'");
            return;
        }

        // An object already written out by another route is not written again. A related path can
        // name a shop prefab that shares its name with the item's own texture — a glider's
        // `Gliders_Shop/glider_aero_suit` beside its `glider_aero_suit` paint — and the search by name
        // then wrote that texture a second time, as a second file and a second operation on the
        // same asset, which a pack applied in whichever order its manifest happened to list them.
        foreach (var info in matches.Where(i => !into.Any(a => a.Address.PathId == i.PathId
                     && string.Equals(a.Address.Container, bundle, StringComparison.OrdinalIgnoreCase))))
            Once(into, _exporter.Export(bundle, file, info, directory));
    }

    /// The top-level name, as it always was here: a Shader answers with its empty `m_Name`, not the
    /// one `AssetNaming` finds inside it.
    private string NameOf(AssetsFileInstance file, AssetFileInfo info)
        => info.TypeId == (int)AssetClassID.Shader
            ? bundles.Context.Deserialize(file, info)?["m_Name"] is { IsDummy: false } name ? name.AsString : ""
            : bundles.Context.NameOf(file, info);
}
