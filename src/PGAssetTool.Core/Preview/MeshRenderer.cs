using System.Numerics;
using PGAssetTool.Core.Export.Meshes;

namespace PGAssetTool.Core.Preview;

/// How the model is being looked at: one orientation, how far back, and what is held in the middle.
///
/// The orientation is a whole rotation rather than a yaw, a pitch and a roll. Three numbers in a
/// fixed order make a turntable — sideways turns the model about one axis of its own, whatever the
/// picture is tilted to, and the pitch has to stop at the poles, past which a drag to the right
/// walks the viewer left. One rotation has none of that.
///
/// What is given up is a level horizon holding the model upright: a trackball can leave it leaning,
/// and square-on is reached by asking for it rather than by dragging carefully.
/// <see cref="Facing"/> and the named views are what ask.
/// <param name="Turn">
/// From the model's own space to the viewer's. Kept as a quaternion because nothing about a drag
/// knows which axis it is about, and composing turns is what a drag does.
/// </param>
/// <param name="Distance">
/// How many of the model's own radii the half-frame covers, so a pistol and a rocket launcher both
/// open at the same apparent size. One puts the bounding sphere exactly inside the shorter side of
/// the pane, which is as close as a model can be framed without a corner of it going off the edge
/// at some angle.
/// </param>
/// <param name="PivotX">
/// The point of the model held at the middle of the frame, offset from the model's own centre and
/// measured in model radii. Zero is the centre of the model, which is where a view starts.
///
/// This is what turning happens around, so panning something to the middle and then turning keeps
/// it there — pan the muzzle of a rocket launcher into view, turn, and the muzzle stays in view.
/// Holding the offset in the frame instead turns the model about its own centre and swings whatever
/// is being looked at straight back out of the frame.
///
/// In radii rather than in model units so it means the same thing on a pistol and a launcher, and
/// so a resized pane keeps the framing rather than throwing it away.
/// </param>
public sealed record Camera
{
    /// Where a view starts: the angle the game draws its own `icon1_big` shop pictures at, so an
    /// author opening a weapon meets the shape they already know rather than a technical view of it.
    ///
    /// Measured, not chosen: 55 weapons matched by hand, read back against each frame the tool can
    /// stand a model up in. Against the bounding box with the muzzle faced they agree to within 5
    /// degrees; against the bounding box alone the yaw spreads three times as far, which is what
    /// facing the model is worth. Fitting the silhouette to the icons themselves tops out around
    /// 0.8 and never sharply, the icons carrying a drawn outline that fattens their shape.
    ///
    /// The 39 that fire agree to 4.6 degrees of yaw. The 16 that do not spread over 35 and sit
    /// around the same middle: a blade has no convention rather than a different one, so there is
    /// one opening angle here and not two, and a knife will not match its own icon whatever is done.
    ///
    /// The roll is not zero, which looks like a mistake and is not: every icon in the game lies
    /// along a diagonal with the muzzle up and to the right.
    public static Quaternion Opening { get; } = Orientation(-0.794f, -0.314f, 0.271f);

    public Quaternion Turn { get; init; } = Opening;

    public float Distance { get; init; } = 1f;

    public float PivotX { get; init; }
    public float PivotY { get; init; }
    public float PivotZ { get; init; }

    /// A view facing the model the way a yaw, a pitch and a roll describe it.
    ///
    /// A named view is exactly that — "from the front", "from above" — and every angle written down
    /// anywhere in this repository is in those terms. Distance and pivot are the framing and belong
    /// to whoever set them, so this only says which way to face.
    public static Camera Facing(float yaw, float pitch, float roll = 0f)
        => new() { Turn = Orientation(yaw, pitch, roll) };

    public Camera Looking(float yaw, float pitch, float roll = 0f)
        => this with { Turn = Orientation(yaw, pitch, roll) };

    /// The three angles this view would be described by. The inverse of <see cref="Facing"/>.
    public (float Yaw, float Pitch, float Roll) Angles => MeshRenderer.View(this).Angles();

    /// <param name="dx">Rightwards, in half-frames: 1 moves the model a half-frame to the right.</param>
    /// <param name="dy">Upwards, in the same units.</param>
    public Camera Panned(float dx, float dy)
    {
        // The cursor drags the model, so the point being held moves the other way. A half-frame is
        // Distance radii across, which is what turns the drag into the units the pivot is kept in.
        var view = MeshRenderer.View(this);
        var (right, up) = (-dx * Distance, -dy * Distance);

        return this with
        {
            PivotX = PivotX + view.Rx * right + view.Ux * up,
            PivotY = PivotY + view.Ry * right + view.Uy * up,
            PivotZ = PivotZ + view.Rz * right + view.Uz * up,
        };
    }

