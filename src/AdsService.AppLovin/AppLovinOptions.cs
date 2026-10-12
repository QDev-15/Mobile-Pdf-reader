namespace AdsService;

/// <param name="SdkKey">AppLovin SDK key, from the AppLovin dashboard (Account → Keys). One key per app.</param>
/// <param name="BannerAdUnitId">MAX banner ad unit ID.</param>
/// <param name="InterstitialAdUnitId">MAX interstitial ad unit ID.</param>
/// <param name="TestMode">Enables AppLovin's own test-ad mode (<c>AppLovinSdkSettings.TestDeviceAdvertisingIds</c> /
/// <c>IsVerboseLogging</c>) for this device/build -- there is no separate "test ad unit ID" concept like AdMob's,
/// AppLovin instead serves test creatives on ad units registered as "Test Mode" in the dashboard, or on devices
/// listed as test devices. Mirrors <see cref="AdMobOptions.UseTestAds"/>'s role (Debug: true; Release: false
/// unless the real IDs are still placeholders) so callers can reuse the exact same decision they already make
/// for AdMob.</param>
public sealed record AppLovinOptions(
    string SdkKey,
    string BannerAdUnitId,
    string InterstitialAdUnitId,
    bool TestMode = false
) : AdsProviderOptions;
