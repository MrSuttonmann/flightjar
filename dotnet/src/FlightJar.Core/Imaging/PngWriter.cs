using System.IO.Compression;
using System.Text;

namespace FlightJar.Core.Imaging;

/// <summary>
/// Minimal PNG encoder — just enough to ship a single-image-data-stream
/// 8-bit RGBA file out of the API. No interlacing, no ancillary chunks
/// beyond IHDR / IDAT / IEND, no per-row filter heuristics (always None).
/// </summary>
/// <remarks>
/// We hand-roll this rather than pull in ImageSharp / SkiaSharp because the
/// only consumer is the blocker-face raster and the compressor side
/// (zlib via <see cref="ZLibStream"/>) is already in the framework. The
/// CRC table and chunk framing are bog-standard PNG spec —
/// <see href="https://www.w3.org/TR/png/"/>.
/// </remarks>
public static class PngWriter
{
    private static readonly byte[] Signature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Encode a row-major 8-bit RGBA image. <paramref name="rgba"/> length
    /// must equal <c>width * height * 4</c>.
    /// </summary>
    public static byte[] EncodeRgba(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("width/height must be positive");
        }
        var expected = checked(width * height * 4);
        if (rgba.Length != expected)
        {
            throw new ArgumentException(
                $"rgba length {rgba.Length} does not match width*height*4 = {expected}");
        }

        using var ms = new MemoryStream();
        ms.Write(Signature);
        WriteIhdr(ms, width, height, bitDepth: 8, colorType: 6);
        WriteIdat(ms, rgba, width, height, bytesPerPixel: 4);
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>
    /// Convenience: encode a single-channel alpha mask as RGBA where every
    /// pixel takes the same RGB triple and alpha comes from the mask. Saves
    /// the caller stitching the bytes themselves; PNG-compressed output is
    /// roughly the same size (zlib eats the constant RGB columns) so the
    /// extra memory pass is negligible.
    /// </summary>
    public static byte[] EncodeAlphaMask(
        int width, int height, ReadOnlySpan<byte> alpha,
        byte r, byte g, byte b)
    {
        var expected = width * height;
        if (alpha.Length != expected)
        {
            throw new ArgumentException(
                $"alpha length {alpha.Length} does not match width*height = {expected}");
        }
        var rgba = new byte[expected * 4];
        for (var i = 0; i < expected; i++)
        {
            rgba[i * 4] = r;
            rgba[i * 4 + 1] = g;
            rgba[i * 4 + 2] = b;
            rgba[i * 4 + 3] = alpha[i];
        }
        return EncodeRgba(width, height, rgba);
    }

    private static void WriteIhdr(Stream s, int w, int h, byte bitDepth, byte colorType)
    {
        Span<byte> data = stackalloc byte[13];
        WriteBE32(data, 0, w);
        WriteBE32(data, 4, h);
        data[8] = bitDepth;
        data[9] = colorType;
        data[10] = 0;
        data[11] = 0;
        data[12] = 0;
        WriteChunk(s, "IHDR", data);
    }

    private static void WriteIdat(
        Stream s, ReadOnlySpan<byte> pixels, int width, int height, int bytesPerPixel)
    {
        var rowSize = width * bytesPerPixel;
        var raw = new byte[height * (1 + rowSize)];
        for (var y = 0; y < height; y++)
        {
            var dstRow = y * (rowSize + 1);
            raw[dstRow] = 0;
            pixels.Slice(y * rowSize, rowSize).CopyTo(raw.AsSpan(dstRow + 1, rowSize));
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }
        WriteChunk(s, "IDAT", compressed.ToArray());
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);

        Span<byte> lenBytes = stackalloc byte[4];
        WriteBE32(lenBytes, 0, data.Length);
        s.Write(lenBytes);
        s.Write(typeBytes);
        s.Write(data);

        var crc = Crc32Update(0xFFFFFFFFu, typeBytes);
        crc = Crc32Update(crc, data);
        crc ^= 0xFFFFFFFFu;

        Span<byte> crcBytes = stackalloc byte[4];
        WriteBE32(crcBytes, 0, (int)crc);
        s.Write(crcBytes);
    }

    private static void WriteBE32(Span<byte> dst, int offset, int v)
    {
        dst[offset] = (byte)(v >> 24);
        dst[offset + 1] = (byte)(v >> 16);
        dst[offset + 2] = (byte)(v >> 8);
        dst[offset + 3] = (byte)v;
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32Update(uint crc, ReadOnlySpan<byte> data)
    {
        var c = crc;
        foreach (var b in data)
        {
            c = CrcTable[(c ^ b) & 0xff] ^ (c >> 8);
        }
        return c;
    }
}
