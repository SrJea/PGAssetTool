using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;
using PGAssetTool.Core.Pack;
using PGAssetTool.Core.Weapons;

namespace PGAssetTool.Core.Mods;

/// Where an asset a pack names turned out to be, when it was not in the bundle the pack names.
///
/// <param name="Bundle">Where it is now. Null for one that was looked for and found nowhere.</param>
/// <param name="Hash">
/// What the manifest said the bundle it was missing from was, at the time it was looked for. Looking
/// means reading the catalogues and opening a handful of bundles, which is seconds; a mod whose asset
/// is simply not in this version of the game would otherwise pay that on every toggle. So a search
/// that found nothing is not repeated until that bundle changes — which is what an update does.
/// </param>
public sealed record Relocated(string? Bundle, string Hash);

/// Following an asset the game has moved from one bundle into another.
///
/// The game reshuffles which bundle holds what between versions and keeps the objects themselves as
/// they were. Measured over every pack kept in this tool against 24.3.7: of the twenty operations
/// that do not apply there, nineteen name an asset that is in another bundle under the same path id
/// — ecw_6's textures in ecw_4, d_w's in bhlw — and seventeen of those are the same picture to the
/// pixel. An address looks inside the bundle it names and nowhere else, so all nineteen fail without
/// this.
///
/// Where to look is the one thing the operation cannot say, and the pack's subject can: the item it
/// is for, whose bundles in this version are what the resolver finds for it. The asset still has to
/// be the same one — the same class, name and path id, or failing a path id, the one bundle among
/// them holding that name — so the subject decides where to look and never what is found.
public static class Relocation
{
    /// An operation's target as the pack names it, which is what a relocation is remembered by.
    public static string Key(AssetAddress target)
        => $"{target.Container}:{target.Class}:{target.Name}#{target.Ordinal}@{target.PathId}";

    /// The operation aimed at wherever its asset was last found.
    public static PackOperation Follow(PackOperation operation, IReadOnlyDictionary<string, Relocated>? moved)
        => moved is not null
           && moved.TryGetValue(Key(operation.Target), out var found)
           && found.Bundle is { Length: > 0 } bundle
            ? operation with { Target = operation.Target with { Container = bundle } }
            : operation;

    /// Every bundle the item a pack is for has anything in, in this version of the game.
    ///
    /// Its prefab and everything that reaches, what its meshes are drawn with, every skin's materials
    /// and model, and what the lookup table files under its name. The look the pack is for comes
    /// first, and its model is walked, since a skin's own textures are only reachable through it.
    public static IReadOnlyList<string> Candidates(BundleSet bundles, GameCatalogs catalogs, PackSubject subject)
    {
        var record = catalogs.Find(subject.Id)
            ?? (subject.Prefab.Length > 0 ? catalogs.Find(subject.Prefab) : null);
        if (record is null) return [];

        var tree = new WeaponResolver(bundles, catalogs).Resolve(record);
        var found = new List<string>();

        void Add(string? bundle)
        {
            if (bundle is { Length: > 0 } && !found.Contains(bundle, StringComparer.OrdinalIgnoreCase))
                found.Add(bundle);
        }

        Add(tree.PrefabBundle);
        foreach (var asset in tree.PrefabAssets) Add(asset.Bundle);
        foreach (var slots in tree.MeshTextures)
        foreach (var texture in slots.BySubMesh)
            Add(texture?.Bundle);

        var skins = tree.Skins
            .OrderByDescending(s => string.Equals(s.Record.Id, subject.Variant, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var skin in skins)
        {
            foreach (var material in skin.Materials)
            {
                Add(material.Bundle);
                foreach (var texture in material.Textures) Add(texture.Bundle);
            }

            if (skin.Model is not { } model) continue;
            Add(model.Bundle);

            if (!string.Equals(skin.Record.Id, subject.Variant, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var asset in ModelClosure(bundles, model)) Add(asset.Bundle);
        }

        foreach (var related in tree.Related) Add(related.Bundle);

        // The shop icon, which is found by the weapon's slug rather than its number and so is not
        // among the related assets for most weapons — #16's is Beretta_icon1_big.
        Add(tree.Icon?.Container);

        return found.Where(bundles.BundleNames.Contains).ToList();
    }

    private static IEnumerable<AssetNode> ModelClosure(BundleSet bundles, SkinModel model)
        => model.Reaches(bundles, new BundleGraph(bundles).Resolve);

    /// Which of the bundles given holds this asset, other than the one it was missing from.
    ///
    /// A bundle holding it under the same path id is the answer outright. Failing that, a bundle
    /// holding one of that class and name is the answer only if it is the only one: the same name in
    /// two of an item's bundles is two assets, and picking one would be writing into a guess.
    public static string? Find(BundleSet bundles, IReadOnlyList<string> candidates, AssetAddress target, string missingFrom)
    {
        var byName = new List<string>();

        foreach (var bundle in candidates)
        {
            if (string.Equals(bundle, missingFrom, StringComparison.OrdinalIgnoreCase)) continue;

            AssetsFileInstance file;
            try { file = bundles.Open(bundle); }
            catch (Exception e) when (e is IOException or KeyNotFoundException) { continue; }

            if (new ContainerIndex(bundles.Context).Resolve(target with { Container = bundle }, file, out var byPathId)
                is null) continue;

            if (byPathId) return bundle;
            byName.Add(bundle);
        }

        return byName.Count == 1 ? byName[0] : null;
    }
}
