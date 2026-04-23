using FlightJar.Decoder.ModeS;

namespace FlightJar.Decoder.Tests.ModeS;

public class ModeSTests
{
    // Test vectors captured from pyModeS 3.2.0 as the golden oracle.
    // Each row: hex, df, icao, typecode, crc, altcode, idcode.
    public static TheoryData<string, int, string?, int?, uint, int?, string?> GoldenVectors()
    {
        var data = new TheoryData<string, int, string?, int?, uint, int?, string?>
        {
            { "8D406B902015A678D4D220AA4BDA", 17, "406B90", 4, 0x000000, null, null },
            { "8D4840D6202CC371C32CE0576098", 17, "4840D6", 4, 0x000000, null, null },
            { "8D40058B58C386435CC412692AD6", 17, "40058B", 11, 0xA31D86, null, null }, // CRC invalid
            { "8D485020994409940838175B284F", 17, "485020", 19, 0x000000, null, null },
            { "A0001838CA3E51F0A82A1C92E472", 20, "68BDDE", null, 0x68BDDE, 38000, null },
            { "A8001EBCFFFB23286004A73F6A5B", 21, "48548E", null, 0x48548E, null, "7333" },
            { "28000A00000000", 5, "4C0FCE", null, 0x4C0FCE, null, "3000" },
            { "20000A0000000A", 4, "EC1155", null, 0xEC1155, null, null }, // altcode "unknown"
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(GoldenVectors))]
    public void Df_MatchesPyModeS(string hex, int df, string? icao, int? tc, uint crc, int? ac, string? sq)
    {
        _ = icao; _ = tc; _ = crc; _ = ac; _ = sq;
        Assert.Equal(df, FlightJar.Decoder.ModeS.ModeS.Df(hex));
    }

    [Theory]
    [MemberData(nameof(GoldenVectors))]
    public void Icao_MatchesPyModeS(string hex, int df, string? icao, int? tc, uint crc, int? ac, string? sq)
    {
        _ = df; _ = tc; _ = crc; _ = ac; _ = sq;
        Assert.Equal(icao, FlightJar.Decoder.ModeS.ModeS.Icao(hex));
    }

    [Theory]
    [MemberData(nameof(GoldenVectors))]
    public void Typecode_MatchesPyModeS(string hex, int df, string? icao, int? tc, uint crc, int? ac, string? sq)
    {
        _ = df; _ = icao; _ = crc; _ = ac; _ = sq;
        Assert.Equal(tc, FlightJar.Decoder.ModeS.ModeS.Typecode(hex));
    }

    [Theory]
    [MemberData(nameof(GoldenVectors))]
    public void Crc_MatchesPyModeS(string hex, int df, string? icao, int? tc, uint crc, int? ac, string? sq)
    {
        _ = df; _ = icao; _ = tc; _ = ac; _ = sq;
        Assert.Equal(crc, FlightJar.Decoder.ModeS.ModeS.Crc(hex));
    }

    [Theory]
    [MemberData(nameof(GoldenVectors))]
    public void Altcode_MatchesPyModeS(string hex, int df, string? icao, int? tc, uint crc, int? ac, string? sq)
    {
        _ = df; _ = icao; _ = tc; _ = crc; _ = sq;
        Assert.Equal(ac, FlightJar.Decoder.ModeS.ModeS.Altcode(hex));
    }

    [Theory]
    [MemberData(nameof(GoldenVectors))]
    public void Idcode_MatchesPyModeS(string hex, int df, string? icao, int? tc, uint crc, int? ac, string? sq)
    {
        _ = df; _ = icao; _ = tc; _ = crc; _ = ac;
        Assert.Equal(sq, FlightJar.Decoder.ModeS.ModeS.Idcode(hex));
    }

    [Fact]
    public void CrcValid_TrueForZeroRemainderDf17()
    {
        Assert.True(FlightJar.Decoder.ModeS.ModeS.CrcValid("8D406B902015A678D4D220AA4BDA"));
        Assert.False(FlightJar.Decoder.ModeS.ModeS.CrcValid("8D40058B58C386435CC412692AD6"));
    }

    [Fact]
    public void CrcValid_FalseForSurveillanceDfs()
    {
        // DF4/5/20/21 are not extended squitters — CrcValid only returns true for DF17/18 with zero remainder.
        Assert.False(FlightJar.Decoder.ModeS.ModeS.CrcValid("A0001838CA3E51F0A82A1C92E472"));
    }
}
