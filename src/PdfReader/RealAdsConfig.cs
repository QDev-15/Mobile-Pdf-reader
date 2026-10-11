namespace PdfReader;

/// <summary>
/// Production ad unit IDs for whichever provider is currently wired up in <see cref="MauiProgram"/> (LevelPlay
/// right now -- see the <c>UseAdsService(new LevelPlayOptions(...))</c> call there). Placeholders until the
/// owner creates real production ad units for PdfReader; cannot reuse DocScanner's (plan section 6/8: a
/// separate ad app/account is required per app). These only take effect on a build made with
/// <c>-p:AdsMode=Pro</c> (PdfReader.csproj) -- the default "Dev" mode always uses the provider's own
/// test/sandbox ids (<see cref="LevelPlayConfig"/>), Debug or Release, APK or AAB. Even then, <see
/// cref="HasRealIds"/> must be true (both ids actually replaced) or <see cref="MauiProgram.CreateMauiApp"/>
/// still falls back to <see cref="LevelPlayConfig"/>'s ids, so a Pro build made before this is actually set up
/// never requests still-placeholder IDs.
///
/// If the active provider is ever switched away from LevelPlay (see the grouped, mostly-commented-out
/// provider block in <see cref="MauiProgram"/>), this file's two ids need to be replaced with that provider's
/// own production ad unit ids -- an AdMob switch would also need AdMob's separate <b>App ID</b>, which is NOT
/// here: see the commented-out AdMob block in PdfReader.csproj and AndroidManifest.xml.
/// </summary>
public static class RealAdsConfig
{
    public const string BannerAdUnitId = "REPLACE_ME_BANNER";
    public const string InterstitialAdUnitId = "REPLACE_ME_INTERSTITIAL";

    /// <summary>False until both ad unit IDs above have been replaced with real ones.</summary>
    public static bool HasRealIds => !BannerAdUnitId.Contains("REPLACE_ME") && !InterstitialAdUnitId.Contains("REPLACE_ME");
}
