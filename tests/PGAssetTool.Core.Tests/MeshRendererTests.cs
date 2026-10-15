using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Core.Tests;

public class MeshRendererTests
{
    private const int Size = 64;

    private static UnityMesh Quad(float z = 0, float scale = 1) => Mesh(
        [-scale, -scale, z, scale, -scale, z, scale, scale, z, -scale, scale, z],
        [0, 1, 2, 0, 2, 3]);

    private static UnityMesh Mesh(float[] positions, int[] indices) => new()
    {
        Name = "preview",
        VertexCount = positions.Length / 3,
        Attributes = new Dictionary<VertexAttribute, float[]> { [VertexAttribute.Position] = positions },
        Dimensions = new Dictionary<VertexAttribute, int> { [VertexAttribute.Position] = 3 },
        Indices = indices,
        SubMeshes = [new SubMesh(0, indices.Length, 0, 0)],
        BindPoses = [],
        BoneNameHashes = [],
    };

    /// A box. Its normals point straight out from the middle, and its triangles are wound so that
    /// the two agree — which is what a solid looks like.
    ///
    /// <param name="inside">
    /// Turn it inside out: the normals point in and the triangles are wound the other way, so the
    /// two still agree with each other and disagree with the world. That is a shell.
    /// </param>
    private static (float[] Positions, float[] Normals, int[] Indices) Box(float size, bool inside = false)
    {
        float[] corners =
        [
            -size, -size, -size,  size, -size, -size,  size, size, -size,  -size, size, -size,
            -size, -size,  size,  size, -size,  size,  size, size,  size,  -size, size,  size,
        ];

        var normals = new float[corners.Length];
        for (var i = 0; i < corners.Length; i++)
            normals[i] = (inside ? -corners[i] : corners[i]) / (size * MathF.Sqrt(3));

        // Each face wound counter-clockwise seen from outside, so (b-a) x (c-a) points outwards.
        int[] faces =
        [
            4, 5, 6, 4, 6, 7,   0, 3, 2, 0, 2, 1,   1, 2, 6, 1, 6, 5,
            0, 4, 7, 0, 7, 3,   3, 7, 6, 3, 6, 2,   0, 1, 5, 0, 5, 4,
        ];

        if (inside)
            for (var i = 0; i + 2 < faces.Length; i += 3)
                (faces[i + 1], faces[i + 2]) = (faces[i + 2], faces[i + 1]);

        return (corners, normals, faces);
    }

    /// A solid whose normals were turned round and whose triangles were not, so the two disagree
    /// and neither can be trusted. #14 Battle Shovel's head is built this way.
    private static (float[] Positions, float[] Normals, int[] Indices) Muddled(float size)
    {
        var box = Box(size);
        return (box.Positions, [.. box.Normals.Select(n => -n)], box.Indices);
    }

    private static UnityMesh Solid((float[] Positions, float[] Normals, int[] Indices) box)
        => Solid(box.Positions, box.Normals, box.Indices);

    private static UnityMesh Solid(float[] positions, float[] normals, int[] indices) => new()
    {
        Name = "preview",
        VertexCount = positions.Length / 3,
        Attributes = new Dictionary<VertexAttribute, float[]>
        {
            [VertexAttribute.Position] = positions,
            [VertexAttribute.Normal] = normals,
        },
        Dimensions = new Dictionary<VertexAttribute, int>
        {
            [VertexAttribute.Position] = 3,
            [VertexAttribute.Normal] = 3,
        },
        Indices = indices,
        SubMeshes = [new SubMesh(0, indices.Length, 0, 0)],
        BindPoses = [],
        BoneNameHashes = [],
    };

    /// Two boxes in one mesh: the second is the first, larger and turned inside out.
    private static UnityMesh WithAnOutline()
    {
        var body = Box(1f);
        var shell = Box(1.15f, inside: true);

        var offset = body.Positions.Length / 3;
        return Solid(
            [.. body.Positions, .. shell.Positions],
            [.. body.Normals, .. shell.Normals],
            [.. body.Indices, .. shell.Indices.Select(i => i + offset)]);
    }

    [Fact]
    public void AnOutlineShellIsRecognisedAndAPlainSolidIsNot()
    {
        // The game draws a silhouette by wrapping the model in a copy of itself, larger and turned
        // inside out, and showing only the side of it that faces away. Drawn as an ordinary
        // surface it covers the model completely — Punk's Shovel came up a flat magenta blob.
        Assert.True(WithAnOutline().CarriesAnOutline);
        Assert.False(Solid(Box(1f)).CarriesAnOutline);
    }

