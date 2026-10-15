using PGAssetTool.Core.Export.Meshes;

namespace PGAssetTool.Core.Preview;

/// The picture a pack shows itself with: its model, wearing its textures, drawn once to a file.
///
/// The game's own weapon icon says which weapon a pack is for and nothing about what the pack does
/// to it — which is the only interesting part, since these are texture and mesh mods. A drawing of
/// the thing itself answers both at a glance.
///
/// Drawn to a file rather than rendered wherever it is shown. A manager listing a dozen mods would
/// otherwise rasterise a dozen models on every repaint, and the angle would be whatever the code
/// chose rather than the one the author thought showed their work best.
public static class PackIcon
{
    /// Written into the workspace under a name the export never produces, so it can never be
    /// mistaken for one of the weapon's own files or picked up as something to replace.
    public const string FileName = "pack-icon.png";

    public const int Size = 256;

    /// Which way up the model is looked at: the angle the preview opens at, which is the angle the
    /// game draws its own shop pictures at. How far away is worked out per model rather than fixed,
    /// which is the one thing this does differently from the preview.
    public static Camera Angle { get; } = new();

    /// How much of the frame the model is made to take up, leaving a margin so it does not read as
    /// a crop of something larger.
    private const float Fill = 0.94f;

    /// Draws the whole model as large as it will go, at the angle it was asked for.
    ///
    /// The distance the preview starts at is a multiple of the model's bounding sphere, which for
    /// a rifle is most of a metre of empty air in every direction that is not along the barrel — so
    /// a fixed distance draws a small object in a large empty square. Where the model actually lands
    /// is worked out first and the framing set from it, which needs no knowledge of the shape and
    /// holds at any angle.
    ///
    /// The whole of it, whatever the view being copied was showing. The pane a snapshot is taken
    /// from is usually much wider than it is tall, and a square picture of the same view cuts both
    /// ends off a long weapon — so what the icon keeps is the angle, and it finds its own distance.
    /// <param name="standing">
    /// How to stand the model up, when the caller knows better than the bounding box does — which
    /// for a weapon means <see cref="Facing"/>, so every icon points the same way. Null takes the
    /// renderer's own answer.
    /// </param>
    public static PreviewImage Render(
        UnityMesh mesh, IReadOnlyList<PreviewImage?>? textures, Camera? camera = null, int size = Size,
        MeshRenderer.Basis? standing = null)
    {
        var target = new RenderTarget();
        target.Resize(size, size);

        var at = camera ?? Angle;
        var viewpoint = standing is { } stood
            ? new Viewpoint(stood, MeshRenderer.View(at))
            : (Viewpoint?)null;

        var view = Frame(mesh, at, viewpoint);
        MeshRenderer.Render(mesh, view, target, textures, viewpoint: viewpoint);

        // The rasterizer writes an opaque pixel where it draws and leaves the rest at zero, so what
        // it produces is already straight alpha and needs no unpicking.
        return new PreviewImage(size, size, target.Bgra);
    }

    /// Puts the model in the middle of a square frame and scales it to fill.
    ///
    /// Exact rather than iterative, because it is measured from the model and not from a picture of
    /// it: a drawing is clipped at the frame, so a model that overflows reads as one that fits, and
    /// correcting from that could only ever creep towards the answer a few percent at a time.
    private static Camera Frame(UnityMesh mesh, Camera camera, Viewpoint? viewpoint)
    {
        if (MeshRenderer.Extent(mesh, camera, viewpoint) is not { } at) return camera;

        // Panned takes half-frames rightwards and upwards; the extent counts downwards.
        var centred = camera.Panned(
            -(at.Left + at.Right) * 0.5f, (at.Top + at.Bottom) * 0.5f);

        // The longer of the two decides, or a long model would be scaled to fit its height and
        // hang off both sides.
        var half = Math.Max(at.Right - at.Left, at.Bottom - at.Top) * 0.5f;
        if (half <= 0) return centred;

        return centred with { Distance = Math.Clamp(camera.Distance * half / Fill, 0.05f, 40f) };
    }

    /// True when anything was actually drawn. A model that missed the frame entirely would write a
    /// file that is transparent from corner to corner, which looks exactly like a missing icon.
    public static bool IsBlank(PreviewImage picture)
    {
        for (var i = 3; i < picture.Bgra.Length; i += 4)
            if (picture.Bgra[i] != 0) return false;
        return true;
    }

    public static void Write(PreviewImage picture, string path)
    {
        var rgba = new byte[picture.Bgra.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = picture.Bgra[i + 2];
            rgba[i + 1] = picture.Bgra[i + 1];
            rgba[i + 2] = picture.Bgra[i];
            rgba[i + 3] = picture.Bgra[i + 3];
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        new StbImageWriteSharp.ImageWriter().WritePng(
            rgba, picture.Width, picture.Height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, file);
    }
}
