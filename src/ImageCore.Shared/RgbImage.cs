namespace ImageCoreService;

/// <summary>8-bit RGB buffer (interleaved R,G,B; row-major, no padding). Platform-neutral
/// twin of <see cref="GrayImage"/> for the steps that need color (edge detection, unwarp).</summary>
public sealed class RgbImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }

    public RgbImage(int width, int height, byte[]? data = null)
    {
        Width = width;
        Height = height;
        Data = data ?? new byte[width * height * 3];
        if (Data.Length != width * height * 3) throw new ArgumentException("Buffer size mismatch.", nameof(data));
    }

    /// <summary>From Android-style packed ARGB ints (alpha ignored).</summary>
    public static RgbImage FromArgb(int[] argb, int width, int height)
    {
        if (argb.Length < width * height) throw new ArgumentException("Pixel buffer too small.", nameof(argb));
        var img = new RgbImage(width, height);
        byte[] d = img.Data;
        Parallel.For(0, height, ParallelScope.Options, y =>
        {
            for (int i = y * width, o = i * 3, end = i + width; i < end; i++, o += 3)
            {
                int p = argb[i];
                d[o] = (byte)(p >> 16);
                d[o + 1] = (byte)(p >> 8);
                d[o + 2] = (byte)p;
            }
        });
        return img;
    }

    /// <summary>Gray -> RGB (R = G = B), for encoders that only take color.</summary>
    public static RgbImage FromGray(GrayImage gray)
    {
        var img = new RgbImage(gray.Width, gray.Height);
        byte[] src = gray.Data, d = img.Data;
        int w = gray.Width;
        Parallel.For(0, gray.Height, ParallelScope.Options, y =>
        {
            for (int i = y * w, o = i * 3, end = i + w; i < end; i++, o += 3)
                d[o] = d[o + 1] = d[o + 2] = src[i];
        });
        return img;
    }

    public GrayImage ToGray()
    {
        var g = new GrayImage(Width, Height);
        byte[] src = Data, d = g.Data;
        int w = Width;
        Parallel.For(0, Height, ParallelScope.Options, y =>
        {
            for (int i = y * w, o = i * 3, end = i + w; i < end; i++, o += 3)
                d[i] = (byte)((src[o] * 299 + src[o + 1] * 587 + src[o + 2] * 114 + 500) / 1000);
        });
        return g;
    }

    /// <summary>Resamples to exactly <paramref name="width"/> x <paramref name="height"/>: an integer box
    /// filter down to within 2x of the target (so a big shrink does not alias), then bilinear.</summary>
    public RgbImage Resize(int width, int height)
    {
        if (width == Width && height == Height) return this;
        int factor = Math.Max(1, Math.Min(Width / width, Height / height));
        RgbImage s = Downscale(factor);
        var dst = new RgbImage(width, height);
        double kx = (double)s.Width / width, ky = (double)s.Height / height;
        Parallel.For(0, height, ParallelScope.Options, y =>
        {
            double sy = (y + 0.5) * ky - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sy), 0, s.Height - 1), y1 = Math.Min(y0 + 1, s.Height - 1);
            double fy = Math.Clamp(sy - y0, 0, 1);
            for (int x = 0; x < width; x++)
            {
                double sx = (x + 0.5) * kx - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sx), 0, s.Width - 1), x1 = Math.Min(x0 + 1, s.Width - 1);
                double fx = Math.Clamp(sx - x0, 0, 1);
                for (int c = 0; c < 3; c++)
                {
                    double top = s.Data[(y0 * s.Width + x0) * 3 + c] * (1 - fx) + s.Data[(y0 * s.Width + x1) * 3 + c] * fx;
                    double bot = s.Data[(y1 * s.Width + x0) * 3 + c] * (1 - fx) + s.Data[(y1 * s.Width + x1) * 3 + c] * fx;
                    dst.Data[(y * width + x) * 3 + c] = (byte)(top * (1 - fy) + bot * fy + 0.5);
                }
            }
        });
        return dst;
    }

    /// <summary>Turned clockwise by <paramref name="turns"/> x 90 degrees (any integer; 0 returns this image).</summary>
    public RgbImage RotateClockwise(int turns)
    {
        turns = ((turns % 4) + 4) % 4;
        if (turns == 0) return this;
        int w = Width, h = Height;
        var dst = turns == 2 ? new RgbImage(w, h) : new RgbImage(h, w);
        int dw = dst.Width;
        byte[] s = Data, d = dst.Data;
        Parallel.For(0, dst.Height, ParallelScope.Options, y =>
        {
            for (int x = 0; x < dw; x++)
            {
                // Source pixel of destination (x, y).
                (int sx, int sy) = turns switch
                {
                    1 => (y, h - 1 - x),         // clockwise: (x, y) -> (h - 1 - y, x)
                    2 => (w - 1 - x, h - 1 - y),
                    _ => (w - 1 - y, x),         // counter-clockwise: (x, y) -> (y, w - 1 - x)
                };
                int o = (y * dw + x) * 3, i = (sy * w + sx) * 3;
                d[o] = s[i];
                d[o + 1] = s[i + 1];
                d[o + 2] = s[i + 2];
            }
        });
        return dst;
    }

    /// <summary>Box-filter downscale by an integer factor (factor 1 returns this image).</summary>
    public RgbImage Downscale(int factor)
    {
        if (factor <= 1) return this;
        int w = Math.Max(1, Width / factor), h = Math.Max(1, Height / factor);
        var dst = new RgbImage(w, h);
        int area = factor * factor;
        Parallel.For(0, h, ParallelScope.Options, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int r = 0, g = 0, b = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int o = ((y * factor + dy) * Width + x * factor) * 3;
                    for (int dx = 0; dx < factor; dx++, o += 3)
                    {
                        r += Data[o];
                        g += Data[o + 1];
                        b += Data[o + 2];
                    }
                }
                int d = (y * w + x) * 3;
                dst.Data[d] = (byte)(r / area);
                dst.Data[d + 1] = (byte)(g / area);
                dst.Data[d + 2] = (byte)(b / area);
            }
        });
        return dst;
    }
}
