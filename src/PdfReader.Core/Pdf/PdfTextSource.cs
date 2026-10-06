using PdfReader.Core.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Outline;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfReader.Core.Pdf;

/// <summary>A table-of-contents entry (PDF bookmark).</summary>
public sealed record OutlineEntry(string Title, int Page, int Level);

/// <summary>
/// Reads the PDF's own text layer (PdfPig, Apache-2.0) and bookmarks. Words come back in page-normalised
/// coordinates in reading order, with the page's /Rotate already applied so they line up with what
/// Android's PdfRenderer draws. A scanned page simply has no words -- the caller then falls back to OCR.
/// Not thread-safe: use one instance per thread (callers lock around it).
/// </summary>
public sealed class PdfTextSource : IDisposable
{
	private readonly PigDocument _doc;

	private PdfTextSource(PigDocument doc) => _doc = doc;

	public static PdfTextSource Open(string path, string? password = null) =>
		new(password == null ? PigDocument.Open(path) : PigDocument.Open(path, new ParsingOptions { Passwords = [password] }));

	public int PageCount => _doc.NumberOfPages;

	public PageText ExtractPage(int pageIndex)
	{
		Page page = _doc.GetPage(pageIndex + 1);
		var bounds = page.CropBox.Bounds;
		double left = bounds.Left, top = bounds.Top, width = bounds.Width, height = bounds.Height;
		if (width <= 0 || height <= 0) return new PageText(pageIndex, [], isOcr: false);
		int rotation = ((page.Rotation.Value % 360) + 360) % 360;

		// PdfPig already applies the page's /Rotate to the letters it returns, so they come back in the
		// displayed (rotated) page space, with the origin at the bottom-left of the rotated page.
		if (rotation is 90 or 270)
		{
			(width, height) = (height, width);
			left = 0;
			top = height;
		}

		// The default word extractor only handles horizontal text; a sideways page needs the
		// angle-agnostic one.
		IEnumerable<Word> words = rotation is 90 or 270
			? NearestNeighbourWordExtractor.Instance.GetWords(page.Letters)
			: page.GetWords();

		var raw = new List<(string Text, NRect Box)>();
		foreach (Word word in words)
		{
			string text = word.Text.Trim();
			if (text.Length == 0) continue;
			var b = word.BoundingBox;
			// PDF space is bottom-left origin; flip into top-left, 0..1.
			float x0 = (float)((b.Left - left) / width), x1 = (float)((b.Right - left) / width);
			float y0 = (float)((top - b.Top) / height), y1 = (float)((top - b.Bottom) / height);
			raw.Add((text, new NRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1))));
		}
		return new PageText(pageIndex, ReadingOrder.ArrangeWords(raw), isOcr: false);
	}

	/// <summary>The bookmarks, flattened, in document order. Empty when the file has none.</summary>
	public IReadOnlyList<OutlineEntry> Outline()
	{
		var result = new List<OutlineEntry>();
		try
		{
			if (!_doc.TryGetBookmarks(out Bookmarks? bookmarks)) return result;
			foreach (BookmarkNode node in bookmarks.GetNodes())
			{
				if (node is DocumentBookmarkNode d && d.PageNumber >= 1)
					result.Add(new OutlineEntry(d.Title, d.PageNumber - 1, d.Level));
			}
		}
		catch
		{
			// A malformed outline must never stop the document from opening.
		}
		return result;
	}

	public void Dispose() => _doc.Dispose();
}

/// <summary>Turns loose words into reading order: group into lines by vertical position, lines top to
/// bottom, words left to right, and number the lines.</summary>
public static class ReadingOrder
{
	public static IReadOnlyList<TextWord> ArrangeWords(IReadOnlyList<(string Text, NRect Box)> words)
	{
		var sorted = words.OrderBy(w => w.Box.CenterY).ToList();
		var lines = new List<List<(string Text, NRect Box)>>();
		foreach (var w in sorted)
		{
			List<(string Text, NRect Box)>? line = null;
			if (lines.Count > 0)
			{
				var last = lines[^1];
				float lineCenter = last.Average(x => x.Box.CenterY);
				float lineHeight = last.Average(x => x.Box.Height);
				// Same line when the word's centre sits within half a line height of the line's centre.
				if (Math.Abs(w.Box.CenterY - lineCenter) <= Math.Max(lineHeight, w.Box.Height) * 0.5f) line = last;
			}
			if (line == null)
			{
				line = [];
				lines.Add(line);
			}
			line.Add(w);
		}

		var result = new List<TextWord>(words.Count);
		for (int i = 0; i < lines.Count; i++)
			foreach (var w in lines[i].OrderBy(x => x.Box.Left))
				result.Add(new TextWord(w.Text, w.Box, i));
		return result;
	}
}
