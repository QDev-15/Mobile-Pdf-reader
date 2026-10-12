using Microsoft.Extensions.DependencyInjection;

namespace AdsService;

/// <summary>Cross-platform placeholder for the free-tier ad banner: a MAUI View with a custom Handler
/// (<c>AdBannerSurfaceHandler</c>) backed by a stable per-page native container that the ONE shared native ad
/// view is moved into, regardless of which provider is configured. Drop this on any page -- no
/// <c>HeightRequest</c>, it sizes itself to the real ad view while shown, and NOTHING (not even a reserved
/// strip) while there is no ad to show, via <see cref="IAdsClient.AreAdsEnabled"/> and
/// <see cref="IAdsClient.IsBannerLoaded"/>, kept live for as long as this instance is on screen.
///
/// Call <see cref="Reattach"/> from a page's <c>OnAppearing</c> if that page can become current again WITHOUT
/// its handler being recreated (e.g. it is cached/reused by the navigation stack rather than always freshly
/// constructed) -- otherwise the shared banner can be left behind in whichever page last created a handler.
/// Pages where the handler is always freshly created on navigation (the common MAUI Shell case) do not need to
/// call it; <see cref="AdBannerSurfaceHandler.CreatePlatformView"/> already attaches on creation.</summary>
public sealed class AdBannerSurface : View
{
    private IAdsClient? _ads;

    public AdBannerSurface()
    {
        IsVisible = false; // nothing to show until proven otherwise, not even for one frame
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        _ads ??= IPlatformApplication.Current?.Services.GetService<IAdsClient>();
        if (_ads == null) return;
        _ads.BannerStateChanged += Apply;
        Apply();
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        if (_ads != null) _ads.BannerStateChanged -= Apply;
    }

    /// <summary>Moves the shared native banner into this page's container if it is not there already. Safe to
    /// call any time, including before the native view exists yet (silently does nothing that tick); call it
    /// again a moment later in that case, the same pattern <c>OnAppearing</c> callers already use for other
    /// just-navigated-to native lookups.</summary>
    public void Reattach()
    {
        (Handler as AdBannerSurfaceHandler)?.Reattach();
        Apply();
    }

    private void Apply() => IsVisible = _ads is { AreAdsEnabled: true, IsBannerLoaded: true };
}
