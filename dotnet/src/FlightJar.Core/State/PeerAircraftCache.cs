using System.Collections.Concurrent;

namespace FlightJar.Core.State;

/// <summary>
/// Thread-safe store for aircraft received from the P2P relay. Written by
/// <c>P2PRelayClientService</c> on the relay receive loop and read by
/// <c>RegistryWorker</c> on each 1 Hz tick to merge peer aircraft into the
/// snapshot. Uses received-at timestamps (not the aircraft's <c>last_seen</c>)
/// to avoid clock-skew issues between instances.
/// </summary>
public sealed class PeerAircraftCache
{
    private sealed record Entry(SnapshotAircraft Aircraft, double ReceivedAt);

    private readonly ConcurrentDictionary<string, Entry> _store =
        new(StringComparer.OrdinalIgnoreCase);

    public void Update(IReadOnlyList<SnapshotAircraft> aircraft, double nowUnix)
    {
        foreach (var ac in aircraft)
        {
            _store[ac.Icao] = new Entry(ac, nowUnix);
        }
    }

    /// <summary>Returns all entries received within <paramref name="maxAgeS"/>
    /// seconds of <paramref name="nowUnix"/>. Stale entries are evicted lazily
    /// at the same time so the dictionary doesn't grow unbounded.</summary>
    public IReadOnlyList<SnapshotAircraft> GetFresh(double nowUnix, double maxAgeS = 65)
    {
        var cutoff = nowUnix - maxAgeS;
        var result = new List<SnapshotAircraft>();
        foreach (var (key, entry) in _store)
        {
            if (entry.ReceivedAt >= cutoff)
            {
                result.Add(entry.Aircraft);
            }
            else
            {
                _store.TryRemove(key, out _);
            }
        }
        return result;
    }

    public int Count => _store.Count;
}
