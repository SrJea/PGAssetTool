using AssetsTools.NET;
using K4os.Compression.LZ4;

namespace PGAssetTool.Core.Mods;

/// How a rebuilt bundle is compressed on its way back into the game.
public enum BundlePacking
{
    /// The same size the game ships.
    Smaller,

    /// About four times quicker to write, and about a sixth larger on disk. The game reads both at
    /// the same speed — LZ4 decompresses at the same rate whatever was spent compressing it — so
    /// this costs nothing but space.
    Faster,
}

/// Writes a bundle back out as a compressed UnityFS file.
///
/// AssetsTools.NET can do this itself, and doing so is twenty seconds an apply. Its `Pack(LZ4)` is
/// LZ4**HC** — the slow, thorough end of the format — through a pure C# port that manages about
/// 28MB/s. A game with thirty mods on it means rebuilding 180MB of bundles, and that one call is
/// nearly all of it: 17.9 of 19 seconds, measured across every bundle the author's installation has.
///
/// The same bytes through a compressor written for speed take 0.9 seconds, at the same level and
/// for the same output size, because LZ4's 128KB blocks are independent and there are as many of
/// them as there are cores. So the compressing is done here and only the layout is borrowed.
///
/// The layout is AssetsTools.NET's own: blocks of 128KB, the block and directory table compressed
/// and placed at the end, the file data starting at the offset the header itself says it does. That
/// shape is not the one the game ships — the shipped bundles keep their table at the front, padded —
/// and it is the one the game is known to load from this tool.
internal static class BundlePacker
{
    /// What Unity splits a bundle into, and what it expects to find on the way back in.
    private const int BlockSize = 128 * 1024;

    /// Beyond this, the blocks are compressed on every core rather than one. Below it the handing
    /// out costs more than the work: a bundle of a few hundred kilobytes is two or three blocks.
    private const int WorthSharing = 4 * 1024 * 1024;

    /// <param name="header">The bundle's own header; its stream flags and sizes are rewritten here.</param>
    /// <param name="original">The table the bundle was read with, for the hash it carries.</param>
    /// <param name="entries">Where each entry sits in <paramref name="payload"/>, in order.</param>
    /// <param name="payload">Every entry's bytes, end to end — what the blocks are cut from.</param>
    public static void Write(
        AssetBundleHeader header, AssetBundleBlockAndDirInfo original, AssetBundleDirectoryInfo[] entries,
        byte[] payload, string outputPath, BundlePacking packing)
    {
        long total = payload.Length;

        var level = packing == BundlePacking.Faster ? LZ4Level.L00_FAST : LZ4Level.L09_HC;
        var count = (int)((total + BlockSize - 1) / BlockSize);
        var compressed = new byte[count][];

        if (total >= WorthSharing)
            Parallel.For(0, count, at => compressed[at] = Squeeze(payload, at, level));
        else
            for (var at = 0; at < count; at++) compressed[at] = Squeeze(payload, at, level);

        var blocks = new AssetBundleBlockInfo[count];
        for (var at = 0; at < count; at++)
        {
            var raw = (uint)Math.Min(BlockSize, total - (long)at * BlockSize);
            var stored = compressed[at];

            blocks[at] = new AssetBundleBlockInfo
            {
                DecompressedSize = raw,
                CompressedSize = (uint)stored.Length,

                // A block that did not get smaller is kept as it is. Already-compressed payloads —
                // an audio bank, a PNG — are most of some bundles, and storing those verbatim is
                // both smaller and faster than storing a compressed copy that grew.
                Flags = (ushort)(stored.Length == raw
                    ? AssetBundleFSHeaderFlags.None
                    : packing == BundlePacking.Faster
                        ? AssetBundleFSHeaderFlags.LZ4Compressed
                        : AssetBundleFSHeaderFlags.LZ4HCCompressed),
            };
        }

        // The table is a few hundred bytes and is read before anything else, so it is always packed
        // thoroughly whatever the blocks got.
        var table = Serialize(w => new AssetBundleBlockAndDirInfo
        {
            Hash = original.Hash,
            BlockInfos = blocks,
            DirectoryInfos = [.. entries],
        }.Write(w));

        var packedTable = Squeeze(table, LZ4Level.L09_HC);

        header.FileStreamHeader.Flags =
            AssetBundleFSHeaderFlags.LZ4HCCompressed
            | AssetBundleFSHeaderFlags.HasDirectoryInfo
            | AssetBundleFSHeaderFlags.BlockAndDirAtEnd;
        header.FileStreamHeader.CompressedSize = (uint)packedTable.Length;
        header.FileStreamHeader.DecompressedSize = (uint)table.Length;

        // Asked of the header rather than worked out here, so the file says of itself exactly what
        // a reader will conclude from it. Every field it depends on is set by now.
        var start = header.GetFileDataOffset();
        header.FileStreamHeader.TotalFileSize =
            start + blocks.Sum(b => (long)b.CompressedSize) + packedTable.Length;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        using (var writer = new AssetsFileWriter(outputPath) { BigEndian = true })
        {
            header.Write(writer);

            // Whatever the header's own offset says is between it and the data. Written as zeroes
            // rather than seeked past, so nothing of whatever was in the file before is left there.
            writer.Write(new byte[start - writer.Position]);

            for (var at = 0; at < count; at++) writer.Write(compressed[at]);
            writer.Write(packedTable);
        }

        Verify(outputPath, entries);
    }

