using Microsoft.Extensions.DependencyInjection;
using PdfReader.Core.Licensing;

namespace PdfReader;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// The start screen first; it builds the main page (either AppShell directly, or the terms gate in
		// front of it -- see CreateMainPage below) while it animates, then hands over -- see
		// Views/SplashPage.xaml.cs. Same technique as the sibling DocScanner repo.
		return new Window(new Views.SplashPage(CreateMainPage));
	}

	/// <summary>Owner's request, 2026-11-12 (ported from the sibling LogoExtract repo's own already-shipped
	/// gate): decides, synchronously and with no network wait, whether the very first screen after Splash is
	/// AppShell directly or the new <see cref="Views.TermsGatePage"/> in front of it. This app's constructor
	/// takes no DI parameters (unlike LogoExtract's own App(ILicenseService, ...)), so <see cref="ILicenseService"/>
	/// is resolved here via the same "IPlatformApplication.Current?.Services.GetService&lt;T&gt;()" self-resolving
	/// idiom this app already uses outside normal page-DI flow (AdsService.Core's AdBannerSurface/
	/// AdBannerSurfaceHandler, AdMobProvider) -- chosen over adding a constructor parameter to App itself,
	/// since nothing else in this app's startup needs one.
	///
	/// Two independent reasons to SKIP the gate and go straight to AppShell:
	/// 1. <see cref="Views.TermsGatePage.TermsAcceptedKey"/> is already true -- this install has agreed once
	///    before. Deliberately versioned (_v1): if the gate's own legal/product copy ever changes
	///    meaningfully later, bumping the key to _v2 naturally re-prompts everyone.
	/// 2. <c>license.State.IsPro</c> is already true from LicenseService's own local cache (populated
	///    synchronously in its constructor, no await needed) -- a returning Pro owner on a fresh
	///    app-data-wipe (which would also have cleared the "accepted" flag above) should not be re-gated
	///    just because that ONE flag got cleared; their Pro status alone already answers the question this
	///    gate is asking.
	///
	/// If neither applies, the gate IS shown; TermsGatePage.OnAppearing kicks off its own short live
	/// RefreshAsync in case a Pro purchase completed on another device since this cache was last refreshed.</summary>
	private static Page CreateMainPage()
	{
		ILicenseService? license = IPlatformApplication.Current?.Services.GetService<ILicenseService>();
		bool accepted = Preferences.Default.Get(Views.TermsGatePage.TermsAcceptedKey, false);
		if (license == null || accepted || license.State.IsPro) return new AppShell();
		return new Views.TermsGatePage(license, () => new AppShell());
	}
}