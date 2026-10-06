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

/// <summary>The text of one page, in reading order (top to bottom, left to right). Comes either from the
/// PDF's own text layer (exact) or from OCR (<see cref="IsOcr"/>, approximate).</summary>
public sealed class PageText
{
	public PageText(int pageIndex, IReadOnlyList<TextWord> words, bool isOcr)
	{
		PageIndex = pageIndex;
		Words = words;
		IsOcr = isOcr;
	}

	public int PageIndex { get; }
	public IReadOnlyList<TextWord> Words { get; }
	public bool IsOcr { get; }

	/// <summary>A page with next to no text is a scan/photo: the caller should fall back to OCR.</summary>
	public bool IsUsable => Words.Count >= 3;

	/// <summary>The whole page as plain text, one line of the page per line of text.</summary>
	public string FullText => Join(0, Words.Count - 1);

	/// <summary>Index of the word under (x, y), or the nearest word within <paramref name="tolerance"/>
	/// (a fraction of the page), or -1.</summary>
	public int HitTest(float x, float y, float tolerance = 0.02f)
	{
		int best = -1;
		float bestDist = float.MaxValue;
		for (int i = 0; i < Words.Count; i++)
		{
			NRect b = Words[i].Box;
			float dx = x < b.Left ? b.Left - x : x > b.Right ? x - b.Right : 0;
			float dy = y < b.Top ? b.Top - y : y > b.Bottom ? y - b.Bottom : 0;
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
