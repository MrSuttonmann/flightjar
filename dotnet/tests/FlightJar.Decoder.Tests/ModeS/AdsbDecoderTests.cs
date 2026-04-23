using FlightJar.Decoder.ModeS;

namespace FlightJar.Decoder.Tests.ModeS;

public class AdsbDecoderTests
{
    [Fact]
    public void DecodesTc4Identification()
    {
        // Oracle: pyModeS 3.2.0 on 8D4840D6202CC371C32CE0576098
        // typecode=4, category=0, callsign='KLM1023'
        var m = AdsbDecoder.Decode(HexMessage.Parse("8D4840D6202CC371C32CE0576098"));
        Assert.NotNull(m);
        Assert.Equal(4, m!.Typecode);
        Assert.Equal(0, m.Category);
        Assert.Equal("KLM1023", m.Callsign);
    }

    [Fact]
    public void DecodesTc11AirbornePosition()
    {
        // Oracle: typecode=11, altitude=38000, cpr_format=1, cpr_lat=74158, cpr_lon=50194
        var m = AdsbDecoder.Decode(HexMessage.Parse("8D40058B58C386435CC412692AD6"));
        Assert.NotNull(m);
        Assert.Equal(11, m!.Typecode);
        Assert.Equal(38000, m.Altitude);
        Assert.Equal(1, m.CprFormat);
        Assert.Equal(74158, m.CprLat);
        Assert.Equal(50194, m.CprLon);
    }

    [Fact]
    public void DecodesTc19VelocityGroundSpeed()
    {
        // Oracle: typecode=19, subtype=1, groundspeed=159, track≈182.88, vr=GNSS, vertical_rate=-832
        var m = AdsbDecoder.Decode(HexMessage.Parse("8D485020994409940838175B284F"));
        Assert.NotNull(m);
        Assert.Equal(19, m!.Typecode);
        Assert.Equal(159.0, m.Groundspeed);
        Assert.Equal(182.8803775528476, m.Track!.Value, 10);
        Assert.Equal(-832, m.VerticalRate);
        Assert.Equal("GNSS", m.VerticalRateSource);
    }

    [Fact]
    public void DecodesTc7SurfacePosition()
    {
        // Oracle: typecode=7, groundspeed=17.0, track=92.8125, cpr_format=1, cpr_lat=39195, cpr_lon=110320
        var m = AdsbDecoder.Decode(HexMessage.Parse("8C4841753A9A153237AEF0F275BE"));
        Assert.NotNull(m);
        Assert.Equal(7, m!.Typecode);
        Assert.True(m.OnGround);
        Assert.Equal(17.0, m.Groundspeed);
        Assert.Equal(92.8125, m.Track);
        Assert.Equal(1, m.CprFormat);
        Assert.Equal(39195, m.CprLat);
        Assert.Equal(110320, m.CprLon);
    }

    [Fact]
    public void ReturnsNullForNonAdsbDf()
    {
        var m = AdsbDecoder.Decode(HexMessage.Parse("A0001838CA3E51F0A82A1C92E472")); // DF20
        Assert.Null(m);
    }
}
