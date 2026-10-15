using System.Collections.Concurrent;
using AssetsTools.NET.Extra;

namespace PGAssetTool.Core.Assets;

/// Maps a serialized file's CAB name to the bundle holding it.
///
/// Every bundle is opened, but only far enough to read its directory: the entry names are in the
/// header, so nothing is unpacked and no asset is touched. Across all 2300 bundles and 4.8 GB that
/// is a twentieth of a second, which is why this is rebuilt rather than cached anywhere.
public sealed class CabIndex
{
    private readonly Dictionary<string, string> _bundleByCab;

    private CabIndex(Dictionary<string, string> bundleByCab) => _bundleByCab = bundleByCab;

    public int Count => _bundleByCab.Count;

    public string? BundleFor(string cab) => _bundleByCab.GetValueOrDefault(cab);

    public static CabIndex Build(BundleSet bundles)
    {
        var found = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Parallel.ForEach(bundles.BundleNames, bundle =>
        {
            // A bare manager, not an AssetsContext: that loads the engine class database, and
            // doing so once per bundle costs thirteen seconds where the whole scan takes fifty
            // milliseconds. Reading directory names needs no type information at all.
            var manager = new AssetsManager();
            try
            {
                var file = manager.LoadBundleFile(bundles.PathOf(bundle), unpackIfPacked: false);
                foreach (var (_, cab) in AssetsContext.SerializedEntries(file)) found[cab] = bundle;
            }
            catch (Exception e) when (e is IOException or NotSupportedException)
            {
            }
            finally
            {
                manager.UnloadAll(true);
            }
        });

        return new CabIndex(new Dictionary<string, string>(found, StringComparer.OrdinalIgnoreCase));
    }
}
