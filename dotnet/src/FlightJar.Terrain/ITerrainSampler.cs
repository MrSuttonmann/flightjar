namespace FlightJar.Terrain;

/// <summary>
/// Synchronous elevation lookup. Returns metres above WGS-84 mean sea level at
/// the given point. Implementations must be callable from the hot path of an
/// LOS walk — no network, no disk I/O.
/// </summary>
public interface ITerrainSampler
{
    /// <summary>Ground elevation in metres MSL. Returns 0 outside the loaded coverage.</summary>
    double ElevationMetres(double lat, double lon);
}
