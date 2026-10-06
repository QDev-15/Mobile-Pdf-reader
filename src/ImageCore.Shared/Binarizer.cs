namespace ImageCoreService;

[System.ComponentModel.TypeConverter(typeof(EnumDescriptionConverter))]
public enum BinarizationMethod
{
    /// <summary>Local adaptive threshold (Sauvola &amp; Pietikäinen, 2000). Default:
    /// handles yellowed paper, uneven scanner lighting, shadows and faint ink.</summary>
    [System.ComponentModel.Description("Sauvola (thích nghi)")] Sauvola,
    /// <summary>Single global threshold (Otsu, 1979). Faster; fine for clean, evenly lit pages.</summary>
    [System.ComponentModel.Description("Otsu (toàn trang)")] Otsu,
}

/// <summary>
/// Gray -> binary conversion. Both algorithms are classic published methods implemented
/// from scratch here (no third-party code, no patents) so they are free to ship in a
/// commercial product.
///
/// Why Sauvola is the default: it computes a threshold per pixel from the local mean m
/// and standard deviation s over a window, T = m * (1 + k * (s / R - 1)). On blank paper s
/// is tiny, so T drops well below the paper level and scanner noise stays white; near
/// strokes s is large, so T approaches m and thin / faint strokes survive. A single global
/// threshold (Otsu, or the old fixed 128) cannot do both on a page with a gradient or a
/// yellowed background. Local mean/variance come from integral images, so the cost is
/// O(pixels) regardless of window size.
/// </summary>
public static class Binarizer
{
    public static GrayImage Binarize(GrayImage src, BinarizationMethod method, int dpi,
        double sauvolaK = DefaultSauvolaK, int windowSize = 0)
    {
        return method == BinarizationMethod.Otsu
            ? Threshold(src, OtsuThreshold(src))
            : Sauvola(src, windowSize > 0 ? windowSize : DefaultWindow(dpi), sauvolaK);
    }

    public const double DefaultSauvolaK = 0.34;

    /// <summary>~1/8 inch window: a few text strokes wide at any DPI.</summary>
    public static int DefaultWindow(int dpi) => Math.Clamp((int)Math.Round(dpi / 8.0) | 1, 15, 151);

    public static int OtsuThreshold(GrayImage src)
    {
        var hist = new long[256];
        foreach (byte v in src.Data) hist[v]++;
        return OtsuThreshold(hist);
    }

