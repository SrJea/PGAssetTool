using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PGAssetTool.Core.Settings;
using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Gui.ViewModels;

/// Whether the alpha toggle follows the asset or follows the person looking at it.
///
/// Shared by every pane, so flipping it in the editor's left half does not leave the right half
/// disagreeing, and moving between panes keeps the answer. Not saved between runs: the automatic
/// default is the better starting point each time, and a deliberate choice is about the picture in
/// front of you rather than a standing preference.
public sealed class AlphaPreference
{
    /// Null until somebody works the toggle; after that, their answer for everything.
    public bool? Chosen { get; set; }
}

/// What is shown beside the tree for whichever node is selected.
public sealed partial class PreviewViewModel(AlphaPreference? alpha = null) : ObservableObject
{
    private readonly AlphaPreference _alpha = alpha ?? new AlphaPreference();
    private PreviewImage? _picture;

    /// Set while Show is choosing the value, so the automatic default is not mistaken for a choice.
    private bool _deciding;

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private UnityMesh? _mesh;
    [ObservableProperty] private PreviewSound? _sound;

    /// How this model is stood up, when something knew better than the bounding box. Null is the
    /// renderer's own answer, which is what anything that is not a weapon gets.
    [ObservableProperty] private MeshRenderer.Basis? _standing;

    /// One per submesh, resolved from the materials the renderer drawing this mesh holds.
    [ObservableProperty] private IReadOnlyList<PreviewImage?>? _meshTextures;

    /// Every texture in the weapon, so a skin can be tried on the model the automatic answer does
    /// not know about. Null means the automatic one.
    public ObservableCollection<TextureChoice> TextureChoices { get; } = [];

    [ObservableProperty] private TextureChoice? _chosenTexture;
    [ObservableProperty] private string _caption = "";
    [ObservableProperty] private string? _nothing = "Select a texture, a mesh or a sound.";

    /// How the model is being looked at.
    ///
    /// Here rather than in the control that draws it, because a view is worth more than the reading
    /// that produced it. The editor re-reads a file whenever anything in the workspace is written —
    /// saving the pack's own icon counts — and a camera owned by the control started over every
    /// time, so turning the model to a good angle and then using it as the icon threw the angle
    /// away at the moment it had proved useful.
    [ObservableProperty] private Camera _camera = new();

    /// The model as it was read, before anything was played on it.
    ///
    /// The frame is taken from this rather than from what is on screen: a pistol whose magazine has
    /// dropped out of it is a taller model than the same pistol at rest, and a view sized to the
    /// moment slides about while the clip runs. It is also what posing starts from each frame,
    /// because a pose is written against the model at rest and not against the last one.
    [ObservableProperty] private UnityMesh? _rest;

    /// The clips that move the model on show, and which of them is playing.
    ///
    /// A weapon's prefab carries clips for the whole of it — the muzzle flash, the shell casing —
    /// and only some of them touch the mesh in front of you. The ones that do not would play as a
    /// model sitting still, which reads as the feature being broken, so they are left out.
    public ObservableCollection<MotionChoice> Clips { get; } = [];

    [ObservableProperty] private MotionChoice? _clip;

    /// Whether the model has anything to play, so the controls stay out of the way when it has not.
    public bool HasClips => Clips.Count > 0;

    [ObservableProperty] private bool _playing;

    /// How far through the clip, in seconds. Bound to the scrub bar, and moved by the timer.
    [ObservableProperty] private double _time;

    public double Length => Clip?.Motion.Length ?? 0;

    private Skeleton? _skeleton;

    /// The other meshes of the same prefab, each with the skeleton that places it — the arms a
    /// weapon is held in, above all.
    ///
    /// Kept beside the model rather than joined to it once, because a clip has to move them: they
    /// are posed one by one and joined again every frame. Joining them once and posing the result
    /// cannot work, since blend indices count against a bone list belonging to one mesh.
    ///
    /// Empty is the ordinary case, and the one the rest of this file was written for.
    private IReadOnlyList<(UnityMesh Mesh, Skeleton Skeleton, bool Frames)> _with = [];

