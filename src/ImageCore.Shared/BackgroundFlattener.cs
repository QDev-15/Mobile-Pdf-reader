namespace ImageCoreService;

/// <summary>
/// Removes uneven lighting from a photographed page: shadows from the phone or a hand, a lamp
/// falloff, yellowed or tinted paper. The paper's brightness is estimated on a small copy of the page
/// (text averaged away, then a local maximum keeps the paper and drops the ink, then a blur), and every
/// pixel is divided by it, so the paper becomes evenly white while ink keeps its contrast. Color pages are
/// estimated per channel, which also neutralises a paper tint (yellow paper, warm lamp light) to white.
///
/// The division maps "a little below the paper level" to pure white too (<see cref="WhitePoint"/>): the
/// grain and noise of a photographed sheet become flat white, which is what makes a cleaned page compress
/// to a fraction of the photo's JPEG size.
/// Memory: the estimates are ~250 px on their long side; the full page is processed row by row.
/// </summary>
public static class BackgroundFlattener
{
    /// <summary>Long edge of the background estimate.</summary>
    private const int EstimateEdge = 256;

    /// <summary>A pixel is never brightened more than 1 / this relative to the paper level (a phone shadow can
    /// drop the paper to ~35% of its lit brightness, so this must stay below that), so a dark
    /// photo or a solid black box on the page is not blown out to grey mush.</summary>
    private const double MinBackgroundRatio = 0.3;

    /// <summary>Share of the local paper level that already counts as pure white.</summary>
    public const float WhitePoint = 1f;

    /// <summary>Returns a new image with the background flattened to white.</summary>
    public static GrayImage Flatten(GrayImage gray)
    {
        (GrayImage bg, int factor) = EstimateBackground(gray);
        var dst = new GrayImage(gray.Width, gray.Height);
        ForEachRow(gray.Width, gray.Height, [bg], factor, (y, rows) =>
        {
            int o = y * gray.Width;
            float[] b = rows[0];
            for (int x = 0; x < gray.Width; x++)
                dst.Data[o + x] = Scale(gray.Data[o + x], b[x]);
        });
        return dst;
    }

    /// <summary>Flattens a color page channel by channel: paper -> white (tint removed), ink and colored marks
    /// (stamps, highlighter, signatures) keep their color.</summary>
    public static RgbImage Flatten(RgbImage rgb)
    {
        int factor = FactorFor(rgb.Width, rgb.Height);
        RgbImage small = rgb.Downscale(factor);
        var bgs = new GrayImage[3];
        for (int c = 0; c < 3; c++)
        {
            var channel = new GrayImage(small.Width, small.Height);
            for (int i = 0; i < channel.Data.Length; i++) channel.Data[i] = small.Data[i * 3 + c];
            bgs[c] = EstimateFromSmall(channel);
        }

        var dst = new RgbImage(rgb.Width, rgb.Height);
        ForEachRow(rgb.Width, rgb.Height, bgs, factor, (y, rows) =>
        {
            int o = y * rgb.Width * 3;
            float[] r = rows[0], g = rows[1], b = rows[2];
            for (int x = 0; x < rgb.Width; x++, o += 3)
            {
                dst.Data[o] = Scale(rgb.Data[o], r[x]);
                dst.Data[o + 1] = Scale(rgb.Data[o + 1], g[x]);
                dst.Data[o + 2] = Scale(rgb.Data[o + 2], b[x]);
            }
        });
        return dst;
    }

    /// <summary>The paper brightness at low resolution and the factor it was shrunk by.</summary>
    public static (GrayImage Background, int Factor) EstimateBackground(GrayImage gray)
    {
        int factor = FactorFor(gray.Width, gray.Height);
        return (EstimateFromSmall(gray.Downscale(factor)), factor);
    }

