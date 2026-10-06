namespace PdfReader;

/// <summary>
/// AdMob ad unit IDs the app uses. These are placeholders (Google's published TEST ids) until the owner
/// creates a real AdMob app + ad units for PdfReader -- cannot reuse DocScanner's (plan section 6/8: a
/// separate AdMob app is required per app). <see cref="HasRealIds"/> is false until both are replaced,
/// which keeps <see cref="MauiProgram.CreateMauiApp"/> forcing test ads even in a Release build, so a
/// build made before AdMob is set up never requests still-placeholder IDs. The AdMob <b>App ID</b> (a
/// different, separate id) is NOT here: it lives in PdfReader.csproj's AndroidManifestPlaceholders.
/// </summary>
public static class AdsConfig
{
    public const string BannerAdUnitId = "REPLACE_ME_BANNER";
    public const string InterstitialAdUnitId = "REPLACE_ME_INTERSTITIAL";

    /// <summary>False until both ad unit IDs above have been replaced with real ones.</summary>
    public static bool HasRealIds => !BannerAdUnitId.Contains("REPLACE_ME") && !InterstitialAdUnitId.Contains("REPLACE_ME");
}
