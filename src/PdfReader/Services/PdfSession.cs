using PdfReader.Core.Annotations;
using Path = System.IO.Path;

namespace PdfReader.Services;

/// <summary>
/// Everything about the one PDF that is open in the reader: the renderer, the page sizes, the text layer
/// (with OCR fallback) and the annotations, which are saved next to the document automatically a moment
/// after every change -- so closing the app never loses an edit, even before it is exported.
/// </summary>
public sealed class PdfSession : IDisposable
{
	private readonly string _annotationsFile;
	private CancellationTokenSource? _saveDelay;
	private bool _disposed;

	private PdfSession(string path, string displayName, PdfPages pages, TextLayerService text, AnnotationDocument annotations, string annotationsFile)
	{
		Path = path;
		DisplayName = displayName;
		Pages = pages;
		Text = text;
		Annotations = annotations;
		_annotationsFile = annotationsFile;
		Annotations.Changed += ScheduleSave;
	}

	public string Path { get; }
	public string DisplayName { get; }
	public PdfPages Pages { get; }
	public TextLayerService Text { get; }
	public AnnotationDocument Annotations { get; }
	public int PageCount => Pages.Count;

	/// <summary>Size of each page in PDF points; filled by <see cref="MeasurePagesAsync"/>.</summary>
	public (int Width, int Height)[]? PageSizes { get; private set; }

	public static PdfSession Open(string localPath, string displayName, PdfLibrary library, OcrService ocr)
	{
		PdfPages pages = PdfPages.Open(localPath);
		try
		{
			string annotationsFile = library.DataFile(localPath, "annotations.json");
			AnnotationDocument annotations = File.Exists(annotationsFile)
				? AnnotationDocument.FromJson(File.ReadAllText(annotationsFile))
				: new AnnotationDocument();
			var text = new TextLayerService(localPath, pages, ocr, library.DataFile(localPath, "ocr.json"));
			return new PdfSession(localPath, displayName, pages, text, annotations, annotationsFile);
		}
		catch
		{
			pages.Dispose();
			throw;
		}
	}

	private readonly object _measureLock = new();
	private int _measuredUpTo;

	/// <summary>Pages measured so far (a prefix of the document). Pages beyond it read as (0, 0) in
	/// <see cref="PageSizes"/>.</summary>
	public int MeasuredCount => Volatile.Read(ref _measuredUpTo);

	public bool FullyMeasured => MeasuredCount >= PageCount;

	/// <summary>Measures pages up to <paramref name="upTo"/> (exclusive) if they are not measured yet. Opening
	/// a long document measures only the first few dozen pages before showing it, and the rest in the
	/// background, so the first page appears at once.</summary>
	public (int Width, int Height)[] MeasureUpTo(int upTo)
	{
		lock (_measureLock)
		{
			upTo = Math.Clamp(upTo, 0, PageCount);
			PageSizes ??= new (int, int)[PageCount];
			if (_measuredUpTo < upTo)
			{
				Pages.MeasureInto(PageSizes, _measuredUpTo, upTo);
				Volatile.Write(ref _measuredUpTo, upTo);
			}
			return PageSizes;
		}
	}

	/// <summary>All page sizes (measures any that are still missing). For export and other work that needs them all.</summary>
	public (int Width, int Height)[] EnsureMeasured() => MeasureUpTo(PageCount);

	public Task MeasureHeadAsync(int count = 24) => Task.Run(() => MeasureUpTo(count));

	public Task MeasureAllAsync() => Task.Run(() => MeasureUpTo(PageCount));

	private void ScheduleSave()
	{
		_saveDelay?.Cancel();
		var cts = _saveDelay = new CancellationTokenSource();
		_ = Task.Delay(800, cts.Token).ContinueWith(t =>
		{
			if (!t.IsCanceled) SaveNow();
		}, TaskScheduler.Default);
	}

	public void SaveNow()
	{
		try
		{
			if (Annotations.Items.Count == 0) File.Delete(_annotationsFile);
			else File.WriteAllText(_annotationsFile, Annotations.ToJson());
		}
		catch (Exception ex)
		{
			Android.Util.Log.Warn("PdfReader", $"saving annotations failed: {ex.Message}");
		}
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		Annotations.Changed -= ScheduleSave;
		_saveDelay?.Cancel();
		SaveNow();
		Text.Dispose();
		Pages.Dispose();
	}
}
