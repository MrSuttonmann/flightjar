namespace FlightJar.Decoder.ModeS;

/// <summary>
/// Decode a Mode S message hex string into a unified <see cref="DecodedMessage"/>.
/// Dispatches by downlink format; DF17/18 use <see cref="AdsbDecoder"/>, DF4/20
/// extract altitude, DF5/21 extract the squawk, DF11 extracts just the ICAO.
/// Other DFs are ignored (return null).
/// </summary>
public static class MessageDecoder
{
    public static DecodedMessage? Decode(string hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return null;
        }
        HexMessage msg;
        try
        {
            msg = HexMessage.Parse(hex);
        }
        catch (ArgumentException)
        {
            return null;
        }
        return Decode(msg);
    }

    public static DecodedMessage? Decode(HexMessage msg)
    {
        var df = ModeS.Df(msg);
        switch (df)
        {
            case 17:
            case 18:
                return DecodeAdsb(msg, df);
            case 4:
            case 20:
                return DecodeAltitudeReply(msg, df);
            case 5:
            case 21:
                return DecodeIdentityReply(msg, df);
            case 11:
                return new DecodedMessage { Df = df, Icao = ModeS.Icao(msg) };
            default:
                return null;
        }
    }

    private static DecodedMessage? DecodeAdsb(HexMessage msg, int df)
    {
        var adsb = AdsbDecoder.Decode(msg);
        if (adsb is null)
        {
            return null;
        }
        var crcValid = ModeS.CrcValid(msg);
        var icao = ModeS.Icao(msg);
        return new DecodedMessage
        {
            Df = df,
            Icao = icao,
            CrcValid = crcValid,
            Typecode = adsb.Typecode,
            Callsign = adsb.Callsign,
            Category = adsb.Category,
            Altitude = adsb.Altitude,
            CprFormat = adsb.CprFormat,
            CprLat = adsb.CprLat,
            CprLon = adsb.CprLon,
            Groundspeed = adsb.Groundspeed,
            Airspeed = adsb.Airspeed,
            Track = adsb.Track,
            Heading = adsb.Heading,
            VerticalRate = adsb.VerticalRate,
        };
    }

    private static DecodedMessage DecodeAltitudeReply(HexMessage msg, int df)
    {
        return new DecodedMessage
        {
            Df = df,
            Icao = ModeS.Icao(msg),
            Altitude = ModeS.Altcode(msg),
        };
    }

    private static DecodedMessage DecodeIdentityReply(HexMessage msg, int df)
    {
        return new DecodedMessage
        {
            Df = df,
            Icao = ModeS.Icao(msg),
            Squawk = ModeS.Idcode(msg),
        };
    }
}
