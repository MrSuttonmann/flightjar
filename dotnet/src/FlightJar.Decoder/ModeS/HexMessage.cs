namespace FlightJar.Decoder.ModeS;

/// <summary>
/// A Mode S message parsed into its canonical representation: a UInt128
/// holding the raw bits (MSB-left, zero-padded on the right for messages
/// shorter than 128 bits) plus the total bit-width (56 for short, 112 for
/// long messages).
/// </summary>
public readonly record struct HexMessage(UInt128 Bits, int TotalBits)
{
    public static HexMessage Parse(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0)
        {
            throw new ArgumentException("hex string must be non-empty and even length", nameof(hex));
        }
        UInt128 value = 0;
        foreach (var c in hex)
        {
            value = (value << 4) | HexNibble(c);
        }
        return new HexMessage(value, hex.Length * 4);
    }

    public static HexMessage FromBytes(ReadOnlySpan<byte> bytes)
    {
        UInt128 value = 0;
        foreach (var b in bytes)
        {
            value = (value << 8) | b;
        }
        return new HexMessage(value, bytes.Length * 8);
    }

    public string ToHex()
    {
        var nibbles = TotalBits / 4;
        Span<char> buf = stackalloc char[nibbles];
        var v = Bits;
        for (var i = nibbles - 1; i >= 0; i--)
        {
            var nibble = (int)(v & 0xF);
            buf[i] = nibble < 10 ? (char)('0' + nibble) : (char)('A' + nibble - 10);
            v >>= 4;
        }
        return new string(buf);
    }

    public override string ToString() => ToHex();

    private static uint HexNibble(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return (uint)(c - '0');
        }
        if (c >= 'a' && c <= 'f')
        {
            return (uint)(c - 'a' + 10);
        }
        if (c >= 'A' && c <= 'F')
        {
            return (uint)(c - 'A' + 10);
        }
        throw new ArgumentException($"'{c}' is not a hex digit");
    }
}
