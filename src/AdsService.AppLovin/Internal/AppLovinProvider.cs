using Com.Applovin.Mediation;
using Com.Applovin.Mediation.Ads;
using Com.Applovin.Sdk;
using View = Android.Views.View;
using ViewGroup = Android.Views.ViewGroup;

namespace AdsService.Internal;

/// <summary>AppLovin MAX wrapper. The Java API surface used here was read directly off the real
/// <c>applovin-sdk-13.6.4.aar</c> bytecode with <c>javap</c> (2026-10-07), the same discipline the app's own
/// history already used for Play In-App Update / native AdMob AdView: <c>AppLovinSdk.getInstance(Context)</c>,
/// <c>.initialize(AppLovinSdkInitializationConfiguration, SdkInitializationListener)</c> (NOT "initializeSdk"),
/// <c>MaxAdView(String, MaxAdFormat)</c> / <c>.setListener(MaxAdViewAdListener)</c> / <c>.loadAd()</c>,
/// <c>MaxInterstitialAd(String)</c> / <c>.setListener(MaxAdListener)</c> / <c>.isReady</c> / <c>.showAd()</c>.
/// <c>MaxAd</c>/<c>MaxError</c> (and therefore <c>MaxAdListener</c>) needed a Metadata.xml fix to auto-bind at
/// all (see <c>Transforms/Metadata.xml</c>) -- not yet build-verified end to end in this session (blocked by a
/// Windows file lock on the generated-bindings folder that would not clear); the exact C# nested-type names
/// below (builder / listener interfaces) follow this project's otherwise consistent binding convention but are
/// the one part of this file that needs a build pass to confirm before relying on it.</summary>
internal sealed class AppLovinProvider(AppLovinOptions options) : IAdProvider
{
    private MaxAdView? _shared;
    private MaxInterstitialAd? _interstitial;

    public bool IsBannerLoaded { get; private set; }

    public event Action? BannerStateChanged;

    public bool IsInterstitialReady => _interstitial?.IsReady ?? false;

    public AppLovinProvider Initialize(Android.Content.Context context)
    {
        AppLovinSdk sdk = AppLovinSdk.GetInstance(context);
        sdk.Settings?.SetVerboseLogging(options.TestMode);

        IAppLovinSdkInitializationConfiguration config = AppLovinSdkInitializationConfiguration
            .Builder(options.SdkKey, context)!
            .Build()!;
        sdk.Initialize(config, new InitListener(this, context));
        return this;
    }

    public View CreateBannerView(Android.Content.Context context)
    {
        if (_shared is { } existing)
        {
            // Already shown on another (still-alive, e.g. back-stack) page: an Android View can only have one
            // parent, so take it back before this page's own container tries to add it.
            (existing.Parent as ViewGroup)?.RemoveView(existing);
            return existing;
        }

        _shared = new MaxAdView(options.BannerAdUnitId, MaxAdFormat.Banner!);
        _shared.SetListener(new BannerListener(this));
        _shared.LoadAd();
        return _shared;
    }

    public void PrepareInterstitial()
    {
        _interstitial ??= CreateInterstitial();
        _interstitial.LoadAd();
    }

    public void ShowInterstitialIfReady()
    {
        if (_interstitial is { IsReady: true } ad) ad.ShowAd();
    }

    private MaxInterstitialAd CreateInterstitial()
    {
        var ad = new MaxInterstitialAd(options.InterstitialAdUnitId);
        ad.SetListener(new InterstitialListener());
        return ad;
    }

    private void SetBannerLoaded(bool loaded)
    {
        if (IsBannerLoaded == loaded) return;
        IsBannerLoaded = loaded;
        BannerStateChanged?.Invoke();
    }

    private sealed class InitListener(AppLovinProvider owner, Android.Content.Context context)
        : Java.Lang.Object, AppLovinSdk.ISdkInitializationListener
    {
        public void OnSdkInitialized(IAppLovinSdkConfiguration? configuration)
        {
            // Preload is implicit: the first CreateBannerView / PrepareInterstitial call after this point
            // creates+loads normally; nothing to kick off here beyond having initialized the SDK.
        }
    }

    /// <summary>MaxAdListener's callbacks (shared by banner and interstitial) -- only onAdLoaded/onAdLoadFailed
    /// matter for <see cref="IAdsClient.IsBannerLoaded"/>; the rest are no-ops here (display/click/hidden are
    /// not surfaced by this generic provider, same scope as the app's former AdMob-only wrapper).</summary>
    private sealed class BannerListener(AppLovinProvider owner) : Java.Lang.Object, IMaxAdViewAdListener
    {
        public void OnAdLoaded(IMaxAd? ad) => owner.SetBannerLoaded(true);
        public void OnAdLoadFailed(string? adUnitId, IMaxError? error) => owner.SetBannerLoaded(false);
        public void OnAdDisplayed(IMaxAd? ad) { }
        public void OnAdDisplayFailed(IMaxAd? ad, IMaxError? error) { }
        public void OnAdClicked(IMaxAd? ad) { }
        public void OnAdHidden(IMaxAd? ad) { }
        public void OnAdExpanded(IMaxAd? ad) { }
        public void OnAdCollapsed(IMaxAd? ad) { }
    }

    private sealed class InterstitialListener : Java.Lang.Object, IMaxAdListener
    {
        public void OnAdLoaded(IMaxAd? ad) { }
        public void OnAdLoadFailed(string? adUnitId, IMaxError? error) { }
        public void OnAdDisplayed(IMaxAd? ad) { }
        public void OnAdDisplayFailed(IMaxAd? ad, IMaxError? error) { }
        public void OnAdClicked(IMaxAd? ad) { }
        public void OnAdHidden(IMaxAd? ad) { }
    }
}
