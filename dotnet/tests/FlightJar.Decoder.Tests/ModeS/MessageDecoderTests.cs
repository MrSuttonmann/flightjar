using FlightJar.Decoder.ModeS;

namespace FlightJar.Decoder.Tests.ModeS;

public class MessageDecoderTests
{
    // DF17/18 share a 14-byte (28 hex) extended squitter layout: DF (5b) | CA-or-CF (3b) |
    // AA (24b) | ME (56b) | PI (24b). Flipping the high nibble of the first byte from 0x8
    // to 0x9 toggles DF17 → DF18; the low nibble of byte 0 is the CF field on DF18, and
    // we can set it independently. CRC won't be valid after the swap, but CF / IsMlat
    // come from the bit layout regardless.
    private const string Df17AdsbHex = "8D406B902015A678D4D220AA4BDA";

    [Fact]
    public void Df17_HasNullCf()
    {
        var decoded = MessageDecoder.Decode(Df17AdsbHex);
        Assert.NotNull(decoded);
        Assert.Equal(17, decoded!.Df);
        Assert.Null(decoded.Cf);
    }

    // DF18 carries a 3-bit CF immediately after the DF; verify the decoder
    // exposes it across the full range. The state layer (AircraftRegistry)
    // is responsible for mapping CF → PositionSource.
    [Theory]
    [InlineData("90406B902015A678D4D220AA4BDA", 0)]
    [InlineData("91406B902015A678D4D220AA4BDA", 1)]
    [InlineData("92406B902015A678D4D220AA4BDA", 2)]
    [InlineData("93406B902015A678D4D220AA4BDA", 3)]
    [InlineData("95406B902015A678D4D220AA4BDA", 5)]
    [InlineData("96406B902015A678D4D220AA4BDA", 6)]
    public void Df18_ExtractsCf(string hex, int expectedCf)
    {
        var decoded = MessageDecoder.Decode(hex);
        Assert.NotNull(decoded);
        Assert.Equal(18, decoded!.Df);
        Assert.Equal(expectedCf, decoded.Cf);
    }

    [Fact]
    public void SurveillanceDf_HasNullCf()
    {
        // DF20 Comm-B reply
        var decoded = MessageDecoder.Decode("A0001838CA3E51F0A82A1C92E472");
        Assert.NotNull(decoded);
        Assert.Equal(20, decoded!.Df);
        Assert.Null(decoded.Cf);
    }
}
