using PdfReader.Views;
using Path = System.IO.Path;

namespace PdfReader.Services;

/// <summary>What happens to a file the app has just produced (an export, a merge, a split part...):
/// keep working with it, save it to Downloads/PdfReader, or share it to another app. The scratch file is
/// removed afterwards.</summary>
public sealed class ResultService(IDownloadsService downloads, PdfOpener opener)
{
	public static string SafeName(string name)
	{
		foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
		name = name.Trim();
		return name.Length == 0 ? "PDF" : name;
	}

	public static string BaseName(string displayName)
	{
		string n = SafeName(displayName);
		return n.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
	}

	/// <summary>Saves a finished PDF straight into Downloads/PdfReader -- no extra "do you want to save?"
	/// step -- then tells the person it is done, with "Xem" (open it) and "Chia sẻ" (share it) on that
	/// same notification. <paramref name="fileName"/> includes ".pdf".</summary>
	public async Task DeliverPdfAsync(string workFile, string fileName)
	{
		Page host = UiHost.Shell;

		if (!downloads.IsSupported)
		{
			// No MediaStore on this Android version: cannot save silently, so ask instead of failing.
			string? choice = await host.ChoiceAsync($"Đã tạo: {fileName}", "Đóng", null, "Mở file mới", "Chia sẻ");
			try
			{
				switch (choice)
				{
					case "Mở file mới": await opener.OpenAsync(workFile, fileName); return;
					case "Chia sẻ": await ShareAsync(workFile, fileName, "application/pdf"); break;
				}
			}
			catch (Exception ex) { await host.AlertAsync("Không hoàn tất được", ex.Message, "Đóng"); }
			finally { if (choice != "Mở file mới") TryDelete(workFile); }
			return;
		}

		string saved;
		try
		{
			saved = await downloads.SaveAsync(workFile, fileName, "application/pdf");
		}
		catch (Exception ex)
		{
			await host.AlertAsync("Không lưu được", ex.Message, "Đóng");
			TryDelete(workFile);
			return;
		}
		await NotifySavedAsync(workFile, fileName, saved);
	}

	/// <summary>The "Đã lưu ..." notification for a file already saved to Downloads/PdfReader, with "Xem"
	/// (open it) and "Chia sẻ" (share it) -- the only two ways to close it, besides the neutral "Xong".
	/// Shared by <see cref="DeliverPdfAsync"/> and by callers (<c>ReaderPage.ExportAsync</c>) that save via
	/// <see cref="SavePdfAsync"/> themselves because they need the busy pill up for the save too.</summary>
	public async Task NotifySavedAsync(string workFile, string fileName, string savedName, long? bytes = null)
	{
		Page host = UiHost.Shell;
		string sizeSuffix = bytes is { } b ? $" · {FormatSize(b)}" : "";
		string? action = await host.ChoiceAsync($"Đã lưu {savedName}{sizeSuffix} vào Tải xuống › {AndroidDownloadsService.Subfolder}", "Xong", null, "Xem", "Chia sẻ");
		try
		{
			switch (action)
			{
				case "Xem": await opener.OpenAsync(workFile, fileName); return; // opening copies it into the library
				case "Chia sẻ": await ShareAsync(workFile, fileName, "application/pdf"); break;
			}
		}
		catch (Exception ex)
		{
			await host.AlertAsync("Không hoàn tất được", ex.Message, "Đóng");
		}
		finally
		{
			if (action != "Xem") TryDelete(workFile);
		}
	}

	/// <summary>"850 KB" below 1000 KB, otherwise "1.2 MB".</summary>
	public static string FormatSize(long bytes)
	{
		long kb = Math.Max(1, bytes / 1024);
		return kb < 1000 ? $"{kb} KB" : $"{bytes / 1_048_576.0:0.#} MB";
	}

	/// <summary>Saves a finished PDF straight into Downloads/PdfReader and returns the name it got and its
	/// size; null when this Android version cannot (the caller falls back to sharing). Deliberately does
	/// NOT delete <paramref name="workFile"/> -- the caller still needs it for "Xem" / "Chia sẻ" on the
	/// notification that follows (<see cref="NotifySavedAsync"/>), which does the cleanup.</summary>
	public async Task<(string Name, long Bytes)?> SavePdfAsync(string workFile, string fileName)
	{
		if (!downloads.IsSupported) return null;
		long bytes = new FileInfo(workFile).Length;
		string saved = await downloads.SaveAsync(workFile, fileName, "application/pdf");
		return (saved, bytes);
	}

	/// <summary>Saves several files (split parts, page images) to Downloads and says where they went.</summary>
	public async Task SaveManyToDownloadsAsync(IReadOnlyList<(string File, string Name)> files, string mimeType)
	{
		Page host = UiHost.Shell;
		if (!downloads.IsSupported)
		{
			await host.AlertAsync("Chưa hỗ trợ", "Lưu vào Tải xuống cần Android 10 trở lên.", "Đóng");
			return;
		}
		try
		{
			foreach ((string file, string name) in files) await downloads.SaveAsync(file, name, mimeType);
			await host.AlertAsync("Đã lưu", $"{files.Count} file trong Tải xuống › {AndroidDownloadsService.Subfolder}.", "OK");
		}
		catch (Exception ex)
		{
			await host.AlertAsync("Không lưu được", ex.Message, "Đóng");
		}
		finally
		{
			foreach ((string file, _) in files) TryDelete(file);
		}
	}

	public async Task ShareAsync(string file, string name, string mimeType)
	{
		// The share sheet shows the file's name, so give the scratch file the proper one.
		string dir = Path.Combine(FileSystem.CacheDirectory, "share");
		Directory.CreateDirectory(dir);
		string target = Path.Combine(dir, name);
		File.Copy(file, target, overwrite: true);
		await Share.Default.RequestAsync(new ShareFileRequest { Title = name, File = new ShareFile(target, mimeType) });
	}

	private static void TryDelete(string path)
	{
		try { File.Delete(path); }
		catch
		{
			// scratch file
		}
	}
}
