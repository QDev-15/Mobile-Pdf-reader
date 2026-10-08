using PdfReader.Core.Ads;
using PdfReader.Core.Licensing;
using AdsClient = AdsService.IAdsClient;

namespace PdfReader.Services;

/// <summary>
/// Whether ads should be visible right now, and the one place in the app that applies "Pro" + this app's own
/// cadence rule to ads -- mirrors <see cref="LicenseService"/> being the one place that talks to Play Billing.
/// The interstitial rule itself (at most once per hour, only at a screen transition) lives in
/// <see cref="AdsPolicy"/> and is unit-tested on its own; the actual ad network (AdMob / AppLovin / Unity
/// LevelPlay) lives entirely in the reusable <c>AdsService</c> library (ported from the sibling DocScanner repo,
/// 2026-10-08) -- this class only wires this app's Pro/cadence policy to that library's generic
/// <see cref="AdsClient"/>, the same relationship DocScanner's own AdsService has with it.
/// </summary>
public interface IAdsService
{
    /// <summary>False once Pro is bought (or restored). Every page's AdBannerSurface reads
    /// <see cref="AdsClient.AreAdsEnabled"/> / <see cref="AdsClient.IsBannerLoaded"/> directly off the library,
    /// which this class keeps in sync with Pro status -- see the constructor below.</summary>
    bool ShowAds { get; }

    /// <summary>True only while the shared banner actually has a creative on screen right now.</summary>
    bool IsBannerLoaded { get; }

    /// <summary>Raised whenever <see cref="ShowAds"/> or <see cref="IsBannerLoaded"/> may have changed.</summary>
    event Action? Changed;

    /// <summary>Call at a natural screen transition -- a file is closed, or a new one is about to open.
    /// Never while a document is actually open on screen (plan section 6's revision of the owner's
    /// original "every hour" idea). Shows an interstitial when <see cref="AdsPolicy"/> says it is due
    /// and one happens to be ready; otherwise this cycle is skipped quietly.</summary>
    void RegisterScreenTransition();

    /// <summary>A PDF was just opened straight from another app. Every 6th such open shows an interstitial
    /// (see <see cref="AdsPolicy.AtExternalOpen"/>).</summary>
    void RegisterExternalOpen();
}

public sealed class AdsService : IAdsService
{
    private const string StateKey = "ads_last_interstitial_utc_ticks";
    private const string ExternalKey = "ads_external_open_count";

    private readonly ILicenseService _license;
    private readonly AdsClient _ads;

    public AdsService(ILicenseService license, AdsClient ads)
    {
        _license = license;
        _ads = ads;
        _ads.AreAdsEnabled = !_license.State.IsPro;
        _license.Changed += () =>
        {
            _ads.AreAdsEnabled = !_license.State.IsPro;
            Changed?.Invoke();
        };
        _ads.BannerStateChanged += () => Changed?.Invoke();
        _ads.PrepareInterstitial(); // one kept ready at all times, so a due transition rarely has to skip
    }

    public bool ShowAds => !_license.State.IsPro;

    public bool IsBannerLoaded => _ads.IsBannerLoaded;

    public event Action? Changed;

    public void RegisterScreenTransition()
    {
        long ticks = Preferences.Default.Get(StateKey, 0L);
        if (ticks == 0)
        {
            // First transition ever: start the one-hour clock now instead of greeting a brand-new user
            // with an ad before they have read anything.
            Preferences.Default.Set(StateKey, DateTimeOffset.UtcNow.UtcTicks);
            _ads.PrepareInterstitial();
            return;
        }
        var state = new AdsState(ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero));
        (AdsState next, bool show) = AdsPolicy.AtScreenTransition(state, _license.State.IsPro, DateTimeOffset.UtcNow);
        Preferences.Default.Set(StateKey, next.LastShownUtc?.UtcTicks ?? 0L);

        if (show) _ads.ShowInterstitialIfReady(); // no-op quietly if not actually ready yet -- see IAdsClient
        _ads.PrepareInterstitial();
    }

    public void RegisterExternalOpen()
    {
        int count = Preferences.Default.Get(ExternalKey, 0);
        (int next, bool show) = AdsPolicy.AtExternalOpen(count, _license.State.IsPro);
        Preferences.Default.Set(ExternalKey, next);
        if (show) _ads.ShowInterstitialIfReady();
        _ads.PrepareInterstitial();
    }
}
