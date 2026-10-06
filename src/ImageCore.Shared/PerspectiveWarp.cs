namespace ImageCoreService;

/// <summary>
/// Straightens a photographed page: resamples the four-sided region <c>srcQuad</c> of a photo
/// into an upright rectangle. Pure managed code, same commercial-license position as the rest
/// of the library.
///
/// Coordinates follow the "pixel edge" convention: pixel <c>i</c> covers <c>[i, i+1)</c>, so a
/// point normalized to 0..1 times the image width lands exactly on the image edge. Each output
/// pixel is mapped through the projective transform to a source position and read with
/// bilinear interpolation; when the source is clearly denser than the output (a photo shrunk to
/// a page) it is read at 2x2 sub-positions and averaged, which keeps small text from aliasing.
///
/// The outline may reach beyond the photo (a sheet cut by the frame has its corners off-picture):
/// what falls outside the source is filled with white (paper), so the outline keeps its true shape
/// and only the part that was never photographed is blank.
/// </summary>
public static class PerspectiveWarp
{
    /// <param name="src">The photo (or the decoded part of it).</param>
    /// <param name="srcQuad">Corner points in <paramref name="src"/> pixels: TL, TR, BR, BL of the page as it
    /// should appear upright.</param>
    /// <param name="outWidth">Width of the result.</param>
    /// <param name="outHeight">Height of the result.</param>
    /// <param name="bends">Curved sides of a page that was not flat (null or flat = the plain projective warp). Each
    /// output point is then the projective point plus a displacement blended from the four sides' bulges (a Coons patch
    /// on top of the perspective): the curved edges land exactly on the output borders and the paper between them is
    /// stretched along, so a bent page comes out with straight edges and (nearly) straight lines.</param>
    public static RgbImage Warp(RgbImage src, Quad srcQuad, int outWidth, int outHeight, PageBends? bends = null)
    {
        if (outWidth < 1 || outHeight < 1) throw new ArgumentOutOfRangeException(nameof(outWidth));

        // Output pixel position -> source position.
        var rect = new[] { new PointD(0, 0), new PointD(outWidth, 0), new PointD(outWidth, outHeight), new PointD(0, outHeight) };
        Homography h = Homography.FromPoints(rect, srcQuad.ToArray());
        double h0 = h[0], h1 = h[1], h2 = h[2], h3 = h[3], h4 = h[4], h5 = h[5], h6 = h[6], h7 = h[7], h8 = h[8];

        // How many source pixels one output pixel spans (average of the two directions).
        double ratio = (Dist(srcQuad.TopLeft, srcQuad.TopRight) / outWidth + Dist(srcQuad.TopLeft, srcQuad.BottomLeft) / outHeight) / 2;
        bool supersample = ratio > 1.25;

        // Bulge displacement of each side, per output column (top, bottom) and per output row (left, right).
        double[]? topX = null, topY = null, botX = null, botY = null, leftX = null, leftY = null, rightX = null, rightY = null;
        if (bends is { IsFlat: false })
        {
            (topX, topY) = SideOffsets(h, srcQuad, bends, 0, outWidth, alongX: true, fixedCoord: 0);
            (botX, botY) = SideOffsets(h, srcQuad, bends, 2, outWidth, alongX: true, fixedCoord: outHeight);
            (leftX, leftY) = SideOffsets(h, srcQuad, bends, 3, outHeight, alongX: false, fixedCoord: 0);
            (rightX, rightY) = SideOffsets(h, srcQuad, bends, 1, outHeight, alongX: false, fixedCoord: outWidth);
        }

        var dst = new RgbImage(outWidth, outHeight);
        int sw = src.Width, sh = src.Height;
        byte[] sData = src.Data;
        byte[] dData = dst.Data;

        Parallel.For(0, outHeight, ParallelScope.Options, y =>
        {
            int o = y * outWidth * 3;
            double v = (y + 0.5) / outHeight;
            for (int x = 0; x < outWidth; x++, o += 3)
            {
                double r = 0, g = 0, b = 0;
                double ox = 0, oy = 0;
                if (topX != null)
                {
                    double u = (x + 0.5) / outWidth;
                    ox = (1 - v) * topX[x] + v * botX![x] + (1 - u) * leftX![y] + u * rightX![y];
                    oy = (1 - v) * topY![x] + v * botY![x] + (1 - u) * leftY![y] + u * rightY![y];
                }
                if (supersample)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        double px = x + 0.25 + (k & 1) * 0.5, py = y + 0.25 + (k >> 1) * 0.5;
                        Sample(sData, sw, sh, h0, h1, h2, h3, h4, h5, h6, h7, h8, px, py, ox, oy, ref r, ref g, ref b);
                    }
                    r *= 0.25; g *= 0.25; b *= 0.25;
                }
                else
                {
                    Sample(sData, sw, sh, h0, h1, h2, h3, h4, h5, h6, h7, h8, x + 0.5, y + 0.5, ox, oy, ref r, ref g, ref b);
                }
                dData[o] = (byte)(r + 0.5);
                dData[o + 1] = (byte)(g + 0.5);
                dData[o + 2] = (byte)(b + 0.5);
            }
        });
        return dst;
    }

    /// <summary>Adds the bilinear sample of the source at the position (px, py) of the output.</summary>
    private static void Sample(byte[] s, int sw, int sh,
        double h0, double h1, double h2, double h3, double h4, double h5, double h6, double h7, double h8,
        double px, double py, double ox, double oy, ref double r, ref double g, ref double b)
    {
        double w = h6 * px + h7 * py + h8;
        // Pixel i is centred at i + 0.5, so shift by half a pixel before interpolating.
        double sx = (h0 * px + h1 * py + h2) / w - 0.5 + ox;
        double sy = (h3 * px + h4 * py + h5) / w - 0.5 + oy;

        // Beyond the outermost pixel centres by half a pixel = not in the photo: paper white.
        if (sx < -0.5 || sy < -0.5 || sx > sw - 0.5 || sy > sh - 0.5)
        {
            r += 255; g += 255; b += 255;
            return;
        }

        int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
        double fx = sx - x0, fy = sy - y0;
        int x1 = Math.Clamp(x0 + 1, 0, sw - 1), y1 = Math.Clamp(y0 + 1, 0, sh - 1);
        x0 = Math.Clamp(x0, 0, sw - 1);
        y0 = Math.Clamp(y0, 0, sh - 1);

        int i00 = (y0 * sw + x0) * 3, i10 = (y0 * sw + x1) * 3, i01 = (y1 * sw + x0) * 3, i11 = (y1 * sw + x1) * 3;
        double w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
        r += s[i00] * w00 + s[i10] * w10 + s[i01] * w01 + s[i11] * w11;
        g += s[i00 + 1] * w00 + s[i10 + 1] * w10 + s[i01 + 1] * w01 + s[i11 + 1] * w11;
        b += s[i00 + 2] * w00 + s[i10 + 2] * w10 + s[i01 + 2] * w01 + s[i11 + 2] * w11;
    }

    /// <summary>For each output column (top / bottom side) or row (left / right side), the bulge of that side at the
    /// point the plain projective map sends the border pixel to: where that point sits on the chord gives the chord
    /// parameter, and the bend there, pushed along the side's outward normal, is the displacement to add.</summary>
    private static (double[] X, double[] Y) SideOffsets(Homography h, Quad quad, PageBends bends, int side, int count,
        bool alongX, double fixedCoord)
    {
        PointD[] c = quad.ToArray();
        PointD a = c[side], bEnd = c[(side + 1) % 4];
        double dx = bEnd.X - a.X, dy = bEnd.Y - a.Y, len2 = dx * dx + dy * dy;
        (double nx, double ny, double length) = PageBends.OutwardNormal(quad, side);
        SideBend bend = bends[side];
        var xs = new double[count];
        var ys = new double[count];
        for (int i = 0; i < count; i++)
        {
            double p = i + 0.5;
            PointD s = alongX ? h.Apply(p, fixedCoord) : h.Apply(fixedCoord, p);
            double t = len2 < 1e-9 ? 0 : Math.Clamp(((s.X - a.X) * dx + (s.Y - a.Y) * dy) / len2, 0, 1);
            double d = bend.Offset(t) * length;
            xs[i] = nx * d;
            ys[i] = ny * d;
        }
        return (xs, ys);
    }

    private static double Dist(PointD a, PointD b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>ISO 216 (A4, A3...): long side / short side = sqrt(2).</summary>
    public const double A4Ratio = 1.41421356237;

    /// <summary>Long : short ratio range an outline must fall in to be treated as an A4 sheet (A4 = 1.414; perspective
    /// and imprecise corners move a real sheet by up to ~20%). Outside it (a sheet cut off by the photo frame, a receipt, a
    /// card) forcing A4 would visibly stretch the content.</summary>
    public const double MinA4Like = 1.15, MaxA4Like = 1.75;

    /// <summary>Long : short ratio of the outline's own shape (average of opposite sides).</summary>
    public static double ShapeRatio(Quad q)
    {
        double w = (Dist(q.TopLeft, q.TopRight) + Dist(q.BottomLeft, q.BottomRight)) / 2;
        double h = (Dist(q.TopLeft, q.BottomLeft) + Dist(q.TopRight, q.BottomRight)) / 2;
        return Math.Max(w, h) / Math.Max(1e-9, Math.Min(w, h));
    }

    /// <summary>True when straightening the outline into an A4 sheet would not distort it noticeably.</summary>
    public static bool IsA4Like(Quad q) => ShapeRatio(q) is >= MinA4Like and <= MaxA4Like;

    /// <summary>True when the outline is wider than tall, judged on the AVERAGE of opposite sides. (The longer of each pair
    /// would flip the answer for a strongly trapezoidal outline, e.g. a corner dragged beyond the photo: owner's page 7
    /// had sides 1349 / 2576 x 2378 / 2329 px, average shape portrait 1 : 1.2, and was straightened into landscape A4,
    /// stretching the text 1.7x.)</summary>
    public static bool IsLandscapeShape(Quad q) =>
        Dist(q.TopLeft, q.TopRight) + Dist(q.BottomLeft, q.BottomRight) > Dist(q.TopLeft, q.BottomLeft) + Dist(q.TopRight, q.BottomRight);

    /// <summary>
    /// Size of an A4 sheet (ratio 1 : sqrt 2) for straightening a quad: landscape when the outline is wider
    /// than tall, otherwise portrait. The long side keeps the finer resolution of the outline (never less
    /// than its longer side, nor than its shorter side times sqrt 2), then is shrunk to fit
    /// <paramref name="maxLongEdge"/> and <paramref name="maxPixels"/> (3508 x 2480 = A4 at 300 DPI).
    /// </summary>
    public static (int Width, int Height) A4Size(Quad q, int maxLongEdge, long maxPixels)
    {
        double w = Math.Max(Dist(q.TopLeft, q.TopRight), Dist(q.BottomLeft, q.BottomRight));
        double h = Math.Max(Dist(q.TopLeft, q.BottomLeft), Dist(q.TopRight, q.BottomRight));
        bool landscape = IsLandscapeShape(q);
        double longSide = landscape ? Math.Max(w, h * A4Ratio) : Math.Max(h, w * A4Ratio);
        longSide = Math.Min(longSide, Math.Min(maxLongEdge, Math.Sqrt(maxPixels * A4Ratio)));
        int l = Math.Max(23, (int)Math.Round(longSide));
        int s = Math.Max(16, (int)Math.Round(l / A4Ratio));
        return landscape ? (l, s) : (s, l);
    }

    /// <summary>
    /// Size of the rectangle a quad should be straightened into: as wide as its longer horizontal
    /// side and as tall as its longer vertical side (so no direction loses resolution), then shrunk
    /// (keeping the shape) until it fits <paramref name="maxLongEdge"/> and <paramref name="maxPixels"/>.
    /// </summary>
    public static (int Width, int Height) OutputSize(Quad q, int maxLongEdge, long maxPixels)
    {
        double w = Math.Max(Dist(q.TopLeft, q.TopRight), Dist(q.BottomLeft, q.BottomRight));
        double h = Math.Max(Dist(q.TopLeft, q.BottomLeft), Dist(q.TopRight, q.BottomRight));
        double scale = Math.Min(1.0, Math.Min(maxLongEdge / Math.Max(w, h), Math.Sqrt(maxPixels / (w * h))));
        return (Math.Max(16, (int)Math.Round(w * scale)), Math.Max(16, (int)Math.Round(h * scale)));
    }
}
