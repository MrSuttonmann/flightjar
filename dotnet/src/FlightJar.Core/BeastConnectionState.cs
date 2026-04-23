namespace FlightJar.Core;

public interface IBeastConnectionState
{
    bool IsConnected { get; }
}

public sealed class BeastConnectionState : IBeastConnectionState
{
    private int _connected;

    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    public void Set(bool connected) => Volatile.Write(ref _connected, connected ? 1 : 0);
}
