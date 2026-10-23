using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Core.Tests;

public class PackIconTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pgassettool-icon").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static UnityMesh Quad() => new()
    {
        Name = "quad",
        VertexCount = 4,
        Attributes = new Dictionary<VertexAttribute, float[]>
        {
            [VertexAttribute.Position] = [-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0],
        },
        Dimensions = new Dictionary<VertexAttribute, int> { [VertexAttribute.Position] = 3 },
        Indices = [0, 1, 2, 0, 2, 3],
        SubMeshes = [new SubMesh(0, 6, 0, 0)],
        BindPoses = [],
        BoneNameHashes = [],
    };

    [Fact]
    public void TheDrawingIsSquareAndHasTheModelInIt()
    {
        var picture = PackIcon.Render(Quad(), null, size: 64);

        Assert.Equal(64, picture.Width);
        Assert.Equal(64, picture.Height);
        Assert.False(PackIcon.IsBlank(picture));
    }

    [Fact]
    public void AViewPannedRightOutOfTheFrameIsBroughtBackRatherThanDrawnEmpty()
    {
        // The icon keeps the angle a view was turned to and finds its own distance and centre, so
        // a snapshot taken while zoomed into one end of a weapon still shows the weapon. Measured
        // off the drawing instead, the framing says nothing at all when there is no drawing.
        var picture = PackIcon.Render(Quad(), null, (new Camera { Distance = 0.4f }).Panned(40, 0), size: 64);

        Assert.False(PackIcon.IsBlank(picture));
    }

    [Fact]
    public void AModelWithNothingInItIsRecognisedAsNoPictureAtAll()
    {
        // A file that is transparent from corner to corner looks exactly like a missing icon, so
        // writing one is worse than not writing anything.
        var nothing = new UnityMesh
        {
            Name = "empty",
            VertexCount = 0,
            Attributes = new Dictionary<VertexAttribute, float[]>(),
            Dimensions = new Dictionary<VertexAttribute, int>(),
            Indices = [],
            SubMeshes = [],
            BindPoses = [],
            BoneNameHashes = [],
        };

        Assert.True(PackIcon.IsBlank(PackIcon.Render(nothing, null, size: 64)));
    }

    [Fact]
    public void AWideModelZoomedIntoIsNotCutOffAtTheSides()
    {
        // The pane a snapshot is copied from is far wider than it is tall, so a square picture of
        // the same view lost both ends of anything long. What the icon keeps is the angle.
        var bar = new UnityMesh
        {
            Name = "bar",
            VertexCount = 4,
            Attributes = new Dictionary<VertexAttribute, float[]>
            {
                [VertexAttribute.Position] = [-4, -0.2f, 0, 4, -0.2f, 0, 4, 0.2f, 0, -4, 0.2f, 0],
            },
            Dimensions = new Dictionary<VertexAttribute, int> { [VertexAttribute.Position] = 3 },
            Indices = [0, 1, 2, 0, 2, 3],
            SubMeshes = [new SubMesh(0, 6, 0, 0)],
            BindPoses = [],
            BoneNameHashes = [],
        };

        var picture = PackIcon.Render(bar, null, (Camera.Facing(0, 0) with { Distance = 0.4f }), size: 64);

        // Nothing may touch the left or right edge: what does is a model running off the side.
        for (var y = 0; y < 64; y++)
        {
            Assert.Equal(0, picture.Bgra[(y * 64) * 4 + 3]);
            Assert.Equal(0, picture.Bgra[(y * 64 + 63) * 4 + 3]);
        }

        Assert.False(PackIcon.IsBlank(picture));
    }

    [Fact]
    public void TheAngleItIsDrawnFromIsTheOneItWasAskedFor()
    {
        var straight = PackIcon.Render(Quad(), null, Camera.Facing(0, 0), size: 64);
        var edgeOn = PackIcon.Render(Quad(), null, Camera.Facing(1.4f, 0), size: 64);

        Assert.NotEqual(Drawn(straight), Drawn(edgeOn));
    }

    [Fact]
    public void WhatIsWrittenReadsBackAsThePictureThatWasDrawn()
    {
        var path = Path.Combine(_directory, PackIcon.FileName);
        PackIcon.Write(PackIcon.Render(Quad(), null, size: 64), path);

        using var file = File.OpenRead(path);
        var read = StbImageSharp.ImageResult.FromStream(file, StbImageSharp.ColorComponents.RedGreenBlueAlpha);

        Assert.Equal(64, read.Width);
        Assert.Equal(64, read.Height);

        // Transparent where nothing was drawn: an icon sits on whatever is behind it, and a black
        // square would be a worse picture than the model on its own.
        Assert.Contains(read.Data.Where((_, i) => i % 4 == 3), a => a == 0);
        Assert.Contains(read.Data.Where((_, i) => i % 4 == 3), a => a == 255);
    }

    private static int Drawn(PreviewImage picture)
    {
        var count = 0;
        for (var i = 3; i < picture.Bgra.Length; i += 4) if (picture.Bgra[i] != 0) count++;
        return count;
    }

    /// A long thin bar, which is what most weapons are shaped like.
    private static UnityMesh Bar() => new()
    {
        Name = "bar",
        VertexCount = 4,
        Attributes = new Dictionary<VertexAttribute, float[]>
        {
            [VertexAttribute.Position] = [-1, -0.08f, 0, 1, -0.08f, 0, 1, 0.08f, 0, -1, 0.08f, 0],
        },
        Dimensions = new Dictionary<VertexAttribute, int> { [VertexAttribute.Position] = 3 },
        Indices = [0, 1, 2, 0, 2, 3],
        SubMeshes = [new SubMesh(0, 6, 0, 0)],
        BindPoses = [],
        BoneNameHashes = [],
    };

    [Fact]
    public void TheModelIsDrawnAcrossTheWholeFrame()
    {
        // A fixed distance is a multiple of the bounding sphere, which for anything long is mostly
        // empty air — a small object in a large empty square.
        var picture = PackIcon.Render(Quad(), null, Camera.Facing(0, 0), size: 128);

        var (left, top, right, bottom) = Extent(picture);
        Assert.True(right - left >= 112, $"only {right - left + 1} of 128 columns were used");
        Assert.True(bottom - top >= 112, $"only {bottom - top + 1} of 128 rows were used");
    }

    [Fact]
    public void ALongModelFillsTheFrameToo()
    {
        // Scaled by whichever side is wider, or a bar would be fitted to its height and hang off
        // both edges.
        var picture = PackIcon.Render(Bar(), null, Camera.Facing(0, 0), size: 128);

        var (left, top, right, bottom) = Extent(picture);
        Assert.True(right - left >= 112, $"only {right - left + 1} of 128 columns were used");
        Assert.True(left >= 0 && right < 128 && top >= 0 && bottom < 128, "it spilled out of the frame");
    }

    [Fact]
    public void ItIsCentredWhereverItStarted()
    {
        // Framing has to undo a pan as well as a zoom: an icon of a model shoved into one corner is
        // not an icon of the model.
        var pushed = Camera.Facing(0, 0).Panned(0.6f, -0.4f);
        var picture = PackIcon.Render(Quad(), null, pushed, size: 128);

        var (left, top, right, bottom) = Extent(picture);
        Assert.InRange((left + right) / 2, 60, 68);
        Assert.InRange((top + bottom) / 2, 60, 68);
    }

    private static (int Left, int Top, int Right, int Bottom) Extent(PreviewImage picture)
    {
        int left = picture.Width, top = picture.Height, right = -1, bottom = -1;
        for (var y = 0; y < picture.Height; y++)
            for (var x = 0; x < picture.Width; x++)
            {
                if (picture.Bgra[(y * picture.Width + x) * 4 + 3] == 0) continue;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
        return (left, top, right, bottom);
    }
}
