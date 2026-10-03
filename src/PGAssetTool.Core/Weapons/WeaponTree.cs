using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;

namespace PGAssetTool.Core.Weapons;

/// A skin, with the materials it names resolved to the bundles holding them and the textures those
/// materials use. Resolved here rather than in the tree so both the exporter and the GUI see the
/// same thing.
public sealed record WeaponSkinView(
    SkinRecord Record, string? DisplayName, IReadOnlyList<SkinMaterial> Materials, SkinModel? Model);

/// A skin that replaces the weapon rather than repainting it.
///
/// Most skins are a set of materials over the same geometry. Some bring their own model, filed
/// under CustomModels by the skin's own id — and for those, the materials the skin names do not
/// resolve against the usual roots at all, because the model carries what it needs. Recorded as a
/// place rather than as its contents: reading the closure of every skin would cost a bundle walk
/// per skin every time a weapon is selected, and it is wanted only when one is being exported.
public sealed record SkinModel(string AssetPath, string Bundle)
{
    /// Everything the model reaches, from the bundle it lives in.
    ///
    /// The object is found by the leaf of the path that named it, which is the same rule the
    /// resolver uses everywhere else. Written out twice before this — once to list a skin's
    /// materials and once to work out which bundles a pack of it can have moved into — and the two
    /// copies had come to disagree about what to do when the bundle is not there.
    ///
    /// Answers nothing rather than throwing: a model that cannot be read is a model that brings no
    /// materials, and neither caller has anything better to do with the news.
    /// <param name="resolve">
    /// How to follow a reference into another bundle. The tree keeps one of these across a whole
    /// weapon; a caller with no reason to cache can build one for the walk.
    /// </param>
    public IReadOnlyList<AssetNode> Reaches(BundleSet bundles, ReferenceWalker.ExternalResolver resolve)
    {
        try
        {
            var file = bundles.Open(Bundle);
            var name = AssetPath[(AssetPath.LastIndexOf('/') + 1)..];
            var root = ReferenceWalker.FindByName(bundles.Context, file, AssetClassID.GameObject, name);

            return root is null
                ? []
                : ReferenceWalker.Closure(
                    bundles.Context, file, root.PathId, resolve, skip: WeaponResolver.Opaque);
        }
        catch (Exception e) when (e is IOException or KeyNotFoundException)
        {
            return [];
        }
    }
}

/// <param name="Main">
/// The texture in the material's main slot — the skin itself, as opposed to the gloss, noise and
/// mask maps beside it. Null for a material that binds no texture at all.
///
/// The distinction is the material's, not a guess from the name: a mask is a mask because of the
/// slot it is bound to, and the game's own naming is not consistent enough to read it any other
/// way. Weapon834 alone has GlossTexture, Gloss2, Noisemap_2 and 'Wawes 1' among its skins.
/// </param>
public sealed record SkinMaterial(
    string Path, string Bundle, string Name, IReadOnlyList<AssetNode> Textures, AssetNode? Main)
{
    /// Where one of this material's textures actually lives.
    ///
    /// The walk names a bundle only for the textures it had to cross a file to reach; one sitting
    /// in the same file as the material is recorded with none, and the material's own is then the
    /// answer. Asked in more than one place — the tree files a texture under this name and the
    /// preview looks the same texture up again by it — and the two answers drifting apart is not
    /// something either end can see, so the rule lives here rather than at each of them.
    public (string Bundle, long PathId) Locate(AssetNode texture)
        => (texture.Bundle.Length > 0 ? texture.Bundle : Bundle, texture.PathId);
}

/// The textures a mesh is drawn with, in submesh order: entry i belongs to submesh i, and a null
/// entry is a material slot whose texture could not be found.
public sealed record MeshTextures(long MeshPathId, IReadOnlyList<AssetNode?> BySubMesh);

