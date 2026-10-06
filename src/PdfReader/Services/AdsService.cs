using PdfReader.Core.Ads;
using PdfReader.Core.Licensing;
using Plugin.AdMob.Services;

namespace PdfReader.Services;

/// <summary>
/// Whether ads should be visible right now, and the one place in the app that talks to AdMob -- mirrors
/// <see cref="LicenseService"/> being the one place that talks to Play Billing. The interstitial rule
/// itself (at most once per hour, only at a screen transition) lives in <see cref="AdsPolicy"/> and is
/// unit-tested on its own. Adapted from DocScanner's AdsService (Mobile-doc-scanner repo): that one
/// counted exports; this counts screen transitions (plan section 6).
/// </summary>
public interface IAdsService
{
    /// <summary>False once Pro is bought (or restored). Every page's AdBannerSurface binds its own
    /// IsVisible to <c>ShowAds &amp;&amp; IsBannerLoaded</c> directly.</summary>
    bool ShowAds { get; }

    /// <summary>True only while the shared banner actually has a creative on screen right now.</summary>
    bool IsBannerLoaded { get; }

    /// <summary>Raised whenever <see cref="ShowAds"/> or <see cref="IsBannerLoaded"/> may have changed.</summary>
    event Action? Changed;

    /// <summary>Called by the platform banner ad listener on every OnAdLoaded / OnAdFailedToLoad.</summary>
    void ReportBannerLoaded(bool loaded);

    /// <summary>Call at a natural screen transition -- a file is closed, or a new one is about to open.
    /// Never while a document is actually open on screen (plan section 6's revision of the owner's
    /// original "every hour" idea). Shows an interstitial when <see cref="AdsPolicy"/> says it is due
    /// and one happens to be ready; otherwise this cycle is skipped quietly.</summary>
    void RegisterScreenTransition();
}

public sealed class AdsService : IAdsService
{
    private const string StateKey = "ads_last_interstitial_utc_ticks";

    private readonly ILicenseService _license;
    private readonly IInterstitialAdService _interstitial;

    public AdsService(ILicenseService license, IInterstitialAdService interstitial)
    {
        _license = license;
        _interstitial = interstitial;
        _license.Changed += () => Changed?.Invoke();
        PrepareInterstitial(); // one kept ready at all times, so a due transition rarely has to skip
    }

    public bool ShowAds => !_license.State.IsPro;

    public bool IsBannerLoaded { get; private set; }

    public event Action? Changed;

    public void ReportBannerLoaded(bool loaded)
    {
        if (IsBannerLoaded == loaded) return;
        IsBannerLoaded = loaded;
        Changed?.Invoke();
    }

    public void RegisterScreenTransition()
    {
        long ticks = Preferences.Default.Get(StateKey, 0L);
        var state = new AdsState(ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero));
        (AdsState next, bool show) = AdsPolicy.AtScreenTransition(state, _license.State.IsPro, DateTimeOffset.UtcNow);
        Preferences.Default.Set(StateKey, next.LastShownUtc?.UtcTicks ?? 0L);

        if (show && _interstitial.IsAdLoaded)
        {
            try { _interstitial.ShowAd(); }
            catch (Exception ex) { Android.Util.Log.Warn("PdfReader", $"ads: interstitial show failed: {ex.Message}"); }
        }
        PrepareInterstitial();
    }

    private void PrepareInterstitial()
    {
        try { _interstitial.PrepareAd(AdsConfig.InterstitialAdUnitId); }
        catch (Exception ex) { Android.Util.Log.Warn("PdfReader", $"ads: interstitial preload failed: {ex.Message}"); }
    }
}
