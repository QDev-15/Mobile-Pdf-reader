using Android.Graphics;
using Path = System.IO.Path;

namespace PdfReader.Services;

/// <summary>
/// The user's own signature as a picture (a photo or scan of a handwritten signature, or an exported
/// digital-signature stamp). The picture is cleaned once on the way in -- a white or light paper background
/// becomes transparent and the empty border is trimmed -- and kept as a PNG in the app's private storage, so
/// it can be placed on any page without a white box around it, and survives the original being deleted.
/// A picture that already has transparency is only trimmed.
/// </summary>
public sealed class SignatureImageStore
{
	private const int MaxSide = 800;

	private readonly string _dir;

	public SignatureImageStore(string appDataDirectory)
	{
		_dir = Path.Combine(appDataDirectory, "signature-images");
		Directory.CreateDirectory(_dir);
	}

	/// <summary>Lets the person pick a picture and returns the cleaned copy; null when they cancel or the
	/// picture has no visible ink.</summary>
	public async Task<(string File, float Aspect)?> PickAndImportAsync()
	{
		FileResult? picked = await FilePicker.Default.PickAsync(new PickOptions
		{
			PickerTitle = "Chọn ảnh chữ ký",
			FileTypes = FilePickerFileType.Images,
		});
		if (picked == null) return null;

		await using Stream input = await picked.OpenReadAsync();
		using var buffer = new MemoryStream();
		await input.CopyToAsync(buffer);
		byte[] bytes = buffer.ToArray();

		(string File, float Aspect)? result = await Task.Run(() => Clean(bytes));
		// The cleaned PNG is never deleted here: pages the picture was already placed on refer to it.
		return result;
	}

	private (string File, float Aspect)? Clean(byte[] bytes)
	{
		// A camera photo can be 12+ megapixels: read it already shrunk instead of decoding all of it first.
		var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
		BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, bounds);
		int sample = 1;
		while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= MaxSide) sample *= 2;
		using Bitmap? decoded = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, new BitmapFactory.Options { InSampleSize = sample });
		if (decoded == null) throw new IOException("Không đọc được ảnh này.");

		Bitmap source = decoded;
		Bitmap? scaled = null;
		int longest = Math.Max(decoded.Width, decoded.Height);
		if (longest > MaxSide)
		{
			double k = (double)MaxSide / longest;
			scaled = Bitmap.CreateScaledBitmap(decoded, Math.Max(1, (int)(decoded.Width * k)), Math.Max(1, (int)(decoded.Height * k)), true)!;
			source = scaled;
		}

		try
		{
			int w = source.Width, h = source.Height;
			int[] px = new int[w * h];
			source.GetPixels(px, 0, w, 0, 0, w, h);

			int see = 0;
			foreach (int p in px) if (((uint)p >> 24) < 250) see++;
			bool hasTransparency = see > px.Length / 100;

			int minX = w, minY = h, maxX = -1, maxY = -1;
			for (int i = 0; i < px.Length; i++)
			{
				uint c = (uint)px[i];
				uint a = c >> 24;
				if (!hasTransparency)
				{
					uint r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
					uint lum = (299 * r + 587 * g + 114 * b) / 1000;
					// Paper (light) becomes see-through, dark ink stays solid, the soft edge in between fades.
					a = lum >= 235 ? 0u : lum <= 140 ? 255u : (235 - lum) * 255 / (235 - 140);
					px[i] = unchecked((int)((a << 24) | (c & 0x00FFFFFF)));
				}
				if (a > 24)
				{
					int x = i % w, y = i / w;
					if (x < minX) minX = x;
					if (x > maxX) maxX = x;
					if (y < minY) minY = y;
					if (y > maxY) maxY = y;
				}
			}
			if (maxX < 0) return null; // nothing visible

			int pad = Math.Max(2, (int)(Math.Max(maxX - minX, maxY - minY) * 0.03));
			int left = Math.Max(0, minX - pad), top = Math.Max(0, minY - pad);
			int right = Math.Min(w - 1, maxX + pad), bottom = Math.Min(h - 1, maxY + pad);

			Bitmap full = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!)!;
			Bitmap? cut = null;
			try
			{
				full.SetPixels(px, 0, w, 0, 0, w, h);
				// Bitmap.CreateBitmap with a region that is the whole picture hands back the SAME bitmap, so a
				// second Dispose of it would crash: only dispose what is really a separate copy.
				bool whole = left == 0 && top == 0 && right == w - 1 && bottom == h - 1;
				Bitmap trimmed = whole ? full : (cut = Bitmap.CreateBitmap(full, left, top, right - left + 1, bottom - top + 1)!);

				string file = Path.Combine(_dir, $"signature-{Guid.NewGuid():N}.png");
				using (FileStream fs = File.Create(file))
					trimmed.Compress(Bitmap.CompressFormat.Png!, 100, fs);
				return (file, (float)trimmed.Width / trimmed.Height);
			}
			finally
			{
				cut?.Dispose();
				full.Dispose();
			}
		}
		finally
		{
			scaled?.Dispose();
		}
	}
}
