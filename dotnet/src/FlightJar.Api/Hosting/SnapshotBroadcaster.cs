using System.Collections.Concurrent;
using System.Threading.Channels;

namespace FlightJar.Api.Hosting;

/// <summary>
/// Fan-out of 1 Hz snapshot JSON to all connected WebSocket clients. Each
/// subscriber gets a bounded channel with drop-oldest overflow so a stuck
/// client can't stall the broadcast. Intentional improvement over Python's
/// unbounded fan-out.
/// </summary>
public sealed class SnapshotBroadcaster
{
    private readonly ConcurrentDictionary<Guid, WsSubscriber> _subscribers = new();

    public int SubscriberCount => _subscribers.Count;

    public WsSubscriber Subscribe()
    {
        var sub = new WsSubscriber();
        _subscribers[sub.Id] = sub;
        return sub;
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var sub))
        {
            sub.Complete();
        }
    }

    public void Broadcast(string payload)
    {
        foreach (var sub in _subscribers.Values)
        {
            sub.TryEnqueue(payload);
        }
    }
}

/// <summary>Per-WebSocket outgoing buffer.</summary>
public sealed class WsSubscriber
{
    public Guid Id { get; } = Guid.NewGuid();

    private readonly Channel<string> _channel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public bool TryEnqueue(string payload) => _channel.Writer.TryWrite(payload);

    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    internal void Complete() => _channel.Writer.TryComplete();
}
