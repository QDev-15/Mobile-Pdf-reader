using Microsoft.Extensions.Logging;
using PdfReader.Core.Licensing;
using PdfReader.Platforms.Android;
using PdfReader.Services;
using PdfReader.Views;
using Plugin.AdMob;

namespace PdfReader;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		// Ads: Debug always uses Google's own test ad units, whatever AdsConfig says, so development
		// never risks a policy strike from clicking a real ad. Release does too, UNLESS
		// AdsConfig.HasRealIds -- see AdsConfig.cs.
#if DEBUG
		Plugin.AdMob.Configuration.AdConfig.UseTestAdUnitIds = true;
#else
		Plugin.AdMob.Configuration.AdConfig.UseTestAdUnitIds = !AdsConfig.HasRealIds;
#endif

		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseAdMob(androidDefaultBannerAdUnitId: AdsConfig.BannerAdUnitId, androidDefaultInterstitialAdUnitId: AdsConfig.InterstitialAdUnitId)
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			})
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<AdBannerSurface, AdBannerSurfaceHandler>();
			});

		builder.Services.AddSingleton(_ => new RecentPdfStore(FileSystem.AppDataDirectory));
		builder.Services.AddSingleton<IDownloadsService, AndroidDownloadsService>();
		builder.Services.AddSingleton<ILicenseService, LicenseService>();
		builder.Services.AddSingleton<IAdsService, AdsService>();

		builder.Services.AddTransient<DashboardPage>();
		builder.Services.AddTransient<ReaderPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
