using Android.Content;
using Android.Gms.Ads;
using Android.Views;
using Android.Widget;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Handlers;
using PdfReader.Services;
using PdfReader.Views;

namespace PdfReader.Platforms.Android;

/// <summary>Maps <see cref="AdBannerSurface"/> to a small container that holds ONE shared native
/// <c>AdView</c> for the whole app (ported from DocScanner, see its AdBannerSurfaceHandler for why one shared
/// instance rather than a fresh AdView per page). Two changes for PdfReader: the banner is an adaptive
/// banner as wide as the screen, and every page owns a stable container the shared AdView is moved into
/// (<see cref="Reattach"/>, called when the page appears) -- so going Dashboard -> Reader -> back, or opening
/// straight into the Reader, always leaves the banner in the page that is on screen.
///
/// Showing/hiding the banner is NOT this class's job: it reports load/fail events to
/// <see cref="IAdsService.ReportBannerLoaded"/>; <see cref="AdBannerSurface"/> owns <c>IsVisible</c> off that.</summary>
internal sealed class AdBannerSurfaceHandler : ViewHandler<AdBannerSurface, FrameLayout>
{
	public static readonly IPropertyMapper<AdBannerSurface, AdBannerSurfaceHandler> Mapper =
		new PropertyMapper<AdBannerSurface, AdBannerSurfaceHandler>(ViewHandler.ViewMapper);

	private static AdView? _shared;
	private static Context? _sharedContext;

	public AdBannerSurfaceHandler() : base(Mapper)
	{
	}

	protected override FrameLayout CreatePlatformView()
	{
		var container = new FrameLayout(Context);
		Attach(container);
		return container;
	}

	/// <summary>Moves the shared AdView into this page's container (it may currently sit in another page's).</summary>
	public void Reattach() => Attach(PlatformView);

	private void Attach(FrameLayout container)
	{
		AdView view = EnsureShared(Context);
		if (view.Parent == container) return;
		(view.Parent as ViewGroup)?.RemoveView(view);
		container.AddView(view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
		{
			Gravity = GravityFlags.CenterHorizontal,
		});
	}

	private static AdView EnsureShared(Context context)
	{
		if (_shared != null && ReferenceEquals(_sharedContext, context)) return _shared;

		// A new activity (the app was closed and opened again): the old AdView belongs to a dead one.
		if (_shared != null)
		{
			(_shared.Parent as ViewGroup)?.RemoveView(_shared);
			_shared.Destroy();
		}

		IAdsService? ads = IPlatformApplication.Current?.Services.GetService<IAdsService>();
		ads?.ReportBannerLoaded(false);
		string adUnitId = Plugin.AdMob.Configuration.AdConfig.UseTestAdUnitIds
			? "ca-app-pub-3940256099942544/6300978111" // Google's published test banner id
			: AdsConfig.BannerAdUnitId;

		var metrics = context.Resources!.DisplayMetrics!;
		int widthDp = (int)(metrics.WidthPixels / metrics.Density);
		_shared = new AdView(context)
		{
			AdUnitId = adUnitId,
			AdSize = AdSize.GetCurrentOrientationAnchoredAdaptiveBannerAdSize(context, widthDp),
			AdListener = new LoadListener(ads),
		};
		_sharedContext = context;
		if (ads?.ShowAds != false) _shared.LoadAd(new AdRequest.Builder().Build());
		return _shared;
	}

	/// <summary>Never tears down the shared AdView just because the page hosting it right now is going
	/// away -- the next page's container simply adopts it.</summary>
	protected override void DisconnectHandler(FrameLayout platformView)
	{
		if (_shared?.Parent == platformView) platformView.RemoveView(_shared);
	}

	private sealed class LoadListener(IAdsService? ads) : AdListener
	{
		public override void OnAdLoaded() => ads?.ReportBannerLoaded(true);
		public override void OnAdFailedToLoad(LoadAdError error)
		{
			global::Android.Util.Log.Warn("PdfReader", $"ads: banner failed to load: {error.Message} (code {error.Code})");
			ads?.ReportBannerLoaded(false);
		}
	}
}
