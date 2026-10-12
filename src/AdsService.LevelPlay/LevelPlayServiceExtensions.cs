using AdsService.Internal;
using Microsoft.Maui.Hosting;

namespace AdsService;

/// <summary>The one call a consuming app makes to use Unity LevelPlay (ironSource) for ads:
/// <c>builder.UseLevelPlayAds(new LevelPlayOptions(...))</c>. Registers the LevelPlay provider, the generic
/// <see cref="IAdsClient"/> facade (AdsService.Core), and the shared <c>AdBannerSurface</c> handler. Nothing
/// else in the app needs to know LevelPlay is the network in use.</summary>
public static class LevelPlayServiceExtensions
{
    public static MauiAppBuilder UseLevelPlayAds(this MauiAppBuilder builder, LevelPlayOptions options) =>
        builder.UseAdsCore(options.IsAdsEnabled, _ => new UnityLevelPlayProvider(options).Initialize(Android.App.Application.Context));
}
