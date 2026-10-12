namespace PdfReader.Services;

/// <summary>
/// Queued handoff from MainActivity (Platforms/Android) to whichever MAUI page is on screen: a PDF
/// was just handed to the app via "Open with" / "Share" from another app.
///
/// A queue, not a single slot: two files opened back-to-back (e.g. picking a second one from the file
/// manager before the app has finished processing the first "Open with" intent) must both be handled, not
/// have the second silently overwrite the first before it is <see cref="Consume"/>d.
///
/// Two setters, not one, because the two callers in MainActivity are NOT interchangeable:
/// <see cref="Set"/> is for OnCreate (a brand-new Activity, so a brand-new AppShell is about to be built
/// -- its own constructor reads <see cref="Peek"/> directly, synchronously, with nothing else in between,
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
	private static readonly Queue<string> Pending = new();

	/// <summary>The next pending Uri, if any, without removing it -- for AppShell's constructor, which only
	/// ever looks at whatever arrived at cold start (see the class comment).</summary>
	public static string? Peek => Pending.Count > 0 ? Pending.Peek() : null;

	public static event Action? Changed;

	/// <summary>Cold start (MainActivity.OnCreate): queue the Uri. The AppShell about to be constructed picks
	/// it up by reading <see cref="Peek"/> directly -- see the class comment for why this must NOT also raise
	/// <see cref="Changed"/>.</summary>
	public static void Set(string uri) => Pending.Enqueue(uri);

	/// <summary>Already running (MainActivity.OnNewIntent): queue the Uri and tell the current AppShell's
	/// subscribed handler -- the only, and definitely live, listener. Queued (not overwritten) so a second
	/// file opened before the first is <see cref="Consume"/>d is not silently dropped.</summary>
	public static void SetAndNotify(string uri)
	{
		Pending.Enqueue(uri);
		Changed?.Invoke();
	}

	/// <summary>Returns the oldest pending Uri (if any) and removes it, so it is only acted on once. A
	/// listener that still finds more afterward (<see cref="Peek"/> non-null) should go round again instead of
	/// assuming it drained everything.</summary>
	public static string? Consume() => Pending.Count > 0 ? Pending.Dequeue() : null;
}
