namespace PdfReader.Core;

/// <summary>
/// DPI / JPEG quality presets for exporting an edited page (highlight/draw/signature baked onto the
/// original page bitmap) back into a PDF. Ported from DocScanner.Core.PdfQuality (Mobile-doc-scanner
/// repo): same three presets, same encode targets. The encode step itself (Bitmap.Compress /
/// SkiaSharp instead of GDI+) is written fresh in the Android project when export (Tier 1) is built.
/// </summary>
public sealed record PdfQuality(string Key, string Label, int Dpi, int JpegQuality)
{
    public static readonly PdfQuality Small = new("small", "Nhỏ · gửi Zalo, email (150 DPI)", 150, 60);
    public static readonly PdfQuality Medium = new("medium", "Vừa · khuyên dùng (200 DPI)", 200, 72);
    public static readonly PdfQuality High = new("high", "Cao · in ấn (300 DPI)", 300, 90);

    public static IReadOnlyList<PdfQuality> All { get; } = [Small, Medium, High];

    public static PdfQuality FromKey(string? key) => All.FirstOrDefault(q => q.Key == key) ?? Medium;

    /// <summary>Long edge in pixels for an A4 page re-encoded at this resolution (11.69 in long side).</summary>
    public int LongEdgePx => (int)Math.Round(11.69 * Dpi);
}
