using System.Text.RegularExpressions;
using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Catalog;

/// The game's registries, each a MonoBehaviour in its own small bundle. Loading all of them takes
/// roughly a third of a second, which is why item lookups need no precomputed index.
public sealed class GameCatalogs
{
    public const string DefaultLanguage = "l_en-gb";

    private GameCatalogs(
        ItemCatalog items, AssetLookup lookup, Localization localization, SkinCatalog skins,
        GearCatalog gear)
        => (Items, Lookup, Localization, Skins, Gear) = (items, lookup, localization, skins, gear);

    /// Every weapon name in every language, for searching. Filled in after construction because it
    /// needs the item catalog that is being built alongside it.
    public WeaponNames? Names { get; private set; }

    public ItemCatalog Items { get; }
    public AssetLookup Lookup { get; }
    public Localization Localization { get; }
    public SkinCatalog Skins { get; }

    /// Everything the game sells that is not a weapon, by kind.
    public GearCatalog Gear { get; }

    /// The items of one kind, whichever kind that is. Weapons come from their own registry and
    /// everything else from the generated ones, and nothing outside here needs to know that.
    public IReadOnlyList<WeaponRecord> Of(ItemKind kind)
        => kind == ItemKinds.Weapon ? Items.Weapons.ToList() : Gear.Of(kind);

    /// The kinds with anything in them, so a picker offers nothing that leads nowhere.
    public IReadOnlyList<ItemKind> Kinds
        => [.. ItemKinds.All.Where(k => k == ItemKinds.Weapon || Gear.Count(k) > 0)];

    /// Whatever the query names, of whatever kind.
    ///
    /// Weapons first and by their own rules — a bare number is an in-game number and nothing else —
    /// because that is what somebody typing at this tool almost always means. Everything else is
    /// found by the id the game knows it as, which is what the lists show and what a pack records.
    public WeaponRecord? Find(string query)
    {
        if (Items.Find(query) is { } weapon) return weapon;

        foreach (var kind in Kinds.Where(k => k != ItemKinds.Weapon))
            if (Gear.Of(kind).FirstOrDefault(
                    r => string.Equals(r.Slug, query, StringComparison.OrdinalIgnoreCase)) is { } found)
                return found;

        return null;
    }

    /// The game's own translation tables, as bundle name and the language in its own script.
    ///
    /// Read from the bundle list rather than hardcoded, so a language added by an update appears
    /// without a change here; one whose name is not known shows its bundle name instead of being
    /// hidden.
    public static IReadOnlyList<(string Bundle, string Name)> Languages(BundleSet bundles)
        => bundles.BundleNames
            .Where(b => b.StartsWith("l_", StringComparison.Ordinal))
            .OrderBy(b => b, StringComparer.Ordinal)
            .Select(b => (b, NativeNames.GetValueOrDefault(b, b)))
            .ToList();

    /// How each language calls itself, for a picker. Missing entries fall back to the bundle name.
    private static readonly Dictionary<string, string> NativeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["l_de"] = "Deutsch",
        ["l_en-gb"] = "English",
        ["l_es"] = "Español",
        ["l_fr"] = "Français",
        ["l_ja"] = "日本語",
        ["l_ko"] = "한국어",
        ["l_pt-br"] = "Português (Brasil)",
        ["l_ru-mo"] = "Русский",
        ["l_tr"] = "Türkçe",
        ["l_zh"] = "简体中文",
        ["l_zh-cht"] = "繁體中文",
    };

    /// <param name="remember">
    /// A directory the name tables may be kept in between runs — a tool's own data folder. Reading
    /// all eleven of the game's is 355ms of the 930 a window waits before it has a list, and they
    /// change only when the game does. Null reads them every time. See WeaponNames.
    /// </param>
    public static GameCatalogs Load(
        BundleSet bundles, string language = DefaultLanguage, string? remember = null)
    {
        var catalogs = new GameCatalogs(
            ItemCatalog.Load(bundles),
            AssetLookup.Load(bundles),
            Localization.Load(bundles, language),
            SkinCatalog.Load(bundles),
            GearCatalog.Load(bundles));

        catalogs.Names = WeaponNames.Load(bundles, catalogs.Items, remember);
        return catalogs;
    }

    /// The same registries read again in another language.
    ///
    /// Only the translation table depends on the language; the items, the lookup table, the skins
    /// and the search index do not. Rebuilding all of them to change which name is displayed costs
    /// the better part of a second for no reason.
    public GameCatalogs WithLanguage(BundleSet bundles, string language)
        => new(Items, Lookup, Localization.Load(bundles, language), Skins, Gear) { Names = Names };
}

