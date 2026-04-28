using FlightJar.Core.State;

namespace FlightJar.Core.Tests.State;

public class AircraftRegistryTests
{
    private static TestAircraftRegistry MakeRegistry(
        double? latRef = null,
        double? lonRef = null,
        ReceiverInfo? receiver = null,
        IAircraftDb? aircraftDb = null)
    {
        var reg = new TestAircraftRegistry(
            latRef: latRef, lonRef: lonRef,
            receiver: receiver, aircraftDb: aircraftDb,
            decoder: FakeDecoder.Decoder);
        reg.PositionOverride = FakeDecoder.DefaultLocal;
        return reg;
    }

    [Fact]
    public void Ingest_AdsbIdentification_PopulatesCallsignAndCategory()
    {
        var reg = MakeRegistry();
        Assert.True(reg.Ingest("ID01beefcafe", now: 100.0));
        var ac = reg.Aircraft["abc123"];
        Assert.Equal("FLY123", ac.Callsign);
        Assert.Equal(3, ac.Category);
        Assert.Equal(1, ac.MsgCount);
    }

    [Fact]
    public void Ingest_Df4Altitude()
    {
        var reg = MakeRegistry();
        Assert.True(reg.Ingest("AC01xxxx", now: 50.0));
        var ac = reg.Aircraft["def456"];
        Assert.Equal(24000, ac.Altitude);
        Assert.Null(ac.Callsign);
    }

    [Fact]
    public void Ingest_Df5Squawk()
    {
        var reg = MakeRegistry();
        reg.Ingest("SQ01xxxx", now: 50.0);
        Assert.Equal("1234", reg.Aircraft["def456"].Squawk);
    }

    [Fact]
    public void BadCrc_DoesNotCreateAircraft()
    {
        var reg = MakeRegistry();
        Assert.False(reg.Ingest("BAD1xxxx", now: 50.0));
        Assert.False(reg.Aircraft.ContainsKey("aaa111"));
    }

    [Fact]
    public void Velocity_PopulatesSpeedTrackVrate()
    {
        var reg = MakeRegistry();
        reg.Ingest("VL01xxxx", now: 10.0);
        var ac = reg.Aircraft["abc123"];
        Assert.Equal(450, ac.Speed);
        Assert.Equal(270.0, ac.Track);
        Assert.Equal(-600, ac.Vrate);
    }

    [Fact]
    public void LocalPosition_UsesReceiverReference()
    {
        var reg = MakeRegistry(latRef: 52.0, lonRef: -1.0);
        reg.Ingest("AP01xxxx", now: 10.0);
        var ac = reg.Aircraft["abc123"];
        Assert.Equal(37000, ac.Altitude);
        Assert.Equal(52.1, ac.Lat);
        Assert.Equal(-1.1, ac.Lon);
        Assert.False(ac.OnGround);
    }

    [Fact]
    public void Snapshot_SkipsAircraftWithOnlyIcao()
    {
        var reg = MakeRegistry();
        reg.Ingest("DF11xxxx", now: 1.0);
        var snap = reg.Snapshot(now: 1.5);
        Assert.Equal(0, snap.Count);
        Assert.Empty(snap.Aircraft);
    }

    [Fact]
    public void Snapshot_IncludesCallsignedAircraft()
    {
        var reg = MakeRegistry();
        reg.Ingest("ID01xxxx", now: 1.0);
        var snap = reg.Snapshot(now: 1.2);
        Assert.Equal(1, snap.Count);
        Assert.Equal("FLY123", snap.Aircraft[0].Callsign);
    }

    [Fact]
    public void Snapshot_DistanceKmUsesDisplayedReceiver()
    {
        var receiver = new ReceiverInfo(Lat: 52.0, Lon: -1.0, AnonKm: 10);
        var reg = MakeRegistry(latRef: 52.0, lonRef: -1.0, receiver: receiver);
        reg.Ingest("AP01xxxx", now: 1.0);
        var snap = reg.Snapshot(now: 1.2);
        Assert.NotNull(snap.Aircraft[0].DistanceKm);
        // (52.1, -1.1) to (52.0, -1.0) is ~13-14 km
        Assert.InRange(snap.Aircraft[0].DistanceKm!.Value, 5, 20);
    }

