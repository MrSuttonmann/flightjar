namespace FlightJar.Decoder.ModeS;

/// <summary>
/// 6-bit ICAO callsign encoding (BDS 0,8). Mirrors pyModeS 3.2.0 <c>_callsign.py</c>.
/// </summary>
public static class CallsignDecoder
{
    private static readonly char[] Table = BuildTable();

    private static char[] BuildTable()
    {
        var table = new char[64];
        for (var i = 0; i < 64; i++)
        {
            if (i >= 1 && i <= 26)
            {
                table[i] = (char)(i | 0x40); // A-Z
            }
            else if (i == 32 || (i >= 48 && i <= 57))
            {
                table[i] = (char)i; // space or 0-9
            }
            else
            {
                table[i] = '#';
            }
        }
        return table;
    }

    /// <summary>
    /// Decode an 8x6-bit callsign from a 48-bit integer. Leading and trailing
    /// whitespace is stripped. Invalid characters render as '#'.
    /// </summary>
    public static string Decode(ulong bits)
    {
        Span<char> buf = stackalloc char[8];
        for (var i = 0; i < 8; i++)
        {
            buf[i] = Table[(bits >> (42 - 6 * i)) & 0x3F];
        }
        return new string(buf).Trim();
    }

    public static bool IsValidChar(int idx) => idx >= 0 && idx < 64 && Table[idx] != '#';
}
