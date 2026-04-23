namespace FlightJar.Decoder.ModeS;

/// <summary>
/// Mode S identity code (squawk) decoder. Mirrors pyModeS 3.2.0 <c>_idcode.py</c>.
/// 13-bit ID field: C1 A1 C2 A2 C4 A4 X B1 D1 B2 D2 B4 D4.
/// </summary>
public static class IdentityCode
{
    public static string Decode(int idcode)
    {
        int Bit(int pos) => (idcode >> (12 - pos)) & 0x1;

        var a = (Bit(5) << 2) | (Bit(3) << 1) | Bit(1);
        var b = (Bit(11) << 2) | (Bit(9) << 1) | Bit(7);
        var c = (Bit(4) << 2) | (Bit(2) << 1) | Bit(0);
        var d = (Bit(12) << 2) | (Bit(10) << 1) | Bit(8);

        Span<char> buf = stackalloc char[4];
        buf[0] = (char)('0' + a);
        buf[1] = (char)('0' + b);
        buf[2] = (char)('0' + c);
        buf[3] = (char)('0' + d);
        return new string(buf);
    }
}
