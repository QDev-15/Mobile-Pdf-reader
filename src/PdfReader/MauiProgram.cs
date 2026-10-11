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
		// the default "Dev" mode always uses the provider's own test/sandbox ad units, Debug or Release,
		// APK or AAB, even uploaded to a Play testing track -- so nobody can accidentally click a real ad
		// and risk a policy strike. Only a build made with -p:AdsMode=Pro uses the real RealAdsConfig ids,
		// and even then only once RealAdsConfig.HasRealIds (both ids actually filled in) -- see RealAdsConfig.cs.
#if ADS_MODE_PRO
		bool useTestAds = !RealAdsConfig.HasRealIds;
#else
		bool useTestAds = true;
#endif

		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			// ===== ACTIVE ad provider: Unity LevelPlay. =====
			// AppKey + test/sandbox ad unit ids live in LevelPlayConfig.cs; the production ids (used once
			// useTestAds is false) live in RealAdsConfig.cs. Exactly one provider block in this file should
			// ever be uncommented -- see the AdMob / AppLovin alternatives commented out below, kept only as
			// a template for switching providers later (a separate app/account + credentials is required per
			// provider, ids cannot be shared with DocScanner's).
			.UseAdsService(new LevelPlayOptions(
				AppKey: LevelPlayConfig.AppKey,
                BannerAdUnitId: useTestAds ? LevelPlayConfig.BannerAdUnitId : RealAdsConfig.BannerAdUnitId,
                InterstitialAdUnitId: useTestAds ? LevelPlayConfig.InterstitialAdUnitId : RealAdsConfig.InterstitialAdUnitId,
                TestMode: LevelPlayConfig.TestMode)
            {
                // MASTER ads switch for this build (PdfReader.csproj's AdsEnabled MSBuild property, default true;
                // same property and meaning as in DocScanner). Off = the ad SDK is not even initialised.
#if ADS_DISABLED
                IsAdsEnabled = false,
#else
                IsAdsEnabled = true,
#endif
            })
			// ===== INACTIVE alternatives -- not wired up, kept only as a switch-over template. =====
			// AdMob: also needs the AdMob App ID block in PdfReader.csproj + AndroidManifest.xml (both
			// currently commented out there too -- uncomment all three together).
			// .UseAdsService(new AdMobOptions(
			//     BannerAdUnitId: useTestAds ? "ca-app-pub-3940256099942544/6300978111" : RealAdsConfig.BannerAdUnitId,
			//     InterstitialAdUnitId: useTestAds ? "ca-app-pub-3940256099942544/1033173712" : RealAdsConfig.InterstitialAdUnitId,
			//     UseTestAds: useTestAds))
			// AppLovin:
			// .UseAdsService(new AppLovinOptions(
			//     SdkKey: "REPLACE_ME_APPLOVIN_SDK_KEY",
			//     BannerAdUnitId: RealAdsConfig.BannerAdUnitId,
			//     InterstitialAdUnitId: RealAdsConfig.InterstitialAdUnitId,
			//     TestMode: useTestAds))
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
		builder.Services.AddSingleton<ProUpgrade>();
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

		MauiApp app = builder.Build();
		// Resolve the ads service NOW (before any page exists): its constructor applies the cached Pro flag to the ads
		// library, which creates the ad SDK lazily. A Pro user therefore never initialises the SDK, builds a banner or
		// loads anything; resolved lazily instead, a banner page could win the race and start ads for a Pro user.
		_ = app.Services.GetRequiredService<IAdsService>();
		return app;
	}
}
