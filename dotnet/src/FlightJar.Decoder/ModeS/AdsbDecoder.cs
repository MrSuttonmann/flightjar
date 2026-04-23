namespace FlightJar.Decoder.ModeS;

/// <summary>
/// DF17/18 (ADS-B) payload decoder. Dispatches on typecode to the matching BDS
/// register decoder. Mirrors pyModeS 3.2.0 <c>decoder/adsb.py</c> and
/// <c>decoder/bds/bds05.py</c>, <c>bds06.py</c>, <c>bds08.py</c>, <c>bds09.py</c>.
/// </summary>
public static class AdsbDecoder
{
    /// <summary>
    /// Decode the ADS-B payload of a DF17/18 message. Returns null when the
    /// message isn't a DF17/18 extended squitter.
    /// </summary>
    public static AdsbMessage? Decode(HexMessage msg)
    {
        var df = ModeS.Df(msg);
        if ((df != 17 && df != 18) || msg.TotalBits != 112)
        {
            return null;
        }

        // ME field is 56 bits starting at message bit 32.
        var payload = ModeSBits.ExtractUnsigned(msg.Bits, 32, 56, 112);
        var tc = (int)((payload >> 51) & 0x1F);

        if (tc >= 1 && tc <= 4)
        {
            return DecodeBds08(payload, tc);
        }
        if (tc >= 5 && tc <= 8)
        {
            return DecodeBds06(payload, tc);
        }
        if ((tc >= 9 && tc <= 18) || (tc >= 20 && tc <= 22))
        {
            return DecodeBds05(payload, tc);
        }
        if (tc == 19)
        {
            return DecodeBds09(payload);
        }

        // Reserved / unknown — expose the TC so callers can log or skip.
        return new AdsbMessage { Typecode = tc };
    }

    // BDS 0,8 — identification + category (TC 1-4).
    private static AdsbMessage DecodeBds08(ulong payload, int tc)
    {
        var category = (int)((payload >> 48) & 0x7);
        var csBits = payload & ((1UL << 48) - 1);
        var callsign = CallsignDecoder.Decode(csBits);
        return new AdsbMessage
        {
            Typecode = tc,
            Category = category,
            Callsign = callsign,
        };
    }

    // BDS 0,6 — surface position (TC 5-8).
    private static AdsbMessage DecodeBds06(ulong payload, int tc)
    {
        var mov = (int)((payload >> 44) & 0x7F);
        var trackStatus = (int)((payload >> 43) & 0x1);
        var trackRaw = (int)((payload >> 36) & 0x7F);
        var cprFormat = (int)((payload >> 34) & 0x1);
        var cprLat = (int)((payload >> 17) & 0x1FFFF);
        var cprLon = (int)(payload & 0x1FFFF);

        double? track = trackStatus == 1 ? trackRaw * 360.0 / 128.0 : null;

        return new AdsbMessage
        {
            Typecode = tc,
            OnGround = true,
            Groundspeed = DecodeSurfaceMovement(mov),
            Track = track,
            CprFormat = cprFormat,
            CprLat = cprLat,
            CprLon = cprLon,
        };
    }

    // Movement encoding bins for BDS 0,6.
    private static readonly int[] MovLb = { 2, 9, 13, 39, 94, 109, 124 };
    private static readonly double[] KtsLb = { 0.125, 1, 2, 15, 70, 100, 175 };
    private static readonly double[] Step = { 0.125, 0.25, 0.5, 1, 2, 5 };

    private static double? DecodeSurfaceMovement(int mov)
    {
        if (mov == 0 || mov > 124)
        {
            return null;
        }
        if (mov == 1)
        {
            return 0.0;
        }
        if (mov == 124)
        {
            return 175.0;
        }
        var i = 0;
        while (i < MovLb.Length && MovLb[i] <= mov)
        {
            i++;
        }
        return KtsLb[i - 1] + (mov - MovLb[i - 1]) * Step[i - 1];
    }

    // BDS 0,5 — airborne position (TC 9-18 baro, 20-22 GNSS).
    private static AdsbMessage DecodeBds05(ulong payload, int tc)
    {
        var ac = (int)((payload >> 36) & 0xFFF);
        var cprFormat = (int)((payload >> 34) & 0x1);
        var cprLat = (int)((payload >> 17) & 0x1FFFF);
        var cprLon = (int)(payload & 0x1FFFF);

        int? altitude;
        if (tc >= 9 && tc <= 18)
        {
            // 12-bit AC -> 13-bit altcode: insert a zero M bit at position 6.
            // Top 6 bits of AC (positions 0-5) shift left by 7; bottom 6 bits become altcode bits 7-12.
            var altcode = ((ac >> 6) << 7) | (ac & 0x3F);
            altitude = AltitudeCode.Decode(altcode);
        }
        else if (tc >= 20 && tc <= 22)
        {
            // GNSS altitude: 12-bit meters converted to feet.
            altitude = (int)(ac * 3.28084);
        }
        else
        {
            altitude = null;
        }

        return new AdsbMessage
        {
            Typecode = tc,
            Altitude = altitude,
            CprFormat = cprFormat,
            CprLat = cprLat,
            CprLon = cprLon,
        };
    }

    // BDS 0,9 — velocity (TC 19).
    private static AdsbMessage DecodeBds09(ulong payload)
    {
        var subtype = (int)((payload >> 48) & 0x7);
        var msg = new AdsbMessage { Typecode = 19 };

        double? groundspeed = null, track = null, airspeed = null, heading = null;

        if (subtype == 1 || subtype == 2)
        {
            var vEwSign = (payload >> 42) & 0x1;
            var vEwMag = (int)((payload >> 32) & 0x3FF);
            var vNsSign = (payload >> 31) & 0x1;
            var vNsMag = (int)((payload >> 21) & 0x3FF);

            if (vEwMag != 0 && vNsMag != 0)
            {
                var vEw = vEwMag - 1;
                var vNs = vNsMag - 1;
                if (subtype == 2)
                {
                    vEw *= 4;
                    vNs *= 4;
                }
                var vWe = vEwSign == 1 ? -vEw : vEw;
                var vSn = vNsSign == 1 ? -vNs : vNs;
                groundspeed = (int)Math.Sqrt((double)vWe * vWe + (double)vSn * vSn);
                track = Math.Atan2(vWe, vSn) * 180.0 / Math.PI;
                if (track < 0)
                {
                    track += 360;
                }
            }
        }
        else if (subtype == 3 || subtype == 4)
        {
            var hdgStatus = (payload >> 42) & 0x1;
            var hdgRaw = (int)((payload >> 32) & 0x3FF);
            var asMag = (int)((payload >> 21) & 0x3FF);

            if (hdgStatus != 0)
            {
                heading = hdgRaw / 1024.0 * 360.0;
            }
            if (asMag != 0)
            {
                airspeed = subtype == 4 ? (asMag - 1) * 4.0 : (asMag - 1.0);
            }
        }

        var vrSource = (payload >> 20) & 0x1;
        var vrSign = (payload >> 19) & 0x1;
        var vrMag = (int)((payload >> 10) & 0x1FF);
        int? verticalRate = null;
        if (vrMag != 0)
        {
            var sign = vrSign == 1 ? -1 : 1;
            verticalRate = sign * (vrMag - 1) * 64;
        }

        return msg with
        {
            Groundspeed = groundspeed,
            Track = track,
            Airspeed = airspeed,
            Heading = heading,
            VerticalRate = verticalRate,
            VerticalRateSource = vrSource == 1 ? "BARO" : "GNSS",
        };
    }
}
