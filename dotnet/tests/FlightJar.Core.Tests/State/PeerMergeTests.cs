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
