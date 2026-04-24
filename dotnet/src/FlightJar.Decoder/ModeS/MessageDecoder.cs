namespace FlightJar.Decoder.ModeS;

/// <summary>
/// Decode a Mode S message hex string into a unified <see cref="DecodedMessage"/>.
/// Dispatches by downlink format; DF17/18 use <see cref="AdsbDecoder"/>, DF4/20
/// extract altitude (plus Comm-B met payload on DF20), DF5/21 extract the
/// squawk (plus Comm-B on DF21), DF11 extracts just the ICAO. Other DFs are
/// ignored (return null).
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
        var commB = df == 20 ? InferCommB(msg) : null;
        return Merge(new DecodedMessage
        {
            Df = df,
            Icao = ModeS.Icao(msg),
            Altitude = ModeS.Altcode(msg),
        }, commB);
    }

    private static DecodedMessage DecodeIdentityReply(HexMessage msg, int df)
    {
        var commB = df == 21 ? InferCommB(msg) : null;
        return Merge(new DecodedMessage
        {
            Df = df,
            Icao = ModeS.Icao(msg),
            Squawk = ModeS.Idcode(msg),
        }, commB);
    }

    private static DecodedMessage Merge(DecodedMessage surv, DecodedMessage? commB)
    {
        if (commB is null)
        {
            return surv;
        }
        return surv with
        {
            Bds = commB.Bds,
            SelectedAltitudeMcpFt = commB.SelectedAltitudeMcpFt,
            SelectedAltitudeFmsFt = commB.SelectedAltitudeFmsFt,
            QnhHpa = commB.QnhHpa,
            FigureOfMerit = commB.FigureOfMerit,
            WindSpeedKt = commB.WindSpeedKt,
            WindDirectionDeg = commB.WindDirectionDeg,
            StaticAirTemperatureC = commB.StaticAirTemperatureC,
            StaticPressureHpa = commB.StaticPressureHpa,
            Turbulence = commB.Turbulence,
            HumidityPct = commB.HumidityPct,
            RollDeg = commB.RollDeg,
            TrueTrackDeg = commB.TrueTrackDeg,
            GroundspeedKt = commB.GroundspeedKt,
            TrackRateDegPerS = commB.TrackRateDegPerS,
            TrueAirspeedKt = commB.TrueAirspeedKt,
            MagneticHeadingDeg = commB.MagneticHeadingDeg,
            IndicatedAirspeedKt = commB.IndicatedAirspeedKt,
            Mach = commB.Mach,
            BaroVerticalRateFpm = commB.BaroVerticalRateFpm,
            InertialVerticalRateFpm = commB.InertialVerticalRateFpm,
        };
    }

    /// <summary>
    /// Infer which BDS register a DF 20/21 Comm-B message carries, and decode
    /// it. Returns null unless exactly one of the four heuristic registers
    /// (4,0 / 4,4 / 5,0 / 6,0) validates — multi-match payloads are ambiguous
    /// and dropped rather than risk polluting aircraft state with fields
    /// decoded against the wrong register.
    /// </summary>
    private static DecodedMessage? InferCommB(HexMessage msg)
    {
        if (msg.TotalBits != 112)
        {
            return null;
        }
        var payload = CommB.Payload(msg);
        var candidates = CommB.Infer(payload);
        if (candidates.Count != 1)
        {
            return null;
        }
        var df = ModeS.Df(msg);
        if (candidates.Bds40)
        {
            var d = CommB.DecodeBds40(payload);
            return new DecodedMessage
            {
                Df = df,
                Bds = "4,0",
                QnhHpa = d.BaroPressureSettingHpa,
                SelectedAltitudeMcpFt = d.SelectedAltitudeMcpFt,
                SelectedAltitudeFmsFt = d.SelectedAltitudeFmsFt,
            };
        }
        if (candidates.Bds44)
        {
            var d = CommB.DecodeBds44(payload);
            return new DecodedMessage
            {
                Df = df,
                Bds = "4,4",
                FigureOfMerit = d.FigureOfMerit,
                WindSpeedKt = d.WindSpeedKt,
                WindDirectionDeg = d.WindDirectionDeg,
                StaticAirTemperatureC = d.StaticAirTemperatureC,
                StaticPressureHpa = d.StaticPressureHpa,
                Turbulence = d.Turbulence,
                HumidityPct = d.HumidityPct,
            };
        }
        if (candidates.Bds50)
        {
            var d = CommB.DecodeBds50(payload);
            return new DecodedMessage
            {
                Df = df,
                Bds = "5,0",
                RollDeg = d.RollDeg,
                TrueTrackDeg = d.TrueTrackDeg,
                GroundspeedKt = d.GroundspeedKt,
                TrackRateDegPerS = d.TrackRateDegPerS,
                TrueAirspeedKt = d.TrueAirspeedKt,
            };
        }
        if (candidates.Bds60)
        {
            var d = CommB.DecodeBds60(payload);
            return new DecodedMessage
            {
                Df = df,
                Bds = "6,0",
                MagneticHeadingDeg = d.MagneticHeadingDeg,
                IndicatedAirspeedKt = d.IndicatedAirspeedKt,
                Mach = d.Mach,
                BaroVerticalRateFpm = d.BaroVerticalRateFpm,
                InertialVerticalRateFpm = d.InertialVerticalRateFpm,
            };
        }
        return null;
    }
}