/// Maps a logical asset path such as "Weapons/Weapon25" to the bundle holding it. The game resolves
/// these strings at runtime, so following binary PPtr references alone leaves the graph disconnected.
public sealed partial class AssetLookup
{
    private readonly Dictionary<string, string> _bundleByPath;
    private readonly Dictionary<int, List<string>> _pathsByWeapon;

    private AssetLookup(Dictionary<string, string> bundleByPath, Dictionary<int, List<string>> pathsByWeapon)
        => (_bundleByPath, _pathsByWeapon) = (bundleByPath, pathsByWeapon);

    /// A weapon's assets are spread over a dozen namespaces, tied together only by the number in
    /// their name. Extracting that token rather than enumerating known namespaces means namespaces
    /// added by a future update are picked up without a code change. Rays use both spellings.
    [GeneratedRegex(@"\b(?:Weapon|Ray)(\d+)")]
    private static partial Regex WeaponToken { get; }

    public static AssetLookup Load(BundleSet bundles)
    {
        var entries = bundles.MonoBehaviour("assets_lookup_table", "*")["TableEntries"]["Array"];
        // Paths collide when compared case-insensitively; the first entry wins, as with localization.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byWeapon = new Dictionary<int, List<string>>();

        foreach (var e in entries.Children)
        {
            var path = e["AssetPath"].AsString;
            if (!map.TryAdd(path, e["BundleName"].AsString)) continue;

            foreach (Match match in WeaponToken.Matches(path))
            {
                if (!int.TryParse(match.Groups[1].ValueSpan, out var number)) continue;
                if (!byWeapon.TryGetValue(number, out var list)) byWeapon[number] = list = [];
                if (!list.Contains(path)) list.Add(path);
            }
        }
        return new AssetLookup(map, byWeapon);
    }

    /// Every asset path whose name carries this prefab number, in any namespace.
    public IReadOnlyList<string> PathsForWeapon(int prefabNumber)
        => _pathsByWeapon.GetValueOrDefault(prefabNumber) ?? (IReadOnlyList<string>)[];

    /// Skin definitions store material paths relative to these roots rather than in full.
    public static readonly string[] SkinAssetRoots =
        ["WeaponSkinsV2/WeaponSkinAssets/", "WeaponSkinsV2/CustomModels/", "WeaponSkinsV2/"];

    public int Count => _bundleByPath.Count;
    public string? BundleFor(string assetPath) => _bundleByPath.GetValueOrDefault(assetPath);
    public IEnumerable<KeyValuePair<string, string>> Entries => _bundleByPath;

    /// Resolves a path that may be written relative to one of the given roots. Returns the full
    /// path that matched along with its bundle.
    public (string Path, string Bundle)? Resolve(string assetPath, params string[] roots)
    {
        if (_bundleByPath.TryGetValue(assetPath, out var direct)) return (assetPath, direct);
        foreach (var root in roots)
            if (_bundleByPath.TryGetValue(root + assetPath, out var prefixed))
                return (root + assetPath, prefixed);
        return null;
    }
}

/// Display strings for one language. Terms are duplicated in the source data; the first wins.
public sealed class Localization
{
    private readonly Dictionary<string, string> _terms;

    private Localization(string language, Dictionary<string, string> terms)
        => (Language, _terms) = (language, terms);

    public string Language { get; }

    public static Localization FromTerms(string language, IEnumerable<KeyValuePair<string, string>> terms)
        => new(language, terms.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal));

    public static Localization Load(BundleSet bundles, string language)
    {
        var terms = new Dictionary<string, string>(StringComparer.Ordinal);
        var array = bundles.MonoBehaviour(language, "*")["mTerms"]["Array"];
        foreach (var e in array.Children)
            terms.TryAdd(e["term"].AsString, e["translation"].AsString);
        return new Localization(language, terms);
    }

    public string? Translate(string? term) => term is null ? null : _terms.GetValueOrDefault(term);
}
