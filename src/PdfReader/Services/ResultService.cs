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

	/// <summary>Asks what to do with a finished PDF. <paramref name="fileName"/> includes ".pdf".</summary>
	public async Task DeliverPdfAsync(string workFile, string fileName)
	{
		Page host = UiHost.Shell;
		string? choice = await host.ChoiceAsync($"Đã tạo: {fileName}", "Để sau", null, "Mở file mới", "Lưu vào Tải xuống", "Chia sẻ");
		try
		{
			switch (choice)
			{
				case "Mở file mới":
					await opener.OpenAsync(workFile, fileName);
					return; // opening copies it into the library; the scratch file is left for the OS temp cleanup
				case "Lưu vào Tải xuống":
					await SaveToDownloadsAsync(host, workFile, fileName, "application/pdf");
					break;
				case "Chia sẻ":
					await ShareAsync(workFile, fileName, "application/pdf");
					break;
			}
		}
		catch (Exception ex)
		{
			await host.AlertAsync("Không hoàn tất được", ex.Message, "Đóng");
		}
		finally
		{
			if (choice != "Mở file mới" && choice != "Chia sẻ") TryDelete(workFile);
		}
	}

	/// <summary>"850 KB" below 1000 KB, otherwise "1.2 MB".</summary>
	public static string FormatSize(long bytes)
	{
		long kb = Math.Max(1, bytes / 1024);
		return kb < 1000 ? $"{kb} KB" : $"{bytes / 1_048_576.0:0.#} MB";
	}

	/// <summary>Saves a finished PDF straight into Downloads/PdfReader and returns the name it got and its
	/// size; null when this Android version cannot (the caller falls back to sharing).</summary>
	public async Task<(string Name, long Bytes)?> SavePdfAsync(string workFile, string fileName)
	{
		if (!downloads.IsSupported) return null;
		long bytes = new FileInfo(workFile).Length;
		string saved = await downloads.SaveAsync(workFile, fileName, "application/pdf");
		TryDelete(workFile);
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

	private async Task SaveToDownloadsAsync(Page host, string file, string name, string mimeType)
	{
		if (!downloads.IsSupported)
		{
			await host.AlertAsync("Chưa hỗ trợ", "Lưu vào Tải xuống cần Android 10 trở lên. Hãy dùng Chia sẻ.", "Đóng");
			return;
		}
		string saved = await downloads.SaveAsync(file, name, mimeType);
		await host.AlertAsync("Đã lưu", $"{saved}\nTrong Tải xuống › {AndroidDownloadsService.Subfolder}.", "OK");
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
