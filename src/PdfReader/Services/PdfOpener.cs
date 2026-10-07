using PdfReader.Models;
using PdfReader.Core.Pdf;
using PdfReader.Views;

namespace PdfReader.Services;

/// <summary>
/// The one way a PDF gets opened in the reader, from whichever door it came in (picker, "Open with",
/// recent list, a merge result): copy it into the app's library, ask for the password if it has one,
/// record it in the recent list, and go to the reader. A natural screen transition, so this is also where
/// an interstitial may be shown (<see cref="IAdsService"/>).
/// </summary>
public sealed class PdfOpener(PdfLibrary library, RecentPdfStore store, IAdsService ads)
{
	/// <param name="external">The file came from another app: the reader then closes the whole app when it is
	/// closed, and the interstitial rule is the every-6th-open one rather than the screen-transition one.</param>
	public async Task<bool> OpenAsync(string uriOrPath, string? displayName = null, bool external = false)
	{
		Page host = UiHost.Shell;
		try
		{
			// Hard rule: no spinner waits more than 1 minute. The password prompt below is NOT part of
			// that budget -- it is a dialog with its own Huỷ button, waiting on the person, not a stuck
			// spinner; only the unattended disk/parse steps are timed.
			(bool imported, string? importedPath) = await library.ImportAsync(uriOrPath).WaitOrTimeoutAsync();
			if (!imported) throw new TimeoutException("Mở file mất hơn 1 phút nên đã dừng chờ. Hãy thử lại.");
			string local = importedPath!;

			(bool probed, int pageCount) = await ProbeAsync(local).WaitOrTimeoutAsync();
			if (!probed) throw new TimeoutException("Đọc file mất hơn 1 phút nên đã dừng chờ. Hãy thử lại.");
			if (pageCount < 0)
			{
				if (!await UnlockAsync(host, local)) return false;
				(probed, pageCount) = await ProbeAsync(local).WaitOrTimeoutAsync();
				if (!probed) throw new TimeoutException("Đọc file mất hơn 1 phút nên đã dừng chờ. Hãy thử lại.");
			}
			if (pageCount == 0) throw new IOException("File PDF này không có trang nào.");

			if (external) ads.RegisterExternalOpen();
			else ads.RegisterScreenTransition();
			RecentPdfRecord record = await store.RecordOpenedAsync(local, displayName ?? PdfLibrary.DisplayNameOf(uriOrPath), pageCount);
			await UiHost.Shell.GoToAsync(nameof(ReaderPage), new Dictionary<string, object>
			{
				["uri"] = record.Uri,
				["displayName"] = record.DisplayName,
				["external"] = external,
			});
			return true;
		}
		catch (Exception ex)
		{
			await host.AlertAsync("Không mở được PDF", ex.Message, "Đóng");
			return false;
		}
	}

	/// <summary>True when the file can be rendered. A password-protected one is unlocked in place (the app's
	/// own private copy) after asking for the password; false when the person gives up.</summary>
	private async Task<bool> UnlockAsync(Page host, string local)
	{
		for (int attempt = 0; attempt < 3; attempt++)
		{
			string? password = await host.PromptAsync("Tài liệu có mật khẩu", attempt == 0 ? "Nhập mật khẩu để mở file này." : "Mật khẩu chưa đúng, thử lại.",
				"Mở", "Huỷ", "Mật khẩu", maxLength: 64, keyboard: Keyboard.Text);
			if (string.IsNullOrEmpty(password)) return false;
			string temp = library.NewWorkFile();
			try
			{
				await Task.Run(() => PdfStructureOps.Decrypt(local, temp, password));
				File.Move(temp, local, overwrite: true);
				return true;
			}
			catch (Exception ex)
			{
				Android.Util.Log.Warn("PdfReader", $"unlock failed: {ex.Message}");
				try { File.Delete(temp); } catch { }
			}
		}
		await host.AlertAsync("Không mở được", "Mật khẩu sai, hoặc kiểu mã hoá của file này chưa được hỗ trợ.", "Đóng");
		return false;
	}

	/// <summary>Page count, or -1 when the file needs a password.</summary>
	private static Task<int> ProbeAsync(string local) =>
		Task.Run(() =>
		{
			try
			{
				using PdfPages pages = PdfPages.Open(local);
				return pages.Count;
			}
			catch (Java.Lang.SecurityException)
			{
				return -1;
			}
		});
}
