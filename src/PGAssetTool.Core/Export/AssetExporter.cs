using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using Fmod5Sharp;
using Fmod5Sharp.FmodTypes;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Import.Audio;

namespace PGAssetTool.Core.Export;

/// A texture a mask is being asked about: which one, how big, and how far a mask has to reach past
/// the triangles for it — which depends on how the game filters this one. See UvCoverage.MarginFor.
public readonly record struct TextureShape(
    string Bundle, long PathId, int Width, int Height, int Margin);

/// <param name="AlphaIsMask">
/// True for a texture written out masked to the part a model samples: its alpha channel is then the
/// mask rather than anything of the original's, and the operation built from it says so.
/// </param>
/// <param name="Wears">
/// For a mesh, the textures it is drawn with — as addresses, which the manifest turns into the paths
/// of whichever of them were written out beside it. Worked out at extraction because that is the
/// only moment anything has the renderers and the materials to hand.
/// </param>
public sealed record ExportedAsset(
    string Path, AssetClassID Class, string Name, string Format, long Bytes, AssetAddress Address,
    bool AlphaIsMask = false, IReadOnlyList<AssetAddress>? Wears = null);

/// Writes assets out in whatever format is actually editable for their type: an image editor can
/// open a PNG, an audio editor a WAV. A type with no such format is not written at all.
public sealed class AssetExporter(BundleSet bundles)
{
    /// The per-type writers do not know the asset's address; Export fills it in on the way out.
    private static readonly AssetAddress Placeholder = new("", "", "");

    public ContainerIndex Index { get; } = new(bundles.Context);

    /// Write textures with no alpha channel at all. See WriteWithoutAlpha for why anyone would.
    public bool Opaque { get; init; }

    /// Which texels of a texture any model actually samples. Null, or a null answer, writes the
    /// whole image. See UvCoverage and WriteMasked.
    public Func<TextureShape, bool[]?>? Coverage { get; set; }

    /// `fileNameOverride` keeps distinct assets that share a name from overwriting each other's
    /// output; the caller knows which names repeat and can qualify them.
    ///
    /// Only what can come back — a texture, a mesh, a sound. Everything else used to be written out
    /// as a field dump and, where the bytes could go back, as a `.dat` beside it. Nothing read the
    /// dumps, and a folder of files an author cannot act on reads as an invitation to try, so an
    /// asset of any other class writes nothing — and leaves no empty folder behind for it either.
    public IReadOnlyList<ExportedAsset> Export(
        string bundle, AssetsFileInstance file, AssetFileInfo info, string directory,
        string? fileNameOverride = null)
    {
        var cls = (AssetClassID)info.TypeId;
        if (!Pack.Replaceable.Supports(cls)) return [];

        var field = bundles.Context.Deserialize(file, info);
        if (field is null) return [];

        var named = AssetNaming.NameOf(field, cls) is { Length: > 0 } n ? n : null;
        var name = named ?? $"{cls}_{info.PathId}";
        var address = Index.AddressOf(bundle, file, info, named ?? "");

        Directory.CreateDirectory(directory);
        var stem = Path.Combine(directory, Sanitize(fileNameOverride ?? name));

        ExportedAsset? exported;
        try
        {
            exported = cls switch
            {
                AssetClassID.Texture2D => ExportTexture(bundle, info.PathId, field, stem),
                AssetClassID.AudioClip => ExportAudio(bundle, field, stem),
                AssetClassID.Mesh => ExportMesh(bundle, info.PathId, field, stem),
                AssetClassID.AnimationClip => ExportAnimation(field, stem),
                _ => null,
            };
        }
        catch (Exception e) when (e is not (IOException or UnauthorizedAccessException or OutOfMemoryException))
        {
            // One asset the game ships in a shape nothing here expected is that asset's loss, and is
            // said so. It used to be the whole extract's: a single avatar mesh with two NaN normals
            // left the item with no workspace at all. Trouble with the disk is still everybody's,
            // because every file after it would fail the same way.
            Failed.Add($"{name}: could not be written out ({e.GetType().Name}: {e.Message})");
            return [];
        }

        return exported is null ? [] : [exported with { Class = cls, Name = name, Address = address }];
    }

    /// The assets Export could not write, with why, since the caller last emptied this.
    public List<string> Failed { get; } = [];

