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
/// whole point of this library.
///
/// When <see cref="AdsProviderOptions.IsAdsEnabled"/> is false the real provider is NOT registered (so its SDK is
/// never initialised and nothing can load an ad); a no-op <see cref="DisabledAdProvider"/> stands in, and
/// everything else in the app (banner surface, app-level services) keeps working unchanged.</summary>
public static class AdsServiceBuilderExtensions
{
    public static MauiAppBuilder UseAdsService(this MauiAppBuilder builder, AdsProviderOptions options)
    {
        // The provider is a FACTORY, invoked lazily by AdsClient the first time ads are actually allowed -- never at startup
        // for a Pro user (see AdsClient). With the master switch off the factory is a no-op stand-in that never loads anything.
        Func<IServiceProvider, IAdProvider> providerFactory = options.IsAdsEnabled
            ? CreateProviderFactory(builder, options)
            : _ => new DisabledAdProvider();

        builder.Services.AddSingleton(sp => new AdsClient(() => providerFactory(sp), options.IsAdsEnabled));
        builder.Services.AddSingleton<IAdsClient>(sp => sp.GetRequiredService<AdsClient>());
        builder.ConfigureMauiHandlers(handlers => handlers.AddHandler<AdBannerSurface, AdBannerSurfaceHandler>());
        return builder;
    }

    private static Func<IServiceProvider, IAdProvider> CreateProviderFactory(MauiAppBuilder builder, AdsProviderOptions options)
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
                return sp => new AdMobProvider(adMob, sp.GetRequiredService<IInterstitialAdService>()).Initialize();

            case AppLovinOptions appLovin:
                return _ => new AppLovinProvider(appLovin).Initialize(Android.App.Application.Context);

            case LevelPlayOptions levelPlay:
                return _ => new UnityLevelPlayProvider(levelPlay).Initialize(Android.App.Application.Context);

            default:
                throw new NotSupportedException($"No IAdProvider registered for {options.GetType()}.");
        }
    }
}
