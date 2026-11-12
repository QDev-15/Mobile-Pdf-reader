using PdfReader.Core.Licensing;
using PdfReader.Services;

namespace PdfReader.Views;

/// <summary>
/// Blocking startup gate shown once before the user ever reaches AppShell/DashboardPage -- owner's request,
/// ported 2026-11-12 from the sibling LogoExtract repo's own already-shipped version of this exact feature.
/// Constructed directly (<c>new Views.TermsGatePage(...)</c>) from App.xaml.cs's own CreateMainPage, the same
/// way App.CreateWindow already constructs <c>new Views.SplashPage(...)</c> and AppShell itself -- there is
/// no Shell instance yet at this point in the startup sequence, so this page is NOT DI-resolved/Shell-routed
/// and is NOT registered in MauiProgram.cs.
///
/// Plain code-behind (named elements + event handlers), matching SettingsPage.xaml.cs's own idiom -- this
/// app has no reference to CommunityToolkit.Mvvm anywhere (confirmed by reading PdfReader.csproj), unlike
/// the sibling LogoExtract/DocScanner repos, so no ViewModel class was introduced just for this one page.
///
/// Exactly 3 outcomes, all funnelled through <see cref="Proceed"/> or <see cref="Exit"/>:
/// <list type="bullet">
/// <item>Buy Pro successfully (Purchased/AlreadyOwned), or a live refresh discovers Pro already true (bought
/// on another device) -- proceeds WITHOUT setting <see cref="TermsAcceptedKey"/>: that flag specifically
/// means "agreed to the ad-supported terms", which isn't true for a Pro buyer; a later refund should
/// correctly re-show this gate next launch (App.xaml.cs's CreateMainPage falls back to
/// <see cref="ILicenseService.State"/>.IsPro for returning Pro owners independently of this flag).</item>
/// <item>Tick the checkbox + "Đồng ý &amp; Tiếp tục" -- the free/ad-supported path; persists the flag.</item>
/// <item>"Từ chối &amp; Thoát", or the hardware/gesture Back button -- exits the whole app.</item>
/// </list>
/// </summary>
public partial class TermsGatePage : ContentPage
{
	/// <summary>Preferences key for "has this install already agreed to this gate's ad-supported terms at
	/// least once". Versioned (_v1) by design: if this page's own legal/product copy ever changes
	/// meaningfully, bumping to _v2 naturally re-prompts every existing install -- same idiom
	/// LicenseService's own "license_is_pro_cached" key already uses for its own (unrelated) flag in this app,
	/// and SettingsPage's ThemeKey/QualityKey's own "no wrapper class, just a local const string" idiom.</summary>
	public const string TermsAcceptedKey = "terms_accepted_v1";

	private readonly ILicenseService _license;
	private readonly Func<Page> _createShell;
	private bool _busy;

	/// <summary>True once any of the 3 outcomes has already fired -- guards against a double hand-off, e.g.
	/// the background refresh's own late "turns out Pro" result arriving a moment after the user already
	/// tapped "Từ chối &amp; Thoát" (or vice versa); whichever happens first wins, the other is ignored.</summary>
	private bool _handled;

	public TermsGatePage(ILicenseService license, Func<Page> createShell)
	{
		InitializeComponent();
		_license = license;
		_createShell = createShell;
		RefreshBuyButtonText();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		// Fire-and-forget on purpose -- showing the gate's own content must not wait on a possibly slow or
		// unreachable Play connection; same "already correct instantly from cache, re-verify live after"
		// idea as App.xaml.cs's CreateMainPage's own synchronous cache read.
		_ = RefreshInBackgroundAsync();

		// Owner-requested, live on-device feedback 2026-11-12: the two main CTAs go side-by-side in
		// landscape. Apply the current orientation immediately (covers the page appearing while already
		// rotated), then track live rotation while this page is on screen.
		ApplyOrientation(DeviceDisplay.Current.MainDisplayInfo.Orientation);
		DeviceDisplay.Current.MainDisplayInfoChanged += OnMainDisplayInfoChanged;
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		// Unsubscribe discipline this app already follows elsewhere for event subscriptions taken in
		// OnAppearing (e.g. SettingsPage's own ILicenseService.Changed -= in OnDisappearing) -- this page is
		// constructed once and never re-shown, but leaving the subscription live after it is gone would
		// still leak a reference to a dead page via DeviceDisplay's own static event.
		DeviceDisplay.Current.MainDisplayInfoChanged -= OnMainDisplayInfoChanged;
	}