    private ExportedAsset? ExportTexture(string bundle, long pathId, AssetTypeValueField field, string stem)
    {
        var texture = TextureFile.ReadTextureFile(field);
        var pixels = ResolvePayload(bundle, field["m_StreamData"], texture.pictureData);
        if (pixels is null || pixels.Length == 0) return null;

        var path = stem + ".png";
        texture.pictureData = pixels;

        var used = Coverage?.Invoke(new TextureShape(bundle, pathId, texture.m_Width, texture.m_Height,
            Meshes.UvCoverage.MarginFor(
                field["m_TextureSettings"]["m_FilterMode"].AsInt, field["m_MipCount"].AsInt)));

        if (used is not null)
        {
            if (!WriteMasked(texture, pixels, path, used)) return null;
        }
        else if (Opaque)
        {
            if (!WriteWithoutAlpha(texture, pixels, path)) return null;
        }
        else if (!texture.DecodeTextureImage(pixels, path, ImageExportType.Png, 100)) return null;

        return new ExportedAsset(path, AssetClassID.Texture2D, "", "png", new FileInfo(path).Length,
            Placeholder, AlphaIsMask: used is not null);
    }

    /// Writes the image with everything no model samples made fully transparent.
    ///
    /// A weapon's texture is an atlas and most of it is nothing: an author opening one has to find
    /// the part that matters by painting and looking. Cleared rather than outlined, because a
    /// transparent region is the one marking every image editor shows the same way.
    ///
    /// The alpha becomes the mask outright — solid inside, empty outside — rather than the
    /// original's. It has to: most of these textures keep emission in that channel and Map_Beretta_A
    /// is 99.8% transparent before anything is done to it, so an export that kept the original alpha
    /// would come out as invisible as it went in and the mask would show nothing at all.
    ///
    /// What the game had there is not lost. The operation this file belongs to is marked
    /// AlphaIsMask, and applying one of those keeps the alpha already in the game — so the channel
    /// is the tool's for as long as the file is on disk, and the author's work is the colour.
    private static bool WriteMasked(TextureFile texture, byte[] pixels, string path, bool[] used)
    {
        var bgra = texture.DecodeTextureRaw(pixels, useBgra: true);
        if (bgra is null || bgra.Length < texture.m_Width * texture.m_Height * 4) return false;
        if (used.Length != texture.m_Width * texture.m_Height) return false;

        var rgba = new byte[texture.m_Width * texture.m_Height * 4];
        var stride = texture.m_Width * 4;

        for (var row = 0; row < texture.m_Height; row++)
        {
            // Unity stores the bottom row first; a PNG starts at the top. The mask was built the
            // PNG's way up, so it is read by the row being written rather than the row being read.
            var from = (texture.m_Height - 1 - row) * stride;
            var to = row * stride;

            for (var x = 0; x < texture.m_Width; x++)
            {
                rgba[to + x * 4] = bgra[from + x * 4 + 2];
                rgba[to + x * 4 + 1] = bgra[from + x * 4 + 1];
                rgba[to + x * 4 + 2] = bgra[from + x * 4];
                rgba[to + x * 4 + 3] = used[row * texture.m_Width + x] ? (byte)255 : (byte)0;
            }
        }

        using var file = File.Create(path);
        new StbImageWriteSharp.ImageWriter().WritePng(
            rgba, texture.m_Width, texture.m_Height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, file);
        return true;
    }

    /// Writes the colour channels and drops the alpha entirely.
    ///
    /// Most of these textures keep something other than coverage in that channel — emission,
    /// usually — so an image editor opens them as mostly-transparent and painting means fighting a
    /// mask that has nothing to do with what is being painted. The alpha is not lost: an image
    /// brought back without one is given the original's again.
    private static bool WriteWithoutAlpha(TextureFile texture, byte[] pixels, string path)
    {
        var bgra = texture.DecodeTextureRaw(pixels, useBgra: true);
        if (bgra is null || bgra.Length < texture.m_Width * texture.m_Height * 4) return false;

        var rgb = new byte[texture.m_Width * texture.m_Height * 3];
        var stride = texture.m_Width * 4;

        for (var row = 0; row < texture.m_Height; row++)
        {
            // Unity stores the bottom row first; a PNG starts at the top.
            var from = (texture.m_Height - 1 - row) * stride;
            var to = row * texture.m_Width * 3;
            for (var x = 0; x < texture.m_Width; x++)
            {
                rgb[to + x * 3] = bgra[from + x * 4 + 2];
                rgb[to + x * 3 + 1] = bgra[from + x * 4 + 1];
                rgb[to + x * 3 + 2] = bgra[from + x * 4];
            }
        }

        using var file = File.Create(path);
        new StbImageWriteSharp.ImageWriter().WritePng(
            rgb, texture.m_Width, texture.m_Height, StbImageWriteSharp.ColorComponents.RedGreenBlue, file);
        return true;
    }

