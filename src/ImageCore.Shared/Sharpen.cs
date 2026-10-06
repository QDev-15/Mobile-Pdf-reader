namespace ImageCoreService;

/// <summary>
/// Unsharp masking (the classic darkroom technique: add back the difference between the picture and a blurred copy).
/// A phone photo of a page is slightly soft (lens, focus, the upscale to 300 DPI): thin strokes and Vietnamese
/// diacritics come out as light gray and a black-and-white threshold drops them ("sửa" -> "sưa", "11/2024" ->
/// "11 2024"). Sharpening first restores their contrast (measured on a simulated phone photo of small text: ink lost
/// by the threshold 8.7 % -> 3.9 %).
/// </summary>
public static class Sharpen
{
    /// <summary>Radius for a page at <paramref name="dpi"/>: about a third of a typical stroke width.</summary>
    public static int RadiusFor(int dpi) => Math.Clamp((int)Math.Round(dpi / 150.0), 1, 4);

    /// <summary>Strength used for black and white pages.</summary>
    public const double DefaultAmount = 0.8;

    /// <summary>v + amount * (v - blur(v)) in place; blur = two passes of a (2r+1) box (close to a Gaussian), borders
    /// replicated. Two extra bytes per pixel of working memory.</summary>
    public static void UnsharpInPlace(GrayImage img, int radius, double amount)
    {
        int w = img.Width, h = img.Height;
        if (w == 0 || h == 0 || radius < 1) return;
        byte[] s = img.Data;
        var blur = new byte[s.Length];
        var tmp = new byte[s.Length];
        BoxRows(s, tmp, w, h, radius);
        BoxColumns(tmp, blur, w, h, radius);
        BoxRows(blur, tmp, w, h, radius);
        BoxColumns(tmp, blur, w, h, radius);

        float a = (float)amount;
        Parallel.For(0, h, ParallelScope.Options, y =>
        {
            for (int i = y * w, end = i + w; i < end; i++)
            {
                float v = s[i] + a * (s[i] - blur[i]);
                s[i] = v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);
            }
        });
    }

    private static void BoxRows(byte[] src, byte[] dst, int w, int h, int r)
    {
        int n = 2 * r + 1;
        Parallel.For(0, h, ParallelScope.Options, y =>
        {
            int o = y * w, sum = 0;
            for (int k = -r; k <= r; k++) sum += src[o + Math.Clamp(k, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                dst[o + x] = (byte)((sum + n / 2) / n);
                sum += src[o + Math.Min(w - 1, x + r + 1)] - src[o + Math.Max(0, x - r)];
            }
        });
    }

    /// <summary>Vertical box, row by row with running column sums (memory-order access), in horizontal bands.</summary>
    private static void BoxColumns(byte[] src, byte[] dst, int w, int h, int r)
    {
        int n = 2 * r + 1;
        int bands = Math.Clamp(Environment.ProcessorCount, 1, Math.Max(1, h / 64));
        int bandHeight = (h + bands - 1) / bands;
        Parallel.For(0, bands, ParallelScope.Options, b =>
        {
            int y0 = b * bandHeight, y1 = Math.Min(h, y0 + bandHeight);
            if (y0 >= y1) return;
            var sum = new int[w];
            for (int k = -r; k <= r; k++)
            {
                int row = Math.Clamp(y0 + k, 0, h - 1) * w;
                for (int x = 0; x < w; x++) sum[x] += src[row + x];
            }
            for (int y = y0; y < y1; y++)
            {
                int o = y * w;
                for (int x = 0; x < w; x++) dst[o + x] = (byte)((sum[x] + n / 2) / n);
                int add = Math.Min(h - 1, y + r + 1) * w, drop = Math.Max(0, y - r) * w;
                for (int x = 0; x < w; x++) sum[x] += src[add + x] - src[drop + x];
            }
        });
    }
}
