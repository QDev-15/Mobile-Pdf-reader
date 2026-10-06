using System.Drawing;

namespace ImageCoreService;

/// <summary>
/// 8-bit luminance buffer (row-major, no padding) -- the working representation for
/// every document-cleanup algorithm here, so none of them has to care about platform
/// pixel formats (GDI+ conversions live in ImageCoreService.GdiGray). For binary images the convention is 0 = ink (black), 255 = paper (white).
/// </summary>
public sealed class GrayImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }

    public GrayImage(int width, int height, byte[]? data = null)
    {
        Width = width;
        Height = height;
        Data = data ?? new byte[width * height];
        if (Data.Length != width * height) throw new ArgumentException("Buffer size mismatch.", nameof(data));
    }

    public byte this[int x, int y]
    {
        get => Data[y * Width + x];
        set => Data[y * Width + x] = value;
    }

    /// <summary>Box-filter downscale by an integer factor (factor 1 returns a copy).</summary>
    public GrayImage Downscale(int factor)
    {
        if (factor <= 1) return new GrayImage(Width, Height, (byte[])Data.Clone());
        int w = Math.Max(1, Width / factor), h = Math.Max(1, Height / factor);
        var dst = new GrayImage(w, h);
        int area = factor * factor;
        Parallel.For(0, h, ParallelScope.Options, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int o = (y * factor + dy) * Width + x * factor;
                    for (int dx = 0; dx < factor; dx++) sum += Data[o + dx];
                }
                dst.Data[y * w + x] = (byte)(sum / area);
            }
        });
        return dst;
    }

    /// <summary>Resamples to exactly <paramref name="width"/> x <paramref name="height"/>: an integer box filter down to
    /// within 2x of the target (so a big shrink does not alias), then bilinear. Upscaling is plain bilinear. Twin of
    /// <see cref="RgbImage.Resize"/>, one channel.</summary>
    public GrayImage Resize(int width, int height)
    {
        if (width == Width && height == Height) return this;
        int factor = Math.Max(1, Math.Min(Width / Math.Max(1, width), Height / Math.Max(1, height)));
        GrayImage s = Downscale(factor);
        var dst = new GrayImage(width, height);
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
                double top = s.Data[y0 * s.Width + x0] * (1 - fx) + s.Data[y0 * s.Width + x1] * fx;
                double bot = s.Data[y1 * s.Width + x0] * (1 - fx) + s.Data[y1 * s.Width + x1] * fx;
                dst.Data[y * width + x] = (byte)(top * (1 - fy) + bot * fy + 0.5);
            }
        });
        return dst;
    }

    /// <summary>Turned clockwise by <paramref name="turns"/> x 90 degrees (any integer; 0 returns this image).</summary>
    public GrayImage RotateClockwise(int turns)
    {
        turns = ((turns % 4) + 4) % 4;
        if (turns == 0) return this;
        (int w, int h) = turns == 2 ? (Width, Height) : (Height, Width);
        return new GrayImage(w, h, Rotation.Turn(Data, Width, Height, turns));
    }
    public GrayImage Crop(Rectangle r)
    {
        var dst = new GrayImage(r.Width, r.Height);
        for (int y = 0; y < r.Height; y++)
            Array.Copy(Data, (r.Y + y) * Width + r.X, dst.Data, y * r.Width, r.Width);
        return dst;
    }
}
