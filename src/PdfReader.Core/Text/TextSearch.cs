using System.Globalization;
using System.Text;

namespace PdfReader.Core.Text;

/// <summary>One occurrence of a search query: the page and the rectangles to highlight.</summary>
public sealed record SearchHit(int Page, IReadOnlyList<NRect> Rects);

public static class TextSearch
{
	/// <summary>Lower-cases and (when <paramref name="ignoreAccents"/>) strips diacritics, so that
	/// "viet" finds "Việt". Vietnamese "đ" has no decomposition and is mapped by hand.</summary>
	public static string Fold(string s, bool ignoreAccents)
	{
		string lower = s.ToLower(CultureInfo.InvariantCulture);
		if (!ignoreAccents) return lower;
		string d = lower.Normalize(NormalizationForm.FormD);
		var sb = new StringBuilder(d.Length);
		foreach (char c in d)
		{
			if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
			sb.Append(c == 'đ' ? 'd' : c);
		}
		return sb.ToString();
	}

	/// <summary>All occurrences of <paramref name="query"/> in one page. A match may span several words
	/// (the query "tài liệu" matches the two words "tài", "liệu"); the whole words are highlighted.</summary>
	public static IReadOnlyList<SearchHit> FindInPage(PageText page, string query, bool ignoreAccents = true)
	{
		var hits = new List<SearchHit>();
		string q = Fold(query.Trim(), ignoreAccents);
		if (q.Length == 0 || page.Words.Count == 0) return hits;

		// Page as one string, words separated by a single space; wordAt maps each char back to its word.
		var sb = new StringBuilder();
		var wordAt = new List<int>();
		for (int i = 0; i < page.Words.Count; i++)
		{
			if (i > 0)
			{
				sb.Append(' ');
				wordAt.Add(i);
			}
			string w = Fold(page.Words[i].Text, ignoreAccents);
			sb.Append(w);
			for (int k = 0; k < w.Length; k++) wordAt.Add(i);
		}
		string hay = sb.ToString();

		int pos = 0;
		while (pos <= hay.Length - q.Length)
		{
			int at = hay.IndexOf(q, pos, StringComparison.Ordinal);
			if (at < 0) break;
			int firstWord = wordAt[at], lastWord = wordAt[at + q.Length - 1];
			hits.Add(new SearchHit(page.PageIndex, page.RectsFor(firstWord, lastWord)));
			pos = at + Math.Max(1, q.Length);
		}
		return hits;
	}

	public static IReadOnlyList<SearchHit> Find(IEnumerable<PageText> pages, string query, bool ignoreAccents = true) =>
		pages.OrderBy(p => p.PageIndex).SelectMany(p => FindInPage(p, query, ignoreAccents)).ToList();
}
