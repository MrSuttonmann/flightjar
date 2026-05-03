using FlightJar.Core.State;

namespace FlightJar.Core.Tests.State;

public sealed class PeerMergeTests
{
    [Fact]
    public void Combine_FillsNullCallsignFromPeer()
    {
        var local = new SnapshotAircraft { Icao = "abc123" };
        var peer = new SnapshotAircraft { Icao = "abc123", Callsign = "BAW123" };

        var merged = PeerMerge.Combine(local, peer);

        Assert.Equal("BAW123", merged.Callsign);
    }

    [Fact]
    public void Combine_PrefersLocalCallsignWhenBothPresent()
    {
        var local = new SnapshotAircraft { Icao = "abc123", Callsign = "LOCAL1" };
        var peer = new SnapshotAircraft { Icao = "abc123", Callsign = "PEERS1" };

        var merged = PeerMerge.Combine(local, peer);

        Assert.Equal("LOCAL1", merged.Callsign);
    }

    [Fact]
    public void Combine_FillsRouteEnrichmentFromPeer()
    {
        // Local: cache miss on adsbdb (no origin/destination yet).
        // Peer: already has origin/destination from its own cache.
        var local = new SnapshotAircraft
        {
            Icao = "abc123",
            Callsign = "BAW123",
            Lat = 51.5,
            Lon = -0.1,
        };
        var peer = new SnapshotAircraft
        {
            Icao = "abc123",
            Callsign = "BAW123",
            Origin = "EGLL",
            Destination = "KJFK",
            Operator = "British Airways",
            OperatorIata = "BA",
        };

        var merged = PeerMerge.Combine(local, peer);

        Assert.Equal("EGLL", merged.Origin);
        Assert.Equal("KJFK", merged.Destination);
        Assert.Equal("British Airways", merged.Operator);
        Assert.Equal("BA", merged.OperatorIata);
        // Local position is preserved.
        Assert.Equal(51.5, merged.Lat);
        Assert.Equal(-0.1, merged.Lon);
    }

    [Fact]
    public void Combine_KeepsLocalPositionEvenWhenPeerHasOne()
    {
        var local = new SnapshotAircraft { Icao = "abc123", Lat = 51.5, Lon = -0.1 };
        var peer = new SnapshotAircraft { Icao = "abc123", Lat = 40.7, Lon = -74.0 };

        var merged = PeerMerge.Combine(local, peer);

        Assert.Equal(51.5, merged.Lat);
        Assert.Equal(-0.1, merged.Lon);
    }

    [Fact]
    public void Combine_FillsPositionFromPeerWhenLocalHasNone()
    {
        // Aircraft heard locally (callsign decoded) but no position fix yet
        // — peer has a fix, so we surface it on the map.
        var local = new SnapshotAircraft { Icao = "abc123", Callsign = "BAW123" };
        var peer = new SnapshotAircraft { Icao = "abc123", Lat = 40.7, Lon = -74.0 };

        var merged = PeerMerge.Combine(local, peer);

        Assert.Equal(40.7, merged.Lat);
        Assert.Equal(-74.0, merged.Lon);
        Assert.Equal("BAW123", merged.Callsign);
    }

    [Fact]
    public void Combine_KeepsLocalReceiverSpecificFields()
    {
        // Receiver-specific fields (distance, signal, msg count, trail)
        // describe THIS receiver's reception — they must never be
        // overwritten by a peer's values.
        var trail = new List<SnapshotTrailPoint>
        {
            new(51.5, -0.1, 30000, 450, false),
        };
        var local = new SnapshotAircraft
        {
            Icao = "abc123",
            DistanceKm = 12.3,
            SignalPeak = 240,
            MsgCount = 1500,
            LastSeen = 1000.0,
            FirstSeen = 500.0,
            Trail = trail,
        };
        var peer = new SnapshotAircraft
        {
            Icao = "abc123",
            DistanceKm = 999.0, // peer's distance, ignore
            SignalPeak = 10,
            MsgCount = 7,
            LastSeen = 2000.0,
            FirstSeen = 1500.0,
            Trail = new List<SnapshotTrailPoint> { new(0, 0, 0, 0, false) },
        };

        var merged = PeerMerge.Combine(local, peer);

        Assert.Equal(12.3, merged.DistanceKm);
        Assert.Equal((byte)240, merged.SignalPeak);
        Assert.Equal(1500, merged.MsgCount);
        Assert.Equal(1000.0, merged.LastSeen);
        Assert.Equal(500.0, merged.FirstSeen);
        Assert.Same(trail, merged.Trail);
    }

    [Fact]
    public void Combine_ResultIsLocalNotPeer()
    {
        // When both receivers see the aircraft, the user has direct
        // contact — the merged record should render as a local aircraft
        // (no peer styling), even if the peer side was tagged.
        var local = new SnapshotAircraft { Icao = "abc123", Callsign = "BAW123" };
        var peer = new SnapshotAircraft { Icao = "abc123", Peer = true };

        var merged = PeerMerge.Combine(local, peer);

        Assert.Null(merged.Peer);
    }