    /// Turns the model by a drag across the screen: rightwards and downwards, in radians.
    ///
    /// A trackball. The model turns about the axis lying across the drag, by an angle proportional
    /// to how far the cursor went — so sideways spins it about the screen's own vertical, downwards
    /// tips its top towards the viewer, and a diagonal does both at once. Nothing here is an axis of
    /// the model, so there is no axis to become unstable and no pole to collapse at.
    ///
    /// What it gives up is that a closed loop of the cursor does not bring the model back to where
    /// it started.
    public Camera Dragged(float right, float down)
    {
        var angle = MathF.Sqrt(right * right + down * down);
        return angle <= 0 ? this : TurnedOnScreen(down, right, 0, angle);
    }

    /// Tilts the model in the plane of the screen, about the axis pointing out of it.
    ///
    /// Orbiting covers two of the three ways a thing can be turned; this is the third. A trackball
    /// reaches it by dragging in a circle, which is a poor way to ask for a small deliberate tilt.
    public Camera Rolled(float dRoll) => TurnedOnScreen(0, 0, 1, dRoll);

    /// A turn about an axis given in the terms of the screen: x right, y up, z out of it.
    ///
    /// The axis turns over in y and z on the way in. The renderer draws through this rotation
    /// mirrored in x — screen-right is the negative of the frame's own right axis, because forward
    /// points at the viewer — and conjugating a turn by a mirror in x is exactly that sign change.
    /// Without it a drag downwards tips the model the wrong way and a roll winds backwards.
    private Camera TurnedOnScreen(float ax, float ay, float az, float angle)
    {
        var length = MathF.Sqrt(ax * ax + ay * ay + az * az);
        if (length <= 1e-9f || angle == 0) return this;

        var delta = Quaternion.CreateFromAxisAngle(
            new Vector3(ax / length, -ay / length, -az / length), angle);

        return this with { Turn = Quaternion.Normalize(delta * Turn) };
    }

    public Camera Zoomed(float factor) => this with { Distance = Math.Clamp(Distance * factor, 0.4f, 20f) };

    /// A yaw, a pitch and a roll as one rotation, in the order the frame is built in: face the
    /// model, tilt up, then turn the picture in its own plane.
    ///
    /// Read out of the frame rather than composed. The trigonometry below is the renderer's own
    /// frame, and deriving the quaternion from it is the only way to be sure the two agree.
    private static Quaternion Orientation(float yaw, float pitch, float roll)
    {
        var (cy, sy) = (MathF.Cos(yaw), MathF.Sin(yaw));
        var (cp, sp) = (MathF.Cos(pitch), MathF.Sin(pitch));

        // Forward points from the model towards the camera, so a larger Z is nearer the viewer.
        var (fx, fy, fz) = (cp * sy, sp, cp * cy);
        var (rx, ry, rz) = (cy, 0f, -sy);
        var (ux, uy, uz) = (fy * rz - fz * ry, fz * rx - fx * rz, fx * ry - fy * rx);

        if (roll != 0)
        {
            var (cr, sr) = (MathF.Cos(roll), MathF.Sin(roll));
            (rx, ry, rz, ux, uy, uz) = (
                rx * cr + ux * sr, ry * cr + uy * sr, rz * cr + uz * sr,
                ux * cr - rx * sr, uy * cr - ry * sr, uz * cr - rz * sr);
        }

        return MeshRenderer.Basis.Of(rx, ry, rz, ux, uy, uz, fx, fy, fz).Turn();
    }
}

/// The two rotations between a model's own vertices and the screen, for a caller that wants to
/// decide them itself rather than take the ones a <see cref="Camera"/> implies.
///
/// <c>Standing</c> stands the model up before anybody looks at it; the renderer's own answer is the
/// bounding box sorted by extent, which knows nothing about which way a gun points — see
/// <see cref="Facing"/>, which does. <c>View</c> is where the viewer is standing.
///
/// Both are ordinary rotations, so a caller with its own answer to either draws through the same
/// rasterizer as everything else instead of a copy of it. Distance and pivot still come from the
/// camera.
public readonly record struct Viewpoint(MeshRenderer.Basis Standing, MeshRenderer.Basis View);

