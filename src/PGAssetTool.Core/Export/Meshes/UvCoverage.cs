namespace PGAssetTool.Core.Export.Meshes;

/// Which texels of a texture a model actually samples.
///
/// A weapon's texture is an atlas: the gun, its sights, a few details, and a great deal of nothing
/// between them. Which part is which is written in the mesh's UVs and nowhere else, so an author
/// opening the image has to work it out by painting and looking — and the parts that are never
/// sampled look exactly like the parts that are.
///
/// This rasterises the triangles into the texture's own grid and answers which texels they land on,
/// so the export can clear the rest.
public static class UvCoverage
{
    /// How far a mask has to reach past the triangles for a texture the game reads this way.
    ///
    /// Point filtering with no mip chain reads exactly the texel under the coordinate and nothing
    /// beside it — and that is nearly everything here: 347 of the 367 textures in the bundle holding
    /// the weapons' own art are point-filtered with one mip. For those the mask is the UVs exactly,
    /// which is what an author sees when they open the same model in Blender, and a margin would
    /// only be the tool disagreeing with them. Anything filtered or mipped does read its neighbour,
    /// and one texel covers that.
    ///
    /// A flat two instead nearly doubles what the mask claims on a 64x64 texture: 65% of #16's
    /// atlas against the 37% its model actually reads.
    public static int MarginFor(int filterMode, int mipCount)
        => filterMode == 0 && mipCount <= 1 ? 0 : 1;

    /// How far outside the square a coordinate may sit before it counts as leaving it.
    private const float Slack = 0.001f;

    /// How much of a texel a triangle has to cover before that texel counts as read.
    ///
    /// A coordinate sitting exactly on a texel boundary belongs, as far as arithmetic goes, to the
    /// texel on the far side of it — and pixel-art UVs land on boundaries constantly, an island
    /// ending at exactly 16/64 rather than at 15.99. Taking every texel a triangle touches therefore
    /// grows every island by a row and a column the model never shows, which is what an author
    /// notices first when they lay the mask over the same UVs in Blender.
    ///
    /// A hundredth of a texel is far below anything a triangle really covers — a UV island one texel
    /// wide still covers whole texels along its length — and far above what touching a boundary
    /// produces, which is nothing at all.
    ///
    /// Not higher, though a texel a triangle only clips looks from Blender like one that does not
    /// belong. How much of a texel a triangle covers says nothing about whether the game draws it:
    /// a sliver of UV stretched over a large face is magnified onto a great many pixels. Measured by
    /// painting everything cleared magenta and rendering from 60 angles: on #64, 55 of the 60
    /// thinnest kept texels were drawn when cleared on their own, one at 13.7% coverage on 1,128
    /// pixels; on #416 a texel covered 1.9% is on 39,556. Raising this to 5% changed nothing on #64
    /// and cleared seven drawn texels on #416, and 18–20% coverage turned up texels nothing draws at
    /// all — so no threshold separates the two, and this stays where it removes only what touching a
    /// boundary produces.
    private const float Least = 0.01f;

    /// The texels these submeshes sample, or null when the answer is "all of it".
    ///
    /// Null rather than a mask of everything, so a caller can tell "keep the whole thing" from
    /// "keep this part" without comparing. It comes back for a mesh with no UVs to read and for one
    /// whose UVs leave the square, which is how a texture is tiled: every texel is then in use, and
    /// clearing any of it would be wrong.
    /// <param name="uses">Each mesh and the submesh of it drawn with this texture.</param>
    /// <param name="margin">How far to reach past the triangles; see MarginFor.</param>
    public static bool[]? Of(
        IEnumerable<(UnityMesh Mesh, int SubMesh)> uses, int width, int height, int margin)
    {
        if (width <= 0 || height <= 0) return null;

        var used = new bool[width * height];
        var any = false;

        Span<float> px = stackalloc float[3];
        Span<float> py = stackalloc float[3];

        foreach (var (mesh, part) in uses)
        {
            if (mesh.Get(VertexAttribute.TexCoord0) is not { } uv) return null;

            var wide = mesh.Dimensions.GetValueOrDefault(VertexAttribute.TexCoord0, 2);
            if (wide < 2) return null;

            var parts = mesh.SubMeshes.Count > 0
                ? mesh.SubMeshes
                : [new SubMesh(0, mesh.Indices.Length, 0, 0)];
            if (part < 0 || part >= parts.Count) continue;

            var from = Math.Max(parts[part].IndexStart, 0);
            var to = Math.Min(from + parts[part].IndexCount, mesh.Indices.Length);


            for (var i = from; i + 2 < to; i += 3)
            {
                var ok = true;

                for (var corner = 0; corner < 3; corner++)
                {
                    var vertex = mesh.Indices[i + corner];
                    if (vertex < 0 || (long)vertex * wide + 1 >= uv.Length) { ok = false; break; }

                    var u = uv[vertex * wide];
                    var v = uv[vertex * wide + 1];
                    if (u < -Slack || u > 1 + Slack || v < -Slack || v > 1 + Slack) return null;

                    px[corner] = u * width;

                    // Unity's V runs up from the bottom; a mask laid over a PNG runs down from the top.
                    py[corner] = (1 - v) * height;
                }

                if (!ok) continue;

                Fill(used, width, height, px, py);
                any = true;
            }
        }

        return any ? Grow(used, width, height, margin) : null;
    }

