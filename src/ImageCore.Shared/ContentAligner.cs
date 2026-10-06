namespace ImageCoreService;

/// <summary>How the text lines of a straightened page lean: <see cref="Angle"/> degrees in the middle of the page (positive =
/// clockwise on screen), changing by <see cref="Slope"/> degrees from the top edge to the bottom edge, measured in
/// <see cref="Bands"/> horizontal bands. Deliberately affine (a single, constant rate of change down the page): a page's
/// own straight edges and text baselines can only ever come out as straight lines or a single gentle curve, never a wave.
/// An earlier version instead followed the measured bands exactly, linearly between their centres (piecewise, not affine):
/// it fixed one page where the straight-line fit missed a band by a degree, but on a different real photo (owner's report
/// 2026-09-29, "T1" batch) two adjacent bands' independently measured angles disagreed by noise alone, and interpolating
/// between them put a visible ripple in the page's own straight bottom edge. The straight-line fit cannot do that -- it
/// trades a rare imperfect correction for never inventing a distortion that was not in the photo.</summary>
public readonly record struct LineTilt(double Angle, double Slope, int Bands)
{
    /// <summary>The lean at height <paramref name="v"/> (0 = top edge, 1 = bottom edge), degrees.</summary>
    public double At(double v) => Angle + Slope * (v - 0.5);
}

/// <summary>
/// Last step of straightening: makes the text lines of the page parallel to its edges. The outline found in the photo is
/// never perfect (a corner a few pixels off, a sheet not quite flat, content printed slightly askew), and a line of text
/// that climbs by 1 degree across a page is plainly visible. The lean of the text lines is measured in four horizontal
/// bands (projection profiles, the classic deskew measure), fitted as a lean that may change from top to bottom, and every
/// row is turned back by the lean at its height. Pages without clear text lines (photos, drawings, mostly blank) are left
/// as they are, and so are leans too large to be a residual error (then the content is meant to be at an angle).
/// Pure managed code.
/// </summary>
public static class ContentAligner
{
    /// <summary>Largest lean that is corrected (degrees).</summary>
    public const double MaxAngle = 8;

    /// <summary>Leans smaller than this everywhere are left alone (degrees): not visible, not worth resampling the page.</summary>
    public const double MinCorrection = 0.15;

    private const int BandCount = 4;

    /// <summary>Optional diagnostics (one line per band); used by the EdgeProbe tool.</summary>
    public static Action<string>? Trace { get; set; }
    private const int AnalysisLongEdge = 1200;

    /// <summary>The lean of the text lines, or null when the page has no clear text lines or is already straight.</summary>
    public static LineTilt? Measure(RgbImage page)
    {
        double scale = Math.Min(1.0, (double)AnalysisLongEdge / Math.Max(page.Width, page.Height));
        RgbImage small = scale < 1 ? page.Resize(Math.Max(1, (int)(page.Width * scale)), Math.Max(1, (int)(page.Height * scale))) : page;
        GrayImage gray = small.ToGray();
        return Measure(gray);
    }

    public static LineTilt? Measure(GrayImage gray)
    {
        if (gray.Width < 64 || gray.Height < 64) return null;
        // Text may run across the page or, on a page photographed sideways (not turned upright yet), down it. Both are
        // measured, and the direction with the sharper line structure wins; a sideways page has its lean measured on
        // the transposed page, which mirrors it, and is corrected as one lean (bands along the text lines' length).
        LineTilt? rows = MeasureRows(gray, out double rowQuality);
        LineTilt? columns = MeasureRows(Transpose(gray), out double columnQuality);
        LineTilt? tilt = columnQuality > rowQuality
            ? columns is { } c ? new LineTilt(-c.Angle, 0, c.Bands) : null
            : rows;
        if (tilt is not { } t) return null;
        double worst = Math.Max(Math.Abs(t.At(0)), Math.Abs(t.At(1)));
        if (worst < MinCorrection || Math.Abs(t.Angle) > MaxAngle) return null;
        // A band at the end of the searched range is probably beyond it: the content is meant to be at an angle there.
        if (Math.Abs(t.At(0)) > MaxAngle - 0.3 || Math.Abs(t.At(1)) > MaxAngle - 0.3) return null;
        return t;
    }

    private static GrayImage Transpose(GrayImage g)
    {
        var t = new GrayImage(g.Height, g.Width);
        for (int y = 0; y < g.Height; y++)
            for (int x = 0; x < g.Width; x++)
                t.Data[x * g.Height + y] = g.Data[y * g.Width + x];
        return t;
    }

