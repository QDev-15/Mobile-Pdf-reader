using Microsoft.Extensions.DependencyInjection;
using PdfReader.Services;

namespace PdfReader.Views;

/// <summary>Cross-platform placeholder for the free-tier ad banner: a MAUI View with a custom Handler
/// (Platforms/Android/AdBannerSurfaceHandler.cs) that hands every page the SAME native AdView instead of
/// building a fresh one each time. Drop at the top of a page (plan: "dải trên cùng" for ads) -- no
/// HeightRequest, it sizes itself to the real AdView while shown, and nothing while there is no ad to
/// show. Ported from DocScanner's AdBannerSurface (Mobile-doc-scanner repo).</summary>
public sealed class AdBannerSurface : View
{
	private IAdsService? _ads;

	public AdBannerSurface()
	{
		IsVisible = false; // nothing to show until proven otherwise, not even for one frame
		Loaded += OnLoaded;
		Unloaded += OnUnloaded;
	}

	private void OnLoaded(object? sender, EventArgs e)
	{
		_ads ??= IPlatformApplication.Current?.Services.GetService<IAdsService>();
		if (_ads == null) return;
		_ads.Changed += Apply;
		Apply();
	}

	private void OnUnloaded(object? sender, EventArgs e)
	{
		if (_ads != null) _ads.Changed -= Apply;
	}

	/// <summary>Moves the shared native banner into this page. Call from the page's OnAppearing.</summary>
	public void Reattach()
	{
		(Handler as PdfReader.Platforms.Android.AdBannerSurfaceHandler)?.Reattach();
		Apply();
		// The native view of a page that has only just appeared may not exist yet: try again a moment later.
		Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(600), () =>
		{
			(Handler as PdfReader.Platforms.Android.AdBannerSurfaceHandler)?.Reattach();
			Apply();
		});
	}

	private void Apply() => IsVisible = _ads is { ShowAds: true, IsBannerLoaded: true };
}