    [Fact]
    public void CommB_Bds44_PopulatesWindAndTemperatureInSnapshot()
    {
        var reg = MakeRegistry();
        reg.Ingest("ID01xxxx", now: 10.0);  // seed callsign so snapshot includes aircraft
        reg.Ingest("BD44xxxx", now: 10.1);
        var snap = reg.Snapshot(now: 10.2);
        var commB = snap.Aircraft[0].CommB;
        Assert.NotNull(commB);
        Assert.Equal(50, commB!.WindSpeedKt);
        Assert.Equal(270.0, commB.WindDirectionDeg);
        Assert.Equal(-55.0, commB.StaticAirTemperatureC);
        Assert.Equal("observed", commB.StaticAirTemperatureSource);
        // TAT is only derived when SAT AND Mach are fresh.
        Assert.Null(commB.TotalAirTemperatureC);
    }

    [Fact]
    public void CommB_Bds60_PopulatesMachAndHeading()
    {
        var reg = MakeRegistry();
        reg.Ingest("ID01xxxx", now: 10.0);
        reg.Ingest("BD60xxxx", now: 10.1);
        var snap = reg.Snapshot(now: 10.2);
        var commB = snap.Aircraft[0].CommB;
        Assert.NotNull(commB);
        Assert.Equal(0.82, commB!.Mach);
        Assert.Equal(285, commB.IndicatedAirspeedKt);
        Assert.Equal(95.0, commB.MagneticHeadingDeg);
    }

    [Fact]
    public void CommB_Bds44AndBds60_DeriveTatFromSatAndMach()
    {
        var reg = MakeRegistry();
        reg.Ingest("ID01xxxx", now: 10.0);
        reg.Ingest("BD44xxxx", now: 10.1);  // SAT = -55 °C
        reg.Ingest("BD60xxxx", now: 10.2);  // Mach = 0.82
        var snap = reg.Snapshot(now: 10.3);
        var commB = snap.Aircraft[0].CommB;
        Assert.NotNull(commB);
        Assert.NotNull(commB!.TotalAirTemperatureC);
        // TAT_K = SAT_K * (1 + 0.2 * M^2) = (218.15) * (1 + 0.2 * 0.6724)
        //       = 218.15 * 1.13448 = 247.47 K → 247.47 - 273.15 = -25.68 °C
        Assert.Equal(-25.68, commB.TotalAirTemperatureC!.Value, 0.1);
    }

    [Fact]
    public void CommB_Bds40_PopulatesQnhAndAutopilotTarget()
    {
        var reg = MakeRegistry();
        reg.Ingest("ID01xxxx", now: 10.0);
        reg.Ingest("BD40xxxx", now: 10.1);
        var snap = reg.Snapshot(now: 10.2);
        var commB = snap.Aircraft[0].CommB;
        Assert.NotNull(commB);
        Assert.Equal(1013.0, commB!.QnhHpa);
        Assert.Equal(36000, commB.SelectedAltitudeMcpFt);
    }

    [Fact]
    public void CommB_StaleValuesAreFilteredFromSnapshot()
    {
        var reg = MakeRegistry();
        reg.Ingest("ID01xxxx", now: 10.0);
        reg.Ingest("BD44xxxx", now: 10.1);
        // Jump clock past CommBMaxAge so the BDS 4,4 decode ages out.
        var snap = reg.Snapshot(now: 10.1 + AircraftRegistry.CommBMaxAge + 1);
        Assert.Null(snap.Aircraft[0].CommB);
    }

