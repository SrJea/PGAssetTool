using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace PGAssetTool.Core.Assets;

/// What a bundle says it holds, by the name the game loads it under.
///
/// Every bundle carries an `AssetBundle` object whose `m_Container` maps a load-time path to the
/// object it names — the same paths the lookup table registers, written as the folder they were
/// authored in plus a file extension and folded to lower case:
/// `WeaponChatIcons/Weapon25_chaticon` is `assets/editor/resources/weaponchaticons/weapon25_chaticon.png`.
/// So an asset path out of the lookup table can be turned into a path id, and thereby into a class,
/// without knowing anything else about what is in the bundle.
///
/// This is the game's own answer, which is why it is used rather than a search by name: a search
/// means reading the name of every object of a class that might match, and a name is not unique
/// inside a bundle. Over 60 weapons' 237 related assets it resolved every one of them, at 1.6ms a
/// weapon against 14ms for the search.
///
/// The `assets/editor/resources/` in front is not matched against, because it is where the game's
/// authors happened to keep these and says nothing about what the game asks for. See `Names` for
/// what is compared instead.
public sealed class BundleContents(BundleSet bundles)
{
    private readonly Dictionary<string, List<(string Path, long PathId)>> _byBundle = new(StringComparer.OrdinalIgnoreCase);

    /// The object a bundle loads under this path, or null if it names none.
    ///
    /// Answers nothing rather than throwing for a bundle that will not open: a caller asking where
    /// something is has nothing better to do with the news than leave the row unresolved.
    public AssetNode? Locate(string bundle, string assetPath)
    {
        AssetsFileInstance file;
        try { file = bundles.Open(bundle); }
        catch (Exception e) when (e is IOException or FileNotFoundException or KeyNotFoundException)
        {
            return null;
        }

        foreach (var (path, pathId) in Table(bundle, file))
        {
            if (!Names(path, assetPath)) continue;
            if (file.file.GetAssetInfo(pathId) is not { } info) continue;
            return new AssetNode(pathId, (AssetClassID)info.TypeId, bundles.Context.NameOf(file, info), bundle);
        }
        return null;
    }

    /// The container table of one bundle. Read once: it is a single object, and one weapon asks
    /// about a dozen paths spread over three or four bundles.
    private List<(string Path, long PathId)> Table(string bundle, AssetsFileInstance file)
    {
        if (_byBundle.TryGetValue(bundle, out var cached)) return cached;

        var table = new List<(string, long)>();
        var info = file.file.AssetInfos.FirstOrDefault(i => i.TypeId == (int)AssetClassID.AssetBundle);
        if (info is not null && bundles.Context.Deserialize(file, info)?["m_Container"]["Array"] is
            { IsDummy: false } entries)
        {
            foreach (var entry in entries.Children)
                table.Add((entry["first"].AsString, entry["second"]["asset"]["m_PathID"].AsLong));
        }
        return _byBundle[bundle] = table;
    }

    /// Whether a container entry names the asset a lookup path asks for: the same path, without the
    /// extension, ending at a folder boundary — so the namespace an asset was registered under is
    /// part of the answer and `Weapon25_chaticon` is not matched by `Weapon250_chaticon`.
    public static bool Names(string entryPath, string assetPath)
    {
        if (assetPath.Length == 0) return false;

        var dot = entryPath.LastIndexOf('.');
        if (dot > entryPath.LastIndexOf('/')) entryPath = entryPath[..dot];

        if (entryPath.Equals(assetPath, StringComparison.OrdinalIgnoreCase)) return true;

        return entryPath.Length > assetPath.Length
            && entryPath[^assetPath.Length..].Equals(assetPath, StringComparison.OrdinalIgnoreCase)
            && entryPath[entryPath.Length - assetPath.Length - 1] == '/';
    }
}
