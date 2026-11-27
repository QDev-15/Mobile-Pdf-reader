using System.Text;

namespace PdfReader.Core.Text;

/// <summary>A rectangle in page-normalised coordinates: (0,0) is the top-left of the page as displayed
/// (rotation already applied), (1,1) the bottom-right. Independent of zoom, device density and DPI, so the
/// same value works for the on-screen overlay, text selection and PDF export.</summary>
public readonly record struct NRect(float Left, float Top, float Right, float Bottom)
{
	public float Width => Right - Left;
	public float Height => Bottom - Top;
	public float CenterX => (Left + Right) / 2;
	public float CenterY => (Top + Bottom) / 2;

	public bool Contains(float x, float y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

	public NRect Inflate(float dx, float dy) => new(Left - dx, Top - dy, Right + dx, Bottom + dy);

	public NRect Offset(float dx, float dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);

	public NRect Union(NRect o) => new(Math.Min(Left, o.Left), Math.Min(Top, o.Top), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));

	public static NRect UnionAll(IEnumerable<NRect> rects)
	{
		bool any = false;
		NRect acc = default;
		foreach (NRect r in rects)
		{
			acc = any ? acc.Union(r) : r;
			any = true;
		}
		return acc;
	}
}

public readonly record struct NPoint(float X, float Y);

/// <summary>One word of page text with where it sits on the page. <see cref="Line"/> groups words that
/// share a baseline, so selections can be drawn as one rectangle per line.</summary>
public sealed record TextWord(string Text, NRect Box, int Line);

/// <summary>One selectable unit smaller than a whole word -- usually a single character, but the whole word
/// when the source has no finer data (OCR). <see cref="WordIndex"/> indexes into <see cref="PageText.Words"/>,
/// so joining/line-grouping at this granularity can still tell words and lines apart (see
/// <see cref="PageText.JoinChars"/>/<see cref="PageText.RectsForChars"/>) without duplicating that data here.</summary>
public readonly record struct TextChar(string Text, NRect Box, int WordIndex);

/// <summary>The text of one page, in reading order (top to bottom, left to right). Comes either from the
/// PDF's own text layer (exact) or from OCR (<see cref="IsOcr"/>, approximate).</summary>
public sealed class PageText
{
	public PageText(int pageIndex, IReadOnlyList<TextWord> words, bool isOcr, IReadOnlyList<TextChar>? chars = null)
	{
		PageIndex = pageIndex;
		Words = words;
		IsOcr = isOcr;
		Chars = chars ?? SynthesizeWholeWordChars(words);
	}

	public int PageIndex { get; }
	public IReadOnlyList<TextWord> Words { get; }
	public bool IsOcr { get; }

	/// <summary>The selectable units for free (not word-snapped) selection -- a page's characters when the
	/// source gives them (the PDF's own text layer), or one unit per whole word otherwise (OCR).</summary>
	public IReadOnlyList<TextChar> Chars { get; }

	private static IReadOnlyList<TextChar> SynthesizeWholeWordChars(IReadOnlyList<TextWord> words)
	{
		var chars = new TextChar[words.Count];
		for (int i = 0; i < words.Count; i++) chars[i] = new TextChar(words[i].Text, words[i].Box, i);
		return chars;
	}

	/// <summary>A page with next to no text is a scan/photo: the caller should fall back to OCR.</summary>
	public bool IsUsable => Words.Count >= 3;

	/// <summary>The whole page as plain text, one line of the page per line of text.</summary>
	public string FullText => Join(0, Words.Count - 1);

	/// <summary>Index of the word under (x, y), or the nearest word within <paramref name="tolerance"/>
	/// (a fraction of the page width), or -1. <paramref name="pageAspect"/> (page height / page width) corrects
	/// the vertical distance into the same page-width units as the horizontal one, the same way every
	/// annotation hit-test (<see cref="PdfReader.Core.Annotations.Annotation.HitTest"/>) already does -- without
	/// it, the hit area is skewed on a non-square page.</summary>
	public int HitTest(float x, float y, float pageAspect = 1f, float tolerance = 0.02f)
	{
		int best = -1;
		float bestDist = float.MaxValue;
		for (int i = 0; i < Words.Count; i++)
		{
			NRect b = Words[i].Box;
			float dx = x < b.Left ? b.Left - x : x > b.Right ? x - b.Right : 0;
			float dy = (y < b.Top ? b.Top - y : y > b.Bottom ? y - b.Bottom : 0) * pageAspect;
			float d = MathF.Sqrt(dx * dx + dy * dy);
			if (d <= tolerance && d < bestDist)
			{
				bestDist = d;
				best = i;
			}
		}
		return best;
	}

	/// <summary>Nearest word by plain distance, with no limit -- used while dragging a selection so it
	/// keeps following the finger even over blank space. -1 only when the page has no words.</summary>
	public int NearestWord(float x, float y)
	{
		int best = -1;
		float bestDist = float.MaxValue;
		for (int i = 0; i < Words.Count; i++)
		{
			NRect b = Words[i].Box;
			float dx = x < b.Left ? b.Left - x : x > b.Right ? x - b.Right : 0;
			float dy = y < b.Top ? b.Top - y : y > b.Bottom ? y - b.Bottom : 0;
			// Weight vertical distance so the finger stays on its own line rather than jumping to a closer
			// word on the line above/below.
			float d = dx * dx + dy * dy * 4;
			if (d < bestDist)
			{
				bestDist = d;
				best = i;
			}
		}
		return best;
	}