/// The buffers a preview draws into, kept across frames.
///
/// The depth buffer is the size of the frame — eleven megabytes at 2000x1500 — and one allocated
/// per frame is all immediately garbage. Held here, a frame allocates nothing at all.
public sealed class RenderTarget
{
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// Premultiplied BGRA, one row after another, ready to hand to a bitmap.
    public byte[] Bgra { get; private set; } = [];

    internal float[] Depth { get; private set; } = [];

    /// The triangles of the frame being drawn, gathered before any is filled. Grown as needed and
    /// kept, like the rest.
    internal MeshRenderer.Triangle[] Pending { get; set; } = [];

    public bool IsEmpty => Width < 1 || Height < 1;

    public void Resize(int width, int height)
    {
        if (Width == width && Height == height) return;

        (Width, Height) = (width, height);
        var pixels = Math.Max(width * height, 0);
        Bgra = new byte[pixels * 4];
        Depth = new float[pixels];
    }
}

/// Draws a mesh into a pixel buffer, in software.
///
/// Avalonia has no 3D of its own, and reaching for OpenGL would trade a few hundred lines for a
/// dependency on whatever driver the machine happens to have. These meshes are tiny — the largest
/// weapon in the game is a few thousand triangles — so a plain z-buffered rasterizer cannot fail to
/// initialise, and the cost is in the pixels rather than the triangles. Filled on every core it
/// redraws a model filling half of a 2400x1500 pane in 5–7ms.
public static class MeshRenderer
{
    /// <param name="textures">
    /// One per submesh, in Unity's order: submesh <c>i</c> is drawn with the texture of material
    /// <c>i</c>. A null entry, or a mesh with more submeshes than textures, falls back to plain
    /// shading rather than to whatever texture happened to be first.
    /// </param>
    /// <param name="framing">
    /// The model to size and centre the view by, when that is not the one being drawn.
    ///
    /// An animation moves vertices, and a frame worked out from where they are now moves with them:
    /// a pistol whose magazine drops out of it is drawn smaller and lower for as long as the
    /// magazine is out, so what an author sees is the whole gun sliding about rather than the part
    /// that is actually moving. Framing by the model at rest holds the camera still.
    /// </param>
    /// <param name="viewpoint">
    /// The two rotations to draw with, when the caller has its own answer. Null takes the bounding
    /// box for the rest pose and the camera's angles for the view, which is what everything in the
    /// tool itself does.
    /// </param>
    /// <param name="shelled">
    /// Whether to leave out the triangles facing away, overriding what the mesh says of itself.
    ///
    /// The outline shell is a property of a whole mesh, and several meshes of one item are shown
    /// joined — so the answer has to come from the parts rather than from the join, where a shell
    /// that fills its own part's box does not fill the whole item's and stops being found.
    /// Culling where nothing is shelled costs nothing: a closed model's far side is behind its
    /// near one either way.
    /// </param>
    public static void Render(
        UnityMesh mesh, Camera camera, RenderTarget target, IReadOnlyList<PreviewImage?>? textures = null,
        UnityMesh? framing = null, Viewpoint? viewpoint = null, bool? shelled = null)
    {
        if (target.IsEmpty) return;

        var (bgra, width, height) = (target.Bgra, target.Width, target.Height);

        var positions = mesh.Get(VertexAttribute.Position);
        if (positions is null || mesh.VertexCount == 0)
        {
            Array.Clear(bgra);
            return;
        }

        var normals = mesh.Get(VertexAttribute.Normal);
        var uvs = mesh.Get(VertexAttribute.TexCoord0);
        var held = framing ?? mesh;
        var (centre, size, radius) = Bounds(held, held.Get(VertexAttribute.Position) ?? positions);
        var upright = viewpoint?.Standing ?? Upright.For(size).AsBasis();
        if (radius <= 0) radius = 1;

        var view = viewpoint?.View ?? View(camera);
        // Distance is literally how many model radii the half-frame covers, so 1.5 leaves a margin.
        var scale = Math.Min(width, height) * 0.5f / (radius * camera.Distance);

        // Whatever the pivot names is what sits in the middle of the frame, and so what turning
        // happens around. Subtracted from the model rather than added to the drawing, which is the
        // whole difference: the other way turns the model about its own centre and swings whatever
        // was panned into view straight back out of it.
        var (pivotX, pivotY, pivotZ) =
            (camera.PivotX * radius, camera.PivotY * radius, camera.PivotZ * radius);

        var depth = target.Depth;
        var pending = target.Pending;
        var count = 0;

        Span<float> sx = stackalloc float[3];
        Span<float> sy = stackalloc float[3];
        Span<float> sz = stackalloc float[3];
        Span<float> shade = stackalloc float[3];
        Span<float> away = stackalloc float[3];
        Span<float> u = stackalloc float[3];
        Span<float> v = stackalloc float[3];

        var cull = shelled ?? mesh.CarriesAnOutline;

        // Submesh by submesh, because which material draws a triangle is decided by which submesh
        // it belongs to. A mesh with no submeshes recorded is drawn whole.
        var parts = mesh.SubMeshes.Count > 0
            ? mesh.SubMeshes
            : [new SubMesh(0, mesh.Indices.Length, 0, 0)];

        for (var part = 0; part < parts.Count; part++)
        {
            var texture = textures is not null && part < textures.Count ? textures[part] : null;
            var from = Math.Max(parts[part].IndexStart, 0);
            var to = Math.Min(from + parts[part].IndexCount, mesh.Indices.Length);

            for (var i = from; i + 2 < to + 1 && i + 2 < mesh.Indices.Length; i += 3)
            {
                var ok = true;
                for (var corner = 0; corner < 3; corner++)
                {
                    var vertex = mesh.Indices[i + corner];
                    if (vertex < 0 || vertex >= mesh.VertexCount) { ok = false; break; }

                    var (mx, my, mz) = upright.Apply(
                        positions[vertex * 3] - centre.X,
                        positions[vertex * 3 + 1] - centre.Y,
                        positions[vertex * 3 + 2] - centre.Z);
                    var (x, y, z) = view.Apply(mx - pivotX, my - pivotY, mz - pivotZ);

                    sx[corner] = width * 0.5f + x * scale;
                    sy[corner] = height * 0.5f - y * scale;
                    sz[corner] = z;

                    shade[corner] = normals is null ? 1f : Lambert(view, upright, normals, vertex);
                    away[corner] = cull && normals is not null ? Facing(view, upright, normals, vertex) : 1f;
                    u[corner] = uvs is null ? 0 : uvs[vertex * 2];
                    v[corner] = uvs is null ? 0 : uvs[vertex * 2 + 1];
                }
                if (!ok) continue;

                // The outline shell, left out. Its near side is the side facing away from you, which
                // is exactly how the game draws it as a silhouette and never as a surface.
                if (away[0] + away[1] + away[2] < 0) continue;

                if (count == pending.Length) Array.Resize(ref pending, Math.Max(64, pending.Length * 2));
                pending[count++] = new Triangle(
                    sx[0], sx[1], sx[2], sy[0], sy[1], sy[2], sz[0], sz[1], sz[2],
                    shade[0], shade[1], shade[2], u[0], u[1], u[2], v[0], v[1], v[2], texture);
            }
        }

        target.Pending = pending;

        // Filled a band of rows at a time, one band per core, each band clearing its own rows first.
        //
        // One thread over every pixel costs 87–90ms a frame at 2400x1500 with a model filling half
        // of it — eleven a second — and 36ms at 1400x1000, which is a drag that visibly lags the
        // cursor. Bands share no pixels, and within each the triangles go in the order they were
        // gathered, so which one wins a pixel is decided exactly as it would be on one thread.
        var triangles = pending;
        var drawn = count;
        var bands = Math.Clamp(Environment.ProcessorCount, 1, Math.Max(height / 32, 1));
        var rows = (height + bands - 1) / bands;

        Parallel.For(0, bands, band =>
        {
            var top = band * rows;
            var bottom = Math.Min(top + rows, height) - 1;
            if (top > bottom) return;

            Array.Clear(bgra, top * width * 4, (bottom - top + 1) * width * 4);
            Array.Fill(depth, float.NegativeInfinity, top * width, (bottom - top + 1) * width);

            for (var t = 0; t < drawn; t++) Fill(bgra, depth, width, top, bottom, in triangles[t]);
        });
    }

