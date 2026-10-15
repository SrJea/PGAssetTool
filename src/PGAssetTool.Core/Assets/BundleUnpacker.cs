using AssetsTools.NET;
using K4os.Compression.LZ4;

namespace PGAssetTool.Core.Assets;

/// Decompresses a bundle whole, into memory, as an uncompressed bundle the library reads directly.
///
/// The library reads an LZ4 bundle a block at a time as bytes are asked for, and a reader asks for
/// them all over the file: a name here, a transform's parent there, a material in between. Each
/// jump to a block other than the one last read decodes that 128KB block again. Finding which way
/// #16 faces walks 353 GameObjects and their parents: 270ms read block by block, 13ms with the
/// bundle already unpacked, and a weapon resolves in about 10ms against about 110.
///
/// What it costs is memory: the bundle's full unpacked size for as long as the reader lives. The
/// prefab bundles run 10 to 30MB unpacked, and the one every weapon's arms come from is 54MB; d_c_f is
/// 216MB. So BundleSet unpacks only while a budget lasts, and reads the rest from disk block by block.
public static class BundleUnpacker
{
    /// The bundle, unpacked, or null for one that would come to more than <paramref name="most"/>
    /// bytes or is compressed any way but LZ4 — the library is left to read those itself.
    /// The file is closed again before this returns.
    public static MemoryStream? Unpack(string path, long most = long.MaxValue)
    {
        var bundle = new AssetBundleFile();
        try
        {
            bundle.Read(new AssetsFileReader(File.OpenRead(path)));
            var header = bundle.Header;
            var table = bundle.BlockAndDirInfo;

            // The low six bits of a block's flags say how it is compressed: none, LZMA, LZ4, LZ4HC.
            if (table.BlockInfos.Any(b => (b.Flags & 0x3F) is not (0 or 2 or 3))) return null;
            if (table.BlockInfos.Sum(b => (long)b.DecompressedSize) > most) return null;

            var origin = header.GetFileDataOffset();
            var from = new long[table.BlockInfos.Length];
            var into = new long[table.BlockInfos.Length];
            long packed = 0, total = 0;
            for (var at = 0; at < from.Length; at++)
            {
                (from[at], into[at]) = (packed, total);
                packed += table.BlockInfos[at].CompressedSize;
                total += table.BlockInfos[at].DecompressedSize;
            }

            var raw = new byte[packed];
            using (var file = File.OpenRead(path))
            {
                file.Position = origin;
                file.ReadExactly(raw);
            }

            // Laid out as the file will be, so the blocks are decoded straight into place.
            var entries = table.DirectoryInfos;
            var tableBytes = Table(table.Hash, total, entries);

            header.FileStreamHeader.Flags =
                AssetBundleFSHeaderFlags.HasDirectoryInfo | AssetBundleFSHeaderFlags.BlockAndDirAtEnd;
            header.FileStreamHeader.CompressedSize = (uint)tableBytes.Length;
            header.FileStreamHeader.DecompressedSize = (uint)tableBytes.Length;
            var start = header.GetFileDataOffset();
            header.FileStreamHeader.TotalFileSize = start + total + tableBytes.Length;

            var image = new byte[checked((int)header.FileStreamHeader.TotalFileSize)];
            using (var headerStream = new MemoryStream(image, 0, (int)start))
                header.Write(new AssetsFileWriter(headerStream) { BigEndian = true });

            Parallel.For(0, from.Length, at =>
            {
                var block = table.BlockInfos[at];
                var source = raw.AsSpan((int)from[at], (int)block.CompressedSize);
                var target = image.AsSpan((int)(start + into[at]), (int)block.DecompressedSize);

                if ((block.Flags & 0x3F) == 0) source.CopyTo(target);
                else if (LZ4Codec.Decode(source, target) != target.Length)
                    throw new InvalidDataException($"Block {at} of '{path}' did not decompress to its stated size.");
            });

            tableBytes.CopyTo(image, start + total);
            return new MemoryStream(image, writable: false);
        }
        finally
        {
            bundle.Close();
        }
    }

    /// One uncompressed block holding everything, and the entries where they already were.
    private static byte[] Table(Hash128 hash, long total, List<AssetBundleDirectoryInfo> entries)
    {
        using var stream = new MemoryStream();
        var writer = new AssetsFileWriter(stream) { BigEndian = true };
        new AssetBundleBlockAndDirInfo
        {
            Hash = hash,
            BlockInfos = [new AssetBundleBlockInfo { CompressedSize = (uint)total, DecompressedSize = (uint)total, Flags = 0 }],
            DirectoryInfos = entries,
        }.Write(writer);
        writer.Flush();
        return stream.ToArray();
    }
}
