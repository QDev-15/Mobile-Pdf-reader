using Android.Graphics;
using Android.Graphics.Pdf;
using Android.OS;

namespace PdfReader.Services;

/// <summary>
/// Pages of a PDF as bitmaps, via Android's own PdfRenderer (built into the OS since Android 5: no
/// library, no licence question). One page is rendered at a time (the renderer allows only one open page
/// at once), from any thread. New for PdfReader -- the core open/render technique mirrors DocScanner's
/// PdfPages (Mobile-doc-scanner repo, used there for PDF import), but <see cref="Open"/> additionally
/// accepts a content:// Uri string directly (needed here because PdfReader's own job is opening
/// arbitrary PDFs handed in by other apps, not just ones it saved itself).
/// </summary>
public sealed class PdfPages : IDisposable
{
	private readonly object _lock = new();
	private readonly ParcelFileDescriptor _file;
	private readonly PdfRenderer _renderer;

	private PdfPages(ParcelFileDescriptor file)
	{
		_file = file;
		_renderer = new PdfRenderer(file);
	}

	/// <summary>Opens a PDF from a content:// Uri string (the normal case -- a file handed in via "Open
	/// with" / "Share", or MAUI's FilePicker) or a plain file:// / filesystem path.</summary>
	public static PdfPages Open(string uriOrPath)
	{
		ParcelFileDescriptor file = uriOrPath.StartsWith("content://", StringComparison.OrdinalIgnoreCase)
			? Android.App.Application.Context.ContentResolver!.OpenFileDescriptor(Android.Net.Uri.Parse(uriOrPath)!, "r")
				?? throw new IOException("Không mở được file PDF.")
			: ParcelFileDescriptor.Open(new Java.IO.File(StripFileScheme(uriOrPath)), ParcelFileMode.ReadOnly)!;
		return new PdfPages(file);
	}

	private static string StripFileScheme(string path) =>
		path.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? Android.Net.Uri.Parse(path)!.Path ?? path : path;

	public int Count => _renderer.PageCount;

	/// <summary>Page <paramref name="index"/> on white, its long edge <paramref name="longEdge"/> pixels.</summary>
	public Bitmap Render(int index, int longEdge)
	{
		lock (_lock)
		{
			// Close() explicitly: Dispose() only releases the .NET wrapper, and PdfRenderer refuses to
			// open another page while one is still open ("Current page not closed").
			PdfRenderer.Page page = _renderer.OpenPage(index);
			try
			{
				double scale = (double)longEdge / Math.Max(page.Width, page.Height);
				int w = Math.Max(1, (int)Math.Round(page.Width * scale)), h = Math.Max(1, (int)Math.Round(page.Height * scale));
				Bitmap bitmap = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!)!;
				bitmap.EraseColor(Android.Graphics.Color.White);
				page.Render(bitmap, null, null, PdfRenderMode.ForDisplay);
				return bitmap;
			}
			finally
			{
				page.Close();
				page.Dispose();
			}
		}
	}

	public void Dispose()
	{
		lock (_lock)
		{
			_renderer.Close();
			_file.Close();
		}
	}
}