/// An asset tied to the weapon by the number in its name rather than by a binary reference.
/// <param name="Asset">
/// The object the path names, when it is one this tool can write back — the chat icon and a skin's
/// shop icon are textures like any other, and an extract writes them out. Null for the rest: a skin
/// definition and a profile animation are context, and there is nothing to be done with them but
/// read where they live. Resolved through the bundle's own container table; see `BundleContents`.
/// </param>
public sealed record RelatedAsset(string Namespace, string Path, string? Bundle, AssetNode? Asset = null);

public sealed record WeaponTree(
    WeaponRecord Record,
    string DisplayName,
    string? PrefabBundle,
    IReadOnlyList<AssetNode> PrefabAssets,
    IReadOnlyList<WeaponSkinView> Skins,
    IReadOnlyList<RelatedAsset> Related,
    IconLocation? Icon,
    IReadOnlyList<string> UnresolvedReasons,
    IReadOnlyList<MeshTextures> MeshTextures,
    AssetNode? MainMesh)
{
    /// The path the game loads the prefab by, under whichever of its kind's roots it is actually
    /// filed: `Weapons/Weapon834`, `Boots/BerserkBoots_up1`. Null for a tree made without one.
    public string? PrefabPath { get; init; }
}

/// Assembles everything belonging to one weapon. Resolution happens on demand: the catalogs plus
/// the single bundle holding the prefab are enough, so nothing is precomputed.
public sealed class WeaponResolver(BundleSet bundles, GameCatalogs catalogs)
{
    private readonly IconResolver _icons = new(bundles, catalogs.Lookup);

    /// Kept for the life of the resolver: a container table read for one weapon's icons is the same
    /// table the next weapon's are in.
    private readonly BundleContents _contents = new(bundles);

    /// How a material binds its paint is the same question wherever it is asked from, and the
    /// editor asks it too — of a mesh rather than of a weapon. One implementation of it, here.
    private readonly Dressing _dressing = new(bundles);

    /// Following a pointer out of one bundle and into the next, shared with the dressing so the
    /// index of which bundle holds which file is built once.
    private BundleGraph Graph => _dressing.Graph;

    /// Reached by weapons, but not part of one: shared engine assets that cannot be replaced here.
    /// Not worth walking into. A shader is neither replaceable nor readable, and dumping the
    /// closure of one produces tens of megabytes of JSON for something nobody can act on.
    public static readonly IReadOnlySet<AssetClassID> Opaque = new HashSet<AssetClassID> { AssetClassID.Shader };

    public WeaponTree Resolve(WeaponRecord record)
    {
        var unresolved = new List<string>();

        // A hidden weapon has no localization key at all, so a missing name is expected rather than
        // a gap worth reporting.
        var displayName = catalogs.Localization.Translate(record.LocalizationKey);
        if (displayName is null && !record.IsHidden)
            unresolved.Add($"no translation for '{record.LocalizationKey}' in {catalogs.Localization.Language}");

        // Whichever of the kind's roots the game actually files this one under. Every kind but
        // weapons has more than one, because the game moved some of them and left the rest.
        var prefabPath = record.Kind.PathsFor(record.PrefabName)
            .FirstOrDefault(p => catalogs.Lookup.BundleFor(p) is not null) ?? record.AssetPath;

        var prefabBundle = catalogs.Lookup.BundleFor(prefabPath);
        if (prefabBundle is null) unresolved.Add($"'{prefabPath}' is not in the asset lookup table");

        var assets = new List<AssetNode>();
        if (prefabBundle is not null)
        {
            var file = bundles.Open(prefabBundle);
            var named = prefabPath[(prefabPath.LastIndexOf('/') + 1)..];
            var root = Prefab(file, prefabBundle, prefabPath);

            if (root is null)
                unresolved.Add($"'{named}' was not found inside bundle '{prefabBundle}'");
            else
                // Filtered after the walk and never during it: what a withheld object points at is
                // found exactly as before — a texture a component names is still a texture — and it
                // is the object itself that does not appear. See Replaceable for which and why.
                assets = ReferenceWalker
                    .Closure(bundles.Context, file, root.Value, Graph.Resolve, skip: Opaque)
                    .Where(a => Pack.Replaceable.CanShow(a.Class))
                    .ToList();
        }

        // A weapon's skins are a registry of their own, three bundles deep, and are looked up by
        // the weapon's index. Every other kind keeps its one skin beside the thing itself, under a
        // root named after the kind — so for those the skin is a related asset like any other and
        // there is nothing to look up.
        var skins = record.Kind == ItemKinds.Weapon
            ? catalogs.Skins.ForWeapon(record.Index)
                .Select(s => (Skin: s, Model: CustomModel(s)))
                .Select(s => new WeaponSkinView(
                    s.Skin, catalogs.Localization.Translate(s.Skin.LocalizationKey),
                    Materials(s.Skin, s.Model), s.Model))
                .ToList()
            : [];

        // The skin materials are already listed under each skin, so they are left out here.
        var related = Related(record, prefabPath)
            .Select(p => Resolved(NamespaceOf(p), p, catalogs.Lookup.BundleFor(p)))
            .OrderBy(r => r.Namespace, StringComparer.Ordinal)
            .ThenBy(r => r.Path, StringComparer.Ordinal)
            .ToList();

        var icon = _icons.ForWeapon(record);
        if (icon is null && !record.IsHidden)
            unresolved.Add($"no icon texture named '{record.Slug}{IconResolver.Suffix}'");

        var dressing = prefabBundle is null ? [] : TexturesForMeshes(prefabBundle, assets);

        return new WeaponTree(
            record, displayName ?? record.Slug, prefabBundle, assets, skins, related, icon, unresolved,
            dressing,
            prefabBundle is null ? null : MainMesh(prefabBundle, assets, dressing, record.Slug, record.PrefabName))
        {
            PrefabPath = prefabPath,
        };
    }

