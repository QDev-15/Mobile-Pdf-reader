using System.Collections.Concurrent;
using System.Text.Json;
using Android.Graphics;
using PdfReader.Core.Json;
using PdfReader.Core.Pdf;
using PdfReader.Core.Text;

namespace PdfReader.Services;

/// <summary>
/// The text of a document's pages, for selecting, copying and searching. Hybrid strategy (plan section 3):
/// a page that has a text layer is read straight from it (PdfPig: exact, instant, no battery); a page that
/// does not -- a scan or photo -- is run through OCR once, and that result is remembered on disk so the
/// same page is never recognised twice. Pages are loaded lazily and cached.
/// </summary>
public sealed class TextLayerService : IDisposable
{
	private const int OcrLongEdge = 2000;

	private readonly string _pdfPath;
	private readonly PdfPages _pages;
	private readonly OcrService _ocr;
	private readonly string _ocrCacheFile;
	private readonly object _nativeLock = new();
	private PdfTextSource? _native;
	private bool _nativeFailed;
	private readonly ConcurrentDictionary<int, PageText> _cache = new();
	private readonly Dictionary<int, List<TextWord>> _ocrStore;
	private readonly SemaphoreSlim _ocrGate = new(1, 1);

	public TextLayerService(string pdfPath, PdfPages pages, OcrService ocr, string ocrCacheFile)
	{
		_pdfPath = pdfPath;
		_pages = pages;
		_ocr = ocr;
		_ocrCacheFile = ocrCacheFile;
		_ocrStore = LoadOcrStore();
	}

	/// <summary>The page's text if it is already loaded, otherwise null (never blocks).</summary>
	public PageText? TryGetCached(int page) => _cache.TryGetValue(page, out PageText? t) ? t : null;

	/// <summary>True when OCR has already been done for this page (so its words are available instantly).</summary>
	public bool HasOcr(int page) => _ocrStore.ContainsKey(page);

	/// <summary>Loads a page's text. With <paramref name="allowOcr"/>, a page without a text layer is
	/// recognised by OCR; without it such a page comes back empty (<see cref="PageText.IsUsable"/> false).</summary>
	public async Task<PageText> GetAsync(int page, bool allowOcr, CancellationToken ct = default)
	{
		if (_cache.TryGetValue(page, out PageText? cached) && (cached.IsUsable || cached.IsOcr || !allowOcr)) return cached;

		PageText? text = null;
		if (_ocrStore.TryGetValue(page, out List<TextWord>? stored))
			text = new PageText(page, stored, isOcr: true);
		text ??= await Task.Run(() => ExtractNative(page), ct);

		if (!text.IsUsable && allowOcr)
			text = await RecognizeAsync(page, ct);

		_cache[page] = text;
		return text;
	}

	private PageText ExtractNative(int page)
	{
		lock (_nativeLock)
		{
			try
			{
				if (_nativeFailed) return new PageText(page, [], isOcr: false);
				_native ??= PdfTextSource.Open(_pdfPath);
				return _native.ExtractPage(page);
			}
			catch (Exception ex)
			{
				// A PDF PdfPig cannot parse still renders fine; it just has no usable text layer here.
				Android.Util.Log.Warn("PdfReader", $"text layer failed on page {page + 1}: {ex.Message}");
				_nativeFailed = _native == null;
				return new PageText(page, [], isOcr: false);
			}
		}
	}

	private async Task<PageText> RecognizeAsync(int page, CancellationToken ct)
	{
		await _ocrGate.WaitAsync(ct); // one OCR at a time: it is the heaviest thing the app does
		try
		{
			if (_ocrStore.TryGetValue(page, out List<TextWord>? done)) return new PageText(page, done, isOcr: true);
			IReadOnlyList<TextWord> words;
			using (Bitmap bitmap = await Task.Run(() => _pages.Render(page, OcrLongEdge), ct))
				words = await _ocr.RecognizeAsync(bitmap);
			_ocrStore[page] = words.ToList();
			SaveOcrStore();
			return new PageText(page, words, isOcr: true);
		}
		finally { _ocrGate.Release(); }
	}

	/// <summary>Table of contents, empty when the document has none.</summary>
	public Task<IReadOnlyList<OutlineEntry>> GetOutlineAsync() =>
		Task.Run<IReadOnlyList<OutlineEntry>>(() =>
		{
			lock (_nativeLock)
			{
				try
				{
					_native ??= PdfTextSource.Open(_pdfPath);
					return _native.Outline();
				}
				catch
				{
					return [];
				}
			}
		});

	/// <summary>Finds <paramref name="query"/> across the whole document. Pages with a text layer are
	/// searched directly; pages without one are searched only when <paramref name="ocrMissing"/> (which
	/// runs OCR on each, slowly -- the result of the pass says how many were skipped otherwise).</summary>
	public async Task<(IReadOnlyList<SearchHit> Hits, int SkippedScanned)> SearchAsync(
		string query, bool ocrMissing, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
	{
		var hits = new List<SearchHit>();
		int skipped = 0;
		for (int p = 0; p < _pages.Count; p++)
		{
			ct.ThrowIfCancellationRequested();
			PageText text = await GetAsync(p, ocrMissing, ct);
			if (!text.IsUsable && !text.IsOcr) skipped++;
			hits.AddRange(TextSearch.FindInPage(text, query));
			progress?.Report((p + 1, _pages.Count));
		}
		return (hits, skipped);
	}

	private Dictionary<int, List<TextWord>> LoadOcrStore()
	{
		try
		{
			if (File.Exists(_ocrCacheFile))
				return JsonSerializer.Deserialize(File.ReadAllText(_ocrCacheFile), CoreJsonContext.Default.DictionaryInt32ListTextWord) ?? [];
		}
		catch
		{
			// unreadable cache: start over
		}
		return [];
	}

	private void SaveOcrStore()
	{
		try { File.WriteAllText(_ocrCacheFile, JsonSerializer.Serialize(_ocrStore, CoreJsonContext.Default.DictionaryInt32ListTextWord)); }
		catch (Exception ex) { Android.Util.Log.Warn("PdfReader", $"ocr cache save failed: {ex.Message}"); }
	}

	/// <summary>Every page's text that OCR has already produced, for "searchable PDF" export.</summary>
	public IReadOnlyDictionary<int, List<TextWord>> OcrResults => _ocrStore;

	public void Dispose()
	{
		lock (_nativeLock)
		{
			_native?.Dispose();
			_native = null;
		}
	}
}