	private void OnMainDisplayInfoChanged(object? sender, DisplayInfoChangedEventArgs e) =>
		MainThread.BeginInvokeOnMainThread(() => ApplyOrientation(e.DisplayInfo.Orientation));

	/// <summary>Moves (not clones) <see cref="BuyButton"/>/<see cref="AgreeButton"/> between the portrait
	/// stack and the landscape two-column row so both layouts share the exact same Button instances -- their
	/// enabled/disabled/busy state (set elsewhere, e.g. <see cref="OnAgreeCheckedChanged"/>, <see cref="SetBusy"/>)
	/// then never needs separate handling per orientation.</summary>
	private void ApplyOrientation(DisplayOrientation orientation)
	{
		bool landscape = orientation == DisplayOrientation.Landscape;
		if (landscape && CtaPortraitRow.Children.Contains(BuyButton))
		{
			CtaPortraitRow.Children.Clear();
			Grid.SetColumn(BuyButton, 0);
			Grid.SetColumn(AgreeButton, 1);
			CtaLandscapeRow.Children.Add(BuyButton);
			CtaLandscapeRow.Children.Add(AgreeButton);
			CtaPortraitRow.IsVisible = false;
			CtaLandscapeRow.IsVisible = true;
		}
		else if (!landscape && CtaLandscapeRow.Children.Contains(BuyButton))
		{
			CtaLandscapeRow.Children.Clear();
			CtaPortraitRow.Children.Add(BuyButton);
			CtaPortraitRow.Children.Add(AgreeButton);
			CtaPortraitRow.IsVisible = true;
			CtaLandscapeRow.IsVisible = false;
		}
	}

	/// <summary>Hardware/gesture Back must behave exactly like the explicit "Từ chối &amp; Thoát" button --
	/// owner's exact spec -- except it closes a purchase-error/timeout dialog first if one happens to be open
	/// right now, same "dismiss the topmost open overlay before falling through" rule every other page in
	/// this app already follows (e.g. SettingsPage/AboutPage's own `this.TryDismiss() || base...`).</summary>
	protected override bool OnBackButtonPressed()
	{
		if (this.TryDismiss()) return true;
		Exit();
		return true;
	}

	/// <summary>Covers the one case the synchronous cache check in App.xaml.cs's CreateMainPage cannot: a Pro
	/// purchase that completed on a DIFFERENT device with the same Google account since this device's own
	/// cache was last refreshed. Short (2.5s) timeout instead of letting a slow/unreachable Play connection
	/// hang this screen's own "please wait" indefinitely -- there isn't one shown here at all, this runs
	/// entirely behind the already-visible gate content.</summary>
	private async Task RefreshInBackgroundAsync()
	{
		try { await _license.RefreshAsync().WaitOrTimeoutAsync(2_500); }
		catch
		{
			// Best-effort only -- the cache already decided it was worth showing this page at all; a failed
			// or timed-out live refresh just means the user answers the question themselves instead.
		}
		MainThread.BeginInvokeOnMainThread(() =>
		{
			RefreshBuyButtonText();
			if (_license.State.IsPro) ProceedWithoutAcceptance(); // outcome 1-equivalent, no tap needed
		});
	}

	private void RefreshBuyButtonText() =>
		// Same "append price once known" pattern as SettingsPage.OnLicenseChanged's own BuyButton.Text --
		// ProPriceText is null until the first successful RefreshAsync (or if Play can't be reached), so the
		// button reads a plain "Mua Pro ngay" until then.
		BuyButton.Text = _license.ProPriceText is { } price ? $"Mua Pro ngay · {price}" : "Mua Pro ngay";