    /// What the view is framed by: the model itself, never what it is held in. Framing by the pair
    /// would shrink every weapon to make room for a forearm reaching out of the picture.
    [ObservableProperty] private UnityMesh? _framing;

    /// The model and everything shown with it, at one moment, in the prefab's own space. Null when
    /// there is nothing to show it with, which leaves every other path exactly as it was.
    ///
    /// Each part is posed by its own skeleton — its bones, its bind poses — and they are joined
    /// afterwards. `Placed` rather than `Pose` because the two have only the prefab's space in
    /// common; see Skeleton.Placed.
    /// <param name="framing">
    /// Leave out the parts that do not size the view — the arms. See MainViewModel.Held.
    /// </param>
    private UnityMesh? Built(Motion? motion, float time, bool framing = false)
    {
        if (_with.Count == 0 || _skeleton is null || _authored is null) return null;

        var parts = new List<UnityMesh> { _skeleton.Placed(_authored, motion, time) };
        foreach (var (mesh, skeleton, frames) in _with)
            if (!framing || frames)
                parts.Add(skeleton.Placed(mesh, motion, time));

        return UnityMesh.Combined(_authored.Name, parts);
    }

    /// What a model can be made to do, if anything.
    private void Dress(Skeleton? skeleton, IReadOnlyList<Motion>? motions,
        IReadOnlyList<(UnityMesh Mesh, Skeleton Skeleton, bool Frames)>? with = null)
    {
        Stop();
        _skeleton = skeleton;
        _with = with ?? [];
        _unposable = false;
        Clips.Clear();

        if (skeleton is not null)
            foreach (var motion in motions ?? [])
                if (skeleton.Moves(motion))
                    Clips.Add(new MotionChoice(motion));

        Clip = null;
        Time = 0;
        OnPropertyChanged(nameof(HasClips));
    }

    partial void OnClipChanged(MotionChoice? value)
    {
        Time = 0;
        OnPropertyChanged(nameof(Length));
        Pose();

        // Choosing one is asking to see it. Stopping is the button beside it.
        if (value is not null) Playing = true;
        else Playing = false;
    }

    partial void OnTimeChanged(double value) => Pose();

    /// How the window asks to be called once for each frame it draws. Set by the window; null where
    /// there is none, which is a test, and then the clip moves only when Advance is called.
    public Action<Action<TimeSpan>>? RequestFrame { get; set; }

    /// Which run of playing the frames belong to, so stopping and starting again cannot leave two
    /// loops of frames running and the clip playing at twice the speed.
    private int _playback;

    /// Plays on the display's own frames, each moving the clip on by the time that really passed.
    ///
    /// It was a timer at thirty a second moving the clip a thirtieth each tick. On a 120Hz screen
    /// that read as a stutter — three frames out of four showed nothing new — and any slow frame
    /// put the clip behind the clock for good, because a missed tick was never made up.
    partial void OnPlayingChanged(bool value)
    {
        var mine = ++_playback;
        if (!value || Clip is null || RequestFrame is not { } request) return;

        TimeSpan? last = null;

        void Frame(TimeSpan now)
        {
            if (mine != _playback || !Playing || Clip is null) return;

            // The first frame only notes the time. Any gap is capped, so a window that was dragged
            // or covered for a while carries on from where it was rather than leaping ahead.
            if (last is { } then) Advance(Math.Min((now - then).TotalSeconds, 0.25));
            last = now;

            request(Frame);
        }

        request(Frame);
    }

    /// Moves the clip on by so much, and the model with it.
    ///
    /// Apart from the frames that call it, because a clock is a poor thing to test through: the one
    /// on screen is the window's own and does not tick in a test that has no window. Everything a
    /// frame does is here, where it can be driven one at a time.
    public void Advance(double seconds)
    {
        if (Clip is not { } clip || Rest is null) { Stop(); return; }

        // Round and round: a reload is under two seconds and watching it once tells you less than
        // watching it three times. The scrub bar follows, so where it is is always where the model
        // is.
        var length = Math.Max(clip.Motion.Length, 0.001);
        Time = (Time + seconds) % length;
    }

    /// Stops without forgetting where it had got to.
    public void Stop() => Playing = false;