    [Fact]
    public void CommB_DifferentRegistersDoNotClobberEachOther()
    {
        var reg = MakeRegistry();
        reg.Ingest("ID01xxxx", now: 10.0);
        reg.Ingest("BD44xxxx", now: 10.1);  // sets wind + SAT
        reg.Ingest("BD60xxxx", now: 10.2);  // sets mach + heading (must NOT clear wind)
        var snap = reg.Snapshot(now: 10.3);
        var commB = snap.Aircraft[0].CommB;
        Assert.NotNull(commB);
        Assert.Equal(50, commB!.WindSpeedKt);
        Assert.Equal(0.82, commB.Mach);
    }

    [Fact]
    public void CommB_DerivesSatFromTasAndMachWhenBds44Absent()
    {
        // BDS 4,4 is pilot-optional; when it never arrives but we do have
        // TAS (BDS 5,0) + Mach (BDS 6,0), SAT is derivable from
        // a = TAS / M, T_K = a² / (γR). TAS 470 kt + M 0.82 → SAT ~ -57 °C.
        var reg = MakeRegistry();
        reg.Ingest("ID01xxxx", now: 10.0);
        reg.Ingest("BD50xxxx", now: 10.1);
        reg.Ingest("BD60xxxx", now: 10.2);
        var snap = reg.Snapshot(now: 10.3);
        var commB = snap.Aircraft[0].CommB;
        Assert.NotNull(commB);
        Assert.NotNull(commB!.StaticAirTemperatureC);
        Assert.Equal(-56.8, commB.StaticAirTemperatureC!.Value, 0.5);
        Assert.Equal("derived", commB.StaticAirTemperatureSource);
        // TAT must also derive in this state (uses the derived SAT + Mach).
        Assert.NotNull(commB.TotalAirTemperatureC);
        Assert.Equal(-27.7, commB.TotalAirTemperatureC!.Value, 0.5);
    }

    [Fact]
    public void CommB_PrefersObservedSatOverDerived()
    {
        // When BDS 4,4 IS present, it wins over the TAS/Mach derivation —
        // a direct reading from the aircraft is always preferable.
        var reg = MakeRegistry();
        reg.Ingest("ID01xxxx", now: 10.0);
        reg.Ingest("BD50xxxx", now: 10.1);  // would derive SAT ~ -57 °C
        reg.Ingest("BD60xxxx", now: 10.2);
        reg.Ingest("BD44xxxx", now: 10.3);  // observed SAT = -55 °C
        var snap = reg.Snapshot(now: 10.4);
        var commB = snap.Aircraft[0].CommB;
        Assert.NotNull(commB);
        Assert.Equal(-55.0, commB!.StaticAirTemperatureC);
        Assert.Equal("observed", commB.StaticAirTemperatureSource);
    }

    [Fact]
    public void CommB_DerivationRejectsImplausibleTemperature()
    {
        // If the TAS/Mach pair would yield a temperature outside [150 K,
        // 320 K] — e.g. Mach 0.05 during a taxi transient with a stale
        // TAS reading — blank the value rather than display garbage.
        var reg = MakeRegistry();
        reg.Ingest("ID01xxxx", now: 10.0);
        reg.Ingest("BD50xxxx", now: 10.1);  // TAS 470 kt
        // Inject a tiny Mach directly to mimic a noise event.
        var ac = reg.Aircraft["abc123"];
        ac.Mach = 0.05;
        ac.Bds60At = 10.2;
        var snap = reg.Snapshot(now: 10.3);
        Assert.Null(snap.Aircraft[0].CommB?.StaticAirTemperatureC);
    }

    [Fact]
    public void Cleanup_EvictsStaleAircraft()
    {
        var reg = MakeRegistry();
        reg.Ingest("ID01xxxx", now: 100.0);
        Assert.True(reg.Aircraft.ContainsKey("abc123"));
        reg.Cleanup(now: 100.0 + AircraftRegistry.AircraftTimeout + 1);
        Assert.False(reg.Aircraft.ContainsKey("abc123"));
    }