	private void OnToggleAgreedTapped(object? sender, TappedEventArgs e) => AgreeCheck.IsChecked = !AgreeCheck.IsChecked;

	private void OnAgreeCheckedChanged(object? sender, CheckedChangedEventArgs e)
	{
		// Explicit Opacity dim alongside IsEnabled: this button's BackgroundColor/BorderColor/TextColor are
		// set directly in XAML (not via the unnamed global Button style), so the global style's own Disabled
		// VisualState setters would not reliably repaint over those local values -- dimming by hand is the
		// same belt-and-suspenders visibility guarantee, independent of style precedence.
		AgreeButton.IsEnabled = e.Value;
		AgreeButton.Opacity = e.Value ? 1 : 0.45;
	}

	private void OnAgreeAndContinueClicked(object? sender, EventArgs e)
	{
		if (!AgreeCheck.IsChecked) return; // belt-and-suspenders -- the button is disabled until ticked anyway
		ProceedWithAcceptance();
	}

	private void OnDeclineClicked(object? sender, EventArgs e) => Exit();

	private async void OnBuyClicked(object? sender, EventArgs e)
	{
		if (_busy) return;
		if (_license.State.IsPro) { ProceedWithoutAcceptance(); return; }
		SetBusy(true);
		try
		{
			// Same hard rule, dialog copy and WaitOrTimeoutAsync helper as SettingsPage.OnBuyClicked and
			// ProUpgrade.ShowAsync: the spinner never stays up longer than 1 minute, even if Google Play
			// never answers; PurchaseProAsync has no cancellation hook, so on timeout it is left running.
			(bool completed, PurchaseOutcome outcome) = await _license.PurchaseProAsync().WaitOrTimeoutAsync();
			if (!completed)
			{
				await this.AlertAsync("Quá thời gian chờ", "Google Play không phản hồi sau 1 phút. Hãy thử lại.", "Đóng");
				return;
			}
			switch (outcome)
			{
				case PurchaseOutcome.Purchased or PurchaseOutcome.AlreadyOwned:
					await this.AlertAsync("Cảm ơn bạn!", "Đã nâng cấp PDF Reader Pro. Quảng cáo đã được tắt.", "OK");
					ProceedWithoutAcceptance();
					break;
				case PurchaseOutcome.Error:
					await this.AlertAsync("Chưa mua được", _license.LastError ?? "Không kết nối được Google Play. Hãy thử lại sau.", "Đóng");
					break;
				// Cancelled: no dialog -- closing Play's own purchase sheet needs no extra confirmation.
			}
		}
		finally { SetBusy(false); }
	}

	private void SetBusy(bool busy)
	{
		_busy = busy;
		Busy.IsVisible = Busy.IsRunning = busy;
	}

	private void ProceedWithAcceptance()
	{
		Preferences.Default.Set(TermsAcceptedKey, true);
		Proceed();
	}

	/// <summary>Buying Pro (or discovering Pro from another device) proceeds WITHOUT persisting the
	/// acceptance flag -- see this class's own doc comment for why (a later refund should re-show this gate,
	/// not skip it forever on a stale flag).</summary>
	private void ProceedWithoutAcceptance() => Proceed();

	private void Proceed()
	{
		if (_handled) return;
		_handled = true;
		// Same hard-cut hand-off technique as SplashPage.OnAppearing's own "Window.Page = main" -- no
		// cross-fade (a fading page would expose the window's plain background behind it mid-transition).
		if (Window != null) Window.Page = _createShell();
	}

	private void Exit()
	{
		if (_handled) return;
		_handled = true;
		// Owner's exact spec: let them exit the app entirely, not just this page. FinishAffinity closes this
		// Activity and every other Activity in the same task stack, which for this single-Activity MAUI app
		// is the whole process -- same API AppInfo-adjacent permission flows elsewhere in MAUI already use.
		Platform.CurrentActivity?.FinishAffinity();
	}
}
