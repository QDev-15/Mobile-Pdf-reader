using View = Android.Views.View;

namespace AdsService.Internal;

/// <summary>What each ad network's wrapper (<c>AdMobProvider</c>, <c>AppLovinProvider</c>) must implement.
/// <c>AdsClient</c> is the single <see cref="IAdsClient"/> exposed to the app; it just forwards to whichever
/// <see cref="IAdProvider"/> was constructed for the <see cref="AdsProviderOptions"/> passed to
/// <c>UseAdsService</c>. Internal: consumers of this library only ever see <see cref="IAdsClient"/>.</summary>
internal interface IAdProvider
{
    /// <summary>True once the banner has a creative on screen; drives <see cref="IAdsClient.IsBannerLoaded"/>.</summary>
    bool IsBannerLoaded { get; }

    /// <summary>Raised whenever <see cref="IsBannerLoaded"/> changes.</summary>
    event Action? BannerStateChanged;

    /// <summary>Creates (or returns the already-shared) native banner view for this provider, parented into
    /// whichever page's <c>AdBannerSurfaceHandler</c> asked for it first -- mirrors the single shared
    /// <c>AdView</c> the app already used for AdMob, now generalised: one native ad view for the whole app's
    /// lifetime regardless of provider, reparented as pages come and go.</summary>
    View CreateBannerView(Android.Content.Context context);

    bool IsInterstitialReady { get; }

    void PrepareInterstitial();

    void ShowInterstitialIfReady();
}