    [Fact]
    public void EmergencySquawk_PopulatesSnapshotField()
    {
        var reg = MakeRegistry();
        reg.Ingest("ID01xxxx", now: 10.0);
        // Emulate a later DF5 setting the squawk to 7700 directly.
        var mutable = reg.Aircraft["abc123"];
        mutable.Squawk = "7700";
        var snap = reg.Snapshot(now: 10.1);
        Assert.Equal("general", snap.Aircraft[0].Emergency);
    }

    [Fact]
    public void NonEmergencySquawk_HasNoEmergencyFlag()
    {
        var reg = MakeRegistry();
        reg.Ingest("ID01xxxx", now: 10.0);
        reg.Aircraft["abc123"].Squawk = "1200";
        var snap = reg.Snapshot(now: 10.1);
        Assert.Null(snap.Aircraft[0].Emergency);
    }

    [Fact]
    public void BaroAltitude_StoredSeparatelyFromGeo()
    {
        var reg = MakeRegistry(latRef: 52.0, lonRef: -1.0);
        reg.Ingest("AP01xxxx", now: 10.0);
        reg.Ingest("GP01xxxx", now: 10.1);
        var ac = reg.Aircraft["abc123"];
        Assert.Equal(37000, ac.AltitudeBaro);
        Assert.Equal(37100, ac.AltitudeGeo);
        Assert.Equal(37000, ac.Altitude);
    }

    [Fact]
    public void GeoOnlyAircraft_ReportsAltitudeViaFallback()
    {
        var reg = MakeRegistry(latRef: 52.0, lonRef: -1.0);
        reg.Ingest("GP01xxxx", now: 10.0);
        var ac = reg.Aircraft["abc123"];
        Assert.Null(ac.AltitudeBaro);
        Assert.Equal(37100, ac.AltitudeGeo);
        Assert.Equal(37100, ac.Altitude);
    }

    [Fact]
    public void MlatTicks_RecordedOnIngest()
    {
        var reg = MakeRegistry();
        reg.Ingest("ID01xxxx", now: 1.0, mlatTicks: 1234567890);
        Assert.Equal(1234567890, reg.Aircraft["abc123"].LastSeenMlat);
    }

    [Fact]
    public void TrailPoints_IncludeAltitudeAndNoGap()
    {
        var reg = MakeRegistry(latRef: 52.0, lonRef: -1.0);
        reg.Ingest("AP01xxxx", now: 1.0);
        reg.Ingest("AP01yyyy", now: 2.0);
        var snap = reg.Snapshot(now: 2.1);
        var trail = snap.Aircraft[0].Trail;
        Assert.NotEmpty(trail);
        foreach (var pt in trail)
        {
            Assert.Equal(37000, pt.Altitude);
            Assert.False(pt.Gap);
        }
    }

    [Fact]
    public void Trail_FlagsGapAfterSignalSilence()
    {
        var reg = MakeRegistry(latRef: 52.0, lonRef: -1.0);
        reg.Ingest("AP01aaaa", now: 100.0);
        reg.Ingest("AP01bbbb", now: 103.0); // 3 s — no gap
        reg.Ingest("AP01cccc", now: 118.0); // 15 s — gap
        reg.Ingest("AP01dddd", now: 120.0); // 2 s — no gap
        var trail = reg.Aircraft["abc123"].Trail;
        Assert.Equal(4, trail.Count);
        Assert.False(trail[0].Gap);
        Assert.False(trail[1].Gap);
        Assert.True(trail[2].Gap);
        Assert.False(trail[3].Gap);
    }

