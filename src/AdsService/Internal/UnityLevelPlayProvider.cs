using Com.Unity3d.Mediation;
using Com.Unity3d.Mediation.Banner;
using Com.Unity3d.Mediation.Interstitial;
using Microsoft.Maui.ApplicationModel;
using FrameLayout = Android.Widget.FrameLayout;
using GravityFlags = Android.Views.GravityFlags;
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
    // CreateBannerView's own View, not the LevelPlayBannerAdView itself: LevelPlayAdSize.CreateAdaptiveAdSize()
    // has the exact same "SDK must be initialized first" constraint as LoadAd() (confirmed on-device 2026-10-09
    // in Mobile-doc-scanner: calling it before init crashes the app outright, SetAdSize(null) throwing on the
    // Java side -- worse than LoadAd's merely-rejected-with-an-error-callback behavior). CreateBannerView must
    // return a View synchronously and LevelPlayBannerAdView has no way to change its ad size after construction,
    // so the real ad view can only be built once init is ready; this stable, empty container is what every page
    // actually gets back and reuses. Compatible with AdBannerSurfaceHandler's own WrapContent+AddView wrapping
    // (re-measures once the real banner is added as this container's child later).
    private FrameLayout? _container;
    private LevelPlayBannerAdView? _shared;
    private LevelPlayInterstitialAd? _interstitial;

    // Init() is async (~1s on a real device): calling LoadAd() before its callback fires is rejected outright
    // (errorCode 625 "Load must be called after init success callback"), not queued or retried by the SDK itself
    // -- confirmed on-device 2026-10-09 in Mobile-doc-scanner (same provider code, ported here), the actual
    // reason ads never showed despite every other piece (dependency, dashboard config, credentials) being
    // correct. CreateBannerView/PrepareInterstitial can legitimately be called that early (banner view is
    // created as soon as the first page appears), so every LoadAd() call goes through RunAfterInit instead of
    // being issued directly.
    private readonly Lock _lock = new();
    private bool _initDone;
    private readonly List<Action> _pendingActions = [];

    public bool IsBannerLoaded { get; private set; }

    public event Action? BannerStateChanged;

    public bool IsInterstitialReady => _interstitial?.IsAdReady ?? false;

    public UnityLevelPlayProvider Initialize(Android.Content.Context context)
    {
        if (options.TestMode) global::Com.Unity3d.Mediation.LevelPlay.SetAdaptersDebug(true);
        LevelPlayInitRequest request = new LevelPlayInitRequest.Builder(options.AppKey)!.Build()!;
        global::Com.Unity3d.Mediation.LevelPlay.Init(context, request, new InitListener(this));
        return this;
    }

    public View CreateBannerView(Android.Content.Context context)
    {
        if (_container is { } existing)
        {
            // Already shown on another (still-alive, e.g. back-stack) page: an Android View can only have one
            // parent, so take it back before this page's own container tries to add it.
            (existing.Parent as ViewGroup)?.RemoveView(existing);
            return existing;
        }

        var container = new FrameLayout(context);
        _container = container;
        RunAfterInit(() =>
        {
            // Adaptive banner (as wide as the screen) instead of the fixed 320x50 a no-config constructor would
            // default to -- see the field comment on _container for why this has to wait until here.
            LevelPlayBannerAdView.Config config = new LevelPlayBannerAdView.Config.Builder()
                .SetAdSize(LevelPlayAdSize.CreateAdaptiveAdSize(context)!)
                .Build();
            var view = new LevelPlayBannerAdView(context, options.BannerAdUnitId, config);
            view.BannerListener = new BannerListener(this); // getter+setter pair binds as a C# property, not a method
            _shared = view;
            // WrapContent + CenterHorizontal: without an explicit Gravity, FrameLayout.AddView defaults to
            // top-start. AdBannerSurfaceHandler's own Attach() already centers this whole container within its
            // per-page wrapper, which happens to be enough on its own (WrapContent sizing already wraps tight
            // around this single child regardless of its internal gravity) -- set here too anyway, for parity
            // with Mobile-doc-scanner's copy (whose simpler Handler has no such outer centering of its own) and
            // so this container is centered correctly even standalone, independent of how its host Handler works.
            container.AddView(view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.CenterHorizontal,
            });
            view.LoadAd();
        });
        return container;
    }

    public void PrepareInterstitial()
    {
        LevelPlayInterstitialAd ad = _interstitial ??= CreateInterstitial();
        RunAfterInit(ad.LoadAd);
    }

    private void RunAfterInit(Action action)
    {
        lock (_lock)
        {
            if (!_initDone) { _pendingActions.Add(action); return; }
        }
        action();
    }

    private void OnInitFinished()
    {
        List<Action> pending;
        lock (_lock)
        {
            _initDone = true;
            pending = [.. _pendingActions];
            _pendingActions.Clear();
        }
        // The queued actions touch Views (container.AddView for the banner): MainThread.BeginInvokeOnMainThread
        // is a cheap no-op if this callback already arrived on the UI thread (observed on-device so far), and
        // the safety net if a future SDK version ever delivers it off-thread.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            foreach (Action action in pending) action();
        });
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

    private sealed class InitListener(UnityLevelPlayProvider owner) : Java.Lang.Object, ILevelPlayInitListener
    {
        // Either outcome unblocks queued LoadAd() calls: a genuine init failure should surface as a normal
        // ad-load failure through the banner/interstitial listeners, not as a silent, permanently-stuck queue.
        public void OnInitSuccess(LevelPlayConfiguration? configuration) => owner.OnInitFinished();
        public void OnInitFailed(LevelPlayInitError? error) => owner.OnInitFinished();
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
