namespace PdfReader.Services;

/// <summary>
/// Every busy pill / spinner in the app must resolve within a bounded time -- a bug, a stuck native
/// callback (OCR, Play Billing, ...), or just a slow disk must never leave a spinner on screen with no
/// way to close it. <see cref="DefaultTimeoutMs"/> (1 minute) is the one place that bound is defined.
/// </summary>
public static class TaskTimeoutExtensions
{
	public const int DefaultTimeoutMs = 60_000;

	/// <summary>Waits for <paramref name="task"/>, but gives up after <paramref name="timeoutMs"/> instead
	/// of waiting forever. True when <paramref name="task"/> actually finished in time (an exception from
	/// it still propagates, as a normal await would); false on timeout -- <paramref name="task"/> is left
	/// running in the background (nothing here can forcibly abort code that was never cancellable), its
	/// eventual result or failure simply unobserved.</summary>
	public static async Task<bool> WaitOrTimeoutAsync(this Task task, int timeoutMs = DefaultTimeoutMs)
	{
		Task winner = await Task.WhenAny(task, Task.Delay(timeoutMs));
		if (winner != task) return false;
		await task; // already completed: rethrows if it faulted, no actual wait
		return true;
	}

	/// <summary>Same as the non-generic overload, returning the result alongside whether it completed in time.</summary>
	public static async Task<(bool Completed, T? Result)> WaitOrTimeoutAsync<T>(this Task<T> task, int timeoutMs = DefaultTimeoutMs)
	{
		Task winner = await Task.WhenAny(task, Task.Delay(timeoutMs));
		if (winner != task) return (false, default);
		return (true, await task);
	}
}
