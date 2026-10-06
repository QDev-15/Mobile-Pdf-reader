using System.Drawing;

namespace ImageCoreService;

/// <summary>
/// Scanned-document cleanup analysis: deskew angle, black-border bounds, despeckle. Bitmap
/// rotation / crop (GDI+) lives in ImageCoreService.BitmapTransforms. Pure managed
/// code (see THIRD-PARTY-NOTICES.md for why no OpenCV): the algorithms are standard and
/// small, and this keeps the app free of a ~60 MB native dependency.
/// </summary>
public static class DocumentCleanup
{
    /// <summary>Working resolution for analysis passes (deskew / crop / blank / color).
    /// Plenty to see text lines, fast enough to run on every scanned page.</summary>
    private const int AnalysisDpi = 100;

    public static int AnalysisFactor(int dpi) => Math.Max(1, dpi / AnalysisDpi);

    #region Deskew

    /// <summary>
    /// Skew angle in degrees (positive = content rotated clockwise on screen; fix it with
    /// RotateArbitrary(src, -angle)), or 0 when no confident estimate exists.
    /// Projection-profile method: for each candidate angle, project the ink pixels onto the
    /// rotated y axis; text lines line up (sharp, high-variance profile) only at the right
    /// angle. Coarse search +/-maxAngle at 0.5 deg, then refine at 0.05 deg.
    /// </summary>
    public static double DetectSkew(GrayImage gray, int dpi, double maxAngle = 15)
    {
        GrayImage small = gray.Downscale(AnalysisFactor(dpi));
        GrayImage bin = Binarizer.Threshold(small, Binarizer.OtsuThreshold(small));

        // Ink points, ignoring a 3% margin (scanner edges / black borders would dominate).
        int mx = small.Width * 3 / 100, my = small.Height * 3 / 100;
        var xs = new List<int>();
        var ys = new List<int>();
        for (int y = my; y < small.Height - my; y++)
            for (int x = mx; x < small.Width - mx; x++)
                if (bin.Data[y * small.Width + x] == 0) { xs.Add(x); ys.Add(y); }

        // Almost blank page or almost all black: nothing to measure.
        long area = (long)small.Width * small.Height;
        if (xs.Count < 200 || xs.Count > area / 2) return 0;

        // Subsample big point sets; the profile shape does not need every pixel.
        int step = Math.Max(1, xs.Count / 60000);
        int diag = (int)Math.Ceiling(Math.Sqrt((double)small.Width * small.Width + (double)small.Height * small.Height));

        double Score(double angleDeg)
        {
            double a = angleDeg * Math.PI / 180;
            double sin = Math.Sin(a), cos = Math.Cos(a);
            var bins = new int[2 * diag + 2];
            for (int i = 0; i < xs.Count; i += step)
            {
                int r = (int)Math.Round(ys[i] * cos - xs[i] * sin) + diag;
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

        double best = 0, bestScore = double.MinValue, zeroScore = Score(0);
        for (double a = -maxAngle; a <= maxAngle + 1e-9; a += 0.5)
        {
            double s = Score(a);
            if (s > bestScore) { bestScore = s; best = a; }
        }
        double coarse = best;
        for (double a = coarse - 0.5; a <= coarse + 0.5 + 1e-9; a += 0.05)
        {
            double s = Score(a);
            if (s > bestScore) { bestScore = s; best = a; }
        }

        // Require a clear improvement over "leave it alone" -- photos / tables with no
        // text lines produce flat, noisy scores that shouldn't trigger a rotation.
        if (bestScore < zeroScore * 1.05) return 0;
        return Math.Abs(best) < 0.1 ? 0 : best;
    }

    #endregion

    #region Black border crop

    /// <summary>
    /// Finds dark scanner borders (lid / feeder background around the sheet) and returns
    /// the rectangle of the actual page, in full-resolution pixels. Returns the full
    /// image when there is no border. A row/column counts as border while more than 60%
    /// of it is dark (below half the paper brightness).
    /// </summary>
    public static Rectangle DetectContentBounds(GrayImage gray, int dpi)
    {
        int f = AnalysisFactor(dpi);
        GrayImage s = gray.Downscale(f);
        int w = s.Width, h = s.Height;
        int paper = Percentile(s, 0.90);
        int dark = Math.Max(40, paper / 2);

        bool RowDark(int y) { int n = 0; for (int x = 0; x < w; x++) if (s.Data[y * w + x] < dark) n++; return n > w * 0.6; }
        bool ColDark(int x, int top, int bottom) { int n = 0; for (int y = top; y < bottom; y++) if (s.Data[y * w + x] < dark) n++; return n > (bottom - top) * 0.6; }

        int limitY = h / 4, limitX = w / 4; // never eat more than a quarter from any side
        int t = 0; while (t < limitY && RowDark(t)) t++;
        int b = h; while (b > h - limitY && RowDark(b - 1)) b--;
        int l = 0; while (l < limitX && ColDark(l, t, b)) l++;
        int r = w; while (r > w - limitX && ColDark(r - 1, t, b)) r--;

        if (t == 0 && l == 0 && b == h && r == w)
            return new Rectangle(0, 0, gray.Width, gray.Height);

        // Back to full resolution, plus one analysis pixel of safety so we crop the
        // anti-aliased border edge too.
        int x0 = Math.Min(gray.Width - 1, (l + (l > 0 ? 1 : 0)) * f);
        int y0 = Math.Min(gray.Height - 1, (t + (t > 0 ? 1 : 0)) * f);
        int x1 = r == w ? gray.Width : Math.Max(x0 + 1, (r - 1) * f);
        int y1 = b == h ? gray.Height : Math.Max(y0 + 1, (b - 1) * f);
        return Rectangle.FromLTRB(x0, y0, x1, y1);
    }

    #endregion

    #region Despeckle

    /// <summary>
    /// Removes isolated ink specks (8-connected components of at most
    /// <paramref name="maxArea"/> pixels) from a binary image in place. Default size
    /// scales with DPI: about 1/100 inch square, well under a period at 8 pt.
    /// </summary>
    public static int Despeckle(GrayImage binary, int maxArea)
    {
        int w = binary.Width, h = binary.Height;
        var visited = new bool[binary.Data.Length];
        var comp = new List<int>(maxArea + 1);
        var stack = new Stack<int>();
        int removed = 0;

        for (int start = 0; start < binary.Data.Length; start++)
        {
            if (binary.Data[start] != 0 || visited[start]) continue;

            comp.Clear();
            bool tooBig = false;
            stack.Push(start);
            visited[start] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                if (!tooBig)
                {
                    comp.Add(p);
                    if (comp.Count > maxArea) tooBig = true;
                }
                int px = p % w, py = p / w;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = py + dy;
                    if ((uint)ny >= (uint)h) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = px + dx;
                        if ((uint)nx >= (uint)w) continue;
                        int q = ny * w + nx;
                        if (binary.Data[q] == 0 && !visited[q]) { visited[q] = true; stack.Push(q); }
                    }
                }
            }
            if (!tooBig)
            {
                foreach (int p in comp) binary.Data[p] = 255;
                removed++;
            }
        }
        return removed;
    }

    public static int DefaultSpeckleArea(int dpi)
    {
        int side = Math.Max(1, dpi / 100);
        return side * side;
    }

    #endregion

    public static int Percentile(GrayImage img, double p)
    {
        var hist = new long[256];
        foreach (byte v in img.Data) hist[v]++;
        long target = (long)(img.Data.Length * p), acc = 0;
        for (int i = 0; i < 256; i++)
        {
            acc += hist[i];
            if (acc >= target) return i;
        }
        return 255;
    }
}
