namespace PdfReader.Services;

/// <summary>
/// Single-slot handoff from MainActivity (Platforms/Android) to whichever MAUI page is on screen: a PDF
/// was just handed to the app via "Open with" / "Share" from another app. MainActivity.OnCreate /
/// OnNewIntent call <see cref="Set"/>; DashboardPage checks <see cref="Consume"/> when it appears and
/// also subscribes to <see cref="Changed"/> while it is the active page (the LaunchMode.SingleTop case:
/// the app is already running and Dashboard is already on screen, so OnAppearing will not fire again).
/// </summary>
public static class PendingPdfOpen
{
	public static string? Uri { get; private set; }

	public static event Action? Changed;

	public static void Set(string uri)
	{
		Uri = uri;
		Changed?.Invoke();
	}

	/// <summary>Returns the pending Uri (if any) and clears it, so it is only acted on once.</summary>
	public static string? Consume()
	{
		string? uri = Uri;
		Uri = null;
		return uri;
	}
}
