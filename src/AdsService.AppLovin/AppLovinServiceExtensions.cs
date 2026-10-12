using AdsService.Internal;
using Microsoft.Maui.Hosting;

namespace AdsService;

/// <summary>The one call a consuming app makes to use AppLovin MAX for ads: <c>builder.UseAppLovinAds(new
/// AppLovinOptions(...))</c>. Registers the AppLovin provider, the generic <see cref="IAdsClient"/> facade
/// (AdsService.Core), and the shared <c>AdBannerSurface</c> handler. Nothing else in the app needs to know
/// AppLovin is the network in use.</summary>
public static class AppLovinServiceExtensions
{
    public static MauiAppBuilder UseAppLovinAds(this MauiAppBuilder builder, AppLovinOptions options) =>
        builder.UseAdsCore(options.IsAdsEnabled, _ => new AppLovinProvider(options).Initialize(Android.App.Application.Context));
}
