using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Android.Graphics;
using PdfReader.Models;
using Path = System.IO.Path;

namespace PdfReader.Services;

/// <summary>
/// Small JSON-backed store for the Dashboard's "recently opened" list -- new for PdfReader, replacing
/// DocScanner's DocumentStore (too specific to its own "scan -> document -> folder" model; plan section
/// 7). Also renders and caches a real first-page thumbnail (plan section 5: "lưới thumbnail trang đầu
/// thật", not just a file-name list) the first time a PDF is recorded.
/// </summary>
public sealed class RecentPdfStore
{
	private readonly string _jsonPath;
	private readonly string _thumbDir;
	private readonly SemaphoreSlim _gate = new(1, 1);

	public RecentPdfStore(string appDataDirectory)
	{
		_jsonPath = Path.Combine(appDataDirectory, "recent.json");
		_thumbDir = Path.Combine(appDataDirectory, "thumbnails");
		Directory.CreateDirectory(_thumbDir);
	}

	public async Task<IReadOnlyList<RecentPdfRecord>> ListAsync()
	{
		await _gate.WaitAsync();
		try { return Load().OrderByDescending(r => r.LastOpenedUtc).ToList(); }
		finally { _gate.Release(); }
	}

	/// <summary>Call right after a PDF is successfully opened. Renders a thumbnail on first sight of this
	/// Uri; later calls just bump <see cref="RecentPdfRecord.LastOpenedUtc"/> and page count.</summary>
	public async Task<RecentPdfRecord> RecordOpenedAsync(string uri, string displayName, int pageCount)
	{
		await _gate.WaitAsync();
		try
		{
			List<RecentPdfRecord> list = Load();
			RecentPdfRecord? existing = list.FirstOrDefault(r => r.Uri == uri);
			string? thumbnailPath = existing?.ThumbnailPath;
			if (thumbnailPath == null || !File.Exists(thumbnailPath))
				thumbnailPath = TryRenderThumbnail(uri);

			var updated = new RecentPdfRecord(uri, displayName, pageCount, DateTimeOffset.UtcNow, existing?.ReadProgress ?? 0)
			{
				ThumbnailPath = thumbnailPath,
			};
			list.RemoveAll(r => r.Uri == uri);
			list.Add(updated);
			Save(list);
			return updated;
		}
		finally { _gate.Release(); }
	}

	public async Task UpdateProgressAsync(string uri, double readProgress)
	{
		await _gate.WaitAsync();
		try
		{
			List<RecentPdfRecord> list = Load();
			int i = list.FindIndex(r => r.Uri == uri);
			if (i < 0) return;
			list[i] = list[i] with { ReadProgress = readProgress };
			Save(list);
		}
		finally { _gate.Release(); }
	}

	private List<RecentPdfRecord> Load()
	{
		if (!File.Exists(_jsonPath)) return [];
		try { return JsonSerializer.Deserialize<List<RecentPdfRecord>>(File.ReadAllText(_jsonPath)) ?? []; }
		catch { return []; } // corrupt/old-shape file: start fresh rather than crash the Dashboard
	}

	private void Save(List<RecentPdfRecord> list) =>
		File.WriteAllText(_jsonPath, JsonSerializer.Serialize(list));

	private string? TryRenderThumbnail(string uri)
	{
		try
		{
			using PdfPages pages = PdfPages.Open(uri);
			if (pages.Count == 0) return null;
			using Bitmap bitmap = pages.Render(0, longEdge: 200);
			string path = Path.Combine(_thumbDir, Sha256Hex(uri) + ".png");
			using FileStream file = File.Create(path);
			bitmap.Compress(Bitmap.CompressFormat.Png!, 100, file); // Compress takes a plain System.IO.Stream
			return path;
		}
		catch (Exception ex)
		{
			Android.Util.Log.Warn("PdfReader", $"thumbnail render failed: {ex.Message}");
			return null;
		}
	}

	private static string Sha256Hex(string s) =>
		Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
