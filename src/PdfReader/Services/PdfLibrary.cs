using System.Security.Cryptography;
using System.Text;
using Path = System.IO.Path;

namespace PdfReader.Services;

/// <summary>
/// The app's own private copies of the PDFs it has opened, plus where per-document data (annotations, OCR
/// text) lives. A PDF handed in by another app or the picker arrives as a content:// Uri whose read
/// permission can vanish when the app restarts (the open TODO in plan section 8, item 4); copying it into
/// app storage once makes "recent files" reliably reopenable and gives PdfPig / PDFsharp a real file to
/// read. Everything stays on the phone.
/// </summary>
public sealed class PdfLibrary
{
	private readonly string _pdfDir;
	private readonly string _dataDir;

	public PdfLibrary(string appDataDirectory)
	{
		_pdfDir = Path.Combine(appDataDirectory, "pdfs");
		_dataDir = Path.Combine(appDataDirectory, "docdata");
		Directory.CreateDirectory(_pdfDir);
		Directory.CreateDirectory(_dataDir);
	}

	public string WorkDirectory { get; } = Path.Combine(Path.GetTempPath(), "pdfreader-work");

	public bool IsInLibrary(string path) => path.StartsWith(_pdfDir, StringComparison.OrdinalIgnoreCase);

	/// <summary>Copies <paramref name="uriOrPath"/> into the library (unless it already lives there) and
	/// returns the local path. Opening the same source again overwrites its copy, so an updated file is
	/// picked up.</summary>
	public Task<string> ImportAsync(string uriOrPath) =>
		Task.Run(() =>
		{
			if (IsInLibrary(uriOrPath)) return uriOrPath;
			string target = Path.Combine(_pdfDir, KeyOf(uriOrPath) + ".pdf");
			string temp = target + ".tmp";
			using (FileStream output = File.Create(temp))
			{
				if (uriOrPath.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
				{
					using Stream input = Android.App.Application.Context.ContentResolver!.OpenInputStream(Android.Net.Uri.Parse(uriOrPath)!)
						?? throw new IOException("Không đọc được file PDF.");
					input.CopyTo(output);
				}
				else
				{
					string path = uriOrPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
						? Android.Net.Uri.Parse(uriOrPath)!.Path ?? uriOrPath
						: uriOrPath;
					using FileStream input = File.OpenRead(path);
					input.CopyTo(output);
				}
			}
			File.Move(temp, target, overwrite: true);
			return target;
		});

	/// <summary>Registers a file this app just produced (merge result, export...) as a document of its own,
	/// moving it into the library under a fresh name.</summary>
	public string AdoptNew(string producedFile)
	{
		string target = Path.Combine(_pdfDir, Guid.NewGuid().ToString("N") + ".pdf");
		File.Move(producedFile, target);
		return target;
	}

	/// <summary>A scratch file path for a result still being built.</summary>
	public string NewWorkFile(string extension = ".pdf")
	{
		Directory.CreateDirectory(WorkDirectory);
		return Path.Combine(WorkDirectory, Guid.NewGuid().ToString("N") + extension);
	}

	/// <summary>Per-document data file, e.g. <c>DataFile(pdf, "annotations.json")</c>.</summary>
	public string DataFile(string localPdfPath, string suffix) => Path.Combine(_dataDir, KeyOf(localPdfPath) + "." + suffix);

	/// <summary>How many documents the app is holding copies of, and how much space they and their saved
	/// annotations / OCR text take.</summary>
	public (int Count, long Bytes) Usage()
	{
		string[] pdfs = Directory.GetFiles(_pdfDir, "*.pdf");
		long bytes = pdfs.Concat(Directory.GetFiles(_dataDir)).Sum(f => new FileInfo(f).Length);
		return (pdfs.Length, bytes);
	}

	public void DeleteDocument(string localPdfPath)
	{
		TryDelete(localPdfPath);
		foreach (string f in Directory.GetFiles(_dataDir, KeyOf(localPdfPath) + ".*")) TryDelete(f);
	}

	/// <summary>A human name for what was opened: the file name a content:// Uri stands for (asked of the
	/// provider), or the last path segment.</summary>
	public static string DisplayNameOf(string uriOrPath)
	{
		if (uriOrPath.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				using Android.Database.ICursor? cursor = Android.App.Application.Context.ContentResolver!.Query(
					Android.Net.Uri.Parse(uriOrPath)!, [Android.Provider.IOpenableColumns.DisplayName], null, null, null);
				if (cursor != null && cursor.MoveToFirst())
				{
					string? name = cursor.GetString(0);
					if (!string.IsNullOrWhiteSpace(name)) return name;
				}
			}
			catch
			{
				// fall through to guessing from the Uri text
			}
		}
		string last = uriOrPath.TrimEnd('/').Split('/').LastOrDefault() ?? uriOrPath;
		return Uri.UnescapeDataString(last);
	}

	/// <summary>Short stable id of a path/Uri.</summary>
	public static string KeyOf(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..24];

	private static void TryDelete(string path)
	{
		try { File.Delete(path); }
		catch
		{
			// best effort
		}
	}
}
