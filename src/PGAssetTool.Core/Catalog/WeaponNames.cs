using System.Text.Json;
using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Catalog;

/// Every name each weapon carries, in every language the game ships.
///
/// A player knows one weapon by one name, but not necessarily the one the tool is displaying: the
/// same gun is Ultimatum, 究極点 and Ультиматум depending on who is looking. Searching only the
/// language on screen means knowing the English name to find the English entry, which is exactly
/// the knowledge someone reaching for a search box does not have.
///
/// All eleven tables together are a third of a second to read and sixteen thousand strings to hold,
/// so there is no reason to make anyone choose.
public sealed class WeaponNames
{
    private readonly Dictionary<int, string[]> _byIndex;

    private WeaponNames(Dictionary<int, string[]> byIndex) => _byIndex = byIndex;

    public int Count => _byIndex.Values.Sum(n => n.Length);

    /// Built from tables already in hand, which is how tests supply one without a game.
    public static WeaponNames FromTables(ItemCatalog items, params Localization[] tables)
        => new(items.Weapons.ToDictionary(w => w.Index, w => tables
            .Select(t => t.Translate(w.LocalizationKey))
            .OfType<string>()
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()));

    /// <param name="remember">
    /// A directory to keep the finished table in, so the next start reads it instead of the game's
    /// eleven. Null reads them every time, which is what a caller with nowhere to write does.
    /// </param>
    public static WeaponNames Load(BundleSet bundles, ItemCatalog items, string? remember = null)
    {
        var languages = GameCatalogs.Languages(bundles);
        var key = Key(bundles, languages, items);

        if (remember is not null && Kept(remember, key) is { } kept) return new WeaponNames(kept);

        var tables = new List<Localization>();
        foreach (var (bundle, _) in languages)
        {
            // One unreadable table is not worth losing the other ten over.
            try { tables.Add(Localization.Load(bundles, bundle)); }
            catch (Exception e) when (e is IOException or InvalidOperationException) { }
        }

        var byIndex = new Dictionary<int, string[]>();
        foreach (var weapon in items.Weapons)
            byIndex[weapon.Index] = tables
                .Select(t => t.Translate(weapon.LocalizationKey))
                .OfType<string>()
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        if (remember is not null) Keep(remember, key, byIndex);
        return new WeaponNames(byIndex);
    }

    /// What the kept table was built from: the language bundles the game ships, each by the hash
    /// its own manifest records, and how many weapons were read against them.
    ///
    /// The hashes are what a game update changes — every one of them changed when the game moved to
    /// Unity 6 — so a table built against the version before it is not mistaken for this one's. A
    /// mod cannot stale it: the tables are read from the game as shipped whatever is installed.
    private static string Key(
        BundleSet bundles, IReadOnlyList<(string Bundle, string Name)> languages, ItemCatalog items)
    {
        var said = new List<string> { $"weapons={items.Weapons.Count()}" };
        foreach (var (bundle, _) in languages)
        {
            try { said.Add($"{bundle}={bundles.HashOf(bundle)}"); }
            catch (KeyNotFoundException) { said.Add($"{bundle}=?"); }
        }

        return string.Join(" ", said);
    }

    private const string FileName = "names.json";

    /// The file: what it was built from, and the table itself.
    private sealed record Table(string Key, Dictionary<int, string[]> Names);

    /// The table read back, or null where there is none, it will not read, or it was built against
    /// something else. Nothing here is worth a word to the caller: a cache that cannot be read is a
    /// cache that is built again.
    private static Dictionary<int, string[]>? Kept(string directory, string key)
    {
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return null;

            var kept = JsonSerializer.Deserialize<Table>(File.ReadAllText(path), Json);
            return kept is not null && kept.Key == key ? kept.Names : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// Written through a file of its own and moved into place, so a start that is killed half way
    /// through writing leaves the one before it rather than half of this one.
    private static void Keep(string directory, string key, Dictionary<int, string[]> names)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var writing = path + ".writing";

            File.WriteAllText(writing, JsonSerializer.Serialize(new Table(key, names), Json));
            File.Move(writing, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public IReadOnlyList<string> For(WeaponRecord weapon)
        => _byIndex.GetValueOrDefault(weapon.Index, []);

    public bool Matches(WeaponRecord weapon, string text)
        => For(weapon).Any(n => n.Contains(text, StringComparison.OrdinalIgnoreCase));
}
