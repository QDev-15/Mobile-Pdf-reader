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

	/// <summary>Size of every page in PDF points (1/72 inch), rotation already applied.</summary>
	public (int Width, int Height)[] MeasureAll()
	{
		var sizes = new (int, int)[Count];
		MeasureInto(sizes, 0, sizes.Length);
		return sizes;
	}

	/// <summary>Measures pages <paramref name="from"/> .. <paramref name="to"/> (exclusive) into <paramref name="sizes"/>.
	/// Takes the renderer lock per small batch, not for the whole range, so rendering the pages on screen is
	/// never held up behind a long measuring pass.</summary>
	public void MeasureInto((int Width, int Height)[] sizes, int from, int to)
	{
		for (int start = from; start < to; start += 16)
		{
			lock (_lock)
			{
				for (int i = start; i < Math.Min(to, start + 16); i++)
				{
					PdfRenderer.Page page = _renderer.OpenPage(i);
					try { sizes[i] = (Math.Max(1, page.Width), Math.Max(1, page.Height)); }
					finally { page.Close(); page.Dispose(); }
				}
			}
		}
	}

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
				return RenderCore(page, scale, 0, 0, Math.Max(1, (int)Math.Round(page.Width * scale)), Math.Max(1, (int)Math.Round(page.Height * scale)));
			}
			finally
			{
				page.Close();
				page.Dispose();
			}
		}
	}

	/// <summary>Whole page, <paramref name="widthPx"/> pixels wide (height follows the page's own aspect).</summary>
	public Bitmap RenderFitWidth(int index, int widthPx)
	{
		lock (_lock)
		{
			PdfRenderer.Page page = _renderer.OpenPage(index);
			try
			{
				double scale = (double)widthPx / page.Width;
				return RenderCore(page, scale, 0, 0, widthPx, Math.Max(1, (int)Math.Round(page.Height * scale)));
			}
			finally
			{
				page.Close();
				page.Dispose();
			}
		}
	}

	/// <summary>Whole page at <paramref name="dpi"/> dots per inch.</summary>
	public Bitmap RenderAtDpi(int index, int dpi)
	{
		lock (_lock)
		{
			PdfRenderer.Page page = _renderer.OpenPage(index);
			try
			{
				double scale = dpi / 72.0;
				return RenderCore(page, scale, 0, 0, Math.Max(1, (int)Math.Round(page.Width * scale)), Math.Max(1, (int)Math.Round(page.Height * scale)));
			}
			finally
			{
				page.Close();
				page.Dispose();
			}
		}
	}

	/// <summary>A window onto a page at a given zoom: <paramref name="pxPerPoint"/> pixels per PDF point, the
	/// window's top-left at (<paramref name="leftPt"/>, <paramref name="topPt"/>) points on the page, its size
	/// <paramref name="outW"/> x <paramref name="outH"/> pixels. How the viewer gets a sharp close-up of the
	/// part of a page on screen without rendering the whole page at that size.</summary>
	public Bitmap RenderRegion(int index, double pxPerPoint, double leftPt, double topPt, int outW, int outH)
	{
		lock (_lock)
		{
			PdfRenderer.Page page = _renderer.OpenPage(index);
			try { return RenderCore(page, pxPerPoint, leftPt, topPt, outW, outH); }
			finally
			{
				page.Close();
				page.Dispose();
			}
		}
	}

	private static Bitmap RenderCore(PdfRenderer.Page page, double scale, double leftPt, double topPt, int w, int h)
	{
		Bitmap bitmap = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!)!;
		bitmap.EraseColor(Android.Graphics.Color.White);
		var matrix = new Matrix();
		matrix.SetScale((float)scale, (float)scale);
		matrix.PostTranslate((float)(-leftPt * scale), (float)(-topPt * scale));
		page.Render(bitmap, null, matrix, PdfRenderMode.ForDisplay);
		return bitmap;
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
