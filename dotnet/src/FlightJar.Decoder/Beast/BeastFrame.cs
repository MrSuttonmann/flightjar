namespace FlightJar.Decoder.Beast;

public enum BeastFrameType : byte
{
    ModeAc = 0x31,
    ModeSShort = 0x32,
    ModeSLong = 0x33,
}

public readonly record struct BeastFrame(
    BeastFrameType Type,
    long MlatTicks,
    byte Signal,
    ReadOnlyMemory<byte> Message)
{
    public int MessageLength => Message.Length;

    public static int ExpectedMessageLength(BeastFrameType type) => type switch
    {
        BeastFrameType.ModeAc => 2,
        BeastFrameType.ModeSShort => 7,
        BeastFrameType.ModeSLong => 14,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
