namespace PdfReader.Models;

/// <summary>
/// One entry in the Dashboard's "recently opened" list. New for PdfReader (plan section 7: DocScanner's
/// DocumentStore/Models are specific to its "scan -> document -> folder" shape and do not fit a reader).
/// <see cref="Uri"/> is whatever string the file was opened with -- a content:// Uri from the system
/// picker or an "Open with" intent, or a plain path -- and is handed straight back to
/// <see cref="Services.PdfPages.Open"/> to reopen it. TODO (plan section 8 item 4): a content:// Uri from
/// ACTION_GET_CONTENT / MAUI's FilePicker is not guaranteed to still be readable after the app restarts;
/// moving to ACTION_OPEN_DOCUMENT + persistable Uri permissions is an open decision, not resolved here.
/// </summary>
public sealed record RecentPdfRecord(string Uri, string DisplayName, int PageCount, DateTimeOffset LastOpenedUtc, double ReadProgress)
{
	/// <summary>PNG thumbnail of the real first page, cached under AppDataDirectory/thumbnails -- null
	/// until <see cref="Services.RecentPdfStore"/> has rendered one for this entry.</summary>
	public string? ThumbnailPath { get; init; }

	/// <summary>0-based page the reader was on when it was last closed, so reopening resumes there.</summary>
	public int LastPage { get; init; }
}
