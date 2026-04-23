namespace FlightJar.Decoder.ModeS;

/// <summary>
/// Bit-extraction primitives for Mode S message decoding.
/// Mirrors pyModeS 3.2.0 <c>_bits.py</c>; bit positions are MSB-first
/// (bit 0 is the leftmost/most significant bit of the message).
/// </summary>
public static class ModeSBits
{
    /// <summary>
    /// Extract <paramref name="width"/> bits starting at bit
    /// <paramref name="start"/> (MSB-first) from a <paramref name="totalBits"/>-wide value.
    /// </summary>
    public static ulong ExtractUnsigned(UInt128 n, int start, int width, int totalBits)
    {
        var shift = totalBits - start - width;
        var mask = width == 64 ? ulong.MaxValue : ((1UL << width) - 1);
        return (ulong)(n >> shift) & mask;
    }

    /// <summary>
    /// Extract <paramref name="width"/> bits as a signed two's-complement integer.
    /// </summary>
    public static long ExtractSigned(UInt128 n, int start, int width, int totalBits)
    {
        var raw = (long)ExtractUnsigned(n, start, width, totalBits);
        var signBit = 1L << (width - 1);
        if ((raw & signBit) != 0)
        {
            raw -= 1L << width;
        }
        return raw;
    }

    // Mode S CRC-24 polynomial: 0xFFF409 (24-bit form, high bit dropped).
    // Per ICAO Annex 10 Vol IV §3.1.2.6.
    private const uint CrcPoly = 0xFFF409;

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var c = (uint)i << 16;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 0x800000) != 0 ? ((c << 1) ^ CrcPoly) : (c << 1);
            }
            table[i] = c & 0xFFFFFF;
        }
        return table;
    }

    /// <summary>
    /// Compute the Mode S CRC-24 remainder of a <paramref name="length"/>-bit message.
    /// Valid DF17/18 messages yield 0; DF20/21 yields the ICAO address (possibly XORed
    /// with a BDS overlay).
    /// </summary>
    public static uint CrcRemainder(UInt128 n, int length)
    {
        var nDataBytes = (length - 24) / 8;
        uint crc = 0;
        var shift = length - 8;
        for (var i = 0; i < nDataBytes; i++)
        {
            var @byte = (uint)(n >> shift) & 0xFF;
            crc = ((crc << 8) & 0xFFFFFF) ^ CrcTable[((crc >> 16) ^ @byte) & 0xFF];
            shift -= 8;
        }
        var parity = (uint)n & 0xFFFFFF;
        return (crc ^ parity) & 0xFFFFFF;
    }
}