    public static int OtsuThreshold(long[] hist)
    {
        long total = 0;
        double sumAll = 0;
        for (int i = 0; i < 256; i++) { total += hist[i]; sumAll += i * (double)hist[i]; }
        if (total == 0) return 128;

        double sumB = 0, bestVar = -1;
        long wB = 0;
        int best = 128;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            long wF = total - wB;
            if (wF == 0) break;
            sumB += t * (double)hist[t];
            double mB = sumB / wB, mF = (sumAll - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > bestVar) { bestVar = between; best = t; }
        }
        return best;
    }

    /// <summary>Pixels &lt;= threshold become ink (0), the rest paper (255).</summary>
    public static GrayImage Threshold(GrayImage src, int threshold)
    {
        var dst = new GrayImage(src.Width, src.Height);
        for (int i = 0; i < src.Data.Length; i++)
            dst.Data[i] = src.Data[i] <= threshold ? (byte)0 : (byte)255;
        return dst;
    }

    /// <summary>Sauvola threshold with a square <paramref name="window"/>, borders clipped (window sums: see <see cref="Scan"/>).</summary>
    public static GrayImage Sauvola(GrayImage src, int window, double k) => Sauvola(src, window, k, 0);

    /// <param name="offset">Brightness shift in gray levels, applied as if every pixel (and so every local mean) were
    /// <paramref name="offset"/> brighter: t = (m + b)(1 + k(s/R - 1)) - b. Positive = fewer, thinner strokes (lighter page);
    /// negative = more ink. 0 is plain Sauvola, bit for bit.</param>
    public static GrayImage Sauvola(GrayImage src, int window, double k, double offset) => Sauvola(src, window, k, offset, 0);

    /// <param name="ramp">0: pure black and white (0 / 255). Above 0: anti-aliased edges, see <see cref="Shade"/>.</param>
    public static GrayImage Sauvola(GrayImage src, int window, double k, double offset, double ramp)
    {
        var dst = new GrayImage(src.Width, src.Height);
        var sink = new ThresholdSink(src.Data, dst.Data, k, offset, ramp);
        Scan(src, window, ref sink);
        return dst;
    }

    /// <summary>The local statistics Sauvola thresholds against, kept so that a new darkness or brightness costs one
    /// comparison per pixel (<see cref="Threshold(GrayImage, SauvolaStats, double, double, GrayImage)"/>) instead of
    /// another pass of window sums: what makes the darkness slider live on the result screen.
    /// 8 bytes per pixel (float mean and standard deviation): meant for screen-size previews.</summary>
    public static SauvolaStats Stats(GrayImage src, int window)
    {
        var stats = new SauvolaStats(src.Width, src.Height);
        var sink = new StatsSink(stats.Mean, stats.Deviation);
        Scan(src, window, ref sink);
        return stats;
    }

    /// <summary>Sauvola with precomputed <paramref name="stats"/> of <paramref name="src"/>, into <paramref name="dst"/>.</summary>
    public static void Threshold(GrayImage src, SauvolaStats stats, double k, double offset, GrayImage dst) =>
        Threshold(src, stats, k, offset, dst, 0);

    /// <param name="ramp">0: pure black and white. Above 0: anti-aliased edges (<see cref="Shade"/>).</param>
    public static void Threshold(GrayImage src, SauvolaStats stats, double k, double offset, GrayImage dst, double ramp)
    {
        byte[] data = src.Data, output = dst.Data;
        float[] mean = stats.Mean, dev = stats.Deviation;
        int w = src.Width;
        Parallel.For(0, src.Height, ParallelScope.Options, y =>
        {
            for (int i = y * w, end = i + w; i < end; i++)
            {
                double t = (mean[i] + offset) * (1 + k * (dev[i] / SauvolaRange - 1)) - offset;
                output[i] = Shade(data[i], t, ramp);
            }
        });
    }

    /// <summary>
    /// The output level of pixel <paramref name="v"/> against threshold <paramref name="t"/>. With no
    /// <paramref name="ramp"/>: black at or below the threshold, white above (plain Sauvola). With a ramp: a pixel
    /// within <paramref name="ramp"/> gray levels of the threshold gets a proportional gray, black / white beyond. Paper
    /// and ink stay pure white and black (the threshold is far from both), but the pixels a stroke edge only partly
    /// covers keep a partial gray: smooth, sharp-looking text instead of the staircase edges of a 1-bit page, which is
    /// also what makes the page look crisp when the screen shows it smaller than its pixels. Exactly the hard threshold
    /// at the 128 midpoint: a pixel is darker than 128 exactly when it is at or below t (up to the half-level rounding).
    /// </summary>
    public static byte Shade(double v, double t, double ramp)
    {
        if (ramp <= 0) return v <= t ? (byte)0 : (byte)255;
        double o = 127.5 + (v - t) * (127.5 / ramp);
        return o <= 0 ? (byte)0 : o >= 255 ? (byte)255 : (byte)o;
    }

    /// <summary>Dynamic range of the standard deviation for 8-bit input (Sauvola's R).</summary>
    private const double SauvolaRange = 128.0;

    /// <summary>Receives each pixel's window mean and variance from <see cref="Scan"/>. A struct type argument, so the
    /// call is inlined into the loop (no delegate per pixel).</summary>
    private interface IWindowSink
    {
        void Put(int index, double mean, double variance);
    }

    private readonly struct ThresholdSink(byte[] data, byte[] output, double k, double offset, double ramp) : IWindowSink
    {
        public void Put(int i, double mean, double variance)
        {
            double t = (mean + offset) * (1 + k * (Math.Sqrt(variance) / SauvolaRange - 1)) - offset;
            output[i] = Shade(data[i], t, ramp);
        }
    }

    private readonly struct StatsSink(float[] mean, float[] deviation) : IWindowSink
    {
        public void Put(int i, double m, double variance)
        {
            mean[i] = (float)m;
            deviation[i] = (float)Math.Sqrt(variance);
        }
    }

    /// <summary>
    /// Mean and variance of the square <paramref name="window"/> around every pixel, borders clipped. Memory is
    /// O(width): each horizontal band of rows keeps running per-column sums of v and v^2 and slides them down one row
    /// at a time (add the row entering the window, drop the one leaving it); the window sum along a row is another
    /// running sum over those columns. An 8.7 MP A4 page therefore needs a few KB of working memory instead of the
    /// ~140 MB two full integral images would take. The sums are exact integers, so the result is bit-identical to
    /// the integral-image version.
    /// </summary>
    private static void Scan<TSink>(GrayImage src, int window, ref TSink sink) where TSink : struct, IWindowSink
    {
        int w = src.Width, h = src.Height;
        int half = Math.Max(0, window / 2);
        if (w == 0 || h == 0) return;

        // Bands run in parallel; each needs to prime its column sums over ~window rows, so keep
        // bands several windows tall.
        int bands = Math.Clamp(Environment.ProcessorCount, 1, Math.Max(1, h / Math.Max(64, 4 * half)));
        int bandHeight = (h + bands - 1) / bands;
        byte[] data = src.Data;
        TSink s0 = sink;

        Parallel.For(0, bands, ParallelScope.Options, b =>
        {
            TSink local = s0;
            int yStart = b * bandHeight, yEnd = Math.Min(h, yStart + bandHeight);
            if (yStart >= yEnd) return;

            var colSum = new long[w];
            var colSq = new long[w];
            void AddRow(int r, int sign)
            {
                int o = r * w;
                for (int x = 0; x < w; x++)
                {
                    int v = data[o + x];
                    colSum[x] += sign * v;
                    colSq[x] += sign * v * v;
                }
            }

            int top = Math.Max(0, yStart - half), bottom = Math.Min(h - 1, yStart + half);
            for (int r = top; r <= bottom; r++) AddRow(r, +1);

            for (int y = yStart; y < yEnd; y++)
            {
                if (y > yStart)
                {
                    int newTop = Math.Max(0, y - half), newBottom = Math.Min(h - 1, y + half);
                    if (newBottom > bottom) AddRow(newBottom, +1);
                    if (newTop > top) AddRow(top, -1);
                    top = newTop;
                    bottom = newBottom;
                }
                int rows = bottom - top + 1;

                // Running window over the columns of this row.
                long s = 0, s2 = 0;
                int right = Math.Min(w - 1, half);
                for (int x = 0; x <= right; x++) { s += colSum[x]; s2 += colSq[x]; }
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (x > 0)
                    {
                        int enter = x + half, leave = x - half - 1;
                        if (enter < w) { s += colSum[enter]; s2 += colSq[enter]; }
                        if (leave >= 0) { s -= colSum[leave]; s2 -= colSq[leave]; }
                    }
                    int x0 = Math.Max(0, x - half), x1 = Math.Min(w - 1, x + half);
                    long n = (long)(x1 - x0 + 1) * rows;
                    double mean = s / (double)n;
                    double variance = Math.Max(0, s2 / (double)n - mean * mean);
                    local.Put(o + x, mean, variance);
                }
            }
        });
    }
}