    /// Puts the model where the clip has it now, or back as it was read when nothing is playing.
    ///
    /// Guarded because of when it runs: once for every frame the display draws, from a property
    /// changing, with nobody waiting for it — so a rig this cannot pose would not be a preview that
    /// fails, it would be the window going. The model then stays as it was read, which is a still
    /// picture of the right thing rather than nothing at all, and the clip stops so that the same
    /// failure is not met sixty times a second. Said once per model, not once per frame.
    private void Pose()
    {
        if (Rest is not { } rest) return;
        if (_skeleton is null || Clip is null || _unposable) { Mesh = rest; return; }

        try
        {
            Mesh = Built(Clip.Motion, (float)Time)
                ?? _skeleton.Pose(_authored ?? rest, Clip.Motion, (float)Time);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException
                                      or KeyNotFoundException or InvalidOperationException)
        {
            _unposable = true;
            Mesh = rest;
            Stop();
            ErrorLog.Record(e, $"posing '{rest.Name}' with '{Clip?.Motion.Name}'");
        }
    }

    /// Set when a model turns out not to be poseable, and cleared when another is put on show.
    private bool _unposable;

    /// The model as it was written, which is what a pose starts from. `Rest` is the same model put
    /// together, which is what is shown and what the framing is taken from.
    private UnityMesh? _authored;

    /// What is on show, as far as "is this still the same thing" goes. Null for anything that has
    /// no lasting identity, which starts the view over the way a different asset does.
    private string? _subject;

    /// The view each model was last left at, by subject.
    ///
    /// Per model rather than one for the last one looked at, because comparing two models means
    /// going back and forth between them: an angle found for one is wanted again on the way back,
    /// and a single remembered view lost it the moment anything else was selected. Kept for the
    /// length of a session and no longer — an entry is a camera and a file name.
    private readonly Dictionary<string, ViewState> _views = new(StringComparer.OrdinalIgnoreCase);

    private sealed record ViewState(Camera Camera, TextureChoice? Texture);

    partial void OnCameraChanged(Camera value) => Remember();

    private void Remember()
    {
        if (_subject is { } subject) _views[subject] = new ViewState(Camera, ChosenTexture);
    }

    /// Whether the alpha channel is being honoured.
    ///
    /// Only some textures mean coverage by it. Icons do — all four hundred of them sit on an empty
    /// background — but a model texture usually carries something else there, emission most often,
    /// and honouring it punches holes in the picture or blanks it entirely. So the default follows
    /// what the object is rather than what the channel contains, and this stays available for
    /// looking at the channel deliberately.
    [ObservableProperty] private bool _showAlpha;

    public bool HasImage => Image is not null;
    public bool HasMesh => Mesh is not null;
    public bool HasSound => Sound is not null;
    public bool CanToggleAlpha => _picture is not null;

    /// Plays the clip through the speakers. Only one plays at a time; see Speaker.
    [RelayCommand]
    private void Play()
    {
        if (Sound is not { } sound) return;
        try { Audio.Speaker.Play(sound.ToWave(), TimeSpan.FromSeconds(sound.Seconds), sound); }
        catch (Exception ex) { Caption = ErrorLog.Said(ex, "playing the clip"); }
    }

    [RelayCommand]
    private static void Silence() => Audio.Speaker.Stop();

    /// What the space bar does: start it, or stop it if it is still going.
    public void PlayOrStop()
    {
        if (Audio.Speaker.IsPlaying) Audio.Speaker.Stop();
        else Play();
    }

    partial void OnShowAlphaChanged(bool value)
    {
        if (!_deciding) _alpha.Chosen = value;
        Redraw();
    }

    public void Clear(string? why = null)
    {
        Dress(null, null);
        _picture = null;
        Image?.Dispose();
        Image = null;
        Mesh = null;
        Rest = null;
        Sound = null;
        Caption = "";
        Nothing = why ?? "Select a texture, a mesh or a sound.";
        Changed();
    }

