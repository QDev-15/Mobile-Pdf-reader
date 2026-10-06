using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ImageCoreService;

/// <summary>
/// Minimal PNG encoder for gray pages (no platform codec needed, so it runs the same on the phone
/// and in unit tests). A black-and-white page is written as a 1-bit PNG: an A4 page at 300 DPI is a
/// few hundred KB, and its compressed data (zlib, one PNG filter byte per row) is exactly what a PDF
/// image with <c>/FlateDecode /Predictor 15</c> expects, so the PDF export can embed it without
/// decoding (see <see cref="PngReader"/>).
/// </summary>
public static class PngWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>1-bit grayscale PNG: pixels below 128 become black (0), the rest white (1).</summary>
    public static byte[] EncodeBilevel(GrayImage img)
    {
        int rowBytes = (img.Width + 7) / 8;
        var raw = new byte[(rowBytes + 1) * img.Height];
        Parallel.For(0, img.Height, ParallelScope.Options, y =>
        {
            int o = y * (rowBytes + 1); // raw[o] = 0: filter "None"
            int src = y * img.Width;
            for (int x = 0; x < img.Width; x++)
                if (img.Data[src + x] >= 128)
                    raw[o + 1 + (x >> 3)] |= (byte)(0x80 >> (x & 7));
        });
        return Encode(img.Width, img.Height, bitDepth: 1, raw);
    }

    /// <summary>8-bit grayscale PNG (lossless).</summary>
    public static byte[] EncodeGray8(GrayImage img)
    {
        var raw = new byte[(img.Width + 1) * img.Height];
        for (int y = 0; y < img.Height; y++)
            Buffer.BlockCopy(img.Data, y * img.Width, raw, y * (img.Width + 1) + 1, img.Width);
        return Encode(img.Width, img.Height, bitDepth: 8, raw);
    }

    /// <summary>8-bit RGBA PNG from <paramref name="rgba"/> (4 bytes per pixel, straight alpha).</summary>
    public static byte[] EncodeRgba(int width, int height, byte[] rgba)
    {
        var raw = new byte[(width * 4 + 1) * height];
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(rgba, y * width * 4, raw, y * (width * 4 + 1) + 1, width * 4);
        return Encode(width, height, bitDepth: 8, raw, colorType: 6);
    }

    private static byte[] Encode(int width, int height, int bitDepth, byte[] filteredRows, byte colorType = 0)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = colorType; // 0 = grayscale, 6 = RGBA
        ihdr[10] = 0; // deflate
        ihdr[11] = 0; // adaptive filtering (we use filter 0 on every row)
        ihdr[12] = 0; // no interlace
        WriteChunk(ms, "IHDR", ihdr);

        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(filteredRows);
            WriteChunk(ms, "IDAT", z.ToArray());
        }
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }
}

/// <summary>Reads the header and the raw compressed image data of a PNG, for embedding in a PDF as-is.</summary>
public static class PngReader
{
    public sealed record PngData(int Width, int Height, int BitDepth, int ColorType, byte[] ZlibData);

    public static PngData Read(byte[] png)
    {
        if (png.Length < 8 || png[0] != 137 || png[1] != 80 || png[2] != 78 || png[3] != 71)
            throw new InvalidDataException("Not a PNG file.");
        int pos = 8, width = 0, height = 0, depth = 0, colorType = 0, interlace = 0;
        using var idat = new MemoryStream();
        while (pos + 8 <= png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            string type = Encoding.ASCII.GetString(png, pos + 4, 4);
            if (len < 0 || pos + 12 + len > png.Length) throw new InvalidDataException("Truncated PNG chunk.");
            ReadOnlySpan<byte> data = png.AsSpan(pos + 8, len);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                depth = data[8];
                colorType = data[9];
                interlace = data[12];
            }
            else if (type == "IDAT") idat.Write(data);
            else if (type == "IEND") break;
            pos += 12 + len;
        }
        if (width <= 0 || height <= 0) throw new InvalidDataException("PNG has no header.");
        if (interlace != 0) throw new InvalidDataException("Interlaced PNG is not supported.");
        return new PngData(width, height, depth, colorType, idat.ToArray());
    }

    /// <summary>Decodes an 8-bit grayscale PNG (any row filter), e.g. a smooth black-and-white page.</summary>
    public static GrayImage DecodeGray8(byte[] png)
    {
        PngData p = Read(png);
        if (p.ColorType != 0 || p.BitDepth != 8) throw new InvalidDataException($"Not an 8-bit gray PNG ({p.ColorType}, {p.BitDepth} bit).");
        int w = p.Width, h = p.Height;
        var raw = new byte[(w + 1) * h];
        using (var z = new System.IO.Compression.ZLibStream(new MemoryStream(p.ZlibData), System.IO.Compression.CompressionMode.Decompress))
            z.ReadExactly(raw);
        var img = new GrayImage(w, h);
        byte[] d = img.Data;
        for (int y = 0; y < h; y++)
        {
            int filter = raw[y * (w + 1)], src = y * (w + 1) + 1, o = y * w;
            for (int x = 0; x < w; x++)
            {
                int a = x > 0 ? d[o + x - 1] : 0, b = y > 0 ? d[o - w + x] : 0, c = x > 0 && y > 0 ? d[o - w + x - 1] : 0;
                int v = raw[src + x];
                d[o + x] = (byte)(filter switch
                {
                    0 => v,
                    1 => v + a,
                    2 => v + b,
                    3 => v + ((a + b) >> 1),
                    4 => v + Paeth(a, b, c),
                    _ => throw new InvalidDataException($"Bad PNG row filter {filter}."),
                });
            }
        }
        return img;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}

/// <summary>CRC-32 (ISO 3309 / PNG).</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Update(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;
}