    /// <summary>The lean of text lines running across <paramref name="gray"/>; <paramref name="quality"/> = how sharp the
    /// line structure is (mean profile contrast of the bands used, 0 when there is none).</summary>
    private static LineTilt? MeasureRows(GrayImage gray, out double quality)
    {
        quality = 0;
        int w = gray.Width, h = gray.Height;
        GrayImage bin = Binarizer.Sauvola(gray, Math.Clamp((Math.Max(w, h) / 40) | 1, 15, 61), Binarizer.DefaultSauvolaK);

        // Ink points away from the page edges (a dark rim left by the outline would read as a line).
        int mx = w * 5 / 100, my = h * 4 / 100;
        var bands = new List<(double V, double Angle, double Weight)>();
        var contrasts = new List<double>();
        int bandHeight = (h - 2 * my) / BandCount;
        for (int b = 0; b < BandCount; b++)
        {
            int y0 = my + b * bandHeight, y1 = y0 + bandHeight;
            var xs = new List<int>();
            var ys = new List<int>();
            for (int y = y0; y < y1; y++)
                for (int x = mx; x < w - mx; x++)
                    if (bin.Data[y * w + x] == 0) { xs.Add(x); ys.Add(y); }
            long area = (long)(y1 - y0) * (w - 2 * mx);
            if (xs.Count < 150 || xs.Count > area * 0.45) continue; // blank, or a dark picture
            if (!BandAngle(xs, ys, w, out double angle, out double contrast)) continue;
            contrasts.Add(Math.Min(contrast, 50));
            Trace?.Invoke($"{w}x{h} band {b}: angle {angle:0.00} contrast {contrast:0.00} ink {xs.Count}");
            if (contrast >= 1.15)
                bands.Add(((y0 + y1) / 2.0 / h, angle, xs.Count * Math.Min(contrast, 3)));
        }
        quality = contrasts.Count == 0 ? 0 : contrasts.Average();
        if (bands.Count == 0) return null;

        // Bands that disagree wildly with the others (a picture, a stamp) are dropped: keep those near the median.
        double median = bands.Select(x => x.Angle).OrderBy(a => a).ElementAt(bands.Count / 2);
        bands = bands.Where(x => Math.Abs(x.Angle - median) <= 1.5).ToList();

        // Weighted line fit angle(v) = a + s (v - 0.5); a single band gives a constant lean.
        double sw = bands.Sum(x => x.Weight);
        double mv = bands.Sum(x => x.Weight * x.V) / sw, ma = bands.Sum(x => x.Weight * x.Angle) / sw;
        double svv = bands.Sum(x => x.Weight * (x.V - mv) * (x.V - mv));
        double slope = bands.Count >= 2 && svv > 1e-6 ? bands.Sum(x => x.Weight * (x.V - mv) * (x.Angle - ma)) / svv : 0;
        slope = Math.Clamp(slope, -3, 3);
        double atCenter = ma + slope * (0.5 - mv);
        return new LineTilt(atCenter, slope, bands.Count);
    }

    /// <summary>Lean of one band's text lines: the angle whose projection profile is sharpest (lines line up), and how
    /// much sharper it is than the flattest angle tried (1 = no structure).</summary>
    private static bool BandAngle(List<int> xs, List<int> ys, int width, out double angle, out double contrast)
    {
        int step = Math.Max(1, xs.Count / 40000);
        double cx = width / 2.0;
        int minY = ys.Min(), maxY = ys.Max();
        int span = maxY - minY + (int)(width * Math.Tan(MaxAngle * Math.PI / 180)) + 4;
        int offset = -minY + span / 2 - (maxY - minY) / 2;

        double Score(double deg)
        {
            double t = Math.Tan(deg * Math.PI / 180);
            var bins = new int[span + 2];
            for (int i = 0; i < xs.Count; i += step)
            {
                int r = (int)Math.Round(ys[i] - t * (xs[i] - cx)) + offset;
                if ((uint)r < (uint)bins.Length) bins[r]++;
            }
            double s = 0;
            for (int i = 1; i < bins.Length; i++)
            {
                double d = bins[i] - bins[i - 1];
                s += d * d;
            }
            return s;
        }

        double best = 0, bestScore = double.MinValue, worst = double.MaxValue;
        for (double a = -MaxAngle; a <= MaxAngle + 1e-9; a += 0.25)
        {
            double s = Score(a);
            if (s > bestScore) { bestScore = s; best = a; }
            worst = Math.Min(worst, s);
        }
        double coarse = best;
        for (double a = coarse - 0.25; a <= coarse + 0.25 + 1e-9; a += 0.025)
        {
            double s = Score(a);
            if (s > bestScore) { bestScore = s; best = a; }
        }
        angle = best;
        contrast = worst > 0 ? bestScore / worst : 1;
        return bestScore > 0;
    }

    /// <summary>The page with every row turned back by the lean at its height (around the page center), so the text lines
    /// come out level; what comes in from outside the page is white. Returns the page itself when there is nothing to do.</summary>
    public static RgbImage Align(RgbImage page, LineTilt? tilt)
    {
        if (tilt is not { } t) return page;
        int w = page.Width, h = page.Height;
        var dst = new RgbImage(w, h);
        byte[] s = page.Data, d = dst.Data;
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
        Parallel.For(0, h, ParallelScope.Options, y =>
        {
            // Lean of the text line that ends up on this row: rotate the row's sampling back by it.
            double a = t.At((y + 0.5) / h) * Math.PI / 180;
            double cos = Math.Cos(a), sin = Math.Sin(a), dy = y - cy;
            for (int x = 0; x < w; x++)
            {
                double dx = x - cx;
                double sx = cx + dx * cos - dy * sin, sy = cy + dx * sin + dy * cos;
                int o = (y * w + x) * 3;
                Bilinear(s, w, h, sx, sy, d, o);
            }
        });
        return dst;
    }

    private static void Bilinear(byte[] src, int w, int h, double x, double y, byte[] dst, int o)
    {
        if (x < 0 || y < 0 || x > w - 1 || y > h - 1)
        {
            dst[o] = dst[o + 1] = dst[o + 2] = 255;
            return;
        }
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(w - 1, x0 + 1), y1 = Math.Min(h - 1, y0 + 1);
        double fx = x - x0, fy = y - y0;
        for (int c = 0; c < 3; c++)
        {
            double top = src[(y0 * w + x0) * 3 + c] * (1 - fx) + src[(y0 * w + x1) * 3 + c] * fx;
            double bottom = src[(y1 * w + x0) * 3 + c] * (1 - fx) + src[(y1 * w + x1) * 3 + c] * fx;
            dst[o + c] = (byte)(top * (1 - fy) + bottom * fy + 0.5);
        }
    }
}