    [Fact]
    public void TeleportGuard_RejectsImplausibleJump()
    {
        // Two different positions: first fixes at (52.1, -1.1), then a
        // second that would be 350 km away in 1 s — must be rejected.
        var calls = 0;
        var reg = new TestAircraftRegistry(latRef: 52.0, lonRef: -1.0, decoder: FakeDecoder.Decoder)
        {
            PositionOverride = (_a, _cf, _cl, _co, _s) =>
            {
                calls++;
                return calls == 1 ? (52.1, -1.1) : (55.0, 3.0);
            },
        };
        reg.Ingest("AP01aaaa", now: 100.0);
        reg.Ingest("AP02bbbb", now: 101.0); // odd frame, triggers resolve
        var ac = reg.Aircraft["abc123"];
        Assert.Equal(52.1, ac.Lat);
        Assert.Equal(-1.1, ac.Lon);
    }

    [Fact]
    public void Snapshot_ExposesBothAltitudeFields()
    {
        var reg = MakeRegistry(latRef: 52.0, lonRef: -1.0);
        reg.Ingest("AP01xxxx", now: 1.0);
        reg.Ingest("GP01xxxx", now: 1.1);
        var entry = reg.Snapshot(now: 1.2).Aircraft[0];
        Assert.Equal(37000, entry.Altitude);
        Assert.Equal(37000, entry.AltitudeBaro);
        Assert.Equal(37100, entry.AltitudeGeo);
    }

    [Fact]
    public void UnknownDf_IsIgnored()
    {
        var reg = MakeRegistry();
        Assert.False(reg.Ingest("XXXXxxxx", now: 1.0));
        Assert.Empty(reg.Aircraft);
    }

    [Fact]
    public void DeadReckon_ExtrapolatesStalePositions()
    {
        var reg = MakeRegistry();
        var ac = new Aircraft { Icao = "abc123" };
        ac.Callsign = "TEST1";
        ac.Lat = 52.0;
        ac.Lon = -1.0;
        ac.Speed = 480.0;
        ac.Track = 90.0;
        ac.AltitudeBaro = 30000;
        ac.LastSeen = 100.0;
        ac.LastPositionTime = 100.0;
        ac.OnGround = false;
        InjectAircraft(reg, ac);

        var snap = reg.Snapshot(now: 110.0);
        var entry = snap.Aircraft[0];
        Assert.True(entry.PositionStale);
        Assert.Equal(52.0, entry.Lat!.Value, 4);
        Assert.InRange(entry.Lon!.Value - (-1.0), 0.02, 0.05);
    }

    [Fact]
    public void DeadReckon_SkippedForFreshPositions()
    {
        var reg = MakeRegistry();
        var ac = new Aircraft { Icao = "abc123" };
        ac.Callsign = "TEST1";
        ac.Lat = 52.0;
        ac.Lon = -1.0;
        ac.Speed = 480.0;
        ac.Track = 90.0;
        ac.AltitudeBaro = 30000;
        ac.LastSeen = 100.0;
        ac.LastPositionTime = 100.0;
        ac.OnGround = false;
        InjectAircraft(reg, ac);

        var snapFresh = reg.Snapshot(now: 100.5);
        Assert.False(snapFresh.Aircraft[0].PositionStale);
        Assert.Equal(-1.0, snapFresh.Aircraft[0].Lon);

        // 50 s on — still extrapolating (keep aircraft alive in registry)
        ac.LastSeen = 150.0;
        var snapLong = reg.Snapshot(now: 150.0);
        Assert.True(snapLong.Aircraft[0].PositionStale);
        Assert.True(snapLong.Aircraft[0].Lon > -1.0);
    }

    [Fact]
    public void DeadReckon_ResumeClearsTrailOnLargeCorrection()
    {
        // First fix goes to (52.0, -1.0). Twenty-five seconds later, a new
        // fix arrives at (52.08, -1.0) — ~9 km north of where eastward
        // extrapolation would place it. That trips the reset.
        var calls = 0;
        var reg = new TestAircraftRegistry(latRef: 52.0, lonRef: -1.0, decoder: FakeDecoder.Decoder)
        {
            PositionOverride = (_a, _cf, _cl, _co, _s) =>
            {
                calls++;
                return calls == 1 ? (52.0, -1.0) : (52.08, -1.0);
            },
        };
        reg.Ingest("AP01aaaa", now: 100.0);
        reg.Aircraft["abc123"].Speed = 480.0;
        reg.Aircraft["abc123"].Track = 90.0;
        Assert.Single(reg.Aircraft["abc123"].Trail);
        reg.Ingest("AP02bbbb", now: 125.0);
        var trail = reg.Aircraft["abc123"].Trail;
        Assert.Single(trail);
        Assert.Equal(52.08, trail[0].Lat);
        Assert.Equal(-1.0, trail[0].Lon);
    }