    /// The GameObject the game loads for this path, as a path id.
    ///
    /// The bundle's own container table first, because a name is not unique inside a bundle: a
    /// pet's bundle holds `Pets/Content/pet_alien_cat`, the model, and `Pets/Infos/pet_alien_cat`,
    /// a description of it, and both GameObjects are called `pet_alien_cat`. Asked by name, the
    /// walk started from whichever came first in the file — the description, for 50 of the 112
    /// pets — and the pet came out as a GameObject and a Transform with no model at all.
    ///
    /// By the leaf of the path, without minding case, only when the table names nothing. The
    /// registry's id and the prefab's own name are the same string for a weapon and only nearly
    /// for everything else: `BerserkBoots_Up1` is filed as `BerserkBoots_up1`, which the lookup
    /// finds and an exact search for the id does not.
    private long? Prefab(AssetsFileInstance file, string bundle, string assetPath)
    {
        if (_contents.Locate(bundle, assetPath) is { Class: AssetClassID.GameObject } listed)
            return listed.PathId;

        var named = assetPath[(assetPath.LastIndexOf('/') + 1)..];
        return ReferenceWalker.FindByName(
            bundles.Context, file, AssetClassID.GameObject, named, StringComparison.OrdinalIgnoreCase)?.PathId;
    }

