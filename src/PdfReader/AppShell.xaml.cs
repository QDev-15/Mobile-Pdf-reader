using PdfReader.Services;
using PdfReader.Views;

namespace PdfReader;

public partial class AppShell : Shell
{
	private static AppShell? _live;

	public AppShell()
	{
		InitializeComponent();
		Routing.RegisterRoute(nameof(ReaderPage), typeof(ReaderPage));
		Routing.RegisterRoute(nameof(PageManagerPage), typeof(PageManagerPage));
		Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
		Routing.RegisterRoute(nameof(AboutPage), typeof(AboutPage));

		// Started by another app's "Open with": go straight to the reader, never through the Dashboard.
		if (PendingPdfOpen.Uri != null)
		{
			var launch = new ShellContent
			{
				Title = "Launch",
				Route = "LaunchPage",
				ContentTemplate = new DataTemplate(typeof(LaunchPage)),
			};
			Items.Add(launch);
			CurrentItem = launch;
		}

		// The app was already running when a PDF arrived from elsewhere.
		if (_live != null) PendingPdfOpen.Changed -= _live.OnPendingOpen;
		_live = this;
		PendingPdfOpen.Changed += OnPendingOpen;
	}

	private void OnPendingOpen() =>
		MainThread.BeginInvokeOnMainThread(async () =>
		{
			string? uri = PendingPdfOpen.Consume();
			PdfOpener? opener = Handler?.MauiContext?.Services.GetService<PdfOpener>();
			if (uri != null && opener != null) await opener.OpenAsync(uri, external: true);
		});
}