    /// One block, compressed, or the block itself if compressing it did not help.
    private static byte[] Squeeze(byte[] payload, int at, LZ4Level level)
    {
        var start = (long)at * BlockSize;
        var length = (int)Math.Min(BlockSize, payload.Length - start);
        return Squeeze(payload.AsSpan((int)start, length), level);
    }

    private static byte[] Squeeze(ReadOnlySpan<byte> source, LZ4Level level)
    {
        var target = new byte[LZ4Codec.MaximumOutputSize(source.Length)];
        var wrote = LZ4Codec.Encode(source, target, level);

        return wrote <= 0 || wrote >= source.Length ? source.ToArray() : target[..wrote];
    }

    /// A bundle's header and table are written the other way round from everything inside it —
    /// UnityFS keeps those big-endian — and nothing says so at the point of writing one.
    private static byte[] Serialize(Action<AssetsFileWriter> write)
    {
        using var stream = new MemoryStream();
        var writer = new AssetsFileWriter(stream) { BigEndian = true };
        write(writer);
        writer.Flush();
        return stream.ToArray();
    }

    /// Reads back what was just written, far enough to know it is a bundle and the right one.
    ///
    /// This is the file the game loads, and it is assembled here by hand rather than by the library
    /// that knows the format. Reading the header and the table back costs a millisecond and turns
    /// any mistake in that assembly into a failure at the moment it was made, rather than into a
    /// game that will not start.
    private static void Verify(string outputPath, AssetBundleDirectoryInfo[] expected)
    {
        var written = new AssetBundleFile();
        try
        {
            written.Read(new AssetsFileReader(File.OpenRead(outputPath)));

            var names = written.BlockAndDirInfo.DirectoryInfos.Select(d => d.Name).ToList();
            if (!names.SequenceEqual(expected.Select(d => d.Name)))
                throw new InvalidDataException(
                    $"The bundle written to '{outputPath}' holds {string.Join(", ", names)} "
                    + $"rather than {string.Join(", ", expected.Select(d => d.Name))}.");

            if (written.Header.FileStreamHeader.TotalFileSize != new FileInfo(outputPath).Length)
                throw new InvalidDataException(
                    $"The bundle written to '{outputPath}' says it is "
                    + $"{written.Header.FileStreamHeader.TotalFileSize} bytes and is "
                    + $"{new FileInfo(outputPath).Length}.");
        }
        finally
        {
            written.Close();
        }
    }
}