    /// A mesh that cannot be unpacked writes nothing rather than failing the export.
    /// Where each of the mesh's bones sits at rest, in the order the bind poses are in, or null for
    /// a mesh nothing skins or one whose rig cannot be read.
    private IReadOnlyList<float[]>? RestPoses(string bundle, long pathId, Meshes.UnityMesh mesh)
    {
        if (mesh.BindPoses.Count == 0) return null;
        if (Preview.Skeleton.For(bundles, bundle, pathId) is not { } skeleton) return null;

        try
        {
            var rest = skeleton.World(null, 0);
            var poses = new List<float[]>(mesh.BindPoses.Count);

            for (var b = 0; b < mesh.BindPoses.Count; b++)
            {
                if (b >= skeleton.Bones.Count) return null;
                poses.Add(rest[skeleton.Bones[b]]);
            }

            return poses;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or KeyNotFoundException)
        {
            return null;
        }
    }

    private ExportedAsset? ExportMesh(string bundle, long pathId, AssetTypeValueField field, string stem)
    {
        try
        {
            var mesh = UnityMesh.Read(field,
                (path, offset, size) => bundles.ReadResource(bundle, path, offset, size));
            if (mesh.VertexCount == 0) return null;
            var path = stem + ".glb";

            // With the bones where the game has them, so what opens in an editor is the weapon and
            // not its parts laid out around the origin. See GlbWriter.Write.
            GlbWriter.Write(mesh, path, RestPoses(bundle, pathId, mesh));
            return new ExportedAsset(path, AssetClassID.Mesh, "", "glb", new FileInfo(path).Length, Placeholder);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// A clip's motion as a `.anim`. A clip that moves no transform — the ones that only switch a
    /// muzzle flash on — writes nothing: there is nothing in it this tool writes back.
    private static ExportedAsset? ExportAnimation(AssetTypeValueField field, string stem)
    {
        if (Preview.Motion.Read(field) is not { } motion) return null;
        var path = stem + Animation.ClipFile.Extension;
        Animation.ClipFile.FromMotion(motion, from: "").Write(path);
        return new ExportedAsset(path, AssetClassID.AnimationClip, "", "anim", new FileInfo(path).Length, Placeholder);
    }

    private ExportedAsset? ExportAudio(string bundle, AssetTypeValueField field, string stem)
    {
        var resource = field["m_Resource"];
        if (resource.IsDummy) return null;
        var payload = bundles.ReadResource(
            bundle, resource["m_Source"].AsString, resource["m_Offset"].AsLong, resource["m_Size"].AsLong);

        if (!FsbLoader.TryLoadFsbFromByteArray(payload, out var bank) || bank is null) return null;
        var sample = bank.Samples.FirstOrDefault();
        if (sample is null) return null;

        // Anything the library can hand back in its own container — Vorbis as an Ogg — is written
        // out that way, since a clip the game already holds compressed gains nothing from being
        // decoded on the way out. Everything that would have become a WAV goes through the same
        // decoding the preview uses instead, which for ADPCM is ours rather than the library's.
        // See FsbAdpcm for why that matters.
        if (bank.Header.AudioType != FmodAudioType.IMAADPCM
            && sample.RebuildAsStandardFileFormat(out var rebuilt, out var extension)
            && rebuilt is not null && extension != "wav")
        {
            var asIs = $"{stem}.{extension}";
            File.WriteAllBytes(asIs, rebuilt);
            return new ExportedAsset(asIs, AssetClassID.AudioClip, "", extension!, rebuilt.Length, Placeholder);
        }

        if (FsbSound.Read(sample, bank.Header) is not { } sound) return null;

        var data = WaveFile.Write(sound);
        var path = $"{stem}.wav";
        File.WriteAllBytes(path, data);
        return new ExportedAsset(path, AssetClassID.AudioClip, "", "wav", data.Length, Placeholder);
    }

    /// Payload bytes either sit inline on the object or in a stream entry it points at.
    private byte[]? ResolvePayload(string bundle, AssetTypeValueField streamData, byte[]? inline)
    {
        if (streamData.IsDummy) return inline;
        var source = streamData["path"].AsString;
        if (source.Length == 0) return inline;
        return bundles.ReadResource(
            bundle, source, streamData["offset"].AsLong, streamData["size"].AsLong);
    }

    /// A name from the game's own data, made into one Windows will take. See `SafeName`.
    ///
    /// An object with no name at all — the game has those — comes back as a word rather than as
    /// nothing, so that what is written is a file and not a bare extension.
    public static string Sanitize(string name) => Settings.SafeName.For(name, "unnamed");
}
