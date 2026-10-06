namespace PdfReader.Services;

/// <summary>Copies a finished file into the phone's public Downloads folder, in its own "PdfReader"
/// subfolder (so it is easy to find later with any file manager). Ported from DocScanner's
/// IDownloadsService (Mobile-doc-scanner repo).</summary>
public interface IDownloadsService
{
	/// <summary>True when this Android version supports it without extra permissions (Android 10+).</summary>
	bool IsSupported { get; }

	/// <summary>Saves a copy under <paramref name="displayName"/> (Android adds " (1)" etc. if taken) and
	/// returns the name it was saved as.</summary>
	Task<string> SaveAsync(string sourcePath, string displayName, string mimeType, CancellationToken ct = default);
}
