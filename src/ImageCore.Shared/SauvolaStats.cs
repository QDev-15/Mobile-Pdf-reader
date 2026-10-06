namespace ImageCoreService;

/// <summary>Per-pixel window mean and standard deviation of a gray image (<see cref="Binarizer.Stats"/>).</summary>
public sealed class SauvolaStats(int width, int height, float[]? mean = null, float[]? deviation = null)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public float[] Mean { get; } = mean ?? new float[width * height];
    public float[] Deviation { get; } = deviation ?? new float[width * height];

    /// <summary>Turned like the image they describe. The window is square, so the statistics of a turned image are
    /// exactly the turned statistics: turning costs a copy, not a new scan.</summary>
    public SauvolaStats RotateClockwise(int turns)
    {
        turns = ((turns % 4) + 4) % 4;
        if (turns == 0) return this;
        (int w, int h) = turns == 2 ? (Width, Height) : (Height, Width);
        return new SauvolaStats(w, h,
            Rotation.Turn(Mean, Width, Height, turns), Rotation.Turn(Deviation, Width, Height, turns));
    }
}

/// <summary>Quarter turns of row-major single-channel buffers.</summary>
internal static class Rotation
{
    /// <summary><paramref name="src"/> (w x h) turned clockwise <paramref name="turns"/> (1..3) quarter turns.</summary>
    public static T[] Turn<T>(T[] src, int w, int h, int turns)
    {
        var dst = new T[src.Length];
        int dw = turns == 2 ? w : h, dh = turns == 2 ? h : w;
        Parallel.For(0, dh, ParallelScope.Options, y =>
        {
            int o = y * dw;
            switch (turns)
            {
                case 1: for (int x = 0; x < dw; x++) dst[o + x] = src[(h - 1 - x) * w + y]; break;             // clockwise
                case 2: for (int x = 0; x < dw; x++) dst[o + x] = src[(h - 1 - y) * w + (w - 1 - x)]; break;
                default: for (int x = 0; x < dw; x++) dst[o + x] = src[x * w + (w - 1 - y)]; break;           // counter-clockwise
            }
        });
        return dst;
    }
}
