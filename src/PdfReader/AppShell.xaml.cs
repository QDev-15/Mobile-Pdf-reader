using PdfReader.Services;
using PdfReader.Views;

namespace PdfReader;

public partial class AppShell : Shell
{
	private static AppShell? _live;

	// Captured only for a cold-start "Open with" (see the constructor): lets RestoreDashboard() put Dashboard
	// back and drop the temporary Launch tab once it is no longer needed.
	private ShellItem? _dashboardItem;
	private ShellItem? _launchItem;

	public AppShell()
	{
		InitializeComponent();
		Routing.RegisterRoute(nameof(ReaderPage), typeof(ReaderPage));
		Routing.RegisterRoute(nameof(PageManagerPage), typeof(PageManagerPage));
		Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
		Routing.RegisterRoute(nameof(AboutPage), typeof(AboutPage));

		// Started by another app's "Open with": go straight to the reader, never through the Dashboard.
		if (PendingPdfOpen.Peek != null)
		{
			// Pull Dashboard out of Items for now -- Shell shows a bottom tab bar the moment there are 2+
			// top-level items, which would put Dashboard right next to Launch instead of going straight to
			// the reader as designed. RestoreDashboard() below puts it back once Launch is no longer needed.
			_dashboardItem = Items.Count > 0 ? Items[0] : null;
			if (_dashboardItem != null) Items.Remove(_dashboardItem);

			var launch = new ShellContent
			{
				Title = "Launch",
				Route = "LaunchPage",
				ContentTemplate = new DataTemplate(typeof(LaunchPage)),
			};
			Items.Add(launch);
			_launchItem = Items[^1];
			CurrentItem = launch;
		}

		// The app was already running when a PDF arrived from elsewhere.
		if (_live != null) PendingPdfOpen.Changed -= _live.OnPendingOpen;
		_live = this;
		PendingPdfOpen.Changed += OnPendingOpen;
	}

	/// <summary>Removes the temporary Launch tab added for a cold-start "Open with" and puts Dashboard back, so
	/// the Shell returns to its normal single-tab state instead of leaving Launch lingering in the Shell for the
	/// rest of this Activity's life. Called once Launch's job is done and nothing is still showing through it
	/// (LaunchPage falls back to Dashboard when it could not open the file).</summary>
	public void RestoreDashboard()
	{
		if (_launchItem != null) { Items.Remove(_launchItem); _launchItem = null; }
		if (_dashboardItem != null && !Items.Contains(_dashboardItem)) Items.Add(_dashboardItem);
	}

	private void OnPendingOpen() =>
		MainThread.BeginInvokeOnMainThread(async () =>
		{
			string? uri = PendingPdfOpen.Consume();
			PdfOpener? opener = Handler?.MauiContext?.Services.GetService<PdfOpener>();
			if (uri != null && opener != null) await opener.OpenAsync(uri, external: true);
		});
}
