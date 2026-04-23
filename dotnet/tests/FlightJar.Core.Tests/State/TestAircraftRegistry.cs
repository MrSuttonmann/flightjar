using FlightJar.Core.State;
using FlightJar.Decoder.ModeS;

namespace FlightJar.Core.Tests.State;

/// <summary>
/// Test-only subclass of <see cref="AircraftRegistry"/> that lets tests drive
/// a deterministic position resolver without crafting real wire bytes.
/// Mirrors the <c>patch("app.aircraft.pms.decode", side_effect=fake_decode)</c>
/// pattern Python tests use.
/// </summary>
internal sealed class TestAircraftRegistry : AircraftRegistry
{
    public Func<Aircraft, int, int, int, bool, (double Lat, double Lon)?>? PositionOverride { get; set; }

    public TestAircraftRegistry(
        double? latRef = null,
        double? lonRef = null,
        ReceiverInfo? receiver = null,
        IAircraftDb? aircraftDb = null,
        Func<string, DecodedMessage?>? decoder = null)
        : base(
            latRef: latRef, lonRef: lonRef,
            receiver: receiver, aircraftDb: aircraftDb,
            decoder: decoder)
    {
    }

    protected override (double Lat, double Lon)? ResolveNewPosition(
        Aircraft ac, int cprFormat, int cprLat, int cprLon, bool surface)
    {
        return PositionOverride?.Invoke(ac, cprFormat, cprLat, cprLon, surface)
               ?? base.ResolveNewPosition(ac, cprFormat, cprLat, cprLon, surface);
    }
}