    [Fact]
    public void AMeshWhoseNormalsFightItsWindingIsLeftAlone()
    {
        // #14 Battle Shovel: its head is wound one way and shaded the other, so the normals say
        // nothing about which side is out. It leans inwards like a shell and is not one, and
        // culling it by facing hollows the head — so the two tests are asked together.
        Assert.False(Solid(Muddled(1f)).CarriesAnOutline);
    }

    [Fact]
    public void AMeshWithNoNormalsCarriesNoOutline()
        => Assert.False(Quad().CarriesAnOutline);

    private static byte[] Draw(UnityMesh mesh, Camera camera)
    {
        var target = new RenderTarget();
        target.Resize(Size, Size);
        MeshRenderer.Render(mesh, camera, target);
        return target.Bgra;
    }

    private static int Covered(byte[] pixels)
    {
        var count = 0;
        for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) count++;
        return count;
    }

    /// The colour written at a pixel, or null where nothing was drawn.
    private static byte? At(byte[] pixels, int x, int y)
    {
        var at = (y * Size + x) * 4;
        return pixels[at + 3] == 0 ? null : pixels[at];
    }

    [Fact]
    public void AQuadFacingTheCameraIsDrawnAroundTheMiddle()
    {
        // Looked at straight on, so the whole thing is in frame and the centre is covered.
        var pixels = Draw(Quad(), Camera.Facing(0, 0));

        Assert.True(Covered(pixels) > 0, "nothing was drawn at all");
        Assert.NotNull(At(pixels, Size / 2, Size / 2));
        Assert.Null(At(pixels, 0, 0));
    }

    [Fact]
    public void TheModelIsCentredWhateverItsCoordinatesAre()
    {
        // A mesh built a long way from the origin still fills the frame: the camera looks at the
        // model's own centre, not at zero.
        var far = Mesh([99, 99, 0, 101, 99, 0, 101, 101, 0, 99, 101, 0], [0, 1, 2, 0, 2, 3]);

        Assert.NotNull(At(Draw(far, Camera.Facing(0, 0)), Size / 2, Size / 2));
    }

    [Fact]
    public void ALargeModelAndASmallOneFillTheFrameTheSame()
    {
        // The distance is a multiple of the model's radius, so a pistol and a rocket launcher both
        // arrive framed rather than one being a speck.
        var straight = Camera.Facing(0, 0);
        var small = Covered(Draw(Quad(scale: 0.01f), straight));
        var large = Covered(Draw(Quad(scale: 100f), straight));

        Assert.Equal(small, large);
    }

    [Fact]
    public void TheNearerSurfaceIsTheOneYouSee()
    {
        // Two quads at the same place on screen, one in front of the other, shaded differently.
        // Both meshes carry all eight vertices so the framing is identical and only the triangle
        // list differs — otherwise the larger bounding box would change the scale and hide the
        // thing being measured.
        float[] positions =
        [
            -1, -1, 1, 1, -1, 1, 1, 1, 1, -1, 1, 1,        // near, facing the camera
            -1, -1, -1, 1, -1, -1, 1, 1, -1, -1, 1, -1,    // far, facing away and down
        ];
        float[] normals =
        [
            0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1,
            0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1, 0,
        ];

        int[] near = [0, 1, 2, 0, 2, 3];
        int[] far = [4, 5, 6, 4, 6, 7];

        var camera = Camera.Facing(0, 0);
        var expected = At(Draw(Lit(positions, normals, near), camera), Size / 2, Size / 2);
        Assert.NotNull(expected);

        // Whichever order the triangles arrive in, the near one is the one that shows.
        Assert.Equal(expected, At(Draw(Lit(positions, normals, [.. near, .. far]), camera), Size / 2, Size / 2));
        Assert.Equal(expected, At(Draw(Lit(positions, normals, [.. far, .. near]), camera), Size / 2, Size / 2));

        // And the far quad really would have looked different, or the check above proves nothing.
        Assert.NotEqual(expected, At(Draw(Lit(positions, normals, far), camera), Size / 2, Size / 2));
    }

    private static UnityMesh Lit(float[] positions, float[] normals, int[] indices) => new()
    {
        Name = "preview",
        VertexCount = positions.Length / 3,
        Attributes = new Dictionary<VertexAttribute, float[]>
        {
            [VertexAttribute.Position] = positions,
            [VertexAttribute.Normal] = normals,
        },
        Dimensions = new Dictionary<VertexAttribute, int>
        {
            [VertexAttribute.Position] = 3,
            [VertexAttribute.Normal] = 3,
        },
        Indices = indices,
        SubMeshes = [new SubMesh(0, indices.Length, 0, 0)],
        BindPoses = [],
        BoneNameHashes = [],
    };

    /// The two views draw the same picture, which is the only sense in which two cameras are equal:
    /// a quaternion and its negative are the same rotation and do not compare equal.
    private static void AssertSameView(Camera expected, Camera got, string what)
    {
        var (a, b) = (MeshRenderer.View(expected), MeshRenderer.View(got));

        Assert.True(
            MathF.Abs(a.Rx - b.Rx) < 1e-4f && MathF.Abs(a.Ry - b.Ry) < 1e-4f && MathF.Abs(a.Rz - b.Rz) < 1e-4f
            && MathF.Abs(a.Ux - b.Ux) < 1e-4f && MathF.Abs(a.Uy - b.Uy) < 1e-4f && MathF.Abs(a.Uz - b.Uz) < 1e-4f
            && MathF.Abs(a.Fx - b.Fx) < 1e-4f && MathF.Abs(a.Fy - b.Fy) < 1e-4f && MathF.Abs(a.Fz - b.Fz) < 1e-4f,
            what);
    }

    [Fact]
    public void DraggingAllTheWayRoundComesBackToWhereItStarted()
    {
        var start = Camera.Facing(0.4f, 0.2f);
        AssertSameView(start, start.Dragged(MathF.Tau, 0), "a whole turn sideways did not come back");
        AssertSameView(start, start.Dragged(0, MathF.Tau), "a whole turn downwards did not come back");
    }

    [Fact]
    public void ThereIsNoPoleToCollapseAt()
    {
        // What the turntable this replaces had to stop at. Its pitch was measured about one axis of
        // the model, so at the pole the frame turned over and a drag to the right walked the viewer
        // left; the stop was there to keep anything from reaching that. A trackball turns about the
        // axis lying across the drag, which is a direction on the screen and not on the model, so
        // there is no axis to line up with and nothing to collapse.
        //
        // A solid rather than a flat one: a plane seen exactly edge-on draws nothing however good
        // the frame is, which would say nothing about the frame.
        var solid = Mesh(
            [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1],
            [0, 1, 2, 0, 1, 3, 0, 2, 3, 1, 2, 3]);

        var at = Camera.Facing(0.3f, 0);

        // Twice round, the long way over the top, stopping at every step of a tenth of a radian.
        for (var step = 0; step < 130; step++)
        {
            at = at.Dragged(0, 0.1f);

            Assert.True(Covered(Draw(solid, at)) > 0, $"nothing was drawn after {step} steps over the top");
            AssertStillARotation(at, $"the frame came apart after {step} steps over the top");
        }
    }

    [Fact]
    public void DraggingRightMovesTheNearFaceRightAtEveryAngle()
    {
        // The property a pole breaks. Whatever the model has been turned to, a drag to the right
        // takes it with the cursor — the face that was towards the viewer moves towards the right
        // of the screen. Asked of the frame rather than of an angle, there being no angle to ask.
        var at = new Camera();

        for (var step = 0; step < 60; step++)
        {
            at = at.Dragged(0.21f, 0.13f);

            var before = MeshRenderer.View(at);
            var after = MeshRenderer.View(at.Dragged(0.2f, 0));

            // Where the face that was towards the viewer has gone, in the new frame.
            var (x, _, _) = after.Apply(before.Fx, before.Fy, before.Fz);

            Assert.True(x > 0, $"a drag to the right moved the near face to {x} after {step} steps");
        }
    }

    [Fact]
    public void DraggingDownTipsTheTopTowardsTheViewer()
    {
        // The other half of the drag, and the easy one to get backwards: pulling the cursor down
        // rolls the model towards you, so the face that was towards the viewer goes down the screen
        // and the top of it comes round to face you.
        var at = new Camera();

        var before = MeshRenderer.View(at);
        var after = MeshRenderer.View(at.Dragged(0, 0.3f));

        var (_, y, _) = after.Apply(before.Fx, before.Fy, before.Fz);
        Assert.True(y < 0, $"a drag downwards moved the near face to {y}");

        var (_, _, z) = after.Apply(before.Ux, before.Uy, before.Uz);
        Assert.True(z > 0, $"a drag downwards left the top at {z} rather than facing the viewer");
    }

    private static void AssertStillARotation(Camera camera, string what)
    {
        var b = MeshRenderer.View(camera);

        Span<float> rows = [b.Rx, b.Ry, b.Rz, b.Ux, b.Uy, b.Uz, b.Fx, b.Fy, b.Fz];

        for (var i = 0; i < 3; i++)
        {
            var length = MathF.Sqrt(
                rows[i * 3] * rows[i * 3] + rows[i * 3 + 1] * rows[i * 3 + 1] + rows[i * 3 + 2] * rows[i * 3 + 2]);
            Assert.True(MathF.Abs(length - 1) < 1e-3f, $"{what}: row {i} is {length} long");

            for (var j = i + 1; j < 3; j++)
            {
                var dot = rows[i * 3] * rows[j * 3]
                    + rows[i * 3 + 1] * rows[j * 3 + 1]
                    + rows[i * 3 + 2] * rows[j * 3 + 2];
                Assert.True(MathF.Abs(dot) < 1e-3f, $"{what}: rows {i} and {j} are not square");
            }
        }
    }

    [Fact]
    public void AnAngleAskedForIsTheAngleThatComesBack()
    {
        // Named views are written as a yaw, a pitch and a roll, and every angle recorded anywhere
        // in this repository is in those terms, so Facing has to answer in them exactly.
        foreach (var (yaw, pitch, roll) in new[]
                 {
                     (0f, 0f, 0f), (0.4f, 0.2f, 0f), (2.1f, -0.4f, 1.1f),
                     (-1.3f, 1.2f, -2.5f), (3f, -1.5f, 0.2f),
                 })
        {
            var (gotYaw, gotPitch, gotRoll) = Camera.Facing(yaw, pitch, roll).Angles;
            AssertSameView(
                Camera.Facing(yaw, pitch, roll), Camera.Facing(gotYaw, gotPitch, gotRoll),
                $"reading back ({yaw}, {pitch}, {roll}) gave ({gotYaw}, {gotPitch}, {gotRoll})");
        }
    }

    [Fact]
    public void ZoomingIsBoundedAtBothEnds()
    {
        Assert.Equal(20f, new Camera().Zoomed(1000f).Distance);
        Assert.Equal(0.4f, new Camera().Zoomed(0.0001f).Distance);
    }

    [Fact]
    public void AMeshWithNothingInItDrawsNothingRatherThanThrowing()
    {
        var empty = Mesh([], []);
        Assert.Equal(0, Covered(Draw(empty, new Camera())));
    }

    [Fact]
    public void IndicesPastTheEndOfTheVertexListAreIgnored()
    {
        // A mesh read from a bundle is not guaranteed well formed, and a preview must not be the
        // thing that crashes on one.
        var broken = Mesh([0, 0, 0, 1, 0, 0, 1, 1, 0], [0, 1, 99]);
        Assert.Equal(0, Covered(Draw(broken, new Camera())));
    }

    /// A long thin bar along one axis, as a stand-in for a barrel.
    private static UnityMesh Bar(int axis, float length = 2f, float thickness = 0.2f)
    {
        float[] size = [thickness, thickness, thickness];
        size[axis] = length;

        var positions = new List<float>();
        foreach (var corner in new[] { -1f, 1f })
        foreach (var side in new[] { -1f, 1f })
        {
            // Two triangles per end, enough to give the bar an extent on every axis.
            var other = (axis + 1) % 3;
            var third = (axis + 2) % 3;
            float[] v = new float[3];
            v[axis] = corner * size[axis] / 2;
            v[other] = side * size[other] / 2;
            v[third] = 0;
            positions.AddRange(v);
        }
        return Mesh([.. positions], [0, 1, 2, 1, 2, 3]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ALongModelLiesAcrossTheScreenWhicheverAxisItWasBuiltOn(int axis)
    {
        // A Mesh carries no orientation of its own: in the game the renderer's transform places it,
        // and a preview has nothing to place it with. Weapons are authored with the barrel along Y,
        // so without this they hang straight down.
        var pixels = Draw(Bar(axis), Camera.Facing(0, 0));

        var (minX, maxX, minY, maxY) = Extent(pixels);
        Assert.True(maxX - minX > maxY - minY,
            $"a bar along axis {axis} drew {maxX - minX} wide by {maxY - minY} tall");
    }

    [Fact]
    public void TheModelIsDrawnTheWayTheGameSeesItRatherThanMirrored()
    {
        // The ground truth is Unity's own camera: it looks along +Z and has +X on its right. So a
        // model with its mass at +X, seen from where that camera stands, draws heavy on the right,
        // and seen from the other side draws heavy on the left. A mirrored projection satisfies
        // one of those and not the other, which is the whole difficulty of noticing it: a weapon's
        // silhouette looks equally plausible either way round, and only a texture with writing on
        // it says which way the picture is facing.
        float[] positions = [0, 0, 0, 2, 0, 0, 2, 0.4f, 0, 0, 0.1f, 0];
        var lopsided = Mesh(positions, [0, 1, 2, 0, 2, 3]);

        var (left, right) = Halves(Draw(lopsided, Camera.Facing(MathF.PI, 0)));
        Assert.True(right > left, $"from the game's own side the heavy end drew {right} right, {left} left");

        var (fromBehindLeft, fromBehindRight) = Halves(Draw(lopsided, Camera.Facing(0, 0)));
        Assert.True(fromBehindLeft > fromBehindRight,
            $"from behind the heavy end drew {fromBehindRight} right, {fromBehindLeft} left");
    }

    /// How much was drawn either side of the middle.
    private static (int Left, int Right) Halves(byte[] pixels)
    {
        var (left, right) = (0, 0);
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
            if (At(pixels, x, y) is not null)
            {
                if (x < Size / 2) left++; else right++;
            }

        return (left, right);
    }

    private static (int MinX, int MaxX, int MinY, int MaxY) Extent(byte[] pixels)
    {
        int minX = Size, maxX = -1, minY = Size, maxY = -1;
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
            if (At(pixels, x, y) is not null)
            {
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        return (minX, maxX, minY, maxY);
    }

    [Fact]
    public void RedrawingTheSameSizeAllocatesNoFrameBuffers()
    {
        // A depth buffer allocated per frame is what makes a large preview pane expensive — eleven
        // megabytes a frame at 2000x1500, all of it immediately garbage.
        //
        // Not nothing at all, though: a frame is filled on every core, and handing the bands to the
        // thread pool costs a closure and some bookkeeping — about two kilobytes a frame, where a
        // frame's own buffers come to fifteen megabytes at this size. So the bound is that twenty
        // frames together allocate less than one depth buffer, which is the thing this is here for.
        const int width = 2000, height = 1500;
        var mesh = Quad();
        var target = new RenderTarget();
        target.Resize(width, height);
        var angles = Enumerable.Range(0, 20).Select(i => Camera.Facing(i * 0.1f, 0)).ToArray();
        MeshRenderer.Render(mesh, angles[0], target);   // warm anything lazy

        // Per-thread, not process-wide: xUnit runs test classes in parallel, so the process-wide
        // counter would pick up whatever another test happened to be doing. The buffers are only
        // ever made on the thread that asks for the frame, so this is the thread that would see one.
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 20; frame++)
            MeshRenderer.Render(mesh, angles[frame], target);

        // A Camera is a record, so allocating one per frame would be the test's own doing, not the
        // renderer's; the angles above are made in advance so only drawing is measured.
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < width * height * sizeof(float),
            $"twenty frames allocated {allocated:N0} bytes, as much as a depth buffer");
    }

    [Fact]
    public void ResizingKeepsTheBuffersInStepWithEachOther()
    {
        var target = new RenderTarget();
        target.Resize(320, 200);
        Assert.Equal(320 * 200 * 4, target.Bgra.Length);

        target.Resize(64, 64);
        Assert.Equal(64 * 64 * 4, target.Bgra.Length);

        // A stale depth buffer from the larger size would leave the new frame testing against
        // whatever the old one held.
        MeshRenderer.Render(Quad(), Camera.Facing(0, 0), target);
        Assert.True(Covered(target.Bgra) > 0);
    }

    [Fact]
    public void ATargetWithNoSizeYetDrawsNothingRatherThanThrowing()
        => MeshRenderer.Render(Quad(), new Camera(), new RenderTarget());

    /// A solid image of one colour, to see where it lands.
    private static PreviewImage Swatch(byte blue, byte green, byte red, int size = 4)
    {
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (blue, green, red, (byte)255);
        return new PreviewImage(size, size, pixels);
    }

    private static UnityMesh Textured(float[] positions, float[] uvs, int[] indices, params SubMesh[] parts) => new()
    {
        Name = "textured",
        VertexCount = positions.Length / 3,
        Attributes = new Dictionary<VertexAttribute, float[]>
        {
            [VertexAttribute.Position] = positions,
            [VertexAttribute.TexCoord0] = uvs,
        },
        Dimensions = new Dictionary<VertexAttribute, int>
        {
            [VertexAttribute.Position] = 3,
            [VertexAttribute.TexCoord0] = 2,
        },
        Indices = indices,
        SubMeshes = parts.Length > 0 ? parts : [new SubMesh(0, indices.Length, 0, 0)],
        BindPoses = [],
        BoneNameHashes = [],
    };

    private static UnityMesh TexturedQuad(params SubMesh[] parts) => Textured(
        [-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0],
        [0, 0, 1, 0, 1, 1, 0, 1],
        [0, 1, 2, 0, 2, 3],
        parts);

    [Fact]
    public void AMeshWithNoTextureIsStillDrawn()
    {
        // Nothing resolves a texture for a great many meshes, and those must not come out blank.
        Assert.True(Covered(Draw(TexturedQuad(), Camera.Facing(0, 0))) > 0);
    }

    [Fact]
    public void TheTextureIsWhatShowsRatherThanTheFlatShade()
    {
        var target = new RenderTarget();
        target.Resize(Size, Size);
        MeshRenderer.Render(TexturedQuad(), Camera.Facing(0, 0), target, [Swatch(200, 0, 0)]);

        var at = ((Size / 2) * Size + Size / 2) * 4;
        Assert.True(target.Bgra[at] > target.Bgra[at + 2],
            $"expected the blue swatch, got B={target.Bgra[at]} G={target.Bgra[at + 1]} R={target.Bgra[at + 2]}");
    }

    [Fact]
    public void EachSubmeshTakesTheTextureOfItsOwnMaterial()
    {
        // Unity pairs submesh i with material i. Getting this wrong shows part of a weapon in
        // another part's colours, which is hard to trace back to the preview.
        var twoHalves = Textured(
            [-1, -1, 0, 0, -1, 0, 0, 1, 0, -1, 1, 0,      // left half
             0, -1, 0, 1, -1, 0, 1, 1, 0, 0, 1, 0],       // right half
            [0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1],
            [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7],
            new SubMesh(0, 6, 0, 0), new SubMesh(6, 6, 0, 0));

        var target = new RenderTarget();
        target.Resize(Size, Size);

        // From the side the game's own camera looks from, where the model's -X is the screen's
        // left, so the halves are where the lines above say they are.
        MeshRenderer.Render(twoHalves, Camera.Facing(MathF.PI, 0), target,
            [Swatch(200, 0, 0), Swatch(0, 0, 200)]);

        // Well inside the drawing: the model is framed with a margin, so a quarter of the way in
        // from the edge falls outside it.
        var left = (Size / 2 * Size + Size / 2 - 8) * 4;
        var right = (Size / 2 * Size + Size / 2 + 8) * 4;

        Assert.True(target.Bgra[left] > target.Bgra[left + 2], "the left half did not take the first texture");
        Assert.True(target.Bgra[right + 2] > target.Bgra[right], "the right half did not take the second texture");
    }

    [Fact]
    public void FewerTexturesThanSubmeshesLeavesTheRestPlain()
    {
        // A material that resolves to nothing must not silently borrow the previous one.
        var twoParts = TexturedQuad(new SubMesh(0, 3, 0, 0), new SubMesh(3, 3, 0, 0));

        var target = new RenderTarget();
        target.Resize(Size, Size);
        MeshRenderer.Render(twoParts, Camera.Facing(0, 0), target, [Swatch(200, 0, 0), null]);

        Assert.True(Covered(target.Bgra) > 0);
    }

    [Fact]
    public void CoordinatesOutsideTheUnitSquareWrapRatherThanClamp()
    {
        // A mesh is free to tile its texture, and clamping would smear the edge texel across it.
        var tiled = Textured(
            [-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0],
            [0, 0, 3, 0, 3, 3, 0, 3],
            [0, 1, 2, 0, 2, 3]);

        var target = new RenderTarget();
        target.Resize(Size, Size);
        MeshRenderer.Render(tiled, Camera.Facing(0, 0), target, [Swatch(200, 0, 0)]);

        Assert.True(Covered(target.Bgra) > 0);
    }

    [Fact]
    public void RollTiltsTheModelInThePlaneOfTheScreen()
    {
        // A bar lying across the screen, rolled a quarter turn, has to end up standing up it —
        // and nothing about which side faces the viewer may change, which is what separates this
        // from turning the camera.
        var bar = Mesh([-1, -0.1f, 0, 1, -0.1f, 0, 1, 0.1f, 0, -1, 0.1f, 0], [0, 1, 2, 0, 2, 3]);

        var flat = Draw(bar, Camera.Facing(0, 0));
        var tilted = Draw(bar, Camera.Facing(0, 0).Rolled(MathF.PI / 2));

        Assert.NotNull(At(flat, Size / 2, Size / 2));
        Assert.NotNull(At(tilted, Size / 2, Size / 2));

        // Wide before, tall after: measured rather than assumed, because a roll applied to the
        // wrong pair of axes still draws something and still looks plausible in one frame.
        Assert.True(Width(flat) > Height(flat), "the bar was not drawn lying down to begin with");
        Assert.True(Height(tilted) > Width(tilted), "rolling it a quarter turn did not stand it up");
    }

    [Fact]
    public void AFullTurnOfRollComesBackToWhereItStarted()
    {
        var bar = Mesh([-1, -0.1f, 0, 1, -0.1f, 0, 1, 0.1f, 0, -1, 0.1f, 0], [0, 1, 2, 0, 2, 3]);

        Assert.Equal(
            Covered(Draw(bar, Camera.Facing(0.4f, 0.2f))),
            Covered(Draw(bar, Camera.Facing(0.4f, 0.2f).Rolled(MathF.Tau))),
            tolerance: 4);
    }

    private static int Width(byte[] pixels) => Extent(pixels, horizontal: true);
    private static int Height(byte[] pixels) => Extent(pixels, horizontal: false);

    private static int Extent(byte[] pixels, bool horizontal)
    {
        int low = Size, high = -1;
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                if (At(pixels, x, y) is null) continue;
                var along = horizontal ? x : y;
                low = Math.Min(low, along);
                high = Math.Max(high, along);
            }
        return high < low ? 0 : high - low + 1;
    }

    [Fact]
    public void PanningMovesWhereTheModelSitsWithoutTurningIt()
    {
        // Half a frame to the right means half a frame to the right, and the model itself is
        // unchanged — which is what separates a pan from an orbit that happens to look similar.
        // Far enough back that half a frame of pan still leaves the whole quad on screen: what is
        // being measured is where it lands, and a clipped model lands wherever the frame ends.
        var quad = Quad(scale: 0.3f);
        var back = (Camera.Facing(0, 0) with { Distance = 2f });

        var centred = Draw(quad, back);
        var moved = Draw(quad, back.Panned(0.5f, 0));

        Assert.Equal(Covered(centred), Covered(moved), tolerance: 4);
        Assert.Equal(Middle(centred) + Size / 4, Middle(moved), tolerance: 2);
    }

    [Fact]
    public void PanningUpMovesItUpTheScreenRatherThanDownIt()
    {
        var quad = Quad(scale: 0.3f);
        var back = (Camera.Facing(0, 0) with { Distance = 2f });

        var centred = Rows(Draw(quad, back));
        var raised = Rows(Draw(quad, back.Panned(0, 0.5f)));

        Assert.True(raised < centred, $"panning up put it at row {raised}, below row {centred}");
    }

    /// The middle column of whatever was drawn.
    private static int Middle(byte[] pixels)
    {
        int low = Size, high = -1;
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                if (At(pixels, x, y) is null) continue;
                low = Math.Min(low, x);
                high = Math.Max(high, x);
            }
        return (low + high) / 2;
    }

    /// The middle row of whatever was drawn.
    private static int Rows(byte[] pixels)
    {
        int low = Size, high = -1;
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                if (At(pixels, x, y) is null) continue;
                low = Math.Min(low, y);
                high = Math.Max(high, y);
            }
        return (low + high) / 2;
    }

    [Fact]
    public void WhatWasPannedToTheMiddleStaysThereWhenTheViewTurns()
    {
        // The whole point of the change: turning happens about whatever is in the middle of the
        // frame, not about the model's own centre. Pan the right-hand end of a bar into the middle
        // and turn; it has to still be there. Turning about the model centre instead would pull it
        // away, and the middle of the frame would be empty.
        // Level and untilted, and stated rather than taken from the defaults: what is being
        // measured is where the pivot ends up, and the opening angle is free to move.
        var bar = Mesh([-1, -0.2f, 0, 1, -0.2f, 0, 1, 0.2f, 0, -1, 0.2f, 0], [0, 1, 2, 0, 2, 3]);
        var straight = (Camera.Facing(0, 0, 0) with { Distance = 1.5f });

        // A half-frame is Distance radii across and the bar is about a radius long each way, so
        // this puts the middle of the frame just inside its right-hand end.
        var panned = straight.Panned(-0.6f, 0);
        Assert.NotNull(At(Draw(bar, panned), Size / 2, Size / 2));

        var turned = Draw(bar, panned.Dragged(1.2f, 0));
        Assert.True(
            Enumerable.Range(Size / 2 - 2, 5).Any(y => At(turned, Size / 2, y) is not null),
            "turning moved the panned end out of the middle of the frame");
    }

    [Fact]
    public void PanningBackTheWayItCameLeavesTheViewWhereItStarted()
    {
        var quad = Quad(scale: 0.3f);
        var start = Camera.Facing(0.4f, 0.2f, 0.3f);

        var there = start.Panned(0.4f, -0.25f);
        var back = there.Panned(-0.4f, 0.25f);

        Assert.Equal(start.PivotX, back.PivotX, 5);
        Assert.Equal(start.PivotY, back.PivotY, 5);
        Assert.Equal(start.PivotZ, back.PivotZ, 5);
        Assert.Equal(Covered(Draw(quad, start)), Covered(Draw(quad, back)));
    }

    [Fact]
    public void ADragMeansTheSameOnScreenWhateverTheModelHasBeenTurnedTo()
    {
        // A turntable turns about one axis of the model, so once the picture is tilted a sideways
        // drag turns it about something that does not look vertical — the axis wanders as you work.
        //
        // What a trackball promises instead is that the drag is a movement of the picture: the turn
        // it adds, read in the frame the viewer is looking at, is the same turn whatever the model
        // was showing beforehand. That is what is asserted, by taking the frame before the drag
        // back out of the frame after it.
        var drag = (Camera at) => MeshRenderer.Basis.Compose(
            MeshRenderer.View(at.Dragged(0.25f, -0.1f)), MeshRenderer.View(at).Inverse());

        var upright = Camera.Facing(0.2f, 0.3f);
        var expected = drag(upright);

        foreach (var at in new[]
                 {
                     upright.Rolled(0.4f), upright.Rolled(MathF.PI / 2), upright.Rolled(-1.9f),
                     Camera.Facing(2.6f, -1.1f, 0.8f), new Camera(),
                 })
        {
            var got = drag(at);

            Assert.True(
                MathF.Abs(expected.Rx - got.Rx) < 1e-4f && MathF.Abs(expected.Ry - got.Ry) < 1e-4f
                && MathF.Abs(expected.Rz - got.Rz) < 1e-4f && MathF.Abs(expected.Ux - got.Ux) < 1e-4f
                && MathF.Abs(expected.Uy - got.Uy) < 1e-4f && MathF.Abs(expected.Uz - got.Uz) < 1e-4f
                && MathF.Abs(expected.Fx - got.Fx) < 1e-4f && MathF.Abs(expected.Fy - got.Fy) < 1e-4f
                && MathF.Abs(expected.Fz - got.Fz) < 1e-4f,
                $"the same drag turned the picture differently from {at.Angles}");
        }
    }

    [Fact]
    public void ADragAndItsOppositeCancelAtAnyTilt()
    {
        var tilted = Camera.Facing(0.2f, 0.3f).Rolled(0.9f);

        AssertSameView(
            tilted, tilted.Dragged(0.2f, -0.15f).Dragged(-0.2f, 0.15f),
            "a drag and its opposite did not cancel");
    }

    [Fact]
    public void TiltingByAnAngleIsTheSameAsAskingForThatAngle()
    {
        // Rolled composes a turn about the axis out of the screen; Facing builds the roll into the
        // frame the old way. They have to agree, or every tilt in the tool reads backwards from
        // every tilt written down — and the ones written down are the game's own.
        foreach (var roll in new[] { 0.3f, -0.7f, 1.9f })
            AssertSameView(
                Camera.Facing(0.4f, 0.2f, roll), Camera.Facing(0.4f, 0.2f).Rolled(roll),
                $"rolling by {roll} is not the same as facing at a roll of {roll}");
    }

    [Fact]
    public void ATiltLeavesWhichSideIsFacingYouAlone()
    {
        // A roll turns the picture in its own plane, so the direction out of the screen cannot move.
        var start = Camera.Facing(0.3f, -0.2f);
        var (before, after) = (MeshRenderer.View(start), MeshRenderer.View(start.Rolled(0.7f)));

        Assert.Equal(before.Fx, after.Fx, 4);
        Assert.Equal(before.Fy, after.Fy, 4);
        Assert.Equal(before.Fz, after.Fz, 4);
    }

    [Fact]
    public void ThousandsOfDragsDoNotWearTheFrameOut()
    {
        // Every drag multiplies one more turn onto the last, so whatever error each leaves is
        // carried and not corrected. Left alone that tells as a model that slowly shears.
        var at = new Camera();
        for (var i = 0; i < 5000; i++) at = at.Dragged(0.03f, -0.017f).Rolled(0.004f);

        AssertStillARotation(at, "five thousand drags left the frame out of square");
    }
}
