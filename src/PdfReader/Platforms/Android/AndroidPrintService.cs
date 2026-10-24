using Android.Content;
using Android.OS;
using Android.Print;
using PdfReader.Core.Pdf;
using PdfReader.Services;
using Stream = System.IO.Stream;

namespace PdfReader;

/// <summary>
/// Hands the file straight to Android's PrintManager as-is (content type "application/pdf"): the OS/print
/// service renders it, so this never has to rasterise pages itself. OnWrite streams the file's bytes into
/// whatever destination the print service asks for -- printer picker (Wi-Fi/network printers via Mopria
/// etc., USB/Bluetooth via a vendor print-service app), paper size and copies are all the platform's own
/// print-preview screen. The one piece this class must still do itself: when the user picks a custom page
/// range in that screen, <paramref name="pages"/> arrives narrowed to just that range, so the bytes actually
/// written have to be narrowed to match -- the OS never slices the PDF for us.
/// </summary>
public sealed class AndroidPrintService(PdfLibrary library) : IPrintService
{
	public bool IsSupported => true; // PrintManager has existed since API 19; this app's floor is 26.

	public Task PrintAsync(string pdfPath, string jobName, int pageCount)
	{
		// PrintManager throws "Can print only from an activity" if fetched from the Application context --
		// it must come from the current Activity.
		Context activity = Platform.CurrentActivity ?? throw new InvalidOperationException("Không tìm thấy màn hình hiện tại để in.");
		var printManager = (PrintManager?)activity.GetSystemService(Context.PrintService)
			?? throw new InvalidOperationException("Thiết bị không hỗ trợ in ấn.");
		string safeName = SafeJobName(jobName);
		var attributes = new PrintAttributes.Builder().Build();
		printManager.Print(safeName, new PdfPrintDocumentAdapter(pdfPath, safeName, Math.Max(1, pageCount), library), attributes);
		return Task.CompletedTask;
	}

	// PrintManager.Print's job-name argument shows up in the OS print queue/notification; an empty or
	// over-long one can look broken there, same reasoning as every other "safe file name" helper in this app.
	private static string SafeJobName(string name) => string.IsNullOrWhiteSpace(name) ? "Document.pdf" : name;

	private sealed class PdfPrintDocumentAdapter(string pdfPath, string jobName, int pageCount, PdfLibrary library) : PrintDocumentAdapter
	{
		public override void OnLayout(PrintAttributes? oldAttributes, PrintAttributes? newAttributes,
			CancellationSignal? cancellationSignal, LayoutResultCallback? callback, Bundle? extras)
		{
			if (cancellationSignal?.IsCanceled == true)
			{
				callback?.OnLayoutCancelled();
				return;
			}

			var info = new PrintDocumentInfo.Builder(jobName)
				.SetContentType(PrintContentType.Document)
				.SetPageCount(pageCount)
				// Without a known page count the system print dialog hides the "custom range" option
				// entirely and only offers "All" -- SetPageCount is what turns on "In từ trang đến trang".
				.Build();
			// changesLayout=true only on the very first call (oldAttributes == null): that is when the
			// system needs OnWrite to actually run to get content at all. Later calls just mean the user
			// tweaked paper size/orientation in the dialog -- this fixed, already-formatted PDF does not
			// need to be regenerated for that, so false there avoids a redundant OnWrite.
			callback?.OnLayoutFinished(info, oldAttributes == null);
		}

		public override void OnWrite(PageRange[]? pages, ParcelFileDescriptor? destination,
			CancellationSignal? cancellationSignal, WriteResultCallback? callback)
		{
			string? tempFile = null;
			try
			{
				bool all = pages == null || pages.Length == 0 || pages.Any(r => r.Equals(PageRange.AllPages));
				string sourcePath = pdfPath;
				PageRange[] writtenRanges = [PageRange.AllPages!];
				if (!all)
				{
					List<PageEdit> selected = ExpandPages(pages!, pageCount);
					if (selected.Count == 0)
					{
						callback?.OnWriteFailed("Không có trang nào để in.");
						return;
					}
					tempFile = library.NewWorkFile();
					PdfStructureOps.Rebuild(pdfPath, selected, tempFile);
					sourcePath = tempFile;
					writtenRanges = pages!;
				}

				using Stream input = File.OpenRead(sourcePath);
				using var output = new ParcelFileDescriptor.AutoCloseOutputStream(destination!);
				byte[] buffer = new byte[81920];
				int read;
				while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
				{
					if (cancellationSignal?.IsCanceled == true)
					{
						callback?.OnWriteCancelled();
						return;
					}
					output.Write(buffer, 0, read);
				}
				callback?.OnWriteFinished(writtenRanges);
			}
			catch (Exception ex)
			{
				callback?.OnWriteFailed(ex.Message);
			}
			finally
			{
				if (tempFile != null) { try { File.Delete(tempFile); } catch { /* best effort */ } }
			}
		}

		// PageRange is 0-based inclusive on both ends, same convention PdfStructureOps already uses.
		private static List<PageEdit> ExpandPages(PageRange[] ranges, int pageCount)
		{
			var pages = new List<PageEdit>();
			foreach (PageRange r in ranges)
			{
				int start = Math.Max(0, r.Start);
				int end = Math.Min(pageCount - 1, r.End);
				for (int p = start; p <= end; p++) pages.Add(new PageEdit(p));
			}
			return pages;
		}
	}
}
