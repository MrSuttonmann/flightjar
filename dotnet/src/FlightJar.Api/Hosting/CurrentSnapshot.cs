using FlightJar.Core.State;

namespace FlightJar.Api.Hosting;

/// <summary>
/// Atomic holder for the most recent registry snapshot + its JSON projection.
/// Written by <see cref="RegistryWorker"/>; read lock-free by HTTP / WS handlers.
/// </summary>
public sealed class CurrentSnapshot
{
    private RegistrySnapshot _snapshot = RegistrySnapshot.Empty;
    private string _json = "{\"now\":0,\"count\":0,\"positioned\":0,\"aircraft\":[]}";

    public RegistrySnapshot Snapshot => Volatile.Read(ref _snapshot);
    public string Json => Volatile.Read(ref _json);

    public void Set(RegistrySnapshot snapshot, string json)
    {
        Volatile.Write(ref _snapshot, snapshot);
        Volatile.Write(ref _json, json);
    }
}
