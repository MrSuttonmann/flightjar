using FlightJar.Decoder.ModeS;

namespace FlightJar.Decoder.Tests.ModeS;

public class IdentityCodeTests
{
    // Oracle values from pyModeS 3.2.0 idcode_to_squawk.
    [Theory]
    [InlineData(0x0000, "0000")]
    [InlineData(0x0808, "1200")]
    [InlineData(0x1FFF, "7777")]
    [InlineData(0x0AAA, "7700")]
    [InlineData(0x1555, "0077")]
    public void Decode_MatchesPyModeS(int raw, string expected)
    {
        Assert.Equal(expected, IdentityCode.Decode(raw));
    }
}
