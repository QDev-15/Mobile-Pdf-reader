using System.Globalization;
using System.Text;
using PdfReader.Core.Text;

namespace PdfReader.Core.Pdf;

/// <summary>One page of a picture-based PDF: a JPEG filling the page, plus (optionally) the words found
/// on it by OCR, laid over it as invisible text so the result is selectable and searchable.</summary>
public sealed record RasterPage(byte[] Jpeg, int PixelWidth, int PixelHeight, double WidthPt, double HeightPt, IReadOnlyList<TextWord>? Text = null);

/// <summary>
/// Writes a PDF whose pages are JPEG pictures -- used for annotated pages (the annotations are painted
/// into the picture), for "compress PDF", and for "searchable PDF" (OCR text hidden under the picture).
/// Hand-written because Android's own PdfDocument stores bitmaps losslessly and so produces huge files,
/// whereas embedding the JPEG as-is lets the quality preset (PdfQuality) really control the size.
/// Pure C#: unit-tested on the PC (read back with PDFsharp and PdfPig).
/// </summary>
public static class RasterPdfWriter
{
	private const int FirstPageObject = 4; // 1 catalog, 2 pages, 3 font; then (page, content, image) per page

	public static void Write(string path, IEnumerable<RasterPage> pages)
	{
		using FileStream file = File.Create(path);
		Write(file, pages);
	}

	public static void Write(Stream output, IEnumerable<RasterPage> pages)
	{
		var offsets = new SortedDictionary<int, long>();
		long position = 0;

		void Raw(byte[] bytes)
		{
			output.Write(bytes, 0, bytes.Length);
			position += bytes.Length;
		}
		void Text(string s) => Raw(Encoding.Latin1.GetBytes(s));
		void Begin(int id)
		{
			offsets[id] = position;
			Text($"{id} 0 obj\n");
		}

		Text("%PDF-1.5\n%âãÏÓ\n");

		// Pass 1 over the pages: they are streamed straight to the file; the glyph table (every character
		// used by any hidden text) is only complete at the end, so the font object is written last.
		var chars = new Dictionary<char, int>(); // char -> CID (1-based)
		int count = 0;
		foreach (RasterPage page in pages)
		{
			int pageId = FirstPageObject + count * 3, contentId = pageId + 1, imageId = pageId + 2;

			string hidden = page.Text is { Count: > 0 } words ? BuildHiddenText(words, page.WidthPt, page.HeightPt, chars) : "";
			string content = $"q {F(page.WidthPt)} 0 0 {F(page.HeightPt)} 0 0 cm /Im0 Do Q\n{hidden}";

			Begin(pageId);
			Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(page.WidthPt)} {F(page.HeightPt)}] " +
				$"/Resources << /XObject << /Im0 {imageId} 0 R >> /Font << /F1 3 0 R >> >> /Contents {contentId} 0 R >>\nendobj\n");

			Begin(contentId);
			byte[] contentBytes = Encoding.Latin1.GetBytes(content);
			Text($"<< /Length {contentBytes.Length} >>\nstream\n");
			Raw(contentBytes);
			Text("\nendstream\nendobj\n");

			Begin(imageId);
			Text($"<< /Type /XObject /Subtype /Image /Width {page.PixelWidth} /Height {page.PixelHeight} " +
				$"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {page.Jpeg.Length} >>\nstream\n");
			Raw(page.Jpeg);
			Text("\nendstream\nendobj\n");

			count++;
		}
		if (count == 0) throw new ArgumentException("Cần ít nhất 1 trang.", nameof(pages));

		Begin(1);
		Text("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

		Begin(2);
		var kids = string.Join(" ", Enumerable.Range(0, count).Select(i => $"{FirstPageObject + i * 3} 0 R"));
		Text($"<< /Type /Pages /Kids [{kids}] /Count {count} >>\nendobj\n");

		int toUnicodeId = FirstPageObject + count * 3;
		WriteFont(chars, toUnicodeId, Begin, Text);

		long xref = position;
		int size = toUnicodeId + 1;
		Text($"xref\n0 {size}\n0000000000 65535 f \n");
		for (int id = 1; id < size; id++) Text($"{offsets[id]:D10} 00000 n \n");
		Text($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
	}

	/// <summary>Invisible text (rendering mode 3), one run per word, squeezed horizontally to cover the
	/// word's box so selection and search highlights land where the word is on the picture.</summary>
	private static string BuildHiddenText(IReadOnlyList<TextWord> words, double widthPt, double heightPt, Dictionary<char, int> chars)
	{
		var sb = new StringBuilder("BT\n3 Tr\n");
		foreach (TextWord w in words)
		{
			if (w.Text.Length == 0) continue;
			double boxW = Math.Max(1, w.Box.Width * widthPt);
			double boxH = Math.Max(1, w.Box.Height * heightPt);
			double fontSize = Math.Max(1, boxH * 0.85);
			double natural = w.Text.Length * 0.5 * fontSize; // every glyph is 500/1000 em wide (DW below)
			double tz = Math.Clamp(boxW / natural * 100, 1, 1000);
			double x = w.Box.Left * widthPt;
			double y = heightPt - w.Box.Bottom * heightPt + boxH * 0.2;

			var hex = new StringBuilder();
			foreach (char c in w.Text)
			{
				if (!chars.TryGetValue(c, out int cid))
				{
					cid = chars.Count + 1;
					chars[c] = cid;
				}
				hex.Append(cid.ToString("X4", CultureInfo.InvariantCulture));
			}
			sb.Append(CultureInfo.InvariantCulture, $"/F1 {F(fontSize)} Tf {F(tz)} Tz 1 0 0 1 {F(x)} {F(y)} Tm <{hex}> Tj\n");
		}
		sb.Append("ET\n");
		return sb.ToString();
	}


	/// <summary>The one font all hidden text uses: a Type0/Identity-H font that is never drawn (so it
	/// embeds no glyphs) with a ToUnicode map from each used CID back to its character -- which is what
	/// text extraction and search read. Object 3 is the font; <paramref name="toUnicodeId"/> its map.</summary>
	private static void WriteFont(Dictionary<char, int> chars, int toUnicodeId, Action<int> begin, Action<string> text)
	{
		var cmap = new StringBuilder();
		cmap.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
		cmap.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
		cmap.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
		var entries = chars.OrderBy(kv => kv.Value).ToList();
		for (int i = 0; i < entries.Count; i += 100)
		{
			var block = entries.Skip(i).Take(100).ToList();
			cmap.Append(CultureInfo.InvariantCulture, $"{block.Count} beginbfchar\n");
			foreach (var kv in block)
				cmap.Append(CultureInfo.InvariantCulture, $"<{kv.Value:X4}> <{(int)kv.Key:X4}>\n");
			cmap.Append("endbfchar\n");
		}
		cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
		string cmapText = cmap.ToString();

		begin(3);
		text("<< /Type /Font /Subtype /Type0 /BaseFont /HiddenText /Encoding /Identity-H " +
			"/DescendantFonts [<< /Type /Font /Subtype /CIDFontType2 /BaseFont /HiddenText " +
			"/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 500 /CIDToGIDMap /Identity " +
			"/FontDescriptor << /Type /FontDescriptor /FontName /HiddenText /Flags 4 /FontBBox [0 -200 1000 800] " +
			"/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >> >>] " +
			$"/ToUnicode {toUnicodeId} 0 R >>\nendobj\n");

		begin(toUnicodeId);
		text($"<< /Length {cmapText.Length} >>\nstream\n{cmapText}\nendstream\nendobj\n");
	}

	private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
