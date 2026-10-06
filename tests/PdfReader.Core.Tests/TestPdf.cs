using System.Text;

namespace PdfReader.Core.Tests;

internal static class TestPdf
{
	/// <summary>A valid PDF with one page per string, each showing that text in Helvetica 24 near the
	/// top-left (x=100, baseline y=700 on a 612x792 page).</summary>
	public static byte[] Make(params string[] pageTexts)
	{
		var objects = new List<string>
		{
			"<< /Type /Catalog /Pages 2 0 R >>",
			"", // pages, filled below
			"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
		};
		var kids = new List<string>();
		foreach (string text in pageTexts)
		{
			int pageObj = objects.Count + 1, contentObj = objects.Count + 2;
			kids.Add($"{pageObj} 0 R");
			objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {contentObj} 0 R >>");
			string stream = $"BT /F1 24 Tf 100 700 Td ({text}) Tj ET";
			objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
		}
		objects[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count {kids.Count} >>";

		var sb = new StringBuilder("%PDF-1.4\n");
		var offsets = new List<int>();
		for (int i = 0; i < objects.Count; i++)
		{
			offsets.Add(sb.Length);
			sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
		}
		int xref = sb.Length;
		sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
		foreach (int o in offsets) sb.Append($"{o:D10} 00000 n \n");
		sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
		return Encoding.ASCII.GetBytes(sb.ToString());
	}

	public static string Write(string dir, string name, params string[] pageTexts)
	{
		string path = Path.Combine(dir, name);
		File.WriteAllBytes(path, Make(pageTexts));
		return path;
	}

	public static string TempDir()
	{
		string dir = Path.Combine(Path.GetTempPath(), "pdfreader-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}
}
