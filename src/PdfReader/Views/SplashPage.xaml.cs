namespace PdfReader.Views;

/// <summary>
/// The start screen, right after the system splash (same milk-white background, see SplashPage.xaml's comment,
/// so the two join up): the logo grows in while the real app (AppShell) is built behind the scenes, then this
/// page is replaced with a cut (no fade: a fading page shows the window's background behind it) once both are
/// done (at least <see cref="MinimumTime"/>, so the screen does not just flash by). Same technique as the
/// sibling DocScanner repo's own SplashPage, minus its continuous "scan bar" sweep -- that motif fits a document
/// SCANNER, not a PDF READER, so this one only plays the entrance animation and leans on the spinner at the
/// bottom for "still working".
/// </summary>
public partial class SplashPage : ContentPage
{
	private static readonly TimeSpan MinimumTime = TimeSpan.FromMilliseconds(1100);

	private readonly Func<Page> _main;
	private bool _started;

	public SplashPage(Func<Page> main)
	{
		InitializeComponent();
		_main = main;
		// AppInfo.Current.BuildString is the native build number (Android versionCode, e.g. PdfReader.csproj's
		// ApplicationVersion) -- distinct from VersionString (ApplicationDisplayVersion, e.g. "1.0").
		VersionLabel.Text = $"Phiên bản {AppInfo.Current.VersionString} (bản dựng {AppInfo.Current.BuildString})";
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (_started) return;
		_started = true;
		var shown = Task.Delay(MinimumTime);

		// FadeTo/ScaleTo (Microsoft.Maui.Controls.ViewExtensions), not "...Async" -- they already return
		// Task<bool>, there is no separate Async-suffixed overload. (2026-10-19: copying DocScanner's own
		// SplashPage literally -- which itself defines its own FadeToAsync/ScaleToAsync/TranslateToAsync
		// wrappers elsewhere, not shown when only SplashPage.xaml.cs was read -- used these names here where no
		// such wrapper exists in PdfReader, and somehow still built; left the page permanently blank at runtime
		// with no compiler or runtime error either way. Root cause not fully explained; use the real API names.)
		await Task.WhenAll(
			Mark.FadeTo(1, 350, Easing.CubicOut),
			Mark.ScaleTo(1, 450, Easing.SpringOut));

		await Task.Yield();
		Page main = _main(); // build the main screen while the minimum-time delay above finishes
		await shown;
		if (Window != null) Window.Page = main;
	}
}
