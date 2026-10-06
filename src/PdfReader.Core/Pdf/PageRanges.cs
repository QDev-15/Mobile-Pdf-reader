namespace PdfReader.Core.Pdf;

/// <summary>Parses what a person types to say which pages they mean: "1-3, 5, 8-" (1-based, inclusive;
/// an open end means "to the last page"). Results are 0-based and inclusive.</summary>
public static class PageRanges
{
	public static IReadOnlyList<(int Start, int End)> Parse(string text, int pageCount)
	{
		var result = new List<(int, int)>();
		foreach (string raw in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			int dash = raw.IndexOf('-');
			int start, end;
			if (dash < 0)
			{
				start = end = ParsePage(raw, pageCount);
			}
			else
			{
				string a = raw[..dash].Trim(), b = raw[(dash + 1)..].Trim();
				start = a.Length == 0 ? 1 : ParsePage(a, pageCount);
				end = b.Length == 0 ? pageCount : ParsePage(b, pageCount);
				if (start > end) throw new FormatException($"Khoảng trang '{raw}' bị ngược.");
			}
			result.Add((start - 1, end - 1));
		}
		if (result.Count == 0) throw new FormatException("Chưa nhập trang nào.");
		return result;
	}

	/// <summary>Every page of every range, in the order given, without repeats.</summary>
	public static IReadOnlyList<int> Flatten(IEnumerable<(int Start, int End)> ranges)
	{
		var seen = new HashSet<int>();
		var pages = new List<int>();
		foreach ((int s, int e) in ranges)
			for (int p = s; p <= e; p++)
				if (seen.Add(p)) pages.Add(p);
		return pages;
	}

	private static int ParsePage(string s, int pageCount)
	{
		if (!int.TryParse(s, out int n)) throw new FormatException($"'{s}' không phải số trang.");
		if (n < 1 || n > pageCount) throw new FormatException($"Trang {n} không tồn tại (tài liệu có {pageCount} trang).");
		return n;
	}
}