    /// How this model is stood up when nobody says otherwise: the bounding box sorted by extent.
    ///
    /// Exposed so a caller offering some other answer can start from this one and compare against
    /// it, which is the only way to tell whether the other answer is better.
    public static Basis Standing(UnityMesh mesh)
    {
        var positions = mesh.Get(VertexAttribute.Position);
        if (positions is null || mesh.VertexCount == 0) return Basis.Identity;

        var (_, size, _) = Bounds(mesh, positions);
        return Upright.For(size).AsBasis();
    }

    /// Where the model lands on screen for a given view, in half-frames from the middle: -1 is the
    /// left or top edge of a square frame, +1 the right or bottom.
    ///
    /// Measured from the vertices rather than from what was drawn, because a drawing is clipped:
    /// a model three times too wide for the frame covers it edge to edge, and everything read off
    /// that says the framing is already perfect.
    public static (float Left, float Top, float Right, float Bottom)? Extent(
        UnityMesh mesh, Camera camera, Viewpoint? viewpoint = null)
    {
        var positions = mesh.Get(VertexAttribute.Position);
        if (positions is null || mesh.VertexCount == 0) return null;

        var (centre, size, radius) = Bounds(mesh, positions);
        if (radius <= 0) radius = 1;

        var upright = viewpoint?.Standing ?? Upright.For(size).AsBasis();
        var view = viewpoint?.View ?? View(camera);
        var (pivotX, pivotY, pivotZ) =
            (camera.PivotX * radius, camera.PivotY * radius, camera.PivotZ * radius);

        // The same projection the rasterizer does, with the frame's own size divided back out: a
        // half-frame is `radius * Distance` of model, whatever the target happens to be.
        var span = radius * camera.Distance;
        float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;

        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var (mx, my, mz) = upright.Apply(
                positions[v * 3] - centre.X, positions[v * 3 + 1] - centre.Y, positions[v * 3 + 2] - centre.Z);
            var (x, y, _) = view.Apply(mx - pivotX, my - pivotY, mz - pivotZ);

            var (sx, sy) = (x / span, -y / span);
            if (sx < left) left = sx;
            if (sx > right) right = sx;
            if (sy < top) top = sy;
            if (sy > bottom) bottom = sy;
        }

