using AdsService.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Hosting;
using Plugin.AdMob;
using Plugin.AdMob.Services;

namespace AdsService;

/// <summary>The one call a consuming app makes: <c>builder.UseAdsService(options)</c> with the
/// <see cref="AdsProviderOptions"/> for whichever network it wants (<see cref="AdMobOptions"/> /
/// <see cref="AppLovinOptions"/> / <see cref="LevelPlayOptions"/>) -- registers the matching provider, the
/// generic <see cref="IAdsClient"/> facade, and the <see cref="AdBannerSurface"/> handler. Nothing else in a
/// consuming app needs to know which provider is active, or that one is swappable for another, which is the
/// whole point of this library.</summary>
public static class AdsServiceBuilderExtensions
{
    public static MauiAppBuilder UseAdsService(this MauiAppBuilder builder, AdsProviderOptions options)
    {
        switch (options)
        {
            case AdMobOptions adMob:
                // Plugin.AdMob's own extension: native Google Mobile Ads SDK init, UMP consent flow, and DI
                // registration of IInterstitialAdService / IAdConsentService (still used directly by this app's
                // Settings page for the "Privacy options" button -- orthogonal to which provider serves ads).
                builder.UseAdMob(androidDefaultBannerAdUnitId: adMob.BannerAdUnitId, androidDefaultInterstitialAdUnitId: adMob.InterstitialAdUnitId);
                // Fully qualified: Microsoft.Maui.Controls.Compatibility (referenced for ViewHandler<,>, see the
                // .csproj) also declares a generic type simply named "Configuration<,>", which an unqualified
                // "Configuration" here resolves to instead of Plugin.AdMob's (CS0305).
                Plugin.AdMob.Configuration.AdConfig.UseTestAdUnitIds = adMob.UseTestAds;
                builder.Services.AddSingleton<IAdProvider>(sp =>
                    new AdMobProvider(adMob, sp.GetRequiredService<IInterstitialAdService>()).Initialize());
                break;

            case AppLovinOptions appLovin:
                builder.Services.AddSingleton<IAdProvider>(_ =>
                    new AppLovinProvider(appLovin).Initialize(Android.App.Application.Context));
                break;

            case LevelPlayOptions levelPlay:
                builder.Services.AddSingleton<IAdProvider>(_ =>
                    new UnityLevelPlayProvider(levelPlay).Initialize(Android.App.Application.Context));
                break;

            default:
                throw new NotSupportedException($"No IAdProvider registered for {options.GetType()}.");
        }

        builder.Services.AddSingleton<IAdsClient>(sp => new AdsClient(sp.GetRequiredService<IAdProvider>()));
        builder.ConfigureMauiHandlers(handlers => handlers.AddHandler<AdBannerSurface, AdBannerSurfaceHandler>());
        return builder;
    }
}
