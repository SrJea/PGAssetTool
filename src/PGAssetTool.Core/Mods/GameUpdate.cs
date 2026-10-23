using PGAssetTool.Core.Game;

namespace PGAssetTool.Core.Mods;

/// An enabled mod whose bundles are no longer the ones the game loads, and which of them moved.
public sealed record StaleMod(InstalledMod Mod, IReadOnlyList<string> Bundles);

/// Whether the game has been updated under the mods installed into it.
///
/// An update replaces the bundles a mod was written into. The game then loads the new, unmodified
/// ones, so every mod on them is simply not in the game any more — and nothing about the tool looks
/// wrong, because the ledger still lists them all as on.
///
/// Noticed from what each mod already records: the hash of every bundle it wrote to, which a
/// reconcile keeps current even for a bundle it left alone. The manifest names the hash the game
/// loads now, so a mod with a bundle whose hash has moved — or which has gone — is one the update
/// took out. Reapplying writes it into the new bundle and records the new hash, which is what
/// makes the notice go away. The version the game reports is only for saying so; hot fixes that
/// keep the version would slip past it, and bundles do not.
public static class GameUpdate
{
    public static IReadOnlyList<StaleMod> Stale(IEnumerable<InstalledMod> mods, IEnumerable<BundleEntry> manifest)
    {
        var now = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest) now[entry.Name] = entry.Hash;

        return mods
            .Where(m => m.Enabled)
            .Select(m => new StaleMod(m, m.TouchedBundles
                .Where(t => !now.TryGetValue(t.Key, out var hash)
                    || !string.Equals(hash, t.Value, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Key)
                .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
                .ToList()))
            .Where(s => s.Bundles.Count > 0)
            .ToList();
    }

    /// A sentence for the top of the window, or null when there is nothing to say.
    /// <param name="version">What the game reports itself as now, when that could be read.</param>
    public static string? Notice(IReadOnlyList<StaleMod> stale, string? version)
    {
        if (stale.Count == 0) return null;

        var was = stale
            .Select(s => s.Mod.GameVersion)
            .Where(v => v.Length > 0 && v != "unknown")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var from = was.Count == 1 && was[0] != version ? $" from {was[0]}" : "";
        var to = version is { Length: > 0 } ? $" to {version}" : "";
        var which = stale.Count == 1 ? $"'{stale[0].Mod.Name}' was" : $"{stale.Count} of your mods were";

        return $"The game has been updated{from}{to} since {which} installed, and the bundles it loads now "
            + "do not have them in. Reapplying writes them into the new ones.";
    }
}