    /// A clip, drawn as its envelope and playable. Nothing starts playing on its own: moving down a
    /// weapon's six sounds would otherwise mean six of them going off unasked.
    public void Show(PreviewSound sound, string caption)
    {
        _picture = null;
        Image?.Dispose();
        Image = null;
        Mesh = null;
        Rest = null;
        Dress(null, null);
        Sound = sound;
        Caption = $"{caption}   {sound.Describe}";
        Nothing = null;
        Changed();
    }

    public void Show(PreviewImage picture, string caption, bool alphaIsCoverage)
    {
        _picture = picture;
        Mesh = null;
        Sound = null;
        Caption = $"{caption}   {picture.Width}×{picture.Height}";
        Nothing = null;

        // What the asset is, until somebody says otherwise — and then what they said, for every
        // picture after it. Resetting to the automatic answer each time meant an icon came back with
        // its alpha honoured however many times it had just been turned off.
        var wanted = _alpha.Chosen ?? alphaIsCoverage;

        // Assigning the property redraws through its change handler, but only when the value moves.
        _deciding = true;
        var unchanged = ShowAlpha == wanted;
        ShowAlpha = wanted;
        _deciding = false;
        if (unchanged) Redraw();
    }

    /// <param name="subject">
    /// Which asset this is, so each one keeps the angle it was turned to and the texture put on it
    /// — through a second reading of the same file, and through a trip to something else and back.
    /// A model nobody has looked at yet starts square, since an angle chosen for a pistol says
    /// nothing about a rocket launcher.
    /// </param>
    /// <param name="standing">
    /// How to stand the model up, when the caller knows better than the bounding box. For a weapon
    /// that is Facing, which reads the prefab so the barrel points the same way every time; for a
    /// mesh opened on its own there is no prefab to ask and this stays null.
    /// </param>
    public void Show(UnityMesh mesh, string caption, IReadOnlyList<PreviewImage?>? textures,
        string? subject = null, Skeleton? skeleton = null, IReadOnlyList<Motion>? motions = null,
        MeshRenderer.Basis? standing = null,
        IReadOnlyList<(UnityMesh Mesh, Skeleton Skeleton, bool Frames)>? with = null)
    {
        Dress(skeleton, motions, with);
        _subject = subject;
        var seen = subject is not null && _views.TryGetValue(subject, out var before) ? before : null;

        _picture = null;
        Sound = null;
        _automatic = textures;
        Image?.Dispose();
        Image = null;
        // Shown put together the way its bones put it together, and kept as it was written for the
        // posing: a clip carries the bones' own transforms already. See Skeleton.Assembled.
        _authored = mesh;

        // With something to be held in, the whole of it moves into the prefab's own space, that
        // being the only space the two share. Framed by the model alone either way.
        Framing = skeleton?.Assembled(mesh) ?? mesh;
        if (Built(null, 0f) is { } together && skeleton is not null)
            (Rest, Framing) = (together,
                Built(null, 0f, framing: true) ?? skeleton.Placed(mesh, null, 0f));
        else
            Rest = Framing;
        Mesh = Rest;
        Standing = standing;
        MeshTextures = textures;
        ChosenTexture = seen?.Texture;
        Camera = seen?.Camera ?? new Camera();
        Caption = $"{caption}   {mesh.VertexCount:N0} vertices, {mesh.Indices.Length / 3:N0} triangles"
            + (mesh.IsSkinned ? $", {mesh.BindPoses.Count} bones" : "")
            + (textures?.Any(t => t is not null) == true ? "" : ", no texture found");
        Nothing = null;
        Changed();
    }

    /// The angles worth having a name for, so a model can be put back to one of them in a click.
    ///
    /// Turning by hand is how a model is looked at, and it is a poor way to arrive anywhere exact —
    /// comparing a change against the original, or taking the same picture of two weapons, wants
    /// the same angle twice rather than nearly. Distance and pivot are left as they are: those are
    /// where somebody has framed the thing, and a named angle is about which way it faces.
    /// These matter more than they did. A trackball has no level horizon holding a model upright,
    /// so square-on is reached by asking for it rather than by dragging carefully back to it.
    public IReadOnlyList<ViewPreset> Views { get; } =
    [
        // The opening view is the only one that carries a tilt, because it is the only one copying
        // the game: the others are the six square-on directions and a tilt would be in the way.
        new("Default", new Camera().Angles.Yaw, new Camera().Angles.Pitch, new Camera().Angles.Roll),
        new("Front", 0f, 0f),
        new("Back", MathF.PI, 0f),
        new("Left", -MathF.PI / 2, 0f),
        new("Right", MathF.PI / 2, 0f),
        new("Top", 0f, MathF.PI / 2),
        new("Bottom", 0f, -MathF.PI / 2),
    ];

