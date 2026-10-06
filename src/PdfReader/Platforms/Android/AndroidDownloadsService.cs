using Android.Content;
using Android.OS;
using Android.Provider;
using PdfReader.Services;

namespace PdfReader;

/// <summary>
/// Android 10+ (API 29): inserts the file into <c>MediaStore.Downloads</c>, which needs no storage
/// permission, under Downloads/<see cref="Subfolder"/>. The entry is written while marked pending
/// (hidden from other apps) and published at the end, so a half-written PDF never shows up in the Files
/// app. Ported from DocScanner's AndroidDownloadsService (Mobile-doc-scanner repo); only
/// <see cref="Subfolder"/> changed.
/// </summary>
public sealed class AndroidDownloadsService : IDownloadsService
{
	public const string Subfolder = "PdfReader";

	public bool IsSupported => OperatingSystem.IsAndroidVersionAtLeast(29);

	public Task<string> SaveAsync(string sourcePath, string displayName, string mimeType, CancellationToken ct = default) =>
		Task.Run(() =>
		{
			if (!OperatingSystem.IsAndroidVersionAtLeast(29)) throw new PlatformNotSupportedException("Cần Android 10 trở lên.");
			ContentResolver resolver = Platform.AppContext.ContentResolver!;
			var values = new ContentValues();
			values.Put(MediaStore.IMediaColumns.DisplayName, displayName);
			values.Put(MediaStore.IMediaColumns.MimeType, mimeType);
			values.Put(MediaStore.IMediaColumns.RelativePath, $"{Android.OS.Environment.DirectoryDownloads}/{Subfolder}");
			values.Put(MediaStore.IMediaColumns.IsPending, 1);

			Android.Net.Uri uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri!, values)
				?? throw new IOException("Không tạo được file trong thư mục Tải xuống.");
			try
			{
				using (Stream output = resolver.OpenOutputStream(uri) ?? throw new IOException("Không ghi được file."))
				using (FileStream input = File.OpenRead(sourcePath))
					input.CopyTo(output);
				ct.ThrowIfCancellationRequested();

				var done = new ContentValues();
				done.Put(MediaStore.IMediaColumns.IsPending, 0);
				resolver.Update(uri, done, null, null);
			}
			catch
			{
				resolver.Delete(uri, null, null);
				throw;
			}

			using var cursor = resolver.Query(uri, [MediaStore.IMediaColumns.DisplayName], null, null, null);
			return cursor != null && cursor.MoveToFirst() ? cursor.GetString(0) ?? displayName : displayName;
		}, ct);
}
