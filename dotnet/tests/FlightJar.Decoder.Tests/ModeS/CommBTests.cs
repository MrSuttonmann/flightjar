using FlightJar.Decoder.ModeS;

namespace FlightJar.Decoder.Tests.ModeS;

public class CommBTests
{
    // Extract the 56-bit MB payload from a full 112-bit Mode S reply exactly
    // the way pyModeS's test suite does: bytes 4..11 inclusive of the 14-byte
    // message (bits 32..87), mirroring CommB.Payload on a parsed HexMessage.
    private static ulong PayloadOf(string hex)
    {
        var msg = HexMessage.Parse(hex);
        return CommB.Payload(msg);
    }

    // ----- BDS 4,0 — selected vertical intention ---------------------------

    [Fact]
    public void IsBds40_AcceptsPyModeSGolden()
    {
        Assert.True(CommB.IsBds40(PayloadOf("A000029C85E42F313000007047D3")));
    }

    [Fact]
    public void IsBds40_RejectsAllZero()
    {
        Assert.False(CommB.IsBds40(0UL));
    }

    [Fact]
    public void IsBds40_RejectsReservedBits39To46Nonzero()
    {
        // pyModeS parity: flipping any reserved bit in 39-46 makes is_bds40
        // reject. We flip bit 39 in the payload MSB indexing (55 - 39 = 16).
        var p = PayloadOf("A000029C85E42F313000007047D3");
        var bad = p | (1UL << (55 - 39));
        Assert.False(CommB.IsBds40(bad));
    }

    [Fact]
    public void IsBds40_RejectsMcpStatusClearValueSet()
    {
        // Same golden vector but with MCP status bit 0 cleared. The MCP alt
        // raw bits 1-12 remain nonzero, so wrong_status(0, 1, 12) fires.
        var p = PayloadOf("A000029C85E42F313000007047D3");
        var bad = p & ~(1UL << 55);
        Assert.False(CommB.IsBds40(bad));
    }

    [Fact]
    public void DecodeBds40_PyModeSGolden()
    {
        // pyModeS oracle: MCP alt 3008 ft, FMS alt 3008 ft, QNH 1020.0 hPa.
        var d = CommB.DecodeBds40(PayloadOf("A000029C85E42F313000007047D3"));
        Assert.Equal(3008, d.SelectedAltitudeMcpFt);
        Assert.Equal(3008, d.SelectedAltitudeFmsFt);
        Assert.NotNull(d.BaroPressureSettingHpa);
        Assert.Equal(1020.0, d.BaroPressureSettingHpa!.Value, 1);
    }

    // ----- BDS 4,4 — meteorological routine --------------------------------

    [Fact]
    public void IsBds44_AcceptsPyModeSGolden()
    {
        Assert.True(CommB.IsBds44(PayloadOf("A0001692185BD5CF400000DFC696")));
    }

    [Fact]
    public void IsBds44_RejectsAllZero()
    {
        Assert.False(CommB.IsBds44(0UL));
    }

    [Fact]
    public void IsBds44_RejectsFomAboveFour()
    {
        var p = PayloadOf("A0001692185BD5CF400000DFC696");
        // Clear FOM nibble then set it to 5.
        var bad = (p & ~(0xFUL << (55 - 3))) | (0b0101UL << (55 - 3));
        Assert.False(CommB.IsBds44(bad));
    }

    [Fact]
    public void IsBds44_RejectsWindSpeedAbove250()
    {
        var p = (1UL << (55 - 3))   // FOM = 1
              | (1UL << (55 - 4))   // wind status
              | (251UL << (55 - 13));  // wind speed = 251 kt
        Assert.False(CommB.IsBds44(p));
    }

    [Fact]
    public void IsBds44_RejectsTempOutOfRange()
    {
        // Temp raw 241 → +60.25 °C, above the +60 range gate.
        var p = (1UL << (55 - 3))
              | (1UL << (55 - 4))
              | (50UL << (55 - 13))
              | (241UL << (55 - 33));
        Assert.False(CommB.IsBds44(p));
    }

