using AdsService;
using Microsoft.Extensions.Logging;
using PdfReader.Core.Licensing;
using PdfReader.Platforms.Android;
using PdfReader.Services;
using PdfReader.Views;
using PdfSharp.Pdf.IO;

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
		bool useTestAds = !AdsConfig.HasRealIds;
#else
		bool useTestAds = true;
#endif

		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			// Ad provider: AdMob, same default as DocScanner's own AdsConfig-driven setup before it switched
			// providers (2026-10-08) -- AppLovinOptions / LevelPlayOptions are both available in the ported
			// AdsService library the moment PdfReader has its own real credentials for either (a separate
			// AdMob/AppLovin/LevelPlay app is required per app, ids cannot be shared with DocScanner's).
			.UseAdsService(new LevelPlayOptions(
				AppKey: LevelPlayConfig.AppKey,
                BannerAdUnitId: useTestAds ? LevelPlayConfig.BannerAdUnitId : AdsConfig.BannerAdUnitId,
                InterstitialAdUnitId: useTestAds ? LevelPlayConfig.InterstitialAdUnitId : AdsConfig.InterstitialAdUnitId,
                TestMode: LevelPlayConfig.TestMode
                ))
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
				fonts.AddFont("MaterialIcons-Regular.ttf", "Icons");
			})
			.ConfigureMauiHandlers(handlers =>
			{
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
		// Fully qualified: the "AdsService" namespace (the ads library) and this app's own
		// "PdfReader.Services.AdsService" class share a name, so unqualified "AdsService" here is ambiguous.
		builder.Services.AddSingleton<IAdsService, PdfReader.Services.AdsService>();

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
