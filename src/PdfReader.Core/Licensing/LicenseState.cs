namespace PdfReader.Core.Licensing;

/// <summary>
/// Whether the paid unlock ("Pro") has been bought. Unlike DocScanner there is no free-use trial counter
/// here -- PdfReader's monetization is simply "ads unless Pro" (plan section 6), so this stays a single
/// flag. Pure data (no Play Billing, no storage): fully unit-testable.
/// </summary>
public sealed record LicenseState(bool IsPro)
{
    public static readonly LicenseState Initial = new(false);

    /// <summary>Short line for the Settings screen.</summary>
    public string SummaryText => IsPro ? "Đã nâng cấp Pro (không quảng cáo)" : "Đang dùng bản miễn phí (có quảng cáo)";
}
