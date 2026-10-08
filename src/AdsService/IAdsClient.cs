namespace AdsService;

/// <summary>The provider-agnostic surface an app talks to after <c>MauiAppBuilder.UseAdsService</c>: load/show
/// state for the shared banner and the interstitial, whichever network is actually configured underneath.
/// Deliberately knows nothing about "Pro" or app-specific cadence rules (e.g. DocScanner's "every 5th PDF
/// export" vs PdfReader's "once an hour at a screen transition") -- that policy belongs in the consuming app
/// (see each app's own <c>Services.IAdsService</c>, which wraps this), so a project with yet another policy can
/// reuse this library unchanged.</summary>
public interface IAdsClient
{
    /// <summary>Generic "should ads show at all" gate -- true by default. Set to false once the consuming app's
    /// own ad-free condition is met (e.g. a Pro purchase); this library has no opinion on what that condition
    /// is. Purely a display/no-op gate, same as today's app-level <c>ShowAds</c>: it does not tear down or stop
    /// preloading anything, it only hides the banner and makes <see cref="ShowInterstitialIfReady"/> a no-op, so
    /// flipping it back to true needs no reload.</summary>
    bool AreAdsEnabled { get; set; }

    /// <summary>True only while the shared banner actually has a creative on screen right now -- false before
    /// the first load, while a refresh is in flight, and whenever the last load/refresh attempt failed.</summary>
    bool IsBannerLoaded { get; }

    /// <summary>Raised whenever <see cref="IsBannerLoaded"/> changes.</summary>
    event Action? BannerStateChanged;

    /// <summary>True once an interstitial has finished preloading and is ready to show immediately.</summary>
    bool IsInterstitialReady { get; }

    /// <summary>Starts (or restarts) loading the next interstitial in the background. Call again after
    /// <see cref="ShowInterstitialIfReady"/> to keep one ready for next time.</summary>
    void PrepareInterstitial();

    /// <summary>Shows the preloaded interstitial if one is ready; otherwise does nothing (never blocks or
    /// throws -- callers should treat ads as best-effort and never let them fail an in-progress user action).</summary>
    void ShowInterstitialIfReady();
}
