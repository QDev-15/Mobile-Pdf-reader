using PdfReader.Core;
using PdfReader.Core.Licensing;
using PdfReader.Platforms.Android;
using PdfReader.Services;

namespace PdfReader.Views;

/// <summary>Settings, modelled on DocScanner's: Pro (remove ads), reading and export defaults, storage,
/// version, and the About page. Defaults live in Preferences under the keys the Reader and the export
/// dialog read (<see cref="ThemeKey"/>, <see cref="QualityKey"/>).</summary>
public partial class SettingsPage : ContentPage
{
	public const string ThemeKey = "reader_theme";
	public const string QualityKey = "export_quality";

	private static readonly string[] ThemeNames = ["Sáng", "Tối", "Sepia (giấy vàng)"];

	private readonly ILicenseService _license;
	private readonly PdfLibrary _library;
	private bool _busy;
	private bool _loading;

	public SettingsPage(ILicenseService license, PdfLibrary library)
	{
		InitializeComponent();
		_license = license;
		_library = library;
		ThemePicker.ItemsSource = ThemeNames;
		QualityPicker.ItemsSource = PdfQuality.All.Select(q => q.Label).ToList();
		VersionLabel.Text = $"Phiên bản {AppInfo.Current.VersionString} (bản dựng {AppInfo.Current.BuildString})";
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		Banner.Reattach();
		_license.Changed += OnLicenseChanged;
		OnLicenseChanged();

		_loading = true;
		ThemePicker.SelectedIndex = Math.Clamp(Preferences.Default.Get(ThemeKey, 0), 0, ThemeNames.Length - 1);
		QualityPicker.SelectedIndex = Math.Max(0, PdfQuality.All.ToList().FindIndex(q => q.Key == Preferences.Default.Get(QualityKey, PdfQuality.Medium.Key)));
		_loading = false;

		(int count, long bytes) = _library.Usage();
		StorageLabel.Text = count == 0 ? "Chưa có tài liệu nào được lưu trong ứng dụng." : $"{count} tài liệu · {FormatSize(bytes)} trong bộ nhớ ứng dụng";
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_license.Changed -= OnLicenseChanged;
	}

	protected override bool OnBackButtonPressed() => Dialogs.TryDismiss() || base.OnBackButtonPressed();

	private void OnLicenseChanged() => MainThread.BeginInvokeOnMainThread(() =>
	{
		bool free = !_license.State.IsPro;
		LicenseLabel.Text = _license.State.SummaryText;
		BuyRow.IsVisible = free;
		ProBenefits.IsVisible = free;
		BuyButton.Text = _license.ProPriceText is { } price ? $"Mua Pro · {price}" : "Mua Pro";
	});

	private async void OnBackClicked(object? sender, EventArgs e) => await UiHost.Shell.GoToAsync("..");

	private void OnThemeChanged(object? sender, EventArgs e)
	{
		if (!_loading && ThemePicker.SelectedIndex >= 0) Preferences.Default.Set(ThemeKey, ThemePicker.SelectedIndex);
	}

	private void OnQualityChanged(object? sender, EventArgs e)
	{
		if (!_loading && QualityPicker.SelectedIndex >= 0) Preferences.Default.Set(QualityKey, PdfQuality.All[QualityPicker.SelectedIndex].Key);
	}

	private async void OnAboutTapped(object? sender, TappedEventArgs e) => await UiHost.Shell.GoToAsync(nameof(AboutPage));

	// ------------------------------------------------------------------ Pro

	private void SetBusy(bool busy)
	{
		_busy = busy;
		LicenseBusy.IsVisible = LicenseBusy.IsRunning = busy;
	}

	private async void OnBuyClicked(object? sender, EventArgs e)
	{
		if (_busy || _license.State.IsPro) return;
		SetBusy(true);
		try
		{
			PurchaseOutcome outcome = await _license.PurchaseProAsync();
			switch (outcome)
			{
				case PurchaseOutcome.Purchased or PurchaseOutcome.AlreadyOwned:
					await this.AlertAsync("Cảm ơn bạn!", "Đã nâng cấp PDF Reader Pro. Quảng cáo đã được tắt.", "OK");
					break;
				case PurchaseOutcome.Error:
					await this.AlertAsync("Chưa mua được", _license.LastError ?? "Không kết nối được Google Play. Hãy thử lại sau.", "Đóng");
					break;
			}
		}
		finally { SetBusy(false); }
	}

	private async void OnRestoreClicked(object? sender, EventArgs e)
	{
		if (_busy) return;
		SetBusy(true);
		try
		{
			PurchaseOutcome outcome = await _license.RestoreAsync();
			string message = outcome switch
			{
				PurchaseOutcome.Purchased or PurchaseOutcome.AlreadyOwned => "Đã khôi phục: tài khoản Google Play này đã mua Pro.",
				PurchaseOutcome.Error => _license.LastError ?? "Không kết nối được Google Play.",
				_ => "Không tìm thấy giao dịch mua Pro nào với tài khoản Google Play đang đăng nhập.",
			};
			await this.AlertAsync("Khôi phục giao dịch", message, "OK");
		}
		finally { SetBusy(false); }
	}

	public static string FormatSize(long bytes)
	{
		long kb = Math.Max(1, bytes / 1024);
		return kb < 1000 ? $"{kb} KB" : $"{bytes / 1_048_576.0:0.#} MB";
	}
}
