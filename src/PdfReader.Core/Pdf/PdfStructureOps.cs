using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Security;
using SharpReader = PdfSharp.Pdf.IO.PdfReader;

namespace PdfReader.Core.Pdf;

/// <summary>One page of a rebuilt document: which page of the source it comes from, and how many degrees
/// (a multiple of 90, clockwise) to turn it on top of whatever rotation it already has. A page that is
/// left out of the list is deleted; the list order is the new page order.</summary>
public sealed record PageEdit(int SourceIndex, int RotateDelta = 0);

/// <summary>
/// Page-level PDF surgery with PDFsharp (MIT, pure C#): merge, split, delete / rotate / reorder pages,
/// password protect / unlock, and swapping chosen pages for pre-rendered ones (how annotated pages are
/// baked in on export). Pages are copied across as they are -- text, vectors and fonts survive.
/// Platform-neutral, so it is unit-tested on the PC.
/// </summary>
public static class PdfStructureOps
{
	public static int PageCount(string path, string? password = null)
	{
		using PdfDocument doc = Open(path, password);
		return doc.PageCount;
	}

	/// <summary>Writes a new PDF made of <paramref name="pages"/> from <paramref name="sourcePath"/>.</summary>
	public static void Rebuild(string sourcePath, IReadOnlyList<PageEdit> pages, string outputPath, string? password = null)
	{
		if (pages.Count == 0) throw new ArgumentException("Tài liệu mới phải có ít nhất 1 trang.", nameof(pages));
		using PdfDocument source = Open(sourcePath, password);
		using var output = new PdfDocument();
		foreach (PageEdit edit in pages)
		{
			if (edit.SourceIndex < 0 || edit.SourceIndex >= source.PageCount)
				throw new ArgumentOutOfRangeException(nameof(pages), $"Trang {edit.SourceIndex + 1} không tồn tại.");
			PdfPage page = output.AddPage(source.Pages[edit.SourceIndex]);
			if (edit.RotateDelta != 0) page.Rotate = Normalize(page.Rotate + edit.RotateDelta);
		}
		output.Save(outputPath);
	}

	/// <summary>Concatenates the PDFs in order.</summary>
	public static void Merge(IReadOnlyList<string> sourcePaths, string outputPath)
	{
		if (sourcePaths.Count == 0) throw new ArgumentException("Chưa chọn file nào.", nameof(sourcePaths));
		using var output = new PdfDocument();
		var opened = new List<PdfDocument>();
		try
		{
			foreach (string path in sourcePaths)
			{
				PdfDocument src = Open(path, null);
				opened.Add(src); // PDFsharp reads imported pages lazily, so the source stays open until Save
				for (int i = 0; i < src.PageCount; i++) output.AddPage(src.Pages[i]);
			}
			output.Save(outputPath);
		}
		finally
		{
			foreach (PdfDocument d in opened) d.Dispose();
		}
	}

	/// <summary>Saves each (0-based, inclusive) range as its own PDF at the path
	/// <paramref name="outputPathFor"/> returns for its index.</summary>
	public static void Split(string sourcePath, IReadOnlyList<(int Start, int End)> ranges, Func<int, string> outputPathFor, string? password = null)
	{
		for (int r = 0; r < ranges.Count; r++)
		{
			List<PageEdit> pages = [];
			for (int p = ranges[r].Start; p <= ranges[r].End; p++) pages.Add(new PageEdit(p));
			Rebuild(sourcePath, pages, outputPathFor(r), password);
		}
	}

	/// <summary>Copy of the document with <paramref name="replacements"/> (page index -> a one-page PDF)
	/// swapped in for those pages. Every other page is copied untouched, so only edited pages are turned
	/// into pictures.</summary>
	public static void ReplacePages(string sourcePath, IReadOnlyDictionary<int, string> replacements, string outputPath, string? password = null)
	{
		using PdfDocument source = Open(sourcePath, password);
		using var output = new PdfDocument();
		var opened = new List<PdfDocument>();
		try
		{
			for (int i = 0; i < source.PageCount; i++)
			{
				if (replacements.TryGetValue(i, out string? replacementPath))
				{
					PdfDocument rep = Open(replacementPath, null);
					opened.Add(rep);
					output.AddPage(rep.Pages[0]);
				}
				else output.AddPage(source.Pages[i]);
			}
			output.Save(outputPath);
		}
		finally
		{
			foreach (PdfDocument d in opened) d.Dispose();
		}
	}

	/// <summary>Copy that asks for <paramref name="userPassword"/> to open (and that password for owner
	/// rights too, unless <paramref name="ownerPassword"/> is given).</summary>
	public static void Encrypt(string sourcePath, string outputPath, string userPassword, string? ownerPassword = null)
	{
		if (string.IsNullOrEmpty(userPassword)) throw new ArgumentException("Mật khẩu không được để trống.", nameof(userPassword));
		using PdfDocument doc = SharpReader.Open(sourcePath, PdfDocumentOpenMode.Modify);
		PdfSecuritySettings security = doc.SecuritySettings;
		security.UserPassword = userPassword;
		security.OwnerPassword = string.IsNullOrEmpty(ownerPassword) ? userPassword : ownerPassword;
		doc.Save(outputPath);
	}

	/// <summary>Writes an unprotected copy of a password-protected PDF. Throws
	/// <see cref="PdfReaderException"/> when the password is wrong.</summary>
	public static void Decrypt(string sourcePath, string outputPath, string password)
	{
		// Importing the pages into a fresh document drops the encryption.
		int count = PageCount(sourcePath, password);
		Rebuild(sourcePath, Enumerable.Range(0, count).Select(i => new PageEdit(i)).ToList(), outputPath, password);
	}

	private static PdfDocument Open(string path, string? password) =>
		password == null
			? SharpReader.Open(path, PdfDocumentOpenMode.Import)
			: SharpReader.Open(path, password, PdfDocumentOpenMode.Import);

	private static int Normalize(int degrees) => ((degrees % 360) + 360) % 360;
}
