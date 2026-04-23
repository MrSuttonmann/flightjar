using FlightJar.Decoder.ModeS;

namespace FlightJar.Decoder.Tests.ModeS;

public class CallsignDecoderTests
{
    [Fact]
    public void DecodesAsciiCallsign()
    {
        // Encode "KLM1023 " as 8x6-bit and decode back.
        ulong bits = 0;
        foreach (var c in "KLM1023 ")
        {
            bits = (bits << 6) | (byte)(c & 0x3F);
        }
        Assert.Equal("KLM1023", CallsignDecoder.Decode(bits));
    }

    [Fact]
    public void AllZeroDecodesToInvalidChars()
    {
        // 0 decodes to eight '#' characters. Trim doesn't strip '#'.
        Assert.Equal("########", CallsignDecoder.Decode(0));
    }

    [Fact]
    public void InvalidChars_RenderAsHash()
    {
        // Oracle from pyModeS: decode_callsign(0x2CC371C32CE05) = '3C##L#8E'
        Assert.Equal("3C##L#8E", CallsignDecoder.Decode(0x2CC371C32CE05));
    }
}
