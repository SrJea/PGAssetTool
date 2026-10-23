using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace PGAssetTool.Core.Assets;

/// Where an asset lives, written so it survives a game update.
///
/// `PathId` is the fast path and is usually still correct, but it does move: comparing one update
/// against the next, 142 objects across 8 bundles changed theirs. So it is a hint to verify against
/// the name, not the identity itself. `Ordinal` separates same-named assets in one container,
/// counted within a class and ordered by path id.
public sealed record AssetAddress(
    string Container,
    string Class,
    string Name,
    int Ordinal = 0,
    long? PathId = null)
{
    public override string ToString() => $"{Container}:{Class}:{Name}" + (Ordinal > 0 ? $"#{Ordinal}" : "");
}

/// Resolves between path ids and the name-based address, per serialized file.
///
/// Indexed one class at a time, because an address only ever looks among its own class. Indexing
/// the whole container reads the name of every GameObject and MonoBehaviour in a prefab bundle to
/// address one texture, which is a large part of what extracting a weapon costs.
public sealed class ContainerIndex(AssetsContext context)
{
    private readonly Dictionary<(string Container, int Class), Dictionary<string, List<long>>> _byContainer = new();

    private Dictionary<string, List<long>> IndexOf(string container, AssetsFileInstance file, int cls)
    {
        if (_byContainer.TryGetValue((container, cls), out var cached)) return cached;

        var index = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var info in file.file.AssetInfos)
        {
            if (info.TypeId != cls) continue;
            var name = NameOf(file, info);
            if (!index.TryGetValue(name, out var ids)) index[name] = ids = [];
            ids.Add(info.PathId);
        }
        foreach (var ids in index.Values) ids.Sort();
        return _byContainer[(container, cls)] = index;
    }

    public AssetAddress AddressOf(string container, AssetsFileInstance file, AssetFileInfo info, string name)
    {
        var ids = IndexOf(container, file, info.TypeId).GetValueOrDefault(name) ?? [];
        var ordinal = ids.IndexOf(info.PathId);
        return new AssetAddress(container, ((AssetClassID)info.TypeId).ToString(), name,
            ordinal < 0 ? 0 : ordinal, info.PathId);
    }

    /// Prefers the recorded path id, but only when the object there still has the expected class and
    /// name. Otherwise falls back to the name, so a pack keeps working after an update moves things.
    public AssetFileInfo? Resolve(AssetAddress address, AssetsFileInstance file, out bool usedPathId)
    {
        usedPathId = false;
        if (!Enum.TryParse<AssetClassID>(address.Class, out var cls)) return null;

        if (address.PathId is { } pathId && file.file.GetAssetInfo(pathId) is { } candidate
            && candidate.TypeId == (int)cls && NameOf(file, candidate) == address.Name)
        {
            usedPathId = true;
            return candidate;
        }

        var ids = IndexOf(address.Container, file, (int)cls).GetValueOrDefault(address.Name);
        if (ids is null || address.Ordinal >= ids.Count) return null;
        return file.file.GetAssetInfo(ids[address.Ordinal]);
    }

    private string NameOf(AssetsFileInstance file, AssetFileInfo info)
        => context.NameOf(file, info);
}
