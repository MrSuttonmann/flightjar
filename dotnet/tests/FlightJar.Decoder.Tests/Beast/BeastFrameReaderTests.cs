using FlightJar.Decoder.Beast;

namespace FlightJar.Decoder.Tests.Beast;

public class BeastFrameReaderTests
{
    private const byte Esc = BeastFrameReader.BeastEscape;

    private static readonly byte[] MsgLong = Hex("8d406b902015a678d4d220aa4bda");
    private static readonly byte[] MsgShort = Hex("5d4ca2d158c901");
    private static readonly byte[] MsgAc = Hex("2000");

    private static byte[] Hex(string s)
    {
        var bytes = new byte[s.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    private static byte[] Frame(byte typeByte, byte[] ts, byte sig, byte[] msg)
    {
        var result = new byte[2 + ts.Length + 1 + msg.Length];
        result[0] = Esc;
        result[1] = typeByte;
        Array.Copy(ts, 0, result, 2, ts.Length);
        result[2 + ts.Length] = sig;
        Array.Copy(msg, 0, result, 3 + ts.Length, msg.Length);
        return result;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var len = parts.Sum(p => p.Length);
        var result = new byte[len];
        var offset = 0;
        foreach (var p in parts)
        {
            Array.Copy(p, 0, result, offset, p.Length);
            offset += p.Length;
        }
        return result;
    }

    private static byte[] EscapeBody(byte[] body)
    {
        var list = new List<byte>(body.Length);
        foreach (var b in body)
        {
            if (b == Esc)
            {
                list.Add(Esc);
                list.Add(Esc);
            }
            else
            {
                list.Add(b);
            }
        }
        return list.ToArray();
    }

    [Fact]
    public void ParsesModeSLongFrame()
    {
        var ts = Hex("0102030405aa");
        var frame = Frame(0x33, ts, 0x64, MsgLong);
        var (consumed, parsed) = BeastFrameReader.ParseOne(frame);

        Assert.Equal(frame.Length, consumed);
        Assert.NotNull(parsed);
        Assert.Equal(BeastFrameType.ModeSLong, parsed.Value.Type);
        Assert.Equal(0x010203_0405aaL, parsed.Value.MlatTicks);
        Assert.Equal(0x64, parsed.Value.Signal);
        Assert.Equal(MsgLong, parsed.Value.Message.ToArray());
    }

    [Fact]
    public void ParsesModeSShortFrame()
    {
        var frame = Frame(0x32, new byte[6], 0x50, MsgShort);
        var (_, parsed) = BeastFrameReader.ParseOne(frame);

        Assert.NotNull(parsed);
        Assert.Equal(BeastFrameType.ModeSShort, parsed.Value.Type);
        Assert.Equal(MsgShort, parsed.Value.Message.ToArray());
    }

    [Fact]
    public void ParsesModeAcFrame()
    {
        var frame = Frame(0x31, new byte[6], 0x20, MsgAc);
        var (_, parsed) = BeastFrameReader.ParseOne(frame);

        Assert.NotNull(parsed);
        Assert.Equal(BeastFrameType.ModeAc, parsed.Value.Type);
        Assert.Equal(MsgAc, parsed.Value.Message.ToArray());
    }

    [Fact]
    public void EmptyBuffer_RequestsMoreData()
    {
        var (consumed, parsed) = BeastFrameReader.ParseOne(Array.Empty<byte>());
        Assert.Equal(0, consumed);
        Assert.Null(parsed);
    }

    [Fact]
    public void PartialFrame_RequestsMoreDataWithoutConsuming()
    {
        var frame = Frame(0x33, new byte[6], 0x00, MsgLong);
        var truncated = frame[..^1];
        var (consumed, parsed) = BeastFrameReader.ParseOne(truncated);

        Assert.Equal(0, consumed);
        Assert.Null(parsed);
    }

    [Fact]
    public void ResyncsToNextEscapeByte()
    {
        var garbage = new byte[] { 0x00, 0x01, 0x02 };
        var buf = Concat(garbage, new byte[] { 0x1a, 0x33 });
        var (consumed, parsed) = BeastFrameReader.ParseOne(buf);

        Assert.Equal(garbage.Length, consumed);
        Assert.Null(parsed);
    }

    [Fact]
    public void DiscardsEntireBufferWithNoEscape()
    {
        var buf = new byte[] { 0x00, 0x01, 0x02 };
        var (consumed, parsed) = BeastFrameReader.ParseOne(buf);

        Assert.Equal(buf.Length, consumed);
        Assert.Null(parsed);
    }

    [Fact]
    public void BadTypeByte_DropsSingleEscape()
    {
        var buf = new byte[] { 0x1a, 0xff };
        var (consumed, parsed) = BeastFrameReader.ParseOne(buf);

        Assert.Equal(1, consumed);
        Assert.Null(parsed);
    }

    [Fact]
    public void EscapedZeroX1AInBody_IsUnescaped()
    {
        var msg = (byte[])MsgLong.Clone();
        msg[3] = Esc;
        var rawBody = Concat(new byte[6], new byte[] { 0x50 }, msg);
        var encodedBody = EscapeBody(rawBody);
        var frame = Concat(new byte[] { Esc, 0x33 }, encodedBody);

        var (consumed, parsed) = BeastFrameReader.ParseOne(frame);

        Assert.Equal(frame.Length, consumed);
        Assert.NotNull(parsed);
        Assert.Equal(msg, parsed.Value.Message.ToArray());
    }

    [Fact]
    public void UnescapedZeroX1AInsideBody_TriggersResync()
    {
        // Header + ts (6 zeros) + sig (0x50) + partial body with stray 0x1A
        var body = Concat(new byte[6], new byte[] { 0x50 }, new byte[] { 0xaa, 0x1a, 0x00 });
        var buf = Concat(new byte[] { Esc, 0x33 }, body);
        var (consumed, parsed) = BeastFrameReader.ParseOne(buf);

        Assert.Null(parsed);
        Assert.True(consumed > 0);
        // Parser positions at the stray 0x1A so the next pass re-evaluates.
        Assert.Equal(Esc, buf[consumed]);
    }

    [Fact]
    public void EscapeAtBufferBoundary_RequestsMoreData()
    {
        var msg = (byte[])MsgLong.Clone();
        msg[0] = Esc;
        var rawBody = Concat(new byte[6], new byte[] { 0x50 }, msg);
        var encodedBody = EscapeBody(rawBody);
        var frame = Concat(new byte[] { Esc, 0x33 }, encodedBody);

        // Truncate to 10 bytes: 2 (header) + 7 (ts/sig) + 1 (first msg byte = 0x1A)
        // The next byte (paired 0x1A of the escape) is missing.
        var (consumed, parsed) = BeastFrameReader.ParseOne(frame.AsSpan(0, 10));

        Assert.Equal(0, consumed);
        Assert.Null(parsed);
    }
}
