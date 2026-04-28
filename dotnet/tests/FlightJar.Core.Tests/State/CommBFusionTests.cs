using FlightJar.Core.State;

namespace FlightJar.Core.Tests.State;

public class CommBFusionTests
{
    private static Aircraft Bare(string icao = "abc123") => new() { Icao = icao };

    [Fact]
    public void Build_ReturnsNullWhenAllRegistersStale()
    {
        var ac = Bare();
        var snap = CommBFusion.Build(ac, now: 100.0);
        Assert.Null(snap);
    }

    [Fact]
    public void Build_ReturnsNullWhenAllRegistersUnseen()
    {
        var ac = Bare();
        // Bds*At fields are 0 (never written). Even at now=0 the freshness
        // check requires Bds*At > 0, so this must come back null.
        var snap = CommBFusion.Build(ac, now: 0.0);
        Assert.Null(snap);
    }

    [Fact]
    public void Build_AgesOutFieldsOlderThanCommBMaxAge()
    {
        var ac = Bare();
        ac.QnhHpa = 1013.0;
        ac.Bds40At = 100.0;
        ac.Mach = 0.78;
        ac.Bds60At = 100.0;
        // 121s after the last decode — both registers expire.
        var snap = CommBFusion.Build(ac, now: 100.0 + CommBFusion.CommBMaxAge + 1);
        Assert.Null(snap);
    }

    [Fact]
    public void Build_KeepsFreshFieldDropsStale()
    {
        var ac = Bare();
        // Now = 200: bds40 fresh (100s old, < 120s window), bds60 stale (199.5s old).
        ac.QnhHpa = 1013.0;
        ac.Bds40At = 100.0;
        ac.Mach = 0.78;
        ac.Bds60At = 0.5;
        var snap = CommBFusion.Build(ac, now: 200.0);
        Assert.NotNull(snap);
        Assert.Equal(1013.0, snap!.QnhHpa);
        Assert.Null(snap.Mach); // Bds60 is stale
    }

    [Fact]
    public void Build_ObservedSatTakesPrecedenceOverDerivable()
    {
        // BDS 4,4 SAT present + BDS 5,0 + 6,0 also present — observed must win.
        var ac = Bare();
        ac.StaticAirTemperatureC = -55.0;
        ac.Bds44At = 50.0;
        ac.TrueAirspeedKt = 460;
        ac.Bds50At = 50.0;
        ac.Mach = 0.78;
        ac.Bds60At = 50.0;

        var snap = CommBFusion.Build(ac, now: 60.0);
        Assert.NotNull(snap);
        Assert.Equal(-55.0, snap!.StaticAirTemperatureC);
        Assert.Equal("observed", snap.StaticAirTemperatureSource);
    }

    [Fact]
    public void Build_DerivesSatFromTasMachWhenBds44Stale()
    {
        // TAS 460 kt @ Mach 0.78 → a = 460*0.5144444/0.78 ≈ 303.42 m/s
        // T = a²/401.874 ≈ 229 K ≈ -44.2 °C
        var ac = Bare();
        ac.TrueAirspeedKt = 460;
        ac.Bds50At = 50.0;
        ac.Mach = 0.78;
        ac.Bds60At = 50.0;
        // Bds44 unset — derivation kicks in.

        var snap = CommBFusion.Build(ac, now: 60.0);
        Assert.NotNull(snap);
        Assert.Equal("derived", snap!.StaticAirTemperatureSource);
        Assert.NotNull(snap.StaticAirTemperatureC);
        Assert.InRange(snap.StaticAirTemperatureC!.Value, -50.0, -40.0);
    }