    private static int FactorFor(int width, int height) =>
        Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)EstimateEdge));

    private static GrayImage EstimateFromSmall(GrayImage small)
    {
        // Local maximum over ~1/40 of the page: wider than any text stroke or line, so only paper
        // survives (text areas take the value of the paper between the letters).
        int radius = Math.Max(2, Math.Max(small.Width, small.Height) / 40);
        GrayImage paper = BoxBlur(MaxFilter(small, radius), radius);

        int level = DocumentCleanup.Percentile(small, 0.95);
        int floor = Math.Max(1, (int)(level * MinBackgroundRatio));
        for (int i = 0; i < paper.Data.Length; i++)
            if (paper.Data[i] < floor) paper.Data[i] = (byte)floor;
        return paper;
    }

    private static byte Scale(byte v, float bg) => (byte)Math.Min(255, (int)(v * 255f / (bg * WhitePoint) + 0.5f));

    /// <summary>Calls <paramref name="row"/> for every full-size row with each background bilinearly
    /// interpolated to that row (one set of row buffers per worker).</summary>
    private static void ForEachRow(int width, int height, GrayImage[] bgs, int factor, Action<int, float[][]> row)
    {
        // The column taps are the same on every row: computed once.
        GrayImage first = bgs[0];
        var xs0 = new int[width];
        var xs1 = new int[width];
        var fxs = new float[width];
        for (int x = 0; x < width; x++)
        {
            double sx = (x + 0.5) / factor - 0.5;
            int x0 = Math.Clamp((int)Math.Floor(sx), 0, first.Width - 1);
            xs0[x] = x0;
            xs1[x] = Math.Min(x0 + 1, first.Width - 1);
            fxs[x] = (float)Math.Clamp(sx - x0, 0, 1);
        }

        Parallel.For(0, height, ParallelScope.Options, () => bgs.Select(_ => new float[width]).ToArray(), (y, _, bufs) =>
        {
            double sy = (y + 0.5) / factor - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sy), 0, first.Height - 1), y1 = Math.Min(y0 + 1, first.Height - 1);
            float fy = (float)Math.Clamp(sy - y0, 0, 1);
            for (int x = 0; x < width; x++)
            {
                int x0 = xs0[x], x1 = xs1[x];
                float fx = fxs[x];
                for (int c = 0; c < bgs.Length; c++)
                {
                    GrayImage bg = bgs[c];
                    float top = bg[x0, y0] * (1 - fx) + bg[x1, y0] * fx;
                    float bot = bg[x0, y1] * (1 - fx) + bg[x1, y1] * fx;
                    bufs[c][x] = Math.Max(1f, top * (1 - fy) + bot * fy);
                }
            }
            row(y, bufs);
            return bufs;
        }, _ => { });
    }


    /// <summary>Square maximum filter (separable).</summary>
    public static GrayImage MaxFilter(GrayImage src, int radius) => Separable(src, radius, max: true);

    /// <summary>Square mean filter (separable), borders clipped.</summary>
    public static GrayImage BoxBlur(GrayImage src, int radius) => Separable(src, radius, max: false);

    private static GrayImage Separable(GrayImage src, int radius, bool max)
    {
        int w = src.Width, h = src.Height;
        var tmp = new GrayImage(w, h);
        var dst = new GrayImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                tmp[x, y] = Reduce(src, x, y, radius, horizontal: true, max);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                dst[x, y] = Reduce(tmp, x, y, radius, horizontal: false, max);
        return dst;
    }

    private static byte Reduce(GrayImage img, int x, int y, int radius, bool horizontal, bool max)
    {
        int lo = Math.Max(0, (horizontal ? x : y) - radius);
        int hi = Math.Min((horizontal ? img.Width : img.Height) - 1, (horizontal ? x : y) + radius);
        int acc = 0;
        for (int i = lo; i <= hi; i++)
        {
            int v = horizontal ? img[i, y] : img[x, i];
            acc = max ? Math.Max(acc, v) : acc + v;
        }
        return (byte)(max ? acc : acc / (hi - lo + 1));
    }
}
