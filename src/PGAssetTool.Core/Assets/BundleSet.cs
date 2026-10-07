using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Game;
using PGAssetTool.Core.Mods;

namespace PGAssetTool.Core.Assets;

/// Name-addressed access to the bundle cache. Resolves a bundle's hash directory through the
/// manifest so callers work in bundle names rather than paths.
public sealed class BundleSet : IDisposable
{
    private readonly AssetsContext _context;
    private readonly Dictionary<string, string> _hashes;

    public GameInstallation Game { get; }

    /// Where a bundle's unmodified copy is kept, for one something has been written into.
    private readonly Func<CacheKind, string, string, string?>? _originals;

    /// <param name="originals">
    /// Asked for the copy of a bundle as it was before any mod was written into it — by cache,
    /// name and hash — answering null for a bundle nothing has touched. Given, this reads the game
    /// as shipped; left out, it reads the game as it is, mods and all. Anything that writes to the
    /// game or checks what is there wants the second, and anything that shows or extracts an item
    /// wants the first.
    /// </param>
    public BundleSet(GameInstallation game, AssetsContext? context = null,
        Func<CacheKind, string, string, string?>? originals = null)
    {
        Game = game;
        _context = context ?? new AssetsContext();
        _originals = originals;
        _hashes = game.ReadManifest().ToDictionary(e => e.Name, e => e.Hash, StringComparer.OrdinalIgnoreCase);
    }

    public AssetsContext Context => _context;
    public IReadOnlyCollection<string> BundleNames => _hashes.Keys;

    public string HashOf(string bundle) => _hashes.TryGetValue(bundle, out var hash)
        ? hash
        : throw new KeyNotFoundException($"'{bundle}' is not in the bundle manifest.");

    /// The copy the game would load, which is not always the one shipped with it — or, for a reader
    /// asked for originals, that copy as it was before anything was written into it.
    ///
    /// Keyed by the hash the manifest names now, so a backup taken before a game update is not
    /// mistaken for the original of the bundle that replaced it.
    public string PathOf(string bundle)
    {
        var hash = HashOf(bundle);
        var live = Game.Resolve(bundle, hash)
            ?? throw new FileNotFoundException($"No copy of '{bundle}' is present in any cache.");

        return _originals?.Invoke(live.Cache, bundle, hash) is { } original && File.Exists(original)
            ? original
            : live.Path;
    }

    /// Whether what this reader hands back for a bundle is the game as it shipped: either the file
    /// still hashes to what the game's own manifest records for it, or something wrote to it and
    /// this copy of the tool kept the original.
    ///
    /// The answer is no when another copy of the tool — another folder, another `PGAssetTool-data` —
    /// installed a mod into this game. Its backups are in its own data folder, so from here the
    /// modded bytes are all there is, and nothing about them says they are not the game's own. That
    /// is what stops an extract: see `Altered`.
    ///
    /// Asked once per bundle and kept, because the answer costs an MD5 over the whole file.
    public bool ReadsAsShipped(string bundle)
    {
        if (_shipped.TryGetValue(bundle, out var already)) return already;

        var hash = HashOf(bundle);
        if (Game.Resolve(bundle, hash) is not { } live) return _shipped[bundle] = false;

        var kept = _originals?.Invoke(live.Cache, bundle, hash);
        return _shipped[bundle] = (kept is not null && File.Exists(kept))
            || BundleIntegrity.IsPristine(live.Path, hash);
    }

    private readonly Dictionary<string, bool> _shipped = new(StringComparer.OrdinalIgnoreCase);

