namespace AdsService;

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
) : AdsProviderOptions;