        return right < left ? null : (left, top, right, bottom);
    }

    /// How much a vertex's normal points at the viewer. Negative is turned away.
    private static float Facing(Basis view, Basis upright, float[] normals, int vertex)
    {
        var (ux, uy, uz) = upright.Apply(normals[vertex * 3], normals[vertex * 3 + 1], normals[vertex * 3 + 2]);
        var (_, _, nz) = view.Apply(ux, uy, uz);
        return nz;
    }

    /// A single light over the viewer's shoulder, with enough ambient that faces turned away stay
    /// readable instead of going black.
    private static float Lambert(Basis view, Basis upright, float[] normals, int vertex)
    {
        var (ux, uy, uz) = upright.Apply(normals[vertex * 3], normals[vertex * 3 + 1], normals[vertex * 3 + 2]);
        var (nx, ny, nz) = view.Apply(ux, uy, uz);
        var length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (length <= 0) return 1f;

        // The light sits up and to the left of the camera, in view space.
        var lambert = (nx * -0.35f + ny * 0.5f + nz * 0.79f) / length;
        return 0.25f + 0.75f * Math.Max(lambert, 0f);
    }

    /// One triangle ready to fill: where its corners landed on screen, and what each corner carries.
    internal readonly record struct Triangle(
        float X0, float X1, float X2, float Y0, float Y1, float Y2, float Z0, float Z1, float Z2,
        float S0, float S1, float S2, float U0, float U1, float U2, float V0, float V1, float V2,
        PreviewImage? Texture);

    /// Fills the part of a triangle that falls in rows `top` to `bottom`.
    ///
    /// Each row is scanned only across the span two of the triangle's edges leave open, worked out in
    /// double precision and widened by more than float arithmetic can be out by. Every pixel inside
    /// it is still decided by the same test as ever, in the same float arithmetic, so narrowing the
    /// span only skips pixels that test would have turned away. The third edge is not used to narrow:
    /// its weight is `1 - w0 - w1`, whose error does not follow the edge's own.
    private static void Fill(byte[] bgra, float[] depth, int width, int top, int bottom, in Triangle t)
    {
        var (x0, x1, x2, y0, y1, y2) = (t.X0, t.X1, t.X2, t.Y0, t.Y1, t.Y2);

        var area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (Math.Abs(area) < 1e-6f) return;

        var minX = Math.Max((int)MathF.Floor(Math.Min(x0, Math.Min(x1, x2))), 0);
        var maxX = Math.Min((int)MathF.Ceiling(Math.Max(x0, Math.Max(x1, x2))), width - 1);
        var minY = Math.Max((int)MathF.Floor(Math.Min(y0, Math.Min(y1, y2))), top);
        var maxY = Math.Min((int)MathF.Ceiling(Math.Max(y0, Math.Max(y1, y2))), bottom);
        if (minX > maxX || minY > maxY) return;

        // How far the float test can be out, as a distance along a row: its products are of numbers
        // no larger than this, and float32 is good to about one part in eight million.
        var reach = Math.Max(Math.Max(Math.Abs(x0), Math.Abs(x1)), Math.Max(Math.Abs(x2), width + 1.0));
        reach = Math.Max(reach, Math.Max(Math.Max(Math.Abs(y0), Math.Abs(y1)), Math.Max(Math.Abs(y2), bottom + 1.0)));
        var slack = 16 * 1.2e-7 * reach * reach;
        var sign = area > 0 ? 1.0 : -1.0;

        for (var y = minY; y <= maxY; y++)
        {
            var py = y + 0.5f;
            var from = minX;
            var to = maxX;
            Narrow(x1, y1, x2, y2, py, sign, slack, ref from, ref to);
            Narrow(x2, y2, x0, y0, py, sign, slack, ref from, ref to);

            for (var x = from; x <= to; x++)
            {
                var px = x + 0.5f;

                // Barycentric coordinates, normalised by the signed area so the sign of the triangle
                // does not matter — back faces are drawn too, since these meshes are not all closed.
                var w0 = ((x1 - px) * (y2 - py) - (x2 - px) * (y1 - py)) / area;
                var w1 = ((x2 - px) * (y0 - py) - (x0 - px) * (y2 - py)) / area;
                var w2 = 1f - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;

                var z = w0 * t.Z0 + w1 * t.Z1 + w2 * t.Z2;
                var at = y * width + x;
                if (z <= depth[at]) continue;
                depth[at] = z;

                var lit = Math.Clamp(w0 * t.S0 + w1 * t.S1 + w2 * t.S2, 0f, 1f);

                byte blue = 240, green = 240, red = 240;
                if (t.Texture is { } texture)
                    Sample(texture, w0 * t.U0 + w1 * t.U1 + w2 * t.U2, w0 * t.V0 + w1 * t.V1 + w2 * t.V2,
                        out blue, out green, out red);

                // Ambient floor so a face turned away stays readable rather than going black.
                bgra[at * 4] = (byte)(blue * (0.17f + 0.83f * lit));
                bgra[at * 4 + 1] = (byte)(green * (0.17f + 0.83f * lit));
                bgra[at * 4 + 2] = (byte)(red * (0.17f + 0.83f * lit));
                bgra[at * 4 + 3] = 255;
            }
        }
    }

    /// Pulls a row's span in to where one edge's test has the triangle's sign, and a margin past it.
    ///
    /// The test is `(xi - px)(yj - py) - (xj - px)(yi - py)`, which along a row is `a·px + b`.
    private static void Narrow(
        float xi, float yi, float xj, float yj, float py, double sign, double slack, ref int from, ref int to)
    {
        var a = sign * ((double)yi - yj);
        if (Math.Abs(a) < 1e-9) return;

        var b = sign * ((double)xi * ((double)yj - py) - (double)xj * ((double)yi - py));
        var crossing = -b / a - 0.5;
        var margin = 2 + slack / Math.Abs(a);

        if (a > 0)
        {
            var lowest = Math.Floor(crossing - margin);
            if (lowest > to) from = to + 1;
            else if (lowest > from) from = (int)lowest;
        }
        else
        {
            var highest = Math.Ceiling(crossing + margin);
            if (highest < from) to = from - 1;
            else if (highest < to) to = (int)highest;
        }
    }

    /// Nearest texel, deliberately. Every texture in this game is small and hand-drawn — 256x256 at
    /// the largest, most of them 64 or 128 — and smoothing them would show something the game never
    /// does. Coordinates wrap, because a mesh is free to use them outside the unit square.
    private static void Sample(PreviewImage texture, float u, float v, out byte blue, out byte green, out byte red)
    {
        var x = (int)MathF.Floor(Wrap(u) * texture.Width);
        // Unity puts the texture origin at the bottom left; the decoded rows run top down.
        var y = (int)MathF.Floor((1f - Wrap(v)) * texture.Height);

        x = Math.Clamp(x, 0, texture.Width - 1);
        y = Math.Clamp(y, 0, texture.Height - 1);

        var at = (y * texture.Width + x) * 4;
        (blue, green, red) = (texture.Bgra[at], texture.Bgra[at + 1], texture.Bgra[at + 2]);
    }

    private static float Wrap(float value)
    {
        var fraction = value - MathF.Floor(value);
        return fraction is >= 0 and < 1 ? fraction : 0;
    }

    /// Stands the model up before the camera looks at it: longest side across the screen, next
    /// longest up it.
    ///
    /// A Mesh asset carries no orientation. In the game it is placed by the transform of whichever
    /// renderer draws it, and a preview showing one on its own has nothing to place it with — so
    /// weapons, whose barrels are authored along Y, arrive pointing straight down. Sorting the
    /// bounding box turns that into a level, side-on view without needing to know what the model is.
    ///
    /// Which end the muzzle is on is not recoverable this way. The obvious guess, that the thinner
    /// end is the barrel, holds for only 46 of the 73 clearly-long meshes in one bundle, with the
    /// margins inside the noise — so it is not guessed at, and a gun may face either way.
    private readonly record struct Upright(int Right, int Up, int Depth, float Flip)
    {
        public (float X, float Y, float Z) Apply(float x, float y, float z)
        {
            Span<float> v = [x, y, z];
            return (v[Right], v[Up], v[Depth] * Flip);
        }

        public static Upright For((float X, float Y, float Z) size)
        {
            Span<float> extents = [size.X, size.Y, size.Z];
            Span<int> order = [0, 1, 2];

            // Three elements, so a pair of passes settles it and a sort is not worth reaching for.
            for (var pass = 0; pass < 2; pass++)
                for (var i = 0; i < 2; i++)
                    if (extents[order[i]] < extents[order[i + 1]])
                        (order[i], order[i + 1]) = (order[i + 1], order[i]);

            // Reordering axes can mirror the model. An odd permutation is put back by flipping
            // depth, which leaves the silhouette alone and only decides which side is towards you.
            var odd = (order[0], order[1]) is (0, 2) or (1, 0) or (2, 1);
            return new Upright(order[0], order[1], order[2], odd ? -1f : 1f);
        }

        /// The same permutation as three rows, so anything that wants to stand a model up some
        /// other way can hand over a rotation and be treated identically.
        public Basis AsBasis() => new(
            Right == 0 ? 1 : 0, Right == 1 ? 1 : 0, Right == 2 ? 1 : 0,
            Up == 0 ? 1 : 0, Up == 1 ? 1 : 0, Up == 2 ? 1 : 0,
            Depth == 0 ? Flip : 0, Depth == 1 ? Flip : 0, Depth == 2 ? Flip : 0);
    }

    /// The camera's axes, as three rows that turn a model-space vector into view space: screen
    /// right, screen up, and the direction out of the screen.
    ///
    /// The first row is the *negative* of the frame's own right axis, because forward points at the
    /// viewer: they stand on the far side of the model from Unity's own camera, and what that
    /// camera has on its right is on their left. Taking it as right draws every model as its own
    /// mirror image — invisible on a gun, obvious the moment a texture has writing on it. So this
    /// is an orthonormal frame with a determinant of minus one, and reading that as a mistake is
    /// the mistake. <see cref="Of"/> and <see cref="Turn"/> are where the sign is put on and taken
    /// off; nothing else should touch it.
    public readonly record struct Basis(
        float Rx, float Ry, float Rz, float Ux, float Uy, float Uz, float Fx, float Fy, float Fz)
    {
        public static Basis Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);

        public (float X, float Y, float Z) Apply(float x, float y, float z)
            => (Rx * x + Ry * y + Rz * z, Ux * x + Uy * y + Uz * z, Fx * x + Fy * y + Fz * z);

        /// From a frame's own right, up and forward — the screen-right sign goes on here.
        public static Basis Of(
            float rx, float ry, float rz, float ux, float uy, float uz, float fx, float fy, float fz)
            => new(-rx, -ry, -rz, ux, uy, uz, fx, fy, fz);

        /// The rotation this frame is, with that sign taken off again.
        public Quaternion Turn()
        {
            float m00 = -Rx, m01 = -Ry, m02 = -Rz;
            float m10 = Ux, m11 = Uy, m12 = Uz;
            float m20 = Fx, m21 = Fy, m22 = Fz;

            // Whichever of the four parts is largest is taken first, which keeps the square root
            // away from zero: solving for w always loses all precision on a half turn, where w is
            // itself zero.
            var trace = m00 + m11 + m22;

            if (trace > 0)
            {
                var s = MathF.Sqrt(trace + 1) * 2;
                return Quaternion.Normalize(new Quaternion(
                    (m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, s / 4));
            }

            if (m00 > m11 && m00 > m22)
            {
                var s = MathF.Sqrt(1 + m00 - m11 - m22) * 2;
                return Quaternion.Normalize(new Quaternion(
                    s / 4, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s));
            }

            if (m11 > m22)
            {
                var s = MathF.Sqrt(1 + m11 - m00 - m22) * 2;
                return Quaternion.Normalize(new Quaternion(
                    (m01 + m10) / s, s / 4, (m12 + m21) / s, (m02 - m20) / s));
            }

            var t = MathF.Sqrt(1 + m22 - m00 - m11) * 2;
            return Quaternion.Normalize(new Quaternion(
                (m02 + m20) / t, (m12 + m21) / t, t / 4, (m10 - m01) / t));
        }

        public static Basis From(Quaternion q)
        {
            var n = Quaternion.Normalize(q);
            float x = n.X, y = n.Y, z = n.Z, w = n.W;
            float xx = x * x, yy = y * y, zz = z * z;
            float xy = x * y, xz = x * z, yz = y * z;
            float wx = w * x, wy = w * y, wz = w * z;

            return Of(
                1 - 2 * (yy + zz), 2 * (xy - wz), 2 * (xz + wy),
                2 * (xy + wz), 1 - 2 * (xx + zz), 2 * (yz - wx),
                2 * (xz - wy), 2 * (yz + wx), 1 - 2 * (xx + yy));
        }

        /// The inverse, which for an orthonormal frame is its transpose — mirrored row and all.
        public Basis Inverse() => new(Rx, Ux, Fx, Ry, Uy, Fy, Rz, Uz, Fz);

        /// <c>outer</c> applied after <c>inner</c>, which is the order the renderer draws in: the
        /// model is stood up and then looked at.
        public static Basis Compose(Basis outer, Basis inner)
        {
            var (rx, ry, rz) = inner.Column(0);
            var (ux, uy, uz) = inner.Column(1);
            var (fx, fy, fz) = inner.Column(2);

            var r = (outer.Rx, outer.Ry, outer.Rz);
            var u = (outer.Ux, outer.Uy, outer.Uz);
            var f = (outer.Fx, outer.Fy, outer.Fz);

            return new Basis(
                Dot(r, (rx, ry, rz)), Dot(r, (ux, uy, uz)), Dot(r, (fx, fy, fz)),
                Dot(u, (rx, ry, rz)), Dot(u, (ux, uy, uz)), Dot(u, (fx, fy, fz)),
                Dot(f, (rx, ry, rz)), Dot(f, (ux, uy, uz)), Dot(f, (fx, fy, fz)));
        }

        private (float, float, float) Column(int at) => at switch
        {
            0 => (Rx, Ux, Fx),
            1 => (Ry, Uy, Fy),
            _ => (Rz, Uz, Fz),
        };

        private static float Dot((float X, float Y, float Z) a, (float X, float Y, float Z) b)
            => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        /// The yaw, pitch and roll this frame would be described by.
        ///
        /// Read off rather than searched for: the forward row is built from the yaw and pitch alone,
        /// so those come straight out of it, and the roll is then the angle the screen-right row
        /// sits at from where it would be with no roll on it.
        public (float Yaw, float Pitch, float Roll) Angles()
        {
            var pitch = MathF.Asin(Math.Clamp(Fy, -1f, 1f));
            var yaw = MathF.Atan2(Fx, Fz);

            var (cy, sy) = (MathF.Cos(yaw), MathF.Sin(yaw));
            var (rx, ry, rz) = (cy, 0f, -sy);
            var (ux, uy, uz) = (Fy * rz - Fz * ry, Fz * rx - Fx * rz, Fx * ry - Fy * rx);

            var (sx, syy, sz) = (-Rx, -Ry, -Rz);
            var roll = MathF.Atan2(
                sx * ux + syy * uy + sz * uz,
                sx * rx + syy * ry + sz * rz);

            return (yaw, pitch, roll);
        }
    }

    public static Basis View(Camera camera) => Basis.From(camera.Turn);

    private static MeshBounds Bounds(UnityMesh mesh, float[] positions) => MeshBounds.Of(mesh, positions);
}
