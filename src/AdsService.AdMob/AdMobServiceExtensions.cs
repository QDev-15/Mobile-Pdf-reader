using AdsService.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Hosting;
using Plugin.AdMob;
using Plugin.AdMob.Services;

namespace AdsService;

/// <summary>The one call a consuming app makes to use AdMob for ads: <c>builder.UseAdMobAds(new
/// AdMobOptions(...))</c>. Registers the AdMob provider, the generic <see cref="IAdsClient"/> facade
/// (AdsService.Core), and the shared <c>AdBannerSurface</c> handler. Nothing else in the app needs to know
/// AdMob is the network in use.</summary>
public static class AdMobServiceExtensions
{
    public static MauiAppBuilder UseAdMobAds(this MauiAppBuilder builder, AdMobOptions options)
    {
        // Plugin.AdMob's own extension: native Google Mobile Ads SDK init, UMP consent flow, and DI
        // registration of IInterstitialAdService / IAdConsentService (still used directly by an app's own
        // Settings page for a "Privacy options" button -- orthogonal to which provider serves ads).
        builder.UseAdMob(androidDefaultBannerAdUnitId: options.BannerAdUnitId, androidDefaultInterstitialAdUnitId: options.InterstitialAdUnitId);
        // Fully qualified: Microsoft.Maui.Controls.Compatibility (referenced by AdsService.Core, for
        // ViewHandler<,>) also declares a generic type simply named "Configuration<,>", which an unqualified
        // "Configuration" here resolves to instead of Plugin.AdMob's (CS0305).
        Plugin.AdMob.Configuration.AdConfig.UseTestAdUnitIds = options.UseTestAds;
        return builder.UseAdsCore(options.IsAdsEnabled, sp => new AdMobProvider(options, sp.GetRequiredService<IInterstitialAdService>()).Initialize());
    }
}
