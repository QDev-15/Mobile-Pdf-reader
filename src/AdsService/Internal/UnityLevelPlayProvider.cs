using Com.Unity3d.Mediation;
using Com.Unity3d.Mediation.Banner;
using Com.Unity3d.Mediation.Interstitial;
using Microsoft.Maui.ApplicationModel;
using View = Android.Views.View;
using ViewGroup = Android.Views.ViewGroup;

namespace AdsService.Internal;

/// <summary>Unity LevelPlay (ironSource) wrapper, built 2026-10-08 the same way as <see cref="AppLovinProvider"/>:
/// the Java API below was read directly off the real <c>mediation-sdk-9.6.1.aar</c> bytecode with <c>javap</c>,
/// not guessed. Uses the modern "LevelPlay" API (<c>com.unity3d.mediation.*</c>), not the older static
/// <c>com.ironsource.mediationsdk.IronSource</c> facade which the same AAR also ships: <c>LevelPlay.Init(context,
/// request, listener)</c>, <c>LevelPlayBannerAdView(context, adUnitId).SetBannerListener(...)</c>,
/// <c>LevelPlayInterstitialAd(adUnitId).SetListener(...)</c>. Showing an interstitial needs a live
/// <see cref="Android.App.Activity"/> (no parameterless overload, unlike AdMob/AppLovin), taken from
/// <see cref="Platform.CurrentActivity"/> -- the same helper the rest of this app already uses for
/// activity-scoped platform calls (photo/PDF pickers, camera).
///
/// Needed a Metadata.xml fix to build at all: the real AAR has hundreds of R8-flattened, 1-2 character obfuscated
/// classes sitting directly in the top-level "com.ironsource" package (not under any "impl" sub-package), plus an
/// adapter-facing SPI tree (<c>com.ironsource.mediationsdk.adunit.adapter.*</c>, <c>com.ironsource.mediationsdk.
/// sdk.I*SmashListener</c>) that referenced them and failed to bind for the same reason -- none of it is public
/// API, see <c>Transforms/Metadata.xml</c> for the exact exclusions.</summary>
internal sealed class UnityLevelPlayProvider(LevelPlayOptions options) : IAdProvider
{
    private LevelPlayBannerAdView? _shared;
    private LevelPlayInterstitialAd? _interstitial;

    public bool IsBannerLoaded { get; private set; }

    public event Action? BannerStateChanged;

    public bool IsInterstitialReady => _interstitial?.IsAdReady ?? false;

    public UnityLevelPlayProvider Initialize(Android.Content.Context context)
    {
        if (options.TestMode) global::Com.Unity3d.Mediation.LevelPlay.SetAdaptersDebug(true);
        LevelPlayInitRequest request = new LevelPlayInitRequest.Builder(options.AppKey)!.Build()!;
        global::Com.Unity3d.Mediation.LevelPlay.Init(context, request, new InitListener());
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

        _shared = new LevelPlayBannerAdView(context, options.BannerAdUnitId);
        _shared.BannerListener = new BannerListener(this); // getter+setter pair binds as a C# property, not a method
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
        if (_interstitial is not { IsAdReady: true } ad) return;
        if (Platform.CurrentActivity is { } activity) ad.ShowAd(activity);
    }

    private LevelPlayInterstitialAd CreateInterstitial()
    {
        var ad = new LevelPlayInterstitialAd(options.InterstitialAdUnitId);
        ad.SetListener(new InterstitialListener());
        return ad;
    }

    private void SetBannerLoaded(bool loaded)
    {
        if (IsBannerLoaded == loaded) return;
        IsBannerLoaded = loaded;
        BannerStateChanged?.Invoke();
    }

    private sealed class InitListener : Java.Lang.Object, ILevelPlayInitListener
    {
        public void OnInitSuccess(LevelPlayConfiguration? configuration) { }
        public void OnInitFailed(LevelPlayInitError? error) { }
    }

    private sealed class BannerListener(UnityLevelPlayProvider owner) : Java.Lang.Object, ILevelPlayBannerAdViewListener
    {
        public void OnAdLoaded(LevelPlayAdInfo? adInfo) => owner.SetBannerLoaded(true);
        public void OnAdLoadFailed(LevelPlayAdError? error) => owner.SetBannerLoaded(false);
        public void OnAdDisplayed(LevelPlayAdInfo? adInfo) { }
        public void OnAdDisplayFailed(LevelPlayAdInfo? adInfo, LevelPlayAdError? error) { }
        public void OnAdClicked(LevelPlayAdInfo? adInfo) { }
        public void OnAdExpanded(LevelPlayAdInfo? adInfo) { }
        public void OnAdCollapsed(LevelPlayAdInfo? adInfo) { }
        public void OnAdLeftApplication(LevelPlayAdInfo? adInfo) { }
    }

    private sealed class InterstitialListener : Java.Lang.Object, ILevelPlayInterstitialAdListener
    {
        public void OnAdLoaded(LevelPlayAdInfo? adInfo) { }
        public void OnAdLoadFailed(LevelPlayAdError? error) { }
        public void OnAdDisplayed(LevelPlayAdInfo? adInfo) { }
        public void OnAdDisplayFailed(LevelPlayAdError? error, LevelPlayAdInfo? adInfo) { }
        public void OnAdClicked(LevelPlayAdInfo? adInfo) { }
        public void OnAdClosed(LevelPlayAdInfo? adInfo) { }
        public void OnAdInfoChanged(LevelPlayAdInfo? adInfo) { }
    }
}