    /// Which of these bundles this reader cannot answer for, in the order given.
    public IReadOnlyList<string> Altered(IEnumerable<string> bundles)
        => bundles.Where(b => b.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(b => _hashes.ContainsKey(b) && !ReadsAsShipped(b))
            .ToList();

    /// Every bundle opened through here, so each is opened exactly once.
    ///
    /// Asking the manager for the same path twice does not reliably hand back the same instance,
    /// and the second one is a second open file that disposing the manager did not close. Reading a
    /// texture's pixels did exactly that — open for the object, open again for the stream the
    /// pixels live in — and left the bundle locked after the reader was disposed. Every write to
    /// the game closes the reader first and trusts that to be enough, so the effect was an install
    /// that failed with the file in use whenever somebody had looked at that bundle first.
    private readonly Dictionary<string, BundleFileInstance> _opened = new(StringComparer.OrdinalIgnoreCase);

    private BundleFileInstance Bundle(string bundle)
    {
        if (_opened.TryGetValue(bundle, out var already)) return already;

        var path = PathOf(bundle);
        if (UnpackBudget > _unpacked && BundleUnpacker.Unpack(path, UnpackBudget - _unpacked) is { } unpacked)
        {
            _unpacked += unpacked.Length;
            return _opened[bundle] = _context.OpenBundle(unpacked, path);
        }

        return _opened[bundle] = _context.OpenBundle(path);
    }

    /// How many bytes of bundles this reader may hold unpacked in memory; see BundleUnpacker. What
    /// does not fit is read from disk a block at a time, as everything was before. Zero unpacks
    /// nothing.
    public long UnpackBudget { get; init; }

    private long _unpacked;

    /// How much of the budget is in use.
    public long Unpacked => _unpacked;

    /// The first serialized file in a bundle. Every content bundle here holds exactly one.
    public AssetsFileInstance Open(string bundle)
    {
        var file = Bundle(bundle);
        var entry = AssetsContext.SerializedEntries(file).First();
        return _context.OpenBundleEntry(file, entry.Index);
    }

    /// Every serialized file in a bundle. A scene bundle holds two — the scene itself, and the
    /// `.sharedAssets` its objects use — where every other bundle here holds one.
    public IReadOnlyList<AssetsFileInstance> OpenAll(string bundle)
    {
        var file = Bundle(bundle);
        return AssetsContext.SerializedEntries(file).Select(e => _context.OpenBundleEntry(file, e.Index)).ToList();
    }

    /// The names of a bundle's stream entries (`.resS`, `.resource`), which is where a scene's
    /// meshes and textures keep their bytes.
    public IReadOnlyList<string> Entries(string bundle)
        => Bundle(bundle).file.BlockAndDirInfo.DirectoryInfos.Select(d => d.Name).ToList();

    public AssetTypeValueField MonoBehaviour(string bundle, string name)
        => TryMonoBehaviour(bundle, name)
           ?? throw new KeyNotFoundException($"No MonoBehaviour named '{name}' in bundle '{bundle}'.");

    public AssetTypeValueField? TryMonoBehaviour(string bundle, string name)
    {
        var file = Open(bundle);
        foreach (var info in file.file.AssetInfos)
        {
            if (info.TypeId != (int)AssetClassID.MonoBehaviour) continue;
            if (name != "*" && _context.NameOf(file, info) != name) continue;
            if (_context.Deserialize(file, info) is { } field) return field;
        }
        return null;
    }

    /// Texture and audio payloads are not stored in the serialized object; they sit in a sibling
    /// .resS or .resource entry that the object points into. This reads that slice.
    public byte[] ReadResource(string bundle, string sourcePath, long offset, long size)
    {
        var wanted = Path.GetFileName(sourcePath);
        var file = Bundle(bundle);
        foreach (var entry in file.file.BlockAndDirInfo.DirectoryInfos)
        {
            if (!string.Equals(entry.Name, wanted, StringComparison.OrdinalIgnoreCase)) continue;

            // Asked of the entry rather than trusted: where an object says its bytes are is data
            // like any other, and these bundles are not always the ones this tool wrote. A size
            // that runs off the end would be an enormous allocation on the way to an unreadable
            // answer, and a negative one an exception from somewhere with nothing to say.
            if (offset < 0 || size < 0 || offset + size > entry.DecompressedSize)
                throw new InvalidDataException(
                    $"'{bundle}' says an object's bytes are {size} long at {offset} in '{wanted}', "
                    + $"which holds {entry.DecompressedSize}.");

            var reader = file.file.DataReader;
            reader.Position = entry.Offset + offset;
            return reader.ReadBytes((int)size);
        }
        throw new FileNotFoundException($"Bundle '{bundle}' has no stream entry '{wanted}'.");
    }

    public void Dispose()
    {
        _opened.Clear();
        _context.Dispose();
    }
}
