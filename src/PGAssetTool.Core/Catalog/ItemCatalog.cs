using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Catalog;

/// Every item in the game carries an index of the form `ordinal * 1000 + categoryCode`.
public enum ItemCategory
{
    Weapon = 1,
    Wearable = 2,
    WeaponSkin = 14,
    Consumable = 19,
    Cape = 21,
    WearableSkin = 29,
    CraftSet = 31,
}

public static class ItemIndex
{
    public const int Scale = 1000;

    public static int For(int ordinal, ItemCategory category) => ordinal * Scale + (int)category;
    public static int Ordinal(int index) => index / Scale;
    public static ItemCategory Category(int index) => (ItemCategory)(index % Scale);

    /// Weapon 25 lives at index 25001 and its prefab is named "Weapon25".
    public static int ForWeapon(int weaponNumber) => For(weaponNumber, ItemCategory.Weapon);
}

/// A weapon carries two unrelated numbers, and confusing them points at the wrong weapon.
///
/// `GameNumber` is the one shown in game and the one players use: dense, unique, 1..N. It comes
/// from `_weaponNumber`. `PrefabNumber` is the number embedded in the prefab name and in every
/// related asset path, and it is sparse — it runs well past the weapon count. The two agree for
/// only six of the 1517 weapons.
public sealed record WeaponRecord(
    int Index,
    int GameNumber,
    int PrefabNumber,
    string PrefabName,
    string Slug,
    string Tag,
    string LocalizationKey)
{
    /// Weapons with no localization key never appear in the game's own list.
    public bool IsHidden => LocalizationKey.Length == 0;

    /// What sort of thing this is.
    ///
    /// Weapons unless said otherwise, which is what most places that build one of these mean.
    /// Everything that reads an item — the resolver, the tree, the preview, the exporter — goes
    /// through this rather than knowing where weapons in particular are kept.
    public ItemKind Kind { get; init; } = ItemKinds.Weapon;

    /// Where the game files this one's prefab: `Weapons/Weapon834`, `Hats/hat_sweet`.
    public string AssetPath => Kind.PathFor(PrefabName);

    /// The number a player reads off the list, where the kind has one. Weapons are numbered and
    /// nothing else is, which is why the rest are found by name.
    public bool IsNumbered => Kind == ItemKinds.Weapon;
}

/// The weapon registry in the `it_d` bundle: `itemDatas` gives every item a slug, `itemRecords`
/// carries the weapon-specific fields.
public sealed class ItemCatalog
{
    private readonly Dictionary<int, WeaponRecord> _byIndex;

    private ItemCatalog(Dictionary<int, WeaponRecord> byIndex) => _byIndex = byIndex;

    public static ItemCatalog FromRecords(IEnumerable<WeaponRecord> records)
        => new(records.ToDictionary(r => r.Index));

    public static ItemCatalog Load(BundleSet bundles)
    {
        var storage = bundles.MonoBehaviour("it_d", "ItemsDataStorage");

        var slugs = new Dictionary<int, string>();
        foreach (var e in storage["itemDatas"]["Array"].Children)
            if (ItemIndex.Category(e["index"].AsInt) == ItemCategory.Weapon)
                slugs.TryAdd(e["index"].AsInt, e["id"].AsString);

        var byIndex = new Dictionary<int, WeaponRecord>();
        foreach (var e in storage["itemRecords"]["Array"].Children)
        {
            int index = e["_index"].AsInt;
            var prefab = e["_prefabName"].AsString;
            byIndex.TryAdd(index, new WeaponRecord(
                Index: index,
                GameNumber: e["_weaponNumber"].AsInt,
                PrefabNumber: ItemIndex.Ordinal(index),
                PrefabName: prefab,
                Slug: slugs.GetValueOrDefault(index, prefab),
                Tag: e["_tag"].AsString,
                LocalizationKey: e["localizeWeaponKey"].AsString));
        }
        return new ItemCatalog(byIndex);
    }

    public int Count => _byIndex.Count;
    public IEnumerable<WeaponRecord> Weapons => _byIndex.Values.OrderBy(w => w.GameNumber);

    public WeaponRecord? ByIndex(int index) => _byIndex.GetValueOrDefault(index);
    public WeaponRecord? ByPrefabNumber(int prefabNumber) => ByIndex(ItemIndex.ForWeapon(prefabNumber));
    public WeaponRecord? ByGameNumber(int gameNumber)
        => _byIndex.Values.FirstOrDefault(w => w.GameNumber == gameNumber);

    /// Accepts an in-game number, a prefab name ("Weapon1257"), or a slug. A bare number is the
    /// in-game one, since that is what a player reads off the weapon list.
    public WeaponRecord? Find(string query)
    {
        if (int.TryParse(query, out var number) && ByGameNumber(number) is { } byGame) return byGame;
        return _byIndex.Values.FirstOrDefault(w =>
                   string.Equals(w.PrefabName, query, StringComparison.OrdinalIgnoreCase))
            ?? _byIndex.Values.FirstOrDefault(w =>
                   string.Equals(w.Slug, query, StringComparison.OrdinalIgnoreCase));
    }

    /// A bare number is the in-game number, matched exactly. Matching it as text would instead find
    /// whichever weapons happen to carry those digits in their prefab name, which is a different
    /// numbering entirely: "819" would return the weapon whose prefab is Weapon819, in-game #401.
    ///
    /// Anything else matches the slug, tag and prefab name, which are always English, plus the
    /// display name when a localization is supplied — otherwise searching in any other language
    /// finds nothing.
    /// <param name="names">
    /// Every language, when it has been read. A player knows one weapon by one name, and it is not
    /// necessarily the one on screen — searching only the displayed language means already knowing
    /// the English name to find the English entry.
    /// </param>
    public IEnumerable<WeaponRecord> Search(string text, WeaponNames? names = null)
    {
        if (int.TryParse(text, out var gameNumber))
            return Weapons.Where(w => w.GameNumber == gameNumber);

        return Weapons.Where(w =>
            w.Slug.Contains(text, StringComparison.OrdinalIgnoreCase)
            || w.Tag.Contains(text, StringComparison.OrdinalIgnoreCase)
            || w.PrefabName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || names?.Matches(w, text) == true);
    }
}
