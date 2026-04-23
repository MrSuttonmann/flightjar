namespace FlightJar.Decoder.ModeS;

/// <summary>
/// Message-level Mode S extractors. Mirrors pyModeS 3.2.0 <c>util.py</c>
/// top-level API (<c>df</c>, <c>icao</c>, <c>typecode</c>, <c>crc</c>,
/// <c>altcode</c>, <c>idcode</c>).
/// </summary>
public static class ModeS
{
    /// <summary>Downlink format (bits 0-4). Values 24-31 clamp to 24 (extended-length Comm-D).</summary>
    public static int Df(HexMessage msg)
    {
        var top = (int)((msg.Bits >> (msg.TotalBits - 5)) & 0x1F);
        return top >= 24 ? 24 : top;
    }

    public static int Df(string hex) => Df(HexMessage.Parse(hex));

    /// <summary>
    /// ICAO 24-bit address. DF11/17/18 carry it explicitly; DF0/4/5/16/20/21
    /// recover it from the CRC remainder (parity field XORed during
    /// interrogation). Returns null for DFs without an address.
    /// </summary>
    public static string? Icao(HexMessage msg)
    {
        var dfv = Df(msg);
        if (dfv == 11 || dfv == 17 || dfv == 18)
        {
            // Bits 8-31 (AA field).
            var aa = ModeSBits.ExtractUnsigned(msg.Bits, 8, 24, msg.TotalBits);
            return aa.ToString("X6", System.Globalization.CultureInfo.InvariantCulture);
        }
        if (dfv == 0 || dfv == 4 || dfv == 5 || dfv == 16 || dfv == 20 || dfv == 21)
        {
            var rem = ModeSBits.CrcRemainder(msg.Bits, msg.TotalBits);
            return rem.ToString("X6", System.Globalization.CultureInfo.InvariantCulture);
        }
        return null;
    }

    public static string? Icao(string hex) => Icao(HexMessage.Parse(hex));

    /// <summary>ADS-B typecode (bits 32-36 of the ME field) for DF17/18, else null.</summary>
    public static int? Typecode(HexMessage msg)
    {
        var dfv = Df(msg);
        if (dfv != 17 && dfv != 18)
        {
            return null;
        }
        // First 5 bits of the ME field (which starts at bit 32 of the message).
        return (int)ModeSBits.ExtractUnsigned(msg.Bits, 32, 5, msg.TotalBits);
    }

    public static int? Typecode(string hex) => Typecode(HexMessage.Parse(hex));

    /// <summary>CRC-24 remainder. 0 for valid DF17/18; ICAO address for DF20/21.</summary>
    public static uint Crc(HexMessage msg) => ModeSBits.CrcRemainder(msg.Bits, msg.TotalBits);

    public static uint Crc(string hex) => Crc(HexMessage.Parse(hex));

    /// <summary>True when the DF17/18 CRC is valid (remainder == 0).</summary>
    public static bool CrcValid(HexMessage msg)
    {
        var dfv = Df(msg);
        return (dfv == 17 || dfv == 18) && Crc(msg) == 0;
    }

    public static bool CrcValid(string hex) => CrcValid(HexMessage.Parse(hex));

    /// <summary>Altitude in feet from the 13-bit AC field (DF0/4/16/20).</summary>
    public static int? Altcode(HexMessage msg)
    {
        var dfv = Df(msg);
        if (dfv != 0 && dfv != 4 && dfv != 16 && dfv != 20)
        {
            return null;
        }
        // AC occupies bits 19-31 (MSB-first, 13 bits).
        var ac = (int)ModeSBits.ExtractUnsigned(msg.Bits, 19, 13, msg.TotalBits);
        return AltitudeCode.Decode(ac);
    }

    public static int? Altcode(string hex) => Altcode(HexMessage.Parse(hex));

    /// <summary>Squawk string from the 13-bit ID field (DF5/21).</summary>
    public static string? Idcode(HexMessage msg)
    {
        var dfv = Df(msg);
        if (dfv != 5 && dfv != 21)
        {
            return null;
        }
        var ident = (int)ModeSBits.ExtractUnsigned(msg.Bits, 19, 13, msg.TotalBits);
        return IdentityCode.Decode(ident);
    }

    public static string? Idcode(string hex) => Idcode(HexMessage.Parse(hex));
}