    [Fact]
    public void DecodeBds44_PyModeSGolden()
    {
        // pyModeS oracle: wind speed 22 kt, wind direction ~344.5°, SAT -48.75 °C.
        var d = CommB.DecodeBds44(PayloadOf("A0001692185BD5CF400000DFC696"));
        Assert.Equal(1, d.FigureOfMerit);
        Assert.Equal(22, d.WindSpeedKt);
        Assert.NotNull(d.WindDirectionDeg);
        Assert.Equal(344.5, d.WindDirectionDeg!.Value, 0.5);
        Assert.Equal(-48.75, d.StaticAirTemperatureC, 0.1);
        Assert.Null(d.StaticPressureHpa);
        Assert.Null(d.HumidityPct);
    }

    // ----- BDS 5,0 — track and turn ----------------------------------------

    [Fact]
    public void IsBds50_AcceptsPyModeSGolden()
    {
        Assert.True(CommB.IsBds50(PayloadOf("A000139381951536E024D4CCF6B5")));
    }

    [Fact]
    public void IsBds50_AcceptsSignedRollGolden()
    {
        Assert.True(CommB.IsBds50(PayloadOf("A0001691FFD263377FFCE02B2BF9")));
    }

    [Fact]
    public void IsBds50_RejectsAllZero()
    {
        Assert.False(CommB.IsBds50(0UL));
    }

    [Fact]
    public void IsBds50_RejectsGroundspeedAbove600()
    {
        // GS raw 301 * 2 = 602 kt, above the 600 kt range gate.
        var p = (1UL << (55 - 23)) | (301UL << (55 - 33));
        Assert.False(CommB.IsBds50(p));
    }

    [Fact]
    public void IsBds50_RejectsTasGsCrossFieldDeltaAbove200()
    {
        // GS raw 150 → 300 kt, TAS raw 300 → 600 kt, |delta| = 300 kt.
        var p = (1UL << (55 - 23))
              | (150UL << (55 - 33))
              | (1UL << (55 - 45))
              | (300UL << (55 - 55));
        Assert.False(CommB.IsBds50(p));
    }

    [Fact]
    public void DecodeBds50_PyModeSGolden()
    {
        // pyModeS oracle: roll ~2.1°, true track ~114.258°, GS 438 kt,
        // track rate ~0.125°/s, TAS 424 kt.
        var d = CommB.DecodeBds50(PayloadOf("A000139381951536E024D4CCF6B5"));
        Assert.NotNull(d.RollDeg);
        Assert.Equal(2.1, d.RollDeg!.Value, 0.1);
        Assert.NotNull(d.TrueTrackDeg);
        Assert.Equal(114.258, d.TrueTrackDeg!.Value, 0.1);
        Assert.Equal(438, d.GroundspeedKt);
        Assert.NotNull(d.TrackRateDegPerS);
        Assert.Equal(0.125, d.TrackRateDegPerS!.Value, 0.01);
        Assert.Equal(424, d.TrueAirspeedKt);
    }

    // ----- BDS 6,0 — heading and speed -------------------------------------

    [Fact]
    public void IsBds60_AcceptsPyModeSGolden()
    {
        Assert.True(CommB.IsBds60(PayloadOf("A00004128F39F91A7E27C46ADC21")));
    }

    [Fact]
    public void IsBds60_RejectsAllZero()
    {
        Assert.False(CommB.IsBds60(0UL));
    }

    [Fact]
    public void IsBds60_RejectsIasAbove500()
    {
        // IAS raw 501 kt (value is already in kt for BDS60), rejects.
        var p = (1UL << (55 - 12)) | (501UL << (55 - 22));
        Assert.False(CommB.IsBds60(p));
    }

