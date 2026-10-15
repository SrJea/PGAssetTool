using AssetsTools.NET;
using AssetsTools.NET.Texture;

namespace PGAssetTool.Core.Mods;

public sealed record TextureChange(
    TextureFormat OriginalFormat,
    TextureFormat WrittenFormat,
    int OldWidth,
    int OldHeight,
    int NewWidth,
    int NewHeight,
    bool AlphaKept = false)
{
    public bool Resized => OldWidth != NewWidth || OldHeight != NewHeight;
    public bool Recoded => OriginalFormat != WrittenFormat;

    public override string ToString()
    {
        var format = Recoded ? $"{OriginalFormat} -> {WrittenFormat}" : OriginalFormat.ToString();
        var size = Resized ? $"{OldWidth}x{OldHeight} -> {NewWidth}x{NewHeight}" : $"{NewWidth}x{NewHeight}";
        return $"{format} {size}" + (AlphaKept ? ", original alpha kept" : "");
    }
}

public static class TextureImporter
{
    /// The encoder handles uncompressed layouts only; every block-compressed format comes back
    /// empty. Since 46% of the game's weapon textures are BC7, that is the common case, not an edge.
    public const TextureFormat Fallback = TextureFormat.RGBA32;

    /// Replaces a Texture2D's pixels with an image file.
    ///
    /// The original format is kept when it can be written. When it cannot, the texture is written
    /// uncompressed rather than refused. That is not a compromise on quality: re-encoding the
    /// author's artwork into a lossy block format would add compression artifacts that were never
    /// in it, which is the whole point of keeping the original format where it can be written. It
    /// costs size, and the original bytes stay in the stream file regardless.
    /// <param name="originalPixels">
    /// The texture as it stands, decoded to BGRA. Used only when the replacement arrives without an
    /// alpha channel: most of these textures keep something other than coverage there — emission,
    /// usually — so an author who wants to repaint the colours should not have to reconstruct a
    /// mask they never touched.
    /// </param>
    /// <param name="alphaIsMask">
    /// True when the file's alpha channel is the tool's own doing rather than the author's: an
    /// export masked to the part a model samples writes the mask there, because these textures are
    /// mostly transparent to begin with and keeping the original alpha would show nothing.
    /// The original alpha is then kept on the way back in, exactly as for a file that arrived
    /// without one at all.
    /// </param>
    public static TextureChange Replace(
        AssetTypeValueField field, string imagePath, byte[]? originalPixels = null,
        bool alphaIsMask = false)
    {
        var texture = TextureFile.ReadTextureFile(field);
        var original = (TextureFormat)texture.m_TextureFormat;
        int oldWidth = texture.m_Width, oldHeight = texture.m_Height;

        var source = ReadImage(imagePath);
        var keepAlpha = (!source.HasAlpha || alphaIsMask)
            && originalPixels is not null
            && source.Width == oldWidth
            && source.Height == oldHeight
            && originalPixels.Length >= oldWidth * oldHeight * 4;

        if (keepAlpha) CopyAlpha(source, originalPixels!, oldHeight);

        var written = original;
        if (!TryEncode(texture, source, original))
        {
            written = Fallback;
            if (!TryEncode(texture, source, Fallback))
                throw new NotSupportedException(
                    $"'{texture.m_Name}' could not be written as {original} or as {Fallback}.");
        }

        // A replaced texture has no mip chain, and claiming one would send the game reading past
        // the data.
        texture.m_MipCount = 1;
        texture.m_MipMap = false;
        texture.WriteTo(field);

        // The pixels are inline now, so the pointer into the shared stream file has to go: left in
        // place it would send the game back to the original bytes.
        var streamData = field["m_StreamData"];
        if (!streamData.IsDummy)
        {
            streamData["path"].AsString = "";
            streamData["offset"].AsULong = 0;
            streamData["size"].AsUInt = 0;
        }

        return new TextureChange(
            original, written, oldWidth, oldHeight, texture.m_Width, texture.m_Height, keepAlpha);
    }

    /// Takes the alpha from the texture already in the game, row for row.
    ///
    /// The decoded original runs bottom up, as Unity stores it; the image just read runs top down.
    private static void CopyAlpha(Image source, byte[] originalBgra, int height)
    {
        var stride = source.Width * 4;
        for (var row = 0; row < height; row++)
        {
            var from = (height - 1 - row) * stride;
            var to = row * stride;
            for (var x = 0; x < source.Width; x++)
                source.Bgra[to + x * 4 + 3] = originalBgra[from + x * 4 + 3];
        }
    }

    private readonly record struct Image(byte[] Bgra, int Width, int Height, bool HasAlpha);

    /// Read here rather than handed to the encoder as a path, because the alpha has to be settled
    /// before anything is encoded.
    private static Image ReadImage(string path)
    {
        using var stream = File.OpenRead(path);
        var image = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha)
            ?? throw new InvalidDataException($"'{Path.GetFileName(path)}' is not an image this reads.");

        var bgra = Preview.PreviewImage.Swapped(image.Data);

        // SourceComp is what the file held, whatever it was decoded into.
        var hasAlpha = image.SourceComp is StbImageSharp.ColorComponents.RedGreenBlueAlpha
                                        or StbImageSharp.ColorComponents.GreyAlpha;
        return new Image(bgra, image.Width, image.Height, hasAlpha);
    }

    private static bool TryEncode(TextureFile texture, Image source, TextureFormat format)
    {
        try
        {
            // Bottom up on the way in, the same way Unity keeps it.
            var flipped = new byte[source.Bgra.Length];
            var stride = source.Width * 4;
            for (var row = 0; row < source.Height; row++)
                Array.Copy(source.Bgra, row * stride, flipped, (source.Height - 1 - row) * stride, stride);

            var encoded = TextureFile.EncodeManagedData(flipped, format, source.Width, source.Height, useBgra: true);
            if (encoded is not { Length: > 0 }) return false;

            texture.m_TextureFormat = (int)format;
            texture.SetPictureData(encoded, source.Width, source.Height);
            return true;
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException)
        {
            return false;
        }
    }

}