	/// <summary>Character-level equivalent of <see cref="HitTest"/>, for free (not word-snapped) selection.</summary>
	public int HitTestChar(float x, float y, float pageAspect = 1f, float tolerance = 0.02f)
	{
		int best = -1;
		float bestDist = float.MaxValue;
		for (int i = 0; i < Chars.Count; i++)
		{
			NRect b = Chars[i].Box;
			float dx = x < b.Left ? b.Left - x : x > b.Right ? x - b.Right : 0;
			float dy = (y < b.Top ? b.Top - y : y > b.Bottom ? y - b.Bottom : 0) * pageAspect;
			float d = MathF.Sqrt(dx * dx + dy * dy);
			if (d <= tolerance && d < bestDist)
			{
				bestDist = d;
				best = i;
			}
		}
		return best;
	}

	/// <summary>Character-level equivalent of <see cref="NearestWord"/>, for free (not word-snapped) selection
	/// -- used while dragging so it keeps following the finger even over blank space.</summary>
	public int NearestChar(float x, float y)
	{
		int best = -1;
		float bestDist = float.MaxValue;
		for (int i = 0; i < Chars.Count; i++)
		{
			NRect b = Chars[i].Box;
			float dx = x < b.Left ? b.Left - x : x > b.Right ? x - b.Right : 0;
			float dy = y < b.Top ? b.Top - y : y > b.Bottom ? y - b.Bottom : 0;
			float d = dx * dx + dy * dy * 4;
			if (d < bestDist)
			{
				bestDist = d;
				best = i;
			}
		}
		return best;
	}

	/// <summary>Text of characters <paramref name="from"/>..<paramref name="to"/> inclusive (either order) --
	/// the free-selection equivalent of <see cref="Join"/>.</summary>
	public string JoinChars(int from, int to)
	{
		if (Chars.Count == 0) return "";
		(int a, int b) = from <= to ? (from, to) : (to, from);
		a = Math.Max(0, a);
		b = Math.Min(Chars.Count - 1, b);
		var sb = new StringBuilder();
		for (int i = a; i <= b; i++)
		{
			if (i > a)
			{
				TextChar prev = Chars[i - 1], cur = Chars[i];
				if (Words[cur.WordIndex].Line != Words[prev.WordIndex].Line) sb.Append('\n');
				else if (cur.WordIndex != prev.WordIndex) sb.Append(' ');
			}
			sb.Append(Chars[i].Text);
		}
		return sb.ToString();
	}

	/// <summary>One rectangle per line covering characters <paramref name="from"/>..<paramref name="to"/> --
	/// the free-selection equivalent of <see cref="RectsFor"/>.</summary>
	public IReadOnlyList<NRect> RectsForChars(int from, int to)
	{
		var rects = new List<NRect>();
		if (Chars.Count == 0) return rects;
		(int a, int b) = from <= to ? (from, to) : (to, from);
		a = Math.Max(0, a);
		b = Math.Min(Chars.Count - 1, b);
		int line = -1;
		NRect cur = default;
		for (int i = a; i <= b; i++)
		{
			TextChar c = Chars[i];
			int charLine = Words[c.WordIndex].Line;
			if (line == charLine) cur = cur.Union(c.Box);
			else
			{
				if (line != -1) rects.Add(cur);
				line = charLine;
				cur = c.Box;
			}
		}
		if (line != -1) rects.Add(cur);
		return rects;
	}

	/// <summary>Text of words <paramref name="from"/>..<paramref name="to"/> inclusive (either order).</summary>
	public string Join(int from, int to)
	{
		if (Words.Count == 0) return "";
		(int a, int b) = from <= to ? (from, to) : (to, from);
		a = Math.Max(0, a);
		b = Math.Min(Words.Count - 1, b);
		var sb = new StringBuilder();
		for (int i = a; i <= b; i++)
		{
			if (i > a) sb.Append(Words[i].Line == Words[i - 1].Line ? ' ' : '\n');
			sb.Append(Words[i].Text);
		}
		return sb.ToString();
	}

	/// <summary>One rectangle per line covering words <paramref name="from"/>..<paramref name="to"/>.</summary>
	public IReadOnlyList<NRect> RectsFor(int from, int to)
	{
		var rects = new List<NRect>();
		if (Words.Count == 0) return rects;
		(int a, int b) = from <= to ? (from, to) : (to, from);
		a = Math.Max(0, a);
		b = Math.Min(Words.Count - 1, b);
		int line = -1;
		NRect cur = default;
		for (int i = a; i <= b; i++)
		{
			TextWord w = Words[i];
			if (line == w.Line) cur = cur.Union(w.Box);
			else
			{
				if (line != -1) rects.Add(cur);
				line = w.Line;
				cur = w.Box;
			}
		}
		if (line != -1) rects.Add(cur);
		return rects;
	}
}
