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
				fonts.AddFont("MaterialIcons-Regular.ttf", "Icons");
			})
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<AdBannerSurface, AdBannerSurfaceHandler>();
				handlers.AddHandler<PdfViewerView, PdfViewerHandler>();
			});

		builder.Services.AddSingleton(_ => new RecentPdfStore(FileSystem.AppDataDirectory));
		builder.Services.AddSingleton(_ => new PdfLibrary(FileSystem.AppDataDirectory));
		builder.Services.AddSingleton(_ => new SignatureStore(FileSystem.AppDataDirectory));
		builder.Services.AddSingleton(_ => new SignatureImageStore(FileSystem.AppDataDirectory));
		builder.Services.AddSingleton<OcrService>();
		builder.Services.AddSingleton<PdfExporter>();
		builder.Services.AddSingleton<PdfOpener>();
		builder.Services.AddSingleton<ResultService>();
		builder.Services.AddSingleton<IDownloadsService, AndroidDownloadsService>();
		builder.Services.AddSingleton<ILicenseService, LicenseService>();
		builder.Services.AddSingleton<IAdsService, AdsService>();

		builder.Services.AddTransient<DashboardPage>();
		builder.Services.AddTransient<ReaderPage>();
		builder.Services.AddTransient<PageManagerPage>();
		builder.Services.AddTransient<SettingsPage>();
		builder.Services.AddTransient<AboutPage>();
		builder.Services.AddTransient<LaunchPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