    [RelayCommand]
    private void Look(ViewPreset? preset)
    {
        if (preset is null) return;
        Camera = Camera.Looking(preset.Yaw, preset.Pitch, preset.Roll);
    }

    /// Going back to the automatic answer is immediate; anything else waits for its picture, which
    /// the shell reads and hands back through Wear.
    partial void OnChosenTextureChanged(TextureChoice? value)
    {
        if (value is null || value.PathId == 0) MeshTextures = _automatic;
        Remember();
    }

    /// Covers the whole model with one texture, which is the point: the question it answers is what
    /// this mesh looks like wearing a different skin, and a skin is not per-submesh.
    public void Wear(PreviewImage picture)
        => MeshTextures = Enumerable
            .Repeat<PreviewImage?>(picture, Math.Max(Mesh?.SubMeshes.Count ?? 1, 1))
            .ToList();

    private IReadOnlyList<PreviewImage?>? _automatic;

    private void Redraw()
    {
        if (_picture is null) return;

        Image?.Dispose();
        Image = ToBitmap(ShowAlpha ? _picture : _picture.Opaque());
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(HasMesh));
        OnPropertyChanged(nameof(HasSound));
        OnPropertyChanged(nameof(CanToggleAlpha));
    }

    private static Bitmap ToBitmap(PreviewImage picture)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(picture.Width, picture.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);

        using (var locked = bitmap.Lock())
            System.Runtime.InteropServices.Marshal.Copy(picture.Bgra, 0, locked.Address, picture.Bgra.Length);

        return bitmap;
    }
}

/// A texture offered for a mesh preview: what it is called and where to find it, and nothing else.
///
/// Deliberately without the decoded picture. It used to carry one, filled in once the texture had
/// been read — but a record is compared by its contents, so the filled-in copy was a different value
/// from the one in the list the combo box was showing. The combo box, asked to select something it
/// did not have, selected nothing instead, and the model went straight back to the texture it
/// started with. Picking a skin appeared to do nothing at all.
/// A named way of facing a model. Which way it faces and how it is tilted: the rest of the camera
/// is the framing, which belongs to whoever set it.
public sealed record ViewPreset(string Name, float Yaw, float Pitch, float Roll = 0f)
{
    public override string ToString() => Name;
}

/// One of a model's animations, as a row in the picker.
public sealed record MotionChoice(Motion Motion)
{
    public string Name => Motion.Name;
    public string Detail => $"{Motion.Length:0.00}s";
    public override string ToString() => Motion.Name;
}

public sealed record TextureChoice(string Name, string Bundle, long PathId)
{
    /// True for a texture the mesh in front of you is actually drawn with, which the list marks so
    /// the two or three that answer the obvious question are findable without reading the order.
    ///
    /// Outside equality on purpose, which is why this record writes its own. The same texture is a
    /// different answer to this question for each mesh, so the list is rebuilt with the flag turned
    /// over as somebody moves between them — and if that made it a different value, the selection
    /// carried across the rebuild would stop matching and the model would undress itself. What
    /// identifies a texture is where it is, not how it is being shown.
    public bool Worn { get; init; }

    /// True for one of this weapon's skins — what a skin material puts in its main slot, rather
    /// than the gloss or mask bound beside it. Lit fainter than what is worn, and never both at
    /// once: the skin the mesh already has on is worn, and saying it twice says nothing.
    public bool Skin { get; init; }

    public bool Equals(TextureChoice? other)
        => other is not null && Name == other.Name && Bundle == other.Bundle && PathId == other.PathId;

    public override int GetHashCode() => HashCode.Combine(Name, Bundle, PathId);

    public override string ToString() => Name;
}
