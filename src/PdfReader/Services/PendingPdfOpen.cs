namespace PdfReader.Services;

/// <summary>
/// Single-slot handoff from MainActivity (Platforms/Android) to whichever MAUI page is on screen: a PDF
/// was just handed to the app via "Open with" / "Share" from another app.
///
/// Two setters, not one, because the two callers in MainActivity are NOT interchangeable:
/// <see cref="Set"/> is for OnCreate (a brand-new Activity, so a brand-new AppShell is about to be built
/// -- its own constructor reads <see cref="Uri"/> directly, synchronously, with nothing else in between,
/// so no event is needed and firing one would be actively wrong). <see cref="SetAndNotify"/> is for
/// OnNewIntent (LaunchMode.SingleTop: the app was already running, the existing AppShell is still the
/// live one, so <see cref="Changed"/> is how it finds out).
///
/// Why this split exists (2026-10-12 crash fix): closing a PDF opened "externally" finishes the Activity
/// (ReaderPage.LeaveAsync), but Finish() does not necessarily kill the whole process -- picking a second
/// file from the file manager right after can relaunch MainActivity.OnCreate IN THE SAME PROCESS, where
/// the static fields here (and the previous AppShell instance) are still alive. With a single Set() that
/// always fired Changed, OnCreate's call fired it BEFORE the new AppShell's constructor had run (it runs
/// later, inside the base.OnCreate() call that follows) -- so the only subscriber was still the OLD
/// AppShell, from the just-finished Activity. That handler ran anyway, calling into a Shell no longer
/// attached to a live Activity, and crashed. OnCreate must never fire this event; only OnNewIntent may.
/// </summary>
public static class PendingPdfOpen
{
	public static string? Uri { get; private set; }

	public static event Action? Changed;

	/// <summary>Cold start (MainActivity.OnCreate): stash the Uri only. The AppShell about to be
	/// constructed picks it up by reading <see cref="Uri"/> directly -- see the class comment for why
	/// this must NOT also raise <see cref="Changed"/>.</summary>
	public static void Set(string uri) => Uri = uri;

	/// <summary>Already running (MainActivity.OnNewIntent): the current AppShell's subscribed handler is
	/// the only -- and definitely live -- listener, so tell it.</summary>
	public static void SetAndNotify(string uri)
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
