namespace AdsService;

/// <summary>Which ad network a given <see cref="AdsProviderOptions"/> configures. Used internally by
/// <c>UseAdsService</c> to pick the matching <c>IAdProvider</c> -- consumers never branch on this themselves,
/// they just construct the options record for whichever network they want.</summary>
public enum AdProviderKind
{
    AdMob,
    AppLovin,
    UnityLevelPlay,
}

/// <summary>Base for a provider's connection details (ad unit IDs, keys, test-mode flag...). Exactly one of
/// these is passed to <c>MauiAppBuilder.UseAdsService</c>; switching ad networks is changing which subclass is
/// constructed there, nothing else in the app.</summary>
public abstract record AdsProviderOptions
{
    public abstract AdProviderKind Kind { get; }
}

/// <param name="BannerAdUnitId">AdMob banner ad unit ID (the final <c>BannerId</c>/<c>InterstitialId</c> the
/// caller resolved -- test vs real is the caller's decision, same as today's <c>AdsConfig</c>).</param>
/// <param name="InterstitialAdUnitId">AdMob interstitial ad unit ID.</param>
/// <param name="UseTestAds">Mirrors <c>Plugin.AdMob.Configuration.AdConfig.UseTestAdUnitIds</c>.</param>
/// <param name="TestDeviceIds">Hashed device IDs that always get test creatives even with real ad unit IDs.</param>
public sealed record AdMobOptions(
    string BannerAdUnitId,
    string InterstitialAdUnitId,
    bool UseTestAds,
    string[]? TestDeviceIds = null
) : AdsProviderOptions
{
    public override AdProviderKind Kind => AdProviderKind.AdMob;
}

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
) : AdsProviderOptions
{
    public override AdProviderKind Kind => AdProviderKind.AppLovin;
}

/// <param name="AppKey">Unity LevelPlay app key, from the LevelPlay dashboard (the SDK's own term; equivalent
/// role to AppLovin's SdkKey / AdMob's App ID). One key per app.</param>
/// <param name="BannerAdUnitId">LevelPlay banner ad unit ID.</param>
/// <param name="InterstitialAdUnitId">LevelPlay interstitial ad unit ID.</param>
/// <param name="TestMode">Turns on adapter debug logging (<c>LevelPlay.setAdaptersDebug</c>) for this
/// device/build. Like AppLovin, LevelPlay has no separate "test ad unit ID" concept -- test creatives come from
/// marking the device/ad unit as test in the dashboard, not a different ID here.</param>
public sealed record LevelPlayOptions(
    string AppKey,
    string BannerAdUnitId,
    string InterstitialAdUnitId,
    bool TestMode = false
) : AdsProviderOptions
{
    public override AdProviderKind Kind => AdProviderKind.UnityLevelPlay;
}
