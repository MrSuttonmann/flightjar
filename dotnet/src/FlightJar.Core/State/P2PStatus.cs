namespace FlightJar.Core.State;

/// <summary>
/// Live status of the outbound P2P relay connection. Written by
/// <c>P2PRelayClientService</c>, read by <c>RegistryWorker</c> when it
/// builds each snapshot. Both <c>Connected</c> and <c>Peers</c> reset to
/// their disconnected defaults when the WebSocket drops.
/// </summary>
public sealed class P2PStatus
{
    private int _connected;
    private int _peers;

    public bool Connected => Volatile.Read(ref _connected) != 0;
    public int Peers => Volatile.Read(ref _peers);

    public void SetConnected()
    {
        Volatile.Write(ref _connected, 1);
    }

    public void SetDisconnected()
    {
        Volatile.Write(ref _connected, 0);
        Volatile.Write(ref _peers, 0);
    }

    public void UpdatePeers(int peers)
    {
        Volatile.Write(ref _peers, peers < 0 ? 0 : peers);
    }
}
