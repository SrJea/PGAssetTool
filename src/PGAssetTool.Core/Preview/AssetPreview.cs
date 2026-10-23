using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Export.Meshes;

namespace PGAssetTool.Core.Preview;

/// Decoded pixels, ready to hand to a bitmap. Rows run top to bottom; Unity stores them the other
/// way up and this flips them, because every image API expects the first row to be the top one.
public sealed record PreviewImage(int Width, int Height, byte[] Bgra)
{
    public int Stride => Width * 4;

    /// Red and blue the other way round, which turns RGBA into BGRA and back.
    ///
    /// Everything that reads an image file gets RGBA and everything that shows or writes one here
    /// wants BGRA, so one loop rather than one wherever those two meet: a copy of it that is wrong
    /// is a picture with its reds and blues exchanged, and nothing else about it wrong.
    public static byte[] Swapped(byte[] pixels)
    {
        var swapped = new byte[pixels.Length];
        for (var at = 0; at + 3 < pixels.Length; at += 4)
        {
            swapped[at] = pixels[at + 2];
            swapped[at + 1] = pixels[at + 1];
            swapped[at + 2] = pixels[at];
            swapped[at + 3] = pixels[at + 3];
        }
        return swapped;
    }

    /// The same pixels with every one made solid.
    ///
    /// Most model textures use alpha for something other than coverage — emission, most often — so
    /// honouring it punches holes in them, and a few come out invisible. Icons are the opposite:
    /// all 400 of them are more than half transparent because they sit on an empty background.
    public PreviewImage Opaque()
    {
        var solid = (byte[])Bgra.Clone();
        for (var i = 3; i < solid.Length; i += 4) solid[i] = 255;
        return this with { Bgra = solid };
    }
}

/// Reads an asset into something that can be shown, without going through a file.
///
/// The exporter writes PNG and glTF because that is what an editor opens. A preview needs neither —
/// it needs pixels and triangles in memory — so the decoding is shared but the writing is not.
public static class AssetPreview
{
    public static PreviewImage? Texture(BundleSet bundles, string bundle, AssetTypeValueField field)
    {
        var texture = TextureFile.ReadTextureFile(field);

        // Nearly every texture in the game keeps its pixels in a sibling .resS rather than on the
        // object, so the payload has to be fetched before anything can be decoded.
        var payload = texture.pictureData;
        var stream = field["m_StreamData"];
        if (!stream.IsDummy && stream["path"].AsString.Length > 0)
            payload = bundles.ReadResource(
                bundle, stream["path"].AsString, stream["offset"].AsLong, stream["size"].AsLong);

        if (payload is null || payload.Length == 0) return null;

        var bgra = texture.DecodeTextureRaw(payload, useBgra: true);
        if (bgra is null || bgra.Length < texture.m_Width * texture.m_Height * 4) return null;

        return new PreviewImage(texture.m_Width, texture.m_Height, FlipRows(bgra, texture.m_Width, texture.m_Height));
    }

    /// Finds the object a preview was asked for.
    ///
    /// Most things are addressed by path id, but an icon is not: the lookup table registers it by
    /// name and records no id, so it has to be found by the name it is registered under — and the
    /// newest weapons keep theirs in the game's own resources.assets rather than in a bundle.
    public static (AssetsFileInstance File, AssetFileInfo Info)? Locate(
        BundleSet bundles, string container, AssetClassID cls, long pathId, string name)
    {
        if (OpenContainer(bundles, container) is not { } file) return null;

        if (pathId != 0)
            return file.file.GetAssetInfo(pathId) is { } byId ? (file, byId) : null;

        // Icon names disagree with the slugs they were built from on capitalisation, which is why
        // the lookup table is case-insensitive; matching exactly here would miss two dozen weapons.
        var byName = ReferenceWalker.FindByName(
            bundles.Context, file, cls, name, StringComparison.OrdinalIgnoreCase);
        return byName is null ? null : (file, byName);
    }

    /// A container is a bundle name, unless it is one of the game's own serialized files.
    private static AssetsFileInstance? OpenContainer(BundleSet bundles, string container)
    {
        if (!container.Contains('.', StringComparison.Ordinal))
        {
            try { return bundles.Open(container); }
            catch (Exception e) when (e is IOException or FileNotFoundException) { return null; }
        }

        // Those carry no type tree, so nothing can be read out of them without the class database.
        // Without it the icon is simply not previewable, which is worth saying rather than hiding.
        if (!bundles.Context.HasClassDatabase) return null;

        var path = bundles.Game.EnumerateSerializedFiles().FirstOrDefault(
            f => string.Equals(Path.GetFileName(f), container, StringComparison.OrdinalIgnoreCase));
        return path is null ? null : bundles.Context.OpenSerializedFile(path);
    }

    /// An AudioClip, decoded from the FMOD bank its bytes actually live in.
    ///
    /// The clip object holds nothing but a pointer into a sibling .resource; the bank there is FSB5,
    /// and the clips inside it are PCM or Vorbis depending on the sound. Fmod5Sharp rebuilds one as
    /// a WAV or an Ogg, and from there it is the same decoding path a replacement takes on the way
    /// in — so a clip that previews wrong is one that would have been packed wrong.
    public static PreviewSound? Audio(BundleSet bundles, string bundle, AssetTypeValueField field)
    {
        var resource = field["m_Resource"];
        if (resource.IsDummy) return null;

        var payload = bundles.ReadResource(
            bundle, resource["m_Source"].AsString, resource["m_Offset"].AsLong, resource["m_Size"].AsLong);

        return Audio(payload);
    }

    /// <param name="bank">An FSB5 bank, as it sits in the bundle's .resource entry.</param>
    public static PreviewSound? Audio(byte[] bank)
    {
        try
        {
            return Import.Audio.FsbSound.Read(bank) is { } sound ? new PreviewSound(sound) : null;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <param name="bundles">Where a mesh that keeps its vertices in a companion file reads them
    /// from. Without it such a mesh has no model to show.</param>
    public static UnityMesh? Mesh(
        AssetTypeValueField field, Assets.BundleSet? bundles = null, string bundle = "")
    {
        try
        {
            var mesh = UnityMesh.Read(field, bundles is null || bundle.Length == 0
                ? null
                : (path, offset, size) => bundles.ReadResource(bundle, path, offset, size));

            return mesh.VertexCount > 0 ? mesh : null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// An edited file read back for comparison against what the game holds.
    ///
    /// The same decoders the importers use, so what is shown is what would be written: a PNG that
    /// the texture importer would reject shows nothing here rather than a preview of something the
    /// pack could not contain.
    public static object? FromFile(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        try
        {
            return extension switch
            {
                "png" or "jpg" or "jpeg" or "bmp" or "tga" => Picture(path),
                "glb" or "gltf" => Import.Meshes.GltfMeshReader.Read(path),
                "wav" or "mp3" or "ogg" => new PreviewSound(Import.Audio.SoundFile.Read(path)),
                _ => null,
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    private static PreviewImage? Picture(string path)
    {
        using var stream = File.OpenRead(path);
        var image = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        if (image is null || image.Width == 0) return null;

        // Stb hands back RGBA rows top down; a bitmap wants BGRA the same way up.
        return new PreviewImage(image.Width, image.Height, PreviewImage.Swapped(image.Data));
    }

    private static byte[] FlipRows(byte[] bgra, int width, int height)
    {
        var stride = width * 4;
        var flipped = new byte[stride * height];
        for (var row = 0; row < height; row++)
            Array.Copy(bgra, row * stride, flipped, (height - 1 - row) * stride, stride);
        return flipped;
    }
}
