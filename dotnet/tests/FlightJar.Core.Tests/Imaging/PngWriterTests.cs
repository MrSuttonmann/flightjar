using FlightJar.Core.Imaging;

namespace FlightJar.Core.Tests.Imaging;

public class PngWriterTests
{
    [Fact]
    public void EncodeRgba_emits_valid_png_signature_and_chunks()
    {
        var rgba = new byte[2 * 2 * 4];
        rgba[0] = 255; rgba[3] = 255;       // (0,0): opaque red
        rgba[1 * 4 + 1] = 255; rgba[1 * 4 + 3] = 255;  // (0,1): opaque green
        rgba[2 * 4 + 2] = 255; rgba[2 * 4 + 3] = 255;  // (1,0): opaque blue
        rgba[3 * 4] = 128; rgba[3 * 4 + 3] = 128;      // (1,1): translucent dark red

        var png = PngWriter.EncodeRgba(2, 2, rgba);

        Assert.True(png.Length > 33);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
            png.Take(8).ToArray());
        Assert.Contains("IHDR", System.Text.Encoding.ASCII.GetString(png));
        Assert.Contains("IDAT", System.Text.Encoding.ASCII.GetString(png));
        Assert.Contains("IEND", System.Text.Encoding.ASCII.GetString(png));
    }

    [Fact]
    public void EncodeRgba_throws_on_size_mismatch()
    {
        Assert.Throws<ArgumentException>(() => PngWriter.EncodeRgba(2, 2, new byte[3]));
    }

    [Fact]
    public void EncodeAlphaMask_inflates_to_constant_rgb_plus_alpha()
    {
        var alpha = new byte[] { 0, 64, 128, 255 };
        var png = PngWriter.EncodeAlphaMask(2, 2, alpha, r: 200, g: 30, b: 30);
        Assert.True(png.Length > 33);
        // The encoded scanlines compress well because three of every four
        // bytes are constant — encoded size should be a small fraction of
        // the raw 16-byte payload's worst case (zlib overhead alone).
        Assert.True(png.Length < 200);
    }

    [Fact]
    public void EncodeAlphaMask_throws_on_size_mismatch()
    {
        Assert.Throws<ArgumentException>(
            () => PngWriter.EncodeAlphaMask(2, 2, new byte[3], 0, 0, 0));
    }
}