    [Fact]
    public void ExtendAirports_AddsPeerOnlyAircraftAirportsToMap()
    {
        // Bug repro: a peer-only aircraft arrives at the receiving instance
        // with Origin/Destination/OriginInfo/DestInfo set (peer enriched it
        // upstream), but the receiver's snapshot.Airports map was built
        // before the merge ran and so contained only locally-tracked
        // aircraft's airports. The frontend reads airports[a.origin] for
        // route name + progress + METAR, so peer-only aircraft rendered
        // only the bare codes.
        var peerOnly = new SnapshotAircraft
        {
            Icao = "abc123",
            Callsign = "BAW123",
            Peer = true,
            Origin = "EGLL",
            Destination = "KJFK",
            OriginInfo = new SnapshotAirport("EGLL", "London Heathrow", "London", "GB", 51.4706, -0.4619),
            DestInfo = new SnapshotAirport("KJFK", "John F Kennedy Intl", "New York", "US", 40.6413, -73.7781),
        };

        var airports = PeerMerge.ExtendAirports(existing: null, aircraft: new[] { peerOnly });

        Assert.True(airports.ContainsKey("EGLL"));
        Assert.True(airports.ContainsKey("KJFK"));
        Assert.Equal("London Heathrow", airports["EGLL"].Name);
        Assert.Equal(51.4706, airports["EGLL"].Lat);
        Assert.Equal(-0.4619, airports["EGLL"].Lon);
        Assert.Equal("John F Kennedy Intl", airports["KJFK"].Name);
    }

    [Fact]
    public void ExtendAirports_PreservesExistingEntriesIncludingMetar()
    {
        // Local aircraft's airports were already added to the map by
        // EnrichSnapshot, with METAR populated from the local cache.
        // Re-running the extension after the peer merge must not clobber
        // those entries — they hold METAR data the static helper has no
        // way to recompute.
        var existing = new Dictionary<string, SnapshotAirportRef>(StringComparer.Ordinal)
        {
            ["EGLL"] = new SnapshotAirportRef("London Heathrow", 51.4706, -0.4619)
            {
                Metar = new SnapshotMetar { Raw = "EGLL 010000Z 27010KT" },
            },
        };
        var ac = new SnapshotAircraft
        {
            Icao = "abc123",
            Origin = "EGLL",
            // A different OriginInfo for the same ICAO — must not overwrite.
            OriginInfo = new SnapshotAirport("EGLL", "Different Name", null, null, 0, 0),
        };

        var airports = PeerMerge.ExtendAirports(existing, new[] { ac });

        Assert.Equal("London Heathrow", airports["EGLL"].Name);
        Assert.Equal("EGLL 010000Z 27010KT", airports["EGLL"].Metar?.Raw);
    }

    [Fact]
    public void ExtendAirports_SkipsAircraftWithNullAirportInfo()
    {
        // Aircraft with no OriginInfo/DestInfo (e.g., adsbdb cache miss
        // and no peer enrichment yet) must not crash the helper or
        // produce empty entries.
        var ac = new SnapshotAircraft { Icao = "abc123" };

        var airports = PeerMerge.ExtendAirports(existing: null, aircraft: new[] { ac });

        Assert.Empty(airports);
    }

    [Fact]
    public void Combine_TakesSeenByOthersFromPeer()
    {
        // The relay computes seen_by_others per-recipient (it knows the
        // contributor set) and stamps it on the peer record. Combine must
        // surface that value on the merged record so the detail panel can
        // render the count even when the aircraft is locally observed.
        var local = new SnapshotAircraft { Icao = "abc123", Callsign = "BAW123" };
        var peer = new SnapshotAircraft { Icao = "abc123", SeenByOthers = 3 };

        var merged = PeerMerge.Combine(local, peer);

        Assert.Equal(3, merged.SeenByOthers);
    }

    [Fact]
    public void Combine_FillsAltitudeAndVelocityFromPeer()
    {
        var local = new SnapshotAircraft { Icao = "abc123", Callsign = "BAW123" };
        var peer = new SnapshotAircraft
        {
            Icao = "abc123",
            Altitude = 35000,
            AltitudeBaro = 35000,
            Track = 270.0,
            Speed = 460.0,
            Vrate = -200,
            Squawk = "1234",
        };

        var merged = PeerMerge.Combine(local, peer);

        Assert.Equal(35000, merged.Altitude);
        Assert.Equal(35000, merged.AltitudeBaro);
        Assert.Equal(270.0, merged.Track);
        Assert.Equal(460.0, merged.Speed);
        Assert.Equal(-200, merged.Vrate);
        Assert.Equal("1234", merged.Squawk);
    }
}
