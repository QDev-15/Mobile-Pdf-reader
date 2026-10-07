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
		// Ads mode (PdfReader.csproj's AdsMode MSBuild property, -> ADS_MODE_PRO), NOT Debug/Release:
		// the default "Dev" mode always uses Google's own test ad units, Debug or Release, APK or AAB,
		// even uploaded to a Play testing track -- so nobody can accidentally click a real ad and risk a
		// policy strike. Only a build made with -p:AdsMode=Pro uses the real AdsConfig ids, and even then
		// only once AdsConfig.HasRealIds (both ids actually filled in) -- see AdsConfig.cs.
#if ADS_MODE_PRO
		Plugin.AdMob.Configuration.AdConfig.UseTestAdUnitIds = !AdsConfig.HasRealIds;
#else
		Plugin.AdMob.Configuration.AdConfig.UseTestAdUnitIds = true;
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
