using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Mods;

/// Opens one bundle for modification and writes it back out.
///
/// Held separately from the read-only BundleSet so that editing never disturbs the caches the rest
/// of the tool reads through.
public sealed class BundleEditor : IDisposable
{
    private readonly AssetsContext _context = new();
    private readonly BundleFileInstance _bundle;
    private readonly int _entryIndex;
    private readonly Dictionary<int, byte[]> _streams = [];

    public BundleEditor(string bundlePath)
    {
        _bundle = _context.OpenBundle(bundlePath);
        var entry = AssetsContext.SerializedEntries(_bundle).First();
        _entryIndex = entry.Index;
        File = _context.OpenBundleEntry(_bundle, _entryIndex);
    }

    public AssetsFileInstance File { get; }
    public AssetsContext Context => _context;

    public AssetTypeValueField? Read(AssetFileInfo info) => _context.Deserialize(File, info);

    public void Stage(AssetFileInfo info, AssetTypeValueField field) => info.SetNewData(field);

    /// Reads a slice of one of the bundle's stream entries — where texture pixels and audio banks
    /// actually live, since the object only points at them.
    public byte[]? ReadStream(string sourcePath, long offset, long size)
    {
        var wanted = Path.GetFileName(sourcePath);
        var entry = _bundle.file.BlockAndDirInfo.DirectoryInfos
            .FirstOrDefault(e => string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase));
        // Nothing there to read, or an object pointing outside the entry it names — which is data
        // like any other and is not always data this tool wrote. See BundleSet.ReadResource.
        if (entry is null || size <= 0 || offset < 0 || offset + size > entry.DecompressedSize) return null;

        var reader = _bundle.file.DataReader;
        reader.Position = entry.Offset + offset;
        return reader.ReadBytes((int)size);
    }


    /// Appends bytes to one of the bundle's stream entries, returning the offset they landed at.
    ///
    /// An AudioClip does not carry its own payload; it points at a byte range in a sibling
    /// .resource entry that every clip in the bundle shares. Overwriting that range in place would
    /// only work while the replacement is no larger, so the new bank goes on the end and the object
    /// is repointed at it. The bundle grows by the size of the clip, but applying a mod always
    /// rebuilds the bundle from its untouched backup, so the growth never compounds.
    public long AppendToStream(string sourcePath, byte[] payload, int alignment = 32)
    {
        var wanted = Path.GetFileName(sourcePath);
        var entries = _bundle.file.BlockAndDirInfo.DirectoryInfos;
        var index = entries.FindIndex(e => string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new FileNotFoundException($"The bundle has no stream entry '{wanted}'.");

        if (!_streams.TryGetValue(index, out var current))
        {
            var reader = _bundle.file.DataReader;
            reader.Position = entries[index].Offset;
            current = reader.ReadBytes((int)entries[index].DecompressedSize);
        }

        // The game's own clips all start on a 32-byte boundary, and FMOD reads the bank as a block.
        var offset = (current.Length + alignment - 1) / alignment * alignment;
        var grown = new byte[offset + payload.Length];
        current.CopyTo(grown, 0);
        payload.CopyTo(grown, offset);
        _streams[index] = grown;

        return offset;
    }

    /// Writes the whole bundle out, LZ4-compressed as the game ships it. Uncompressed would also
    /// load, but every bundle here is compressed and there is no reason to be the odd one out.
    ///
    /// The compressing itself is BundlePacker's, not the library's, because the library's is the
    /// single slowest thing this tool does.
    ///
    /// The entries are laid end to end straight into the buffer the blocks are cut from. Written out
    /// uncompressed into memory and read back to be packed instead, the largest bundle in the game —
    /// 216MB — is held five or six times over while it goes.
    public void Save(string outputPath, BundlePacking packing = BundlePacking.Smaller)
    {
        var original = _bundle.file.BlockAndDirInfo;
        var directory = original.DirectoryInfos;
        using var assets = Serialize(w => File.file.Write(w, 0), directory[_entryIndex].DecompressedSize);

        long Length(int index) => index == _entryIndex ? assets.Length
            : _streams.TryGetValue(index, out var grown) ? grown.Length
            : directory[index].DecompressedSize;

        var payload = new byte[Enumerable.Range(0, directory.Count).Sum(Length)];
        var entries = new AssetBundleDirectoryInfo[directory.Count];
        var reader = _bundle.file.DataReader;
        long at = 0;

        for (var index = 0; index < directory.Count; index++)
        {
            var length = Length(index);
            if (index == _entryIndex) assets.GetBuffer().AsSpan(0, (int)assets.Length).CopyTo(payload.AsSpan((int)at));
            else if (_streams.TryGetValue(index, out var grown)) grown.CopyTo(payload, at);
            else
            {
                reader.Position = directory[index].Offset;
                for (long read = 0; read < length;)
                {
                    var got = reader.Read(payload, (int)(at + read), (int)Math.Min(int.MaxValue, length - read));
                    if (got <= 0) throw new EndOfStreamException($"'{directory[index].Name}' ended early.");
                    read += got;
                }
            }

            entries[index] = new AssetBundleDirectoryInfo
            {
                Offset = at,
                DecompressedSize = length,
                Flags = directory[index].Flags,
                Name = directory[index].Name,
            };
            at += length;
        }

        BundlePacker.Write(_bundle.file.Header, original, entries, payload, outputPath, packing);
    }

    /// Sized to what it is expected to come to, so the stream does not double its way up to it, and
    /// handed back as the stream rather than copied out: it is read once, into the payload.
    private static MemoryStream Serialize(Action<AssetsFileWriter> write, long expected)
    {
        var stream = new MemoryStream((int)Math.Min(int.MaxValue, expected + 64 * 1024));
        var writer = new AssetsFileWriter(stream);
        write(writer);
        writer.Flush();
        return stream;
    }

    public void Dispose() => _context.Dispose();
}