    [Fact]
    public void Build_BlanksDerivedSatWhenOutsidePlausibilityWindow()
    {
        // Pathological TAS 800 kt @ Mach 0.20 — speed of sound implied
        // would be ~2057 m/s → T ≈ 10500 K. Out of range → null.
        var ac = Bare();
        ac.TrueAirspeedKt = 800;
        ac.Bds50At = 50.0;
        ac.Mach = 0.20;
        ac.Bds60At = 50.0;

        var snap = CommBFusion.Build(ac, now: 60.0);
        Assert.NotNull(snap);
        Assert.Null(snap!.StaticAirTemperatureC);
        Assert.Null(snap.StaticAirTemperatureSource);
    }

    [Fact]
    public void Build_SkipsDerivationWhenMachBelowFloor()
    {
        // Mach < 0.1 — derivation skipped (taxi noise floor).
        var ac = Bare();
        ac.TrueAirspeedKt = 30;
        ac.Bds50At = 50.0;
        ac.Mach = 0.05;
        ac.Bds60At = 50.0;

        var snap = CommBFusion.Build(ac, now: 60.0);
        // bds50/60 are fresh so we still get a snapshot, but no SAT.
        Assert.NotNull(snap);
        Assert.Null(snap!.StaticAirTemperatureC);
    }

    [Fact]
    public void Build_DerivesTatFromObservedSatPlusMach()
    {
        // SAT -55 °C = 218.15 K, Mach 0.78 → TAT = 218.15 * (1 + 0.2 * 0.78²)
        //  = 218.15 * 1.12168 ≈ 244.65 K ≈ -28.5 °C
        var ac = Bare();
        ac.StaticAirTemperatureC = -55.0;
        ac.Bds44At = 50.0;
        ac.Mach = 0.78;
        ac.Bds60At = 50.0;

        var snap = CommBFusion.Build(ac, now: 60.0);
        Assert.NotNull(snap);
        Assert.NotNull(snap!.TotalAirTemperatureC);
        Assert.InRange(snap.TotalAirTemperatureC!.Value, -29.5, -27.5);
    }

    [Fact]
    public void Build_TatNullWhenMachStale()
    {
        var ac = Bare();
        ac.StaticAirTemperatureC = -55.0;
        ac.Bds44At = 100.0;
        ac.Mach = 0.78;
        ac.Bds60At = 100.0 - CommBFusion.CommBMaxAge - 5;  // stale

        var snap = CommBFusion.Build(ac, now: 100.0);
        Assert.NotNull(snap);
        Assert.Equal(-55.0, snap!.StaticAirTemperatureC);
        Assert.Null(snap.TotalAirTemperatureC);
    }

    [Fact]
    public void Build_PassesThroughAllFreshRegisters()
    {
        var ac = Bare();
        ac.SelectedAltitudeMcpFt = 32000;
        ac.QnhHpa = 1013.0;
        ac.Bds40At = 50.0;
        ac.WindSpeedKt = 60;
        ac.WindDirectionDeg = 270.0;
        ac.Bds44At = 50.0;
        ac.RollDeg = -2.5;
        ac.TrueTrackDeg = 90.0;
        ac.GroundspeedKt = 450;
        ac.Bds50At = 50.0;
        ac.IndicatedAirspeedKt = 280;
        ac.Mach = 0.78;
        ac.BaroVerticalRateFpm = 1500;
        ac.Bds60At = 50.0;

        var snap = CommBFusion.Build(ac, now: 60.0);
        Assert.NotNull(snap);
        Assert.Equal(32000, snap!.SelectedAltitudeMcpFt);
        Assert.Equal(1013.0, snap.QnhHpa);
        Assert.Equal(60, snap.WindSpeedKt);
        Assert.Equal(270.0, snap.WindDirectionDeg);
        Assert.Equal(-2.5, snap.RollDeg);
        Assert.Equal(90.0, snap.TrueTrackDeg);
        Assert.Equal(450, snap.GroundspeedKt);
        Assert.Equal(280, snap.IndicatedAirspeedKt);
        Assert.Equal(0.78, snap.Mach);
        Assert.Equal(1500, snap.BaroVerticalRateFpm);
    }
}
