namespace ImageCoreService;

/// <summary>
/// Brightness / contrast of a page as one linear map per channel: <c>out = in * Scale + Offset</c>, clamped to 0..255.
/// Being linear, it is exactly what a GPU color matrix does, so a screen can show a slider's effect live (Android
/// ColorMatrix on the displayed picture) and the saved page gets the same numbers applied to its pixels
/// (<see cref="Apply(RgbImage)"/>). Contrast pivots on mid-gray (128), so it does not also brighten or darken the page.
/// </summary>
/// <param name="Brightness">-100..100 (0 = unchanged): shifts every level by up to +/-128.</param>
/// <param name="Contrast">-100..100 (0 = unchanged): multiplies the distance from mid-gray by 2^(Contrast / 70),
/// about x0.37 .. x2.7.</param>
public readonly record struct ToneAdjust(int Brightness, int Contrast)
{
    public static readonly ToneAdjust None = new(0, 0);

    public bool IsNeutral => Brightness == 0 && Contrast == 0;

    public float Scale => MathF.Pow(2f, Math.Clamp(Contrast, -100, 100) / 70f);

    /// <summary>In 0..255 levels (the unit of Android's ColorMatrix translation column).</summary>
    public float Offset => 128f * (1f - Scale) + BrightnessLevels;

    /// <summary>The brightness part alone, in gray levels (+/-128): what black and white uses (it shifts the threshold).</summary>
    public float BrightnessLevels => Math.Clamp(Brightness, -100, 100) * 1.28f;

    /// <summary>The 4x5 row-major color matrix (R, G, B scaled and offset, alpha kept) for Android's ColorMatrix.</summary>
    public float[] ColorMatrix()
    {
        float s = Scale, o = Offset;
        return
        [
            s, 0, 0, 0, o,
            0, s, 0, 0, o,
            0, 0, s, 0, o,
            0, 0, 0, 1, 0,
        ];
    }

    /// <summary>Output level of every input level.</summary>
    public byte[] Lut()
    {
        var lut = new byte[256];
        float s = Scale, o = Offset;
        for (int v = 0; v < 256; v++) lut[v] = (byte)Math.Clamp((int)MathF.Round(v * s + o), 0, 255);
        return lut;
    }

    /// <summary>Applies the map in place (no-op when neutral).</summary>
    public void Apply(RgbImage image) => Apply(image.Data, image.Width * 3, image.Height);

    /// <summary>Applies the map in place (no-op when neutral).</summary>
    public void Apply(GrayImage image) => Apply(image.Data, image.Width, image.Height);

    private void Apply(byte[] data, int rowBytes, int rows)
    {
        if (IsNeutral) return;
        byte[] lut = Lut();
        Parallel.For(0, rows, ParallelScope.Options, y =>
        {
            for (int i = y * rowBytes, end = i + rowBytes; i < end; i++) data[i] = lut[data[i]];
        });
    }
}
