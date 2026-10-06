using Android.Graphics;
using PdfReader.Core;
using PdfReader.Core.Pdf;
using PdfReader.Core.Text;
using RectF = Android.Graphics.RectF;

namespace PdfReader.Services;

/// <summary>
/// Turns what is on screen into new PDF files. Pages that were edited (or that are being compressed /
/// made searchable) are rendered to a picture at the chosen quality (<see cref="PdfQuality"/>: DPI + JPEG
/// quality), with the annotations painted in by <see cref="AnnotationPainter"/>, and written as JPEG-backed
/// pages (<see cref="RasterPdfWriter"/>). Every other page is copied across untouched, so unedited pages
/// keep their text, vectors and small size. Results are scratch files in the work directory; the caller
/// decides whether to save them to Downloads, share them, or open them.
/// </summary>
public sealed class PdfExporter(PdfLibrary library)
{
	private const int MaxLongEdgePx = 4200;

	/// <summary>The document with all its annotations baked in. A document with none is just copied.</summary>
	public Task<string> ExportAnnotatedAsync(PdfSession session, PdfQuality quality, IProgress<double>? progress = null) =>
		Task.Run(() =>
		{
			IReadOnlySet<int> pages = session.Annotations.PagesWithAnnotations();
			string output = library.NewWorkFile();
			if (pages.Count == 0)
			{
				File.Copy(session.Path, output, overwrite: true);
				return output;
			}
			ReplaceWithRasters(session, pages.OrderBy(p => p).ToList(), quality, includeAnnotations: true, ocrWords: null, output, progress);
			return output;
		});

	/// <summary>Every page re-encoded as a picture at <paramref name="quality"/> -- the way to make a scan
	/// small. Annotations are included.</summary>
	public Task<string> CompressAsync(PdfSession session, PdfQuality quality, IProgress<double>? progress = null) =>
		Task.Run(() =>
		{
			string output = library.NewWorkFile();
			WriteAllRaster(session, quality, includeAnnotations: true, ocrWords: null, output, progress);
			return output;
		});

	/// <summary>Makes scanned pages searchable: each page without a text layer is OCR'd (if not already) and
	/// rewritten as picture + hidden text. Pages that already have text are left exactly as they are.
	/// Returns null when no page needed it.</summary>
	public async Task<string?> MakeSearchableAsync(PdfSession session, PdfQuality quality, IProgress<double>? progress = null, CancellationToken ct = default)
	{
		var words = new Dictionary<int, IReadOnlyList<TextWord>>();
		for (int p = 0; p < session.PageCount; p++)
		{
			ct.ThrowIfCancellationRequested();
			PageText text = await session.Text.GetAsync(p, allowOcr: true, ct);
			if (text.IsOcr && text.IsUsable) words[p] = text.Words;
			progress?.Report(0.6 * (p + 1) / session.PageCount); // OCR is the slow part
		}
		if (words.Count == 0) return null;

		return await Task.Run(() =>
		{
			string output = library.NewWorkFile();
			var inner = progress == null ? null : new Progress<double>(v => progress.Report(0.6 + 0.4 * v));
			ReplaceWithRasters(session, words.Keys.OrderBy(p => p).ToList(), quality, includeAnnotations: true, words, output, inner);
			return output;
		}, ct);
	}

	/// <summary>PNG or JPEG file for each requested page, at <paramref name="dpi"/>.</summary>
	public Task<IReadOnlyList<string>> PagesToImagesAsync(PdfSession session, IReadOnlyList<int> pages, bool png, int dpi, IProgress<double>? progress = null) =>
		Task.Run<IReadOnlyList<string>>(() =>
		{
			var files = new List<string>();
			for (int i = 0; i < pages.Count; i++)
			{
				using Bitmap bitmap = RenderPage(session, pages[i], dpi, includeAnnotations: true);
				string file = library.NewWorkFile(png ? ".png" : ".jpg");
				using (FileStream fs = File.Create(file))
					bitmap.Compress(png ? Bitmap.CompressFormat.Png! : Bitmap.CompressFormat.Jpeg!, png ? 100 : 92, fs);
				files.Add(file);
				progress?.Report((i + 1.0) / pages.Count);
			}
			return files;
		});

