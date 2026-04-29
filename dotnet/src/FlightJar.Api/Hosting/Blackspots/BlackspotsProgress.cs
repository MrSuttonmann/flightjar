namespace FlightJar.Api.Hosting.Blackspots;

/// <summary>
/// Coarse-grained pipeline phase for the live progress readout. Ordered
/// so a stale callback from an earlier phase can be ignored without a
/// branch per phase. <see cref="Idle"/> is the "nothing's running"
/// sentinel.
/// </summary>
public enum BlackspotsProgressPhase
{
    Idle = 0,
    LoadingTerrain = 1,
    ComputingGrid = 2,
    ComputingFace = 3,
}

/// <summary>
/// What <see cref="BlackspotsCompute.GetProgress"/> hands back to the
/// progress endpoint. <see cref="Fraction"/> is in [0, 1] within the
/// phase; the frontend stitches the phases together into a single
/// fluid bar.
/// </summary>
public readonly record struct BlackspotsProgressSnapshot(
    bool Active, double Fraction, BlackspotsProgressPhase Phase);
