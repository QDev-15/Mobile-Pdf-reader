using PdfReader.Services;

namespace PdfReader.Views;

/// <summary>
/// What the app shows when it was started by another app's "Open with" / "Share" of a PDF: just the logo
/// for the instant it takes to copy the file in, then straight into the reader -- the Dashboard is never
/// shown. If the file cannot be opened the person lands on the Dashboard instead.
/// </summary>
public sealed class LaunchPage : ContentPage
{
	private readonly PdfOpener _opener;
	private bool _started;

	public LaunchPage(PdfOpener opener)
	{
		_opener = opener;
		BackgroundColor = (Color)Application.Current!.Resources["MilkBackground"];
		Shell.SetNavBarIsVisible(this, false);
		Content = new Grid
		{
			Children =
			{
				new VerticalStackLayout
				{
					VerticalOptions = LayoutOptions.Center,
					HorizontalOptions = LayoutOptions.Center,
					Spacing = 18,
					Children =
					{
						new Image { Source = "logo.png", WidthRequest = 96, HeightRequest = 96 },
						new ActivityIndicator { IsRunning = true, Color = (Color)Application.Current.Resources["Primary"], HeightRequest = 26 },
					},
				},
			},
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (_started) return;
		_started = true;

		string? uri = PendingPdfOpen.Consume();
		bool opened = uri != null && await _opener.OpenAsync(uri, external: true);
		if (!opened) await UiHost.Shell.GoToAsync("//DashboardPage");
	}

	protected override bool OnBackButtonPressed()
	{
		Platform.CurrentActivity?.Finish();
		return true;
	}
}
