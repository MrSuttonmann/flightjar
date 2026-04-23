namespace FlightJar.Decoder.Beast;

/// <summary>
/// BEAST wire-format parser.
///
/// Frame layout: 0x1A &lt;type&gt; &lt;6-byte MLAT&gt; &lt;1-byte signal&gt; &lt;message&gt;.
/// Any 0x1A byte inside the body is escaped on the wire as 0x1A 0x1A.
/// </summary>
public static class BeastFrameReader
{
    public const byte BeastEscape = 0x1A;

    /// <summary>
    /// Parse as many frames as possible from the start of <paramref name="buffer"/>,
    /// appending them to <paramref name="frames"/>. Returns the number of bytes
    /// consumed (caller advances past them).
    /// </summary>
    public static int ParseMany(ReadOnlySpan<byte> buffer, ICollection<BeastFrame> frames)
    {
        var totalConsumed = 0;
        while (totalConsumed < buffer.Length)
        {
            var (bytes, frame) = ParseOne(buffer[totalConsumed..]);
            if (bytes == 0)
            {
                break;
            }
            totalConsumed += bytes;
            if (frame.HasValue)
            {
                frames.Add(frame.Value);
            }
        }
        return totalConsumed;
    }

    /// <summary>
    /// Parse one frame from the start of <paramref name="buf"/>.
    /// </summary>
    /// <returns>
    /// (consumed, frame). consumed == 0 means "need more data — do not drop".
    /// frame == null means the consumed bytes were garbage (resync).
    /// </returns>
    public static (int Consumed, BeastFrame? Frame) ParseOne(ReadOnlySpan<byte> buf)
    {
        if (buf.IsEmpty)
        {
            return (0, null);
        }

        if (buf[0] != BeastEscape)
        {
            var nxt = buf[1..].IndexOf(BeastEscape);
            if (nxt < 0)
            {
                return (buf.Length, null);
            }
            return (1 + nxt, null);
        }

        if (buf.Length < 2)
        {
            return (0, null);
        }

        if (!TryGetFrameType(buf[1], out var type, out var msgLen))
        {
            return (1, null);
        }

        var bodyNeeded = 6 + 1 + msgLen;

        // Max body is 6 + 1 + 14 = 21 bytes. stackalloc is safe.
        Span<byte> body = stackalloc byte[bodyNeeded];
        var bodyFilled = 0;
        var i = 2;

        while (bodyFilled < bodyNeeded)
        {
            if (i >= buf.Length)
            {
                return (0, null);
            }
            var b = buf[i];
            if (b == BeastEscape)
            {
                if (i + 1 >= buf.Length)
                {
                    return (0, null);
                }
                if (buf[i + 1] == BeastEscape)
                {
                    body[bodyFilled++] = BeastEscape;
                    i += 2;
                }
                else
                {
                    return (i, null);
                }
            }
            else
            {
                body[bodyFilled++] = b;
                i += 1;
            }
        }

        var mlat = ReadUInt48BigEndian(body[..6]);
        var signal = body[6];
        var msg = body[7..(7 + msgLen)].ToArray();

        return (i, new BeastFrame(type, mlat, signal, msg));
    }

    private static bool TryGetFrameType(byte raw, out BeastFrameType type, out int msgLen)
    {
        switch (raw)
        {
            case 0x31:
                type = BeastFrameType.ModeAc;
                msgLen = 2;
                return true;
            case 0x32:
                type = BeastFrameType.ModeSShort;
                msgLen = 7;
                return true;
            case 0x33:
                type = BeastFrameType.ModeSLong;
                msgLen = 14;
                return true;
            default:
                type = default;
                msgLen = 0;
                return false;
        }
    }

    private static long ReadUInt48BigEndian(ReadOnlySpan<byte> bytes)
    {
        return ((long)bytes[0] << 40)
             | ((long)bytes[1] << 32)
             | ((long)bytes[2] << 24)
             | ((long)bytes[3] << 16)
             | ((long)bytes[4] << 8)
             | bytes[5];
    }
}
