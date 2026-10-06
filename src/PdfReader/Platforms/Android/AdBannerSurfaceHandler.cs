using Android.Gms.Ads;
using Android.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Handlers;
using PdfReader.Services;
using PdfReader.Views;

namespace PdfReader.Platforms.Android;

/// <summary>Maps <see cref="AdBannerSurface"/> to ONE shared native <c>AdView</c> for the whole app.
/// Ported from DocScanner's AdBannerSurfaceHandler (Mobile-doc-scanner repo) -- see that file's doc
/// comment for why a shared instance through a real Handler (rather than a fresh AdView per page) is
/// what fixes both the performance problem and the banner not reliably showing.
///
/// Showing/hiding the banner is NOT this class's job: it only creates the native AdView, reparents it as
/// pages come and go, and reports load/fail events to <see cref="IAdsService.ReportBannerLoaded"/>;
/// <see cref="AdBannerSurface"/> owns <c>IsVisible</c> off that state.</summary>
internal sealed class AdBannerSurfaceHandler : ViewHandler<AdBannerSurface, AdView>
{
	public static readonly IPropertyMapper<AdBannerSurface, AdBannerSurfaceHandler> Mapper =
		new PropertyMapper<AdBannerSurface, AdBannerSurfaceHandler>(ViewHandler.ViewMapper);

	private static AdView? _shared;

	public AdBannerSurfaceHandler() : base(Mapper)
	{
	}

	protected override AdView CreatePlatformView()
	{
		if (_shared is { } existing)
		{
			(existing.Parent as ViewGroup)?.RemoveView(existing);
			return existing;
		}

		IAdsService? ads = IPlatformApplication.Current?.Services.GetService<IAdsService>();
		string adUnitId = Plugin.AdMob.Configuration.AdConfig.UseTestAdUnitIds
			? "ca-app-pub-3940256099942544/6300978111" // Google's published test banner id
			: AdsConfig.BannerAdUnitId;
		_shared = new AdView(Context) { AdUnitId = adUnitId, AdSize = AdSize.Banner, AdListener = new LoadListener(ads) };
		if (ads?.ShowAds != false) _shared.LoadAd(new AdRequest.Builder().Build());
		return _shared;
	}

	/// <summary>Never tears down the shared AdView just because the page hosting it right now is going
	/// away -- the next page's handler simply reparents it.</summary>
	protected override void DisconnectHandler(AdView platformView)
	{
	}

	private sealed class LoadListener(IAdsService? ads) : AdListener
	{
		public override void OnAdLoaded() => ads?.ReportBannerLoaded(true);
		public override void OnAdFailedToLoad(LoadAdError error) => ads?.ReportBannerLoaded(false);
	}
}