    /// Everything else the game files under this item's name.
    ///
    /// A weapon's belongings are found by the number in its prefab name — the icon, the chat icon,
    /// the profile, the pickup — because that number is in every one of their paths and the weapon's
    /// id is in none of them. Everything else is the other way round: a hat's skin is
    /// `HatsSkins/skin_<id>` and its icon `OfferIcons/<id>_icon1_big`, so its own id is what finds
    /// them, and a search for it turns up the lot without a table to consult.
    private IEnumerable<string> Related(WeaponRecord record, string prefabPath)
    {
        if (record.Kind == ItemKinds.Weapon)
            return catalogs.Lookup.PathsForWeapon(record.PrefabNumber)
                .Where(p => p != prefabPath
                    && !p.StartsWith("WeaponSkinsV2/WeaponSkinAssets/", StringComparison.Ordinal));

        // Another item whose id is this one and more owns whatever names it. `pet_horse` and
        // `pet_horse_figure` are two pets, and every path of the second one names the first as well
        // by the rule below — so Parade Horse collected Knight Figure's prefab and icons, and a pack
        // made from it could rewrite another item altogether. Nine items in the game have a
        // neighbour like that: Evil Dragon and Dragon Form Glider, Block Car and Block Racer,
        // Foundation Spec Ops and three of its squad.
        var longer = OtherIds()
            .Where(id => id.Length > record.Slug.Length
                && id.Contains(record.Slug, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return catalogs.Lookup.Entries
            .Select(e => e.Key)
            .Where(p => p != prefabPath && Names(p, record.Slug) && !longer.Any(id => Names(p, id)))
            .ToList();
    }

    /// Every id the catalogue knows outside the weapons, once per resolver.
    private IReadOnlyList<string> OtherIds()
        => _otherIds ??= [.. catalogs.Kinds
            .Where(k => k != ItemKinds.Weapon)
            .SelectMany(k => catalogs.Of(k))
            .Select(r => r.Slug)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private IReadOnlyList<string>? _otherIds;

    /// A related path, with the object behind it when that object is one this tool writes back.
    ///
    /// Asked for every related path of every item that gets selected, which is why it goes through
    /// the bundle's own table rather than looking for the name: 1.6ms a weapon, and the classes it
    /// answers are what tells an icon from a skin definition.
    private RelatedAsset Resolved(string space, string path, string? bundle)
    {
        if (bundle is null) return new RelatedAsset(space, path, null);

        var asset = _contents.Locate(bundle, path);
        return new RelatedAsset(
            space, path, bundle, asset is not null && Pack.Replaceable.Supports(asset.Class) ? asset : null);
    }

    /// Whether a path is about this item rather than about one whose id merely starts the same way.
    ///
    /// `hat_sweet` must not collect `hat_sweet_dreams`, so the id has to end where the path's own
    /// name does or run into a suffix the game adds — `_icon1_big`, `_preview`, `_game`.
    private static bool Names(string path, string id)
    {
        var leaf = path[(path.LastIndexOf('/') + 1)..];
        var at = leaf.IndexOf(id, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;

        // Something before it has to be a word boundary too: `skin_hat_sweet` counts, `xhat_sweet`
        // does not.
        if (at > 0 && leaf[at - 1] is not ('_' or '-')) return false;

        var after = at + id.Length;
        return after == leaf.Length || leaf[after] is '_' or '-' or '.';
    }

    /// Which of a model's meshes is the thing it is a model of.
    ///
    /// Every weapon's prefab holds the player's arms as well as the gun. They are the same class in
    /// the same bundle, and the name is no help: the early weapons' meshes are not named after the
    /// weapon at all — #1's is 'pixlgun_mesh', #5 Heavy Machine Gun's is 'Machinegun_Mesh',
    /// #30 Guerilla Rifle's is 'SVD_2_mesh' — while the arms are named the same in all of them, which
    /// is a rule about the arms rather than about the weapon and would break the moment it moved.
    ///
    /// So it is decided by what each mesh is drawn with: the one wearing a texture that belongs to
    /// this weapon is the weapon, and the arms wear one shared by every weapon in the game.
    ///
    /// A texture belongs to the weapon if it is bundled with it or named after it, and it takes
    /// only one of the two. Neither holds on its own — #1045 Pulling Sucker Gun keeps its map in a
    /// shared bundle and #2 Shotgun's map is not named after the weapon either — but nothing the
    /// arms wear satisfies either one, which is the point: their texture is shared, so it is
    /// neither in one weapon's bundle nor named for one. The mesh's own name is kept as a second
    /// voice, worth something when it agrees and outweighed when it does not, and having anything
    /// to wear breaks a tie between two meshes neither of which is dressed from here.
    ///
    /// <param name="named">Names the thing might be called after, best first.</param>
    /// The first-person arms, which every weapon's prefab carries under this name.
    public const string Arms = "Arms_Mesh";

    public static AssetNode? MainMesh(
        string bundle, IReadOnlyList<AssetNode> assets, IReadOnlyList<MeshTextures> dressing,
        params string[] named)
    {
        var meshes = assets.Where(a => a.Class == AssetClassID.Mesh).ToList();
        if (meshes.Count <= 1) return meshes.FirstOrDefault();

        // Stable, so meshes that score the same keep the order the walk found them in — except
        // that between two the data cannot tell apart, the arms lose. That is the one place a name
        // is trusted outright, and it is a name the game gives every weapon rather than one it
        // gives this weapon: 84 of 85 sampled across the catalogue carry a mesh called exactly
        // this. It settles the handful the tests above cannot, where a weapon is called one thing
        // and its art another — #1081 Hammer Sword is 'class_knight_hammer' wearing
        // 'brave_lion_map' out of a shared bundle, and nothing about it says which mesh is a sword.
        return meshes
            .OrderByDescending(Score)
            .ThenBy(m => string.Equals(m.Name, Arms, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .First();

        bool Called(string what)
            => named.Any(n => n.Length > 0 && what.Contains(n, StringComparison.OrdinalIgnoreCase));

        int Score(AssetNode mesh)
        {
            var worn = dressing.FirstOrDefault(d => d.MeshPathId == mesh.PathId)?.BySubMesh
                .Where(t => t is not null).ToList() ?? [];

            var its = worn.Any(t => Called(t!.Name)
                || string.Equals(t.Bundle, bundle, StringComparison.OrdinalIgnoreCase));

            return (its ? 4 : 0) + (Called(mesh.Name) ? 2 : 0) + (worn.Count > 0 ? 1 : 0);
        }
    }

    /// Which textures each mesh in the weapon is actually drawn with.
    ///
    /// A Mesh asset says nothing about its appearance; the renderer that draws it holds the
    /// materials, and Unity pairs submesh i with material i. A SkinnedMeshRenderer names its own
    /// mesh, while a MeshRenderer leaves that to a MeshFilter on the same GameObject, so both
    /// shapes have to be followed to cover every weapon.
    /// The same question asked of something resolved later: a skin's own model, whose closure is
    /// read when somebody opens it rather than when the weapon is selected.
    ///
    /// Public because the answer is the same one, worked out the same way. A second implementation
    /// for the skin case would be a second thing to get wrong about how Unity pairs a submesh with
    /// a material.
    public IReadOnlyList<MeshTextures> TexturesFor(string bundle, IReadOnlyList<AssetNode> assets)
        => TexturesForMeshes(bundle, assets);

    private IReadOnlyList<MeshTextures> TexturesForMeshes(string prefabBundle, IReadOnlyList<AssetNode> assets)
        => _dressing.OfModel(prefabBundle, assets);

    private AssetNode? MainTextureIn(AssetsFileInstance file, string bundle, AssetTypeValueField material)
        => _dressing.MainTextureIn(file, bundle, material);

    private AssetsFileInstance? SafeOpen(string bundle)
    {
        try { return bundles.Open(bundle); }
        catch (Exception e) when (e is IOException or FileNotFoundException) { return null; }
    }

    /// The materials a skin names, and the textures each of them uses.
    ///
    /// A skin records its materials as paths relative to one of the skin roots rather than as
    /// pointers, so they have to be looked up rather than followed. Anything that does not resolve
    /// is left out: the tree shows what can be acted on, not what is missing.
    /// The model a skin brings with it, if it brings one. Filed under the skin's own id.
    private SkinModel? CustomModel(SkinRecord skin)
    {
        var path = CustomModelRoot + skin.Id;
        return catalogs.Lookup.BundleFor(path) is { } bundle ? new SkinModel(path, bundle) : null;
    }

    public const string CustomModelRoot = "WeaponSkinsV2/CustomModels/";

    ///
    /// A skin that brings a model can name materials the lookup table has never heard of, because
    /// they are the model's own and only the model refers to them. #416's Northern Lights names
    /// two by their bare names, and both are Materials in the bundle its model is in. So a name
    /// the table does not know is looked for there before it is given up on — in that bundle and
    /// nowhere else, since a bare name elsewhere could be anybody's.
    private IReadOnlyList<SkinMaterial> Materials(SkinRecord skin, SkinModel? model)
    {
        var materials = new List<SkinMaterial>();

        // The model's own materials, walked only if a name is not found any other way — which is six
        // skins in the game — since walking a model is the cost the tree otherwise defers.
        IReadOnlyList<AssetNode>? walked = null;
        IReadOnlyList<AssetNode> OfModel() => walked ??= ModelMaterials(model);

        foreach (var path in skin.MaterialPaths)
        {
            string full, bundle;
            if (catalogs.Lookup.Resolve(path, AssetLookup.SkinAssetRoots) is { } filed) (full, bundle) = filed;
            else if (model is not null) (full, bundle) = (path, model.Bundle);
            else continue;

            var name = full[(full.LastIndexOf('/') + 1)..];
            AssetsFileInstance file;
            try { file = bundles.Open(bundle); }
            catch (Exception e) when (e is IOException or FileNotFoundException) { continue; }

            var info = ReferenceWalker.FindByName(
                bundles.Context, file, AssetClassID.Material, name, StringComparison.OrdinalIgnoreCase);

            // Not by its own name either. Six skins name a material a little differently from what
            // their model calls it — `Weapon917_plastic_instigator` for `plastic_instigator_map`,
            // `Weapon1819_idols_slayer.asset` for `Weapon1819_idols_slayer` — so it is taken from
            // among the model's own materials, and only when exactly one is the same name once the
            // prefix, the `_map` and any extension are set aside. Three more name nothing at all.
            if (info is null && model is not null && Alike(OfModel(), name) is { } alike)
            {
                bundle = alike.Bundle.Length > 0 ? alike.Bundle : model.Bundle;
                try { file = bundles.Open(bundle); }
                catch (Exception e) when (e is IOException or FileNotFoundException) { continue; }

                info = file.file.GetAssetInfo(alike.PathId);
                name = alike.Name;
            }

            if (info is null) continue;

            // Only the textures, not the whole closure: a material also reaches its shader, and a
            // shader is neither replaceable nor worth a row.
            var textures = ReferenceWalker
                .Closure(bundles.Context, file, info.PathId, Graph.Resolve, skip: Opaque)
                .Where(n => n.Class == AssetClassID.Texture2D)
                .ToList();

            var field = bundles.Context.Deserialize(file, info);
            var main = field is null ? null : MainTextureIn(file, bundle, field);

            materials.Add(new SkinMaterial(full, bundle, name, textures, main));
        }

        return materials;
    }

    /// Every material a skin's own model reaches.
    private IReadOnlyList<AssetNode> ModelMaterials(SkinModel? model)
    {
        if (model is null) return [];

        return [.. model.Reaches(bundles, Graph.Resolve).Where(n => n.Class == AssetClassID.Material)];
    }

    /// A material's name with what a skin and its model disagree about set aside: a `WeaponNNN_`
    /// prefix, a `_map` suffix, and an extension.
    public static string Plainly(string name)
    {
        var plain = Path.GetFileNameWithoutExtension(name);

        if (plain.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase))
        {
            var at = "Weapon".Length;
            while (at < plain.Length && char.IsAsciiDigit(plain[at])) at++;
            if (at > "Weapon".Length && at < plain.Length && plain[at] == '_') plain = plain[(at + 1)..];
        }

        return plain.EndsWith("_map", StringComparison.OrdinalIgnoreCase) ? plain[..^"_map".Length] : plain;
    }

    /// The one material among these that is plainly the one named, or null for none or several.
    public static AssetNode? Alike(IEnumerable<AssetNode> materials, string named)
    {
        var plain = Plainly(named);
        if (plain.Length == 0) return null;

        var found = materials
            .Where(m => m.Class == AssetClassID.Material
                && string.Equals(Plainly(m.Name), plain, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(m => (m.Bundle.ToLowerInvariant(), m.PathId))
            .Take(2)
            .ToList();

        return found.Count == 1 ? found[0] : null;
    }

    private static string NamespaceOf(string path)
    {
        var slash = path.IndexOf('/');
        return slash > 0 ? path[..slash] : "(root)";
    }
}
