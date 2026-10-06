namespace PdfReader.Services;

/// <summary>Where the app's Shell is, reached through the Application's window. <c>Shell.Current</c> throws
/// "Unable to determine the current Shell instance" when asked before a window has a page (a cold start
/// straight from another app's "Open with") or while a second window exists, so everything goes through
/// here instead.</summary>
public static class UiHost
{
	public static Shell Shell =>
		Application.Current?.Windows.Select(w => w.Page).OfType<Shell>().FirstOrDefault()
		?? throw new InvalidOperationException("Ứng dụng chưa sẵn sàng.");

	public static Shell? ShellOrNull => Application.Current?.Windows.Select(w => w.Page).OfType<Shell>().FirstOrDefault();
}