	/// <summary>Copy that asks for a password to open.</summary>
	public Task<string> ProtectAsync(string sourcePdf, string password) =>
		Task.Run(() =>
		{
			string output = library.NewWorkFile();
			PdfStructureOps.Encrypt(sourcePdf, output, password);
			return output;
		});

	private void ReplaceWithRasters(PdfSession session, IReadOnlyList<int> pages, PdfQuality quality, bool includeAnnotations,
		IReadOnlyDictionary<int, IReadOnlyList<TextWord>>? ocrWords, string output, IProgress<double>? progress)
	{
		var replacements = new Dictionary<int, string>();
		try
		{
			for (int i = 0; i < pages.Count; i++)
			{
				string file = library.NewWorkFile();
				RasterPdfWriter.Write(file, [BuildRasterPage(session, pages[i], quality, includeAnnotations, ocrWords)]);
				replacements[pages[i]] = file;
				progress?.Report((i + 1.0) / pages.Count);
			}
			try
			{
				PdfStructureOps.ReplacePages(session.Path, replacements, output);
			}
			catch (Exception ex)
			{
				// PDFsharp could not take the original apart (some encrypted / unusual files): fall back to
				// turning the whole document into pictures, which always works.
				Android.Util.Log.Warn("PdfReader", $"page-level export failed ({ex.Message}); rasterising all pages");
				WriteAllRaster(session, quality, includeAnnotations, ocrWords, output, progress);
			}
		}
		finally
		{
			foreach (string f in replacements.Values) TryDelete(f);
		}
	}

	private void WriteAllRaster(PdfSession session, PdfQuality quality, bool includeAnnotations,
		IReadOnlyDictionary<int, IReadOnlyList<TextWord>>? ocrWords, string output, IProgress<double>? progress)
	{
		int total = session.PageCount;
		RasterPdfWriter.Write(output, Enumerable.Range(0, total).Select(i =>
		{
			RasterPage page = BuildRasterPage(session, i, quality, includeAnnotations, ocrWords);
			progress?.Report((i + 1.0) / total);
			return page;
		}));
	}

	private RasterPage BuildRasterPage(PdfSession session, int page, PdfQuality quality, bool includeAnnotations,
		IReadOnlyDictionary<int, IReadOnlyList<TextWord>>? ocrWords)
	{
		(int w, int h) = session.EnsureMeasured()[page];
		using Bitmap bitmap = RenderPage(session, page, quality.Dpi, includeAnnotations);
		using var jpeg = new MemoryStream();
		bitmap.Compress(Bitmap.CompressFormat.Jpeg!, quality.JpegQuality, jpeg);
		IReadOnlyList<TextWord>? words = null;
		ocrWords?.TryGetValue(page, out words);
		return new RasterPage(jpeg.ToArray(), bitmap.Width, bitmap.Height, w, h, words);
	}

	private static Bitmap RenderPage(PdfSession session, int page, int dpi, bool includeAnnotations)
	{
		(int w, int h) = session.EnsureMeasured()[page];
		int effectiveDpi = (int)Math.Min(dpi, MaxLongEdgePx * 72.0 / Math.Max(w, h));
		Bitmap bitmap = session.Pages.RenderAtDpi(page, Math.Max(36, effectiveDpi));
		if (includeAnnotations)
		{
			var annotations = session.Annotations.OnPage(page).ToList();
			if (annotations.Count > 0)
			{
				using var canvas = new Canvas(bitmap);
				AnnotationPainter.Draw(canvas, annotations, new RectF(0, 0, bitmap.Width, bitmap.Height));
			}
		}
		return bitmap;
	}

	private static void TryDelete(string path)
	{
		try { File.Delete(path); }
		catch
		{
			// scratch file; the OS clears the temp directory eventually
		}
	}
}