    [Fact]
    public void DeadReckon_SkippedOnGround()
    {
        var reg = MakeRegistry();
        var ac = new Aircraft { Icao = "abc123" };
        ac.Callsign = "TEST1";
        ac.Lat = 52.0;
        ac.Lon = -1.0;
        ac.Speed = 15.0;
        ac.Track = 90.0;
        ac.AltitudeBaro = 0;
        ac.OnGround = true;
        ac.LastSeen = 100.0;
        ac.LastPositionTime = 100.0;
        InjectAircraft(reg, ac);

        var snap = reg.Snapshot(now: 110.0);
        Assert.False(snap.Aircraft[0].PositionStale);
        Assert.Equal(52.0, snap.Aircraft[0].Lat);
        Assert.Equal(-1.0, snap.Aircraft[0].Lon);
    }

    [Fact]
    public void OnNewAircraft_FiresOncePerFreshEntry()
    {
        var reg = MakeRegistry();
        var seen = new List<(string Icao, double Ts)>();
        reg.OnNewAircraft += (icao, ts) => seen.Add((icao, ts));
        reg.Ingest("ID01aaaa", now: 100.0);
        reg.Ingest("ID01bbbb", now: 101.0);
        Assert.Single(seen);
        Assert.Equal("abc123", seen[0].Icao);
    }

    [Fact]
    public void OnPosition_FiresPerAcceptedFix()
    {
        var reg = MakeRegistry(latRef: 52.0, lonRef: -1.0);
        var seen = new List<(double Lat, double Lon)>();
        reg.OnPosition += (lat, lon) => seen.Add((lat, lon));
        reg.Ingest("AP01aaaa", now: 10.0);
        reg.Ingest("AP01bbbb", now: 11.0);
        Assert.Equal(2, seen.Count);
        foreach (var (lat, lon) in seen)
        {
            Assert.Equal(52.1, lat, 6);
            Assert.Equal(-1.1, lon, 6);
        }
    }

    [Fact]
    public void OnPosition_DoesNotFireForNonPositional()
    {
        var reg = MakeRegistry(latRef: 52.0, lonRef: -1.0);
        var seen = new List<(double, double)>();
        reg.OnPosition += (lat, lon) => seen.Add((lat, lon));
        reg.Ingest("VL01xxxx", now: 1.0); // velocity
        reg.Ingest("AC01xxxx", now: 2.0); // DF4 altcode
        reg.Ingest("SQ01xxxx", now: 3.0); // DF5 squawk
        Assert.Empty(seen);
    }

    [Fact]
    public void OnPosition_SwallowsExceptions()
    {
        var reg = MakeRegistry(latRef: 52.0, lonRef: -1.0);
        reg.OnPosition += (_, _) => throw new InvalidOperationException("boom");
        Assert.True(reg.Ingest("AP01aaaa", now: 1.0));
    }

    // Helper: inject an aircraft directly into the registry's internal dict
    // for tests that pre-seed state without going through Ingest.
    private static void InjectAircraft(TestAircraftRegistry reg, Aircraft ac)
    {
        // The internal dict is exposed via the IReadOnlyDictionary. For tests
        // we need write access — use reflection to reach the private _aircraft
        // field. Acceptable for a test helper.
        var field = typeof(AircraftRegistry).GetField("_aircraft",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_aircraft field missing");
        var dict = (Dictionary<string, Aircraft>)field.GetValue(reg)!;
        dict[ac.Icao] = ac;
    }
}
