using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Gui.Controls;

/// Draws a clip's envelope: one vertical line per pixel column, from its quietest sample to its
/// loudest.
///
/// What this has to answer is whether a replacement is the same shape as what it replaces — the
/// same length, the same attack, roughly the same level. A picture of that is faster to read than
/// any number, and it is visible without having to play anything.
public sealed class WaveformView : Control
{
    public static readonly StyledProperty<PreviewSound?> SoundProperty =
        AvaloniaProperty.Register<WaveformView, PreviewSound?>(nameof(Sound));

    public PreviewSound? Sound
    {
        get => GetValue(SoundProperty);
        set => SetValue(SoundProperty, value);
    }

    /// The envelope is recomputed only when the clip or the width changes; a clip is hundreds of
    /// thousands of samples, and a resize should not re-read all of them per frame.
    private (float Low, float High)[]? _envelope;
    private PreviewSound? _drawn;
    private int _columns;

    static WaveformView() => AffectsRender<WaveformView>(SoundProperty);

    public override void Render(DrawingContext context)
    {
        var width = (int)Bounds.Width;
        var height = Bounds.Height;
        if (Sound is not { } sound || width < 2 || height < 4) return;

        if (_envelope is null || _columns != width || !ReferenceEquals(_drawn, sound))
        {
            _envelope = sound.Envelope(width);
            _columns = width;
            _drawn = sound;
        }

        var middle = height / 2;
        var wave = new Pen(new SolidColorBrush(Color.FromRgb(0x7c, 0xc4, 0xff)), 1);
        var centre = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xff, 0xff, 0xff)), 1);

        context.DrawLine(centre, new Point(0, middle), new Point(width, middle));

        for (var x = 0; x < _envelope.Length; x++)
        {
            var (low, high) = _envelope[x];

            // Silence still gets a mark, or a quiet passage reads as a gap in the drawing rather
            // than as quiet.
            var top = middle - high * middle;
            var bottom = middle - low * middle;
            if (bottom - top < 1) (top, bottom) = (middle - 0.5, middle + 0.5);

            context.DrawLine(wave, new Point(x + 0.5, top), new Point(x + 0.5, bottom));
        }

        // Where the clip has got to, and only for the clip that is actually sounding: the editor
        // puts two of these on the page and one of them is silent.
        if (!ReferenceEquals(Audio.Speaker.Sounding, sound)) return;

        var at = width * Audio.Speaker.Through;
        context.DrawLine(
            new Pen(new SolidColorBrush(Color.FromRgb(0xff, 0xd0, 0x66)), 1.5),
            new Point(at, 0), new Point(at, height));
    }

    /// Redraws while something is playing, and stops as soon as nothing is.
    ///
    /// Driven from the speaker rather than run all the time: a waveform is on the page whenever a
    /// sound is selected, and following behind every one of those would be a redraw a frame for a
    /// line that is not moving.
    private bool _following;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Audio.Speaker.Changed += Follow;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Audio.Speaker.Changed -= Follow;
        _following = false;
    }

    /// On the window's own frames, which is where the line is actually drawn. Asked for one at a
    /// time: each frame asks for the next while the sound is still playing, so nothing is queued
    /// behind a window that is covered or dragged. A model's clip plays the same way; see
    /// PreviewViewModel.RequestFrame.
    private void Follow()
    {
        InvalidateVisual();
        if (!Audio.Speaker.IsPlaying || _following) return;
        if (TopLevel.GetTopLevel(this) is not { } window) return;

        _following = true;

        void Frame(TimeSpan now)
        {
            if (!_following) return;

            InvalidateVisual();
            if (!Audio.Speaker.IsPlaying) { _following = false; return; }

            window.RequestAnimationFrame(Frame);
        }

        window.RequestAnimationFrame(Frame);
    }
}