    [Fact]
    public void IsBds60_AcceptsMachExactlyOne()
    {
        // Raw 250 * 2.048/512 = 1.0 exactly — boundary accepts per pyModeS v3.
        var p = (1UL << (55 - 23)) | (250UL << (55 - 33));
        Assert.True(CommB.IsBds60(p));
    }

    [Fact]
    public void IsBds60_RejectsMachAboveOne()
    {
        var p = (1UL << (55 - 23)) | (251UL << (55 - 33));
        Assert.False(CommB.IsBds60(p));
    }

    [Fact]
    public void DecodeBds60_PyModeSGolden()
    {
        // pyModeS oracle: heading 42.715°, IAS 252 kt, Mach 0.42,
        // baro vr -1920 fpm, inertial vr -1920 fpm.
        var d = CommB.DecodeBds60(PayloadOf("A00004128F39F91A7E27C46ADC21"));
        Assert.NotNull(d.MagneticHeadingDeg);
        Assert.Equal(42.715, d.MagneticHeadingDeg!.Value, 0.01);
        Assert.Equal(252, d.IndicatedAirspeedKt);
        Assert.NotNull(d.Mach);
        Assert.Equal(0.42, d.Mach!.Value, 0.005);
        Assert.Equal(-1920, d.BaroVerticalRateFpm);
        Assert.Equal(-1920, d.InertialVerticalRateFpm);
    }

    // ----- Ambiguity handling in MessageDecoder ----------------------------

    [Fact]
    public void MessageDecoder_RoutesUnambiguousBds40OnDf20()
    {
        var m = MessageDecoder.Decode("A000029C85E42F313000007047D3");
        Assert.NotNull(m);
        Assert.Equal(20, m!.Df);
        Assert.Equal("4,0", m.Bds);
        Assert.Equal(3008, m.SelectedAltitudeMcpFt);
        Assert.NotNull(m.QnhHpa);
        Assert.Equal(1020.0, m.QnhHpa!.Value, 1);
    }

    [Fact]
    public void MessageDecoder_RoutesUnambiguousBds50OnDf20()
    {
        var m = MessageDecoder.Decode("A000139381951536E024D4CCF6B5");
        Assert.NotNull(m);
        Assert.Equal(20, m!.Df);
        Assert.Equal("5,0", m.Bds);
        Assert.Equal(424, m.TrueAirspeedKt);
        Assert.Equal(438, m.GroundspeedKt);
    }

    [Fact]
    public void MessageDecoder_RoutesUnambiguousBds60OnDf20()
    {
        var m = MessageDecoder.Decode("A00004128F39F91A7E27C46ADC21");
        Assert.NotNull(m);
        Assert.Equal(20, m!.Df);
        Assert.Equal("6,0", m.Bds);
        Assert.NotNull(m.Mach);
        Assert.Equal(0.42, m.Mach!.Value, 0.005);
        Assert.Equal(252, m.IndicatedAirspeedKt);
    }

    [Fact]
    public void MessageDecoder_ShortReplyHasNoCommBFields()
    {
        // DF 4/5 are short-format (56-bit) replies with no Comm-B payload,
        // so the decoder must leave every CommB field null regardless of
        // what the surveillance decode returns.
        var m = MessageDecoder.Decode("20000A0000000A");
        Assert.NotNull(m);
        Assert.Equal(4, m!.Df);
        Assert.Null(m.Bds);
        Assert.Null(m.Mach);
        Assert.Null(m.QnhHpa);
        Assert.Null(m.WindSpeedKt);
        Assert.Null(m.TrueAirspeedKt);
    }

    [Fact]
    public void MessageDecoder_AllZeroCommBPayloadLeavesBdsNull()
    {
        // All four heuristic validators reject the all-zero payload via the
        // early payload==0 guard, so Bds stays null even though DF20
        // surveillance (altitude) still decodes.
        var m = MessageDecoder.Decode("A0000000000000000000000000AB");
        Assert.NotNull(m);
        Assert.Equal(20, m!.Df);
        Assert.Null(m.Bds);
    }
}
