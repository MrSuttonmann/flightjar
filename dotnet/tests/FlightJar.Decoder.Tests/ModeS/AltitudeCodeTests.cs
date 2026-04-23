using FlightJar.Decoder.ModeS;

namespace FlightJar.Decoder.Tests.ModeS;

public class AltitudeCodeTests
{
    // Oracle values captured from pyModeS 3.2.0 altcode_to_altitude.
    [Theory]
    [InlineData(0x01A2, 3700)]  // Q=1 linear, mid-range
    [InlineData(0x0B49, null)]  // invalid Gillham (n100 in {0,5,6})
    [InlineData(0x01FD, null)]  // invalid
    [InlineData(0x0000, null)]  // altitude unknown
    [InlineData(0x1FFF, null)]  // invalid
    public void Decode_MatchesPyModeS(int ac, int? expected)
    {
        Assert.Equal(expected, AltitudeCode.Decode(ac));
    }
}
