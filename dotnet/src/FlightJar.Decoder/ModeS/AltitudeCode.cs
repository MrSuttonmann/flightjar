namespace FlightJar.Decoder.ModeS;

/// <summary>
/// Mode S altitude code (AC field) decoder. Mirrors pyModeS 3.2.0 <c>_altcode.py</c>.
/// </summary>
public static class AltitudeCode
{
    /// <summary>
    /// Decode a 13-bit AC field to feet, or null for "unknown" (all zero) /
    /// invalid / meter-based encodings.
    /// </summary>
    public static int? Decode(int ac)
    {
        if (ac == 0)
        {
            return null;
        }

        var mBit = (ac >> 6) & 0x1;
        var qBit = (ac >> 4) & 0x1;

        if (mBit == 0 && qBit == 1)
        {
            // 25-foot interval linear encoding.
            var n = ((ac >> 2) & 0x7E0) | ((ac >> 1) & 0x10) | (ac & 0xF);
            return n * 25 - 1000;
        }

        if (mBit == 0 && qBit == 0)
        {
            return DecodeGillham(ac);
        }

        // M = 1: meter-based. Not handled.
        return null;
    }

    private static int? DecodeGillham(int ac)
    {
        // 13-bit AC layout MSB-first: C1 A1 C2 A2 C4 A4 M B1 Q B2 D2 B4 D4
        int Bit(int pos) => (ac >> (12 - pos)) & 0x1;

        int c1 = Bit(0), a1 = Bit(1);
        int c2 = Bit(2), a2 = Bit(3);
        int c4 = Bit(4), a4 = Bit(5);
        var b1 = Bit(7);
        int b2 = Bit(9), d2 = Bit(10);
        int b4 = Bit(11), d4 = Bit(12);

        // Per DO-260: D2 D4 A1 A2 A4 B1 B2 B4 (Gillham 500-ft), C1 C2 C4 (100-ft).
        var gc500 = (d2 << 7) | (d4 << 6) | (a1 << 5) | (a2 << 4)
                  | (a4 << 3) | (b1 << 2) | (b2 << 1) | b4;
        var gc100 = (c1 << 2) | (c2 << 1) | c4;

        var n500 = GrayToInt(gc500);
        var n100 = GrayToInt(gc100);

        if (n100 == 0 || n100 == 5 || n100 == 6)
        {
            return null;
        }
        if (n100 == 7)
        {
            n100 = 5;
        }
        if ((n500 & 1) != 0)
        {
            n100 = 6 - n100;
        }
        return n500 * 500 + n100 * 100 - 1300;
    }

    private static int GrayToInt(int n)
    {
        n ^= n >> 8;
        n ^= n >> 4;
        n ^= n >> 2;
        n ^= n >> 1;
        return n;
    }
}