    private static void Fill(bool[] used, int width, int height, Span<float> px, Span<float> py)
    {
        var left = Math.Max((int)MathF.Floor(MathF.Min(px[0], MathF.Min(px[1], px[2]))), 0);
        var right = Math.Min((int)MathF.Ceiling(MathF.Max(px[0], MathF.Max(px[1], px[2]))), width - 1);
        var top = Math.Max((int)MathF.Floor(MathF.Min(py[0], MathF.Min(py[1], py[2]))), 0);
        var bottom = Math.Min((int)MathF.Ceiling(MathF.Max(py[0], MathF.Max(py[1], py[2]))), height - 1);

        var area = Edge(px[0], py[0], px[1], py[1], px[2], py[2]);

        // A triangle with no area in UV space still draws. Every one of its corners reads the same
        // texel, which is how a face painted from a single point of the atlas is written — and it is
        // the one thing the coverage below cannot see, since it covers nothing. Answered the way the
        // game answers it: the texel the point falls in, by the same flooring.
        if (MathF.Abs(area) < 1e-6f)
        {
            for (var corner = 0; corner < 3; corner++)
            {
                var x = Math.Clamp((int)MathF.Floor(px[corner]), 0, width - 1);
                var y = Math.Clamp((int)MathF.Floor(py[corner]), 0, height - 1);
                used[y * width + x] = true;
            }

            return;
        }

        for (var y = top; y <= bottom; y++)
        for (var x = left; x <= right; x++)
            if (Covered(px, py, x, y, area) > Least) used[y * width + x] = true;
    }

    /// How much of one texel a triangle covers, from none of it to all of it.
    private static float Covered(Span<float> px, Span<float> py, int x, int y, float area)
    {
        // Wholly inside, which is most of the texels under any triangle bigger than a few of them.
        // A triangle is convex, so a square whose four corners are all within it is within it.
        if (Within(px, py, x, y, area) && Within(px, py, x + 1, y, area)
            && Within(px, py, x, y + 1, area) && Within(px, py, x + 1, y + 1, area))
            return 1f;

        Span<float> xs = stackalloc float[8];
        Span<float> ys = stackalloc float[8];
        Span<float> cx = stackalloc float[8];
        Span<float> cy = stackalloc float[8];

        for (var corner = 0; corner < 3; corner++) { xs[corner] = px[corner]; ys[corner] = py[corner]; }
        var count = 3;

        // The triangle cut down by each of the texel's four sides in turn. What survives is the
        // piece of the triangle inside the texel, and its area is the answer.
        count = Clip(xs, ys, count, cx, cy, 1, 0, x);
        count = Clip(cx, cy, count, xs, ys, -1, 0, -(x + 1));
        count = Clip(xs, ys, count, cx, cy, 0, 1, y);
        count = Clip(cx, cy, count, xs, ys, 0, -1, -(y + 1));

        if (count < 3) return 0f;

        var twice = 0f;
        for (var i = 0; i < count; i++)
        {
            var next = (i + 1) % count;
            twice += xs[i] * ys[next] - xs[next] * ys[i];
        }

        return MathF.Abs(twice) * 0.5f;
    }

    /// Whether a point is on the inner side of all three edges, whichever way the triangle is wound.
    private static bool Within(Span<float> px, Span<float> py, float x, float y, float area)
        => Edge(px[1], py[1], px[2], py[2], x, y) / area >= 0
            && Edge(px[2], py[2], px[0], py[0], x, y) / area >= 0
            && Edge(px[0], py[0], px[1], py[1], x, y) / area >= 0;

    /// Sutherland–Hodgman against one half-plane: keeps everything where ax*x + ay*y >= b, and puts
    /// a new corner wherever an edge crosses the line.
    private static int Clip(
        Span<float> xs, Span<float> ys, int count, Span<float> ox, Span<float> oy,
        float ax, float ay, float b)
    {
        var kept = 0;

        for (var i = 0; i < count; i++)
        {
            var next = (i + 1) % count;
            var here = ax * xs[i] + ay * ys[i] - b;
            var there = ax * xs[next] + ay * ys[next] - b;

            if (here >= 0) { ox[kept] = xs[i]; oy[kept] = ys[i]; kept++; }

            if (here >= 0 != there >= 0 && kept < ox.Length)
            {
                var along = here / (here - there);
                ox[kept] = xs[i] + (xs[next] - xs[i]) * along;
                oy[kept] = ys[i] + (ys[next] - ys[i]) * along;
                kept++;
            }
        }

        return kept;
    }

    private static float Edge(float ax, float ay, float bx, float by, float cx, float cy)
        => (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);

    /// Spreads the mask outwards by the margin, in two passes rather than one square per texel.
    private static bool[] Grow(bool[] used, int width, int height, int margin)
    {
        var across = new bool[used.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (!used[y * width + x]) continue;
            for (var at = Math.Max(x - margin, 0); at <= Math.Min(x + margin, width - 1); at++)
                across[y * width + at] = true;
        }

        var grown = new bool[used.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (!across[y * width + x]) continue;
            for (var at = Math.Max(y - margin, 0); at <= Math.Min(y + margin, height - 1); at++)
                grown[at * width + x] = true;
        }

        return grown;
    }
}
