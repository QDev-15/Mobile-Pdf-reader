using System.ComponentModel;

namespace ImageCoreService;

/// <summary>How a scanned page is kept.</summary>
public enum PageColorMode
{
    /// <summary>The photo's colors, straightened only.</summary>
    [Description("Màu")] Color,
    /// <summary>Grayscale with the background flattened (shadows / yellow paper removed).</summary>
    [Description("Xám")] Gray,
    /// <summary>Pure black and white (adaptive threshold): smallest files, crisp text.</summary>
    [Description("Đen trắng")] BlackWhite,
}

/// <param name="Darkness">0..100, 50 = default. Higher keeps fainter strokes (and more noise) in black and white.</param>
/// <param name="CleanBackground">Flatten shadows / yellowed paper to even white (gray and black-and-white modes).</param>
public sealed record FilterOptions(
    PageColorMode Mode,
    int Darkness = FilterOptions.DefaultDarkness,
    bool CleanBackground = true,
    bool Despeckle = true,
    BinarizationMethod Method = BinarizationMethod.Sauvola,
    bool Sharpen = true,
    bool Smooth = true)
{
    public const int DefaultDarkness = 50;

    /// <summary>Brightness / contrast: color and gray pages use both; black and white uses the brightness only (a lighter
    /// or darker page before the threshold, next to <see cref="Darkness"/>).</summary>
    public ToneAdjust Tone { get; init; }

    /// <summary>Whether two settings give the same picture: darkness only matters in black and white, background
    /// cleaning only outside color, brightness / contrast only outside black and white.</summary>
    public static bool SameLook(FilterOptions a, FilterOptions b) =>
        a.Mode == b.Mode
        && (a.Mode != PageColorMode.BlackWhite || a.Darkness == b.Darkness)
        && (a.Mode == PageColorMode.Color || a.CleanBackground == b.CleanBackground)
        // black and white uses brightness only (it moves the threshold); the others use brightness and contrast
        && (a.Mode == PageColorMode.BlackWhite ? a.Tone.Brightness == b.Tone.Brightness : a.Tone == b.Tone);
}

/// <summary>A filtered page: either color (<see cref="Color"/>) or gray / black-and-white (<see cref="Gray"/>).</summary>
public sealed class FilteredPage
{
    public RgbImage? Color { get; init; }
    public GrayImage? Gray { get; init; }
    /// <summary>True when <see cref="Gray"/> holds only 0 and 255.</summary>
    public bool IsBilevel { get; init; }
    /// <summary>A black-and-white page (bilevel, or with anti-aliased stroke edges: <see cref="FilterOptions.Smooth"/>).</summary>
    public bool IsBlackWhite { get; init; }
    public int Width => Color?.Width ?? Gray!.Width;
    public int Height => Color?.Height ?? Gray!.Height;
}

/// <summary>
/// Turns a straightened page photo into its final look. Runs on the in-memory warp result, so the
/// page is compressed exactly once, when it is saved.
/// </summary>
public static class DocumentFilter
{
    /// <summary>Sauvola k for a darkness setting: 50 -> 0.34 (the tuned default), 0 -> 0.55 (thin, clean),
    /// 100 -> 0.13 (bold, keeps faint pencil).</summary>
    public static double SauvolaKFor(int darkness) => 0.55 - 0.0042 * Math.Clamp(darkness, 0, 100);

    /// <summary>Otsu threshold shift for a darkness setting (+/- 40 levels).</summary>
    public static int OtsuOffsetFor(int darkness) => (int)Math.Round((Math.Clamp(darkness, 0, 100) - 50) * 0.8);

    /// <summary>Anti-aliasing of a smooth black-and-white page: gray levels either side of the threshold that get a
    /// partial gray (<see cref="Binarizer.Shade"/>). About a third of a pixel of soft edge on a phone photo.</summary>
    public const double SmoothRamp = 12;

    /// <summary>The page before the threshold: gray, background flattened, sharpened (<see cref="ImageCoreService.Sharpen"/>).
    /// Always a new image (the caller may keep it).</summary>
    public static GrayImage BlackWhiteSource(RgbImage page, FilterOptions o, int dpi)
    {
        GrayImage gray = page.ToGray();
        if (o.CleanBackground) gray = BackgroundFlattener.Flatten(gray);
        if (o.Sharpen)
        {
            ImageCoreService.Sharpen.UnsharpInPlace(gray, ImageCoreService.Sharpen.RadiusFor(dpi), ImageCoreService.Sharpen.DefaultAmount);
        }
        return gray;
    }

    /// <summary>Despeckle for a black-and-white page that may have anti-aliased edges: the specks are found on its
    /// black / white version (darker than 128) and whitened in the page.</summary>
    public static void Despeckle(GrayImage bw, int dpi)
    {
        int area = DocumentCleanup.DefaultSpeckleArea(dpi);
        var hard = new GrayImage(bw.Width, bw.Height);
        byte[] d = bw.Data, m = hard.Data;
        bool soft = false;
        for (int i = 0; i < d.Length; i++)
        {
            m[i] = d[i] < 128 ? (byte)0 : (byte)255;
            if (d[i] is not (0 or 255)) soft = true;
        }
        if (!soft)
        {
            DocumentCleanup.Despeckle(bw, area);
            return;
        }
        DocumentCleanup.Despeckle(hard, area);
        for (int i = 0; i < d.Length; i++)
            if (m[i] == 255 && d[i] < 128) d[i] = 255;
    }

    /// <param name="dpi">Resolution of the page (sets the Sauvola window and the speck size).</param>
    public static FilteredPage Apply(RgbImage page, FilterOptions o, int dpi)
    {
        switch (o.Mode)
        {
            case PageColorMode.Color:
                o.Tone.Apply(page); // in place: the page is the fresh warp result
                return new FilteredPage { Color = page }; // the photo's own colors (cleaning measured no size gain on real pages)

            case PageColorMode.Gray:
            {
                GrayImage gray = page.ToGray();
                if (o.CleanBackground) gray = BackgroundFlattener.Flatten(gray);
                o.Tone.Apply(gray);
                return new FilteredPage { Gray = gray };
            }

            default:
            {
                GrayImage gray = BlackWhiteSource(page, o, dpi);
                bool smooth = o.Smooth && o.Method == BinarizationMethod.Sauvola;
                GrayImage bin = o.Method == BinarizationMethod.Otsu
                    ? Binarizer.Threshold(gray, Math.Clamp(Binarizer.OtsuThreshold(gray) + OtsuOffsetFor(o.Darkness) - (int)MathF.Round(o.Tone.BrightnessLevels), 1, 254))
                    : Binarizer.Sauvola(gray, Binarizer.DefaultWindow(dpi), SauvolaKFor(o.Darkness), o.Tone.BrightnessLevels, smooth ? SmoothRamp : 0);
                if (o.Despeckle) Despeckle(bin, dpi);
                return new FilteredPage { Gray = bin, IsBilevel = !smooth, IsBlackWhite = true };
            }
        }
    }
}
