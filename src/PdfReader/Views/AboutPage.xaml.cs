using PdfReader.Services;

namespace PdfReader.Views;

/// <summary>About the app: author and contact, version, terms of use, privacy, copyright and third-party
/// components. Same content and contact details as DocScanner's.</summary>
public partial class AboutPage : ContentPage
{
	public const string SupportEmail = "nguyenquynhvp.ictu@gmail.com";
	public const string SupportPhone = "0988632841";

	public AboutPage()
	{
		InitializeComponent();
		VersionLabel.Text = $"Phiên bản {AppInfo.Current.VersionString} (bản dựng {AppInfo.Current.BuildString})";
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		Banner.Reattach();
	}

	protected override bool OnBackButtonPressed() => Dialogs.TryDismiss() || base.OnBackButtonPressed();

	private async void OnEmail(object? sender, TappedEventArgs e)
	{
		string subject = Uri.EscapeDataString($"PDF Reader {AppInfo.Current.VersionString} - hỗ trợ");
		try { await Launcher.Default.OpenAsync($"mailto:{SupportEmail}?subject={subject}"); }
		catch (Exception)
		{
			await Clipboard.Default.SetTextAsync(SupportEmail);
			await this.AlertAsync("Email", "Đã chép địa chỉ email.", "OK");
		}
	}

	private async void OnPhone(object? sender, TappedEventArgs e)
	{
		try { await Launcher.Default.OpenAsync($"tel:{SupportPhone}"); }
		catch (Exception)
		{
			await Clipboard.Default.SetTextAsync(SupportPhone);
			await this.AlertAsync("Điện thoại", "Đã chép số điện thoại.", "OK");
		}
	}
}
