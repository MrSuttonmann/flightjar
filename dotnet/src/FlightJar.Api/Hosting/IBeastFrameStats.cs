namespace FlightJar.Api.Hosting;

/// <summary>
/// Read-only counter of BEAST frames the registry worker has accepted
/// since startup. Implemented by <see cref="RegistryWorker"/>; consumed
/// by the <c>/api/stats</c>, <c>/metrics</c>, and telemetry surfaces so
/// they don't have to depend on the worker concretely.
/// </summary>
public interface IBeastFrameStats
{
    long FrameCount { get; }
}
