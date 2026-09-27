using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Gui.Controls;

/// Shows a mesh, turned by dragging and zoomed by the wheel.
///
/// The rasterizer lives in the core and writes straight into the bitmap's own memory, so a frame is
/// one pass over the triangles and a blit; nothing is allocated per frame except when the control
/// is resized.
public sealed class MeshView : Control
{
    public static readonly StyledProperty<UnityMesh?> MeshProperty =
        AvaloniaProperty.Register<MeshView, UnityMesh?>(nameof(Mesh));

    /// One texture per submesh, in Unity order. Null entries and a short list are both fine; those
    /// parts are drawn plain.
    public static readonly StyledProperty<IReadOnlyList<PreviewImage?>?> TexturesProperty =
        AvaloniaProperty.Register<MeshView, IReadOnlyList<PreviewImage?>?>(nameof(Textures));

    /// The model to size and centre the view by, when an animation is moving the one being drawn.
    /// Null means the view is framed by whatever is on screen, which is the ordinary case.
    public static readonly StyledProperty<UnityMesh?> FramingProperty =
        AvaloniaProperty.Register<MeshView, UnityMesh?>(nameof(Framing));

    /// Where the model is being looked at from.
    ///
    /// A property rather than a field, and bound two ways, so the view outlives the control: the
    /// pane that draws a mesh is rebuilt whenever what it shows is read again, and an angle kept
    /// here alone went back to square every time. It also lets two panes share one view, which is
    /// what makes a side-by-side comparison a comparison.
    public static readonly StyledProperty<Camera> CameraProperty =
        AvaloniaProperty.Register<MeshView, Camera>(
            nameof(Camera), new Camera(), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public UnityMesh? Mesh
    {
        get => GetValue(MeshProperty);
        set => SetValue(MeshProperty, value);
    }

    public IReadOnlyList<PreviewImage?>? Textures
    {
        get => GetValue(TexturesProperty);
        set => SetValue(TexturesProperty, value);
    }

    public UnityMesh? Framing
    {
        get => GetValue(FramingProperty);
        set => SetValue(FramingProperty, value);
    }

    /// How to stand the model up, when something upstream knows better than the bounding box.
    ///
    /// For a weapon that is Facing, which reads the prefab and turns the model so the barrel points
    /// the same way every time. Null leaves the renderer to sort the bounding box, which is right
    /// for anything that is not a weapon and for a mesh opened on its own in the editor.
    public static readonly StyledProperty<MeshRenderer.Basis?> StandingProperty =
        AvaloniaProperty.Register<MeshView, MeshRenderer.Basis?>(nameof(Standing));

    public MeshRenderer.Basis? Standing
    {
        get => GetValue(StandingProperty);
        set => SetValue(StandingProperty, value);
    }

    private Viewpoint? Viewpoint
        => Standing is { } stood ? new Viewpoint(stood, MeshRenderer.View(Camera)) : null;

    public Camera Camera
    {
        get => GetValue(CameraProperty);
        set => SetValue(CameraProperty, value);
    }

    private WriteableBitmap? _bitmap;
    private readonly RenderTarget _target = new();
    private PixelSize _size;
    private Point? _dragging;

    static MeshView()
    {
        // A new model no longer gets a fresh viewpoint from here. Whether one asset has replaced
        // another or the same one has merely been read again is a question the control cannot
        // answer, and answering it wrongly threw away an angle somebody had just chosen; whoever
        // owns the camera decides.
        AffectsRender<MeshView>(
            MeshProperty, TexturesProperty, CameraProperty, FramingProperty, StandingProperty);
    }

    /// Set while the middle button is down, which pans instead of turning.
    private bool _panning;

    /// Draws what is on screen into a square picture, at whatever angle it is being looked at.
    ///
    /// Its own target rather than the one on screen: an icon is square and a fixed size, and the
    /// pane is neither. The camera is the one the person turned to, which is the whole point —
    /// they have already decided what shows the model best.
    public PreviewImage? Snapshot(int size)
        => Mesh is { } mesh ? PackIcon.Render(mesh, Textures, Camera, size, Standing) : null;

    /// Puts the view back to square. A tilt or a pan is easy to lose track of, and hunting the way
    /// back by hand is worse than either was useful.
    public void Recentre() => Camera = new Camera();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _panning = e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed;
        _dragging = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragging is not { } from) return;
        var to = e.GetPosition(this);
        _dragging = to;

        // The middle button drags the model around the frame, as it does in the modelling tools
        // this sits beside. Zooming in on the muzzle of a rocket launcher is otherwise impossible:
        // the view is centred on the whole model and only the middle of it can be reached.
        if (_panning)
        {
            var half = Math.Min(Bounds.Width, Bounds.Height) * 0.5;
            if (half > 0)
                Camera = Camera.Panned((float)((to.X - from.X) / half), (float)((from.Y - to.Y) / half));
            return;
        }

        // Shift tilts the model in the plane of the screen instead of turning it. A modifier rather
        // than a mode: the ordinary drag keeps working exactly as it did, and this is the third way
        // of turning something, which orbiting alone cannot reach.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Negative for a rightward drag, for the same reason the turning drag reads the way it
            // does: the picture is a true view rather than a mirrored one, and the tilt has to
            // follow the cursor across the picture as it is drawn.
            Camera = Camera.Rolled((float)((from.X - to.X) * 0.01));
            return;
        }

        // Dragging takes the model with it: cursor right turns the near face right. The opposite
        // convention — moving the camera instead — reads as the model going the wrong way.
        // A drag across the full width turns it most of the way round.
        Camera = Camera.Dragged((float)((to.X - from.X) * 0.01), (float)((to.Y - from.Y) * 0.01));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _dragging = null;
        _panning = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        Camera = Camera.Zoomed(e.Delta.Y > 0 ? 0.88f : 1.14f);
    }

    /// How many device pixels one unit of this control's own layout is drawn as.
    ///
    /// The scale of the screen and the scale the window is drawn at, together, read off the way this
    /// control actually lands on it rather than asked of either separately. The model is rasterised
    /// at that many pixels and drawn back into the room the layout gave it, so it is drawn once per
    /// pixel it is shown at: rendered at the layout's own size and left to be magnified, a model on
    /// a 150% display — or in a window scaled up in Options — came out soft, which on a model is
    /// most visible on exactly the thin edges somebody is looking at.
    ///
    /// One, unless something says otherwise: nothing in the visual tree is a transform in the
    /// headless runs, and the arithmetic below then leaves the size exactly as it was.
    private double PixelsPerUnit
    {
        get
        {
            if (VisualRoot is not { } root) return 1;
            var scale = this.TransformToVisual((Visual)root)?.M11 ?? 1;
            return scale > 0 ? scale * root.RenderScaling : 1;
        }
    }

    public override void Render(DrawingContext context)
    {
        var scale = PixelsPerUnit;
        var width = (int)(Bounds.Width * scale);
        var height = (int)(Bounds.Height * scale);
        if (Mesh is not { } mesh || width < 2 || height < 2) return;

        if (_bitmap is null || _size.Width != width || _size.Height != height)
        {
            _size = new PixelSize(width, height);
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(_size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            _target.Resize(width, height);
        }

        MeshRenderer.Render(mesh, Camera, _target, Textures, Framing, Viewpoint);

        using (var locked = _bitmap.Lock())
            System.Runtime.InteropServices.Marshal.Copy(_target.Bgra, 0, locked.Address, _target.Bgra.Length);

        // Into the room the layout gave it, which is what `width` and `height` are a multiple of.
        context.DrawImage(_bitmap, new Rect(0, 0, Bounds.Width, Bounds.Height));
    }
}
