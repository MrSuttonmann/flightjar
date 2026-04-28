using FlightJar.Core.State.Persistence;
using FlightJar.Decoder.ModeS;

namespace FlightJar.Core.State;

/// <summary>
/// Owns the per-aircraft state and produces snapshots for readers. Mirrors
/// <c>app/aircraft.py</c>:<c>AircraftRegistry</c>.
///
/// Thread safety: intended for single-writer operation (one ingest thread
/// calls <see cref="Ingest(string, double, long?, byte?)"/>). Readers get
/// a snapshot via <see cref="Snapshot(double)"/>; the produced record tree
/// is immutable and safe to share.
/// </summary>
public class AircraftRegistry
{
    public const double PositionPairMaxAge = 10.0;
    public const int TrailMaxPoints = 300;
    public const double AircraftTimeout = 60.0;
    public const double PersistMaxAge = 600.0;
    public const double DeadReckonMinAge = 1.5;
    public const double SignalLostMinAge = 10.0;
    public const double DeadReckonResumeResetKm = 5.0;

    /// <summary>Forwarding alias for <see cref="CommBFusion.CommBMaxAge"/>;
    /// kept on the registry so existing callers don't need to know about
    /// the fusion module.</summary>
    public const double CommBMaxAge = CommBFusion.CommBMaxAge;

    private readonly Dictionary<string, Aircraft> _aircraft = new();
    private readonly Func<string, DecodedMessage?> _decoder;
    private readonly Func<double> _clock;

    public double? LatRef { get; }
    public double? LonRef { get; }
    public ReceiverInfo? Receiver { get; }
    public string? SiteName { get; }
    public IAircraftDb? AircraftDb { get; }

    /// <summary>Fires once when a brand-new tail is first recorded.
    /// Multicast — multiple subscribers can attach without clobbering
    /// each other.</summary>
    public event Action<string, double>? OnNewAircraft;

    /// <summary>Fires after each accepted position fix.</summary>
    public event Action<double, double>? OnPosition;

    public AircraftRegistry(
        double? latRef = null,
        double? lonRef = null,
        ReceiverInfo? receiver = null,
        string? siteName = null,
        IAircraftDb? aircraftDb = null,
        Func<string, DecodedMessage?>? decoder = null,
        Func<double>? clock = null)
    {
        LatRef = latRef;
        LonRef = lonRef;
        Receiver = receiver;
        SiteName = siteName;
        AircraftDb = aircraftDb;
        _decoder = decoder ?? MessageDecoder.Decode;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
    }

    public IReadOnlyDictionary<string, Aircraft> Aircraft => _aircraft;

    /// <summary>
    /// Decode one Mode S message and update state. Returns true if the
    /// message was accepted (known DF with a recoverable ICAO).
    /// </summary>
    public bool Ingest(string hex, double? now = null, long? mlatTicks = null, byte? signal = null)
    {
        var decoded = _decoder(hex);
        if (decoded is null)
        {
            return false;
        }
        return IngestDecoded(decoded, now, mlatTicks, signal);
    }

    /// <summary>Test-hook: feed a pre-decoded message directly.</summary>
    public bool IngestDecoded(DecodedMessage decoded, double? now = null, long? mlatTicks = null, byte? signal = null)
    {
        var t = now ?? _clock();
        bool accepted;
        switch (decoded.Df)
        {
            case 17:
            case 18:
                accepted = IngestAdsb(decoded, t, mlatTicks);
                break;
            case 4:
            case 5:
            case 11:
            case 20:
            case 21:
                accepted = IngestSurveillance(decoded, t, mlatTicks);
                break;
            default:
                return false;
        }
        if (accepted && signal is byte sig && decoded.Icao is string icao
            && _aircraft.TryGetValue(icao, out var ac)
            && (ac.SignalPeak is null || sig > ac.SignalPeak))
        {
            ac.SignalPeak = sig;
        }
        return accepted;
    }

    private Aircraft Get(string icao)
    {
        if (_aircraft.TryGetValue(icao, out var ac))
        {
            return ac;
        }
        var now = _clock();
        ac = new Aircraft { Icao = icao, FirstSeen = now };
        _aircraft[icao] = ac;
        try
        {
            OnNewAircraft?.Invoke(icao, now);
        }
        catch
        {
            // Callback failures must not break ingest — the feed keeps flowing.
        }
        return ac;
    }

    private bool IngestAdsb(DecodedMessage r, double now, long? mlatTicks)
    {
        if (!r.CrcValid)
        {
            return false;
        }
        if (r.Icao is not string icao || r.Typecode is not int tc)
        {
            return false;
        }
        var ac = Get(icao);
        ac.LastSeen = now;
        if (mlatTicks is long mt)
        {
            ac.LastSeenMlat = mt;
        }
        ac.MsgCount++;

        if (tc >= 1 && tc <= 4)
        {
            if (r.Callsign is string cs)
            {
                var trimmed = cs.TrimEnd('_', ' ').Trim();
                ac.Callsign = string.IsNullOrEmpty(trimmed) ? null : trimmed;
            }
            if (r.Category is int cat)
            {
                ac.Category = cat;
            }
        }
        else if (tc >= 5 && tc <= 8)
        {
            ac.OnGround = true;
            UpdatePosition(ac, r, now, surface: true);
        }
        else if (tc >= 9 && tc <= 18)
        {
            ac.OnGround = false;
            if (r.Altitude is int alt)
            {
                ac.AltitudeBaro = alt;
            }
            UpdatePosition(ac, r, now, surface: false);
        }
        else if (tc >= 20 && tc <= 22)
        {
            ac.OnGround = false;
            if (r.Altitude is int alt)
            {
                ac.AltitudeGeo = alt;
            }
            UpdatePosition(ac, r, now, surface: false);
        }
        else if (tc == 19)
        {
            var spd = r.Groundspeed ?? r.Airspeed;
            if (spd is double s)
            {
                ac.Speed = s;
            }
            var trk = r.Track ?? r.Heading;
            if (trk is double t)
            {
                ac.Track = t;
            }
            if (r.VerticalRate is int vr)
            {
                ac.Vrate = vr;
            }
        }
        return true;
    }

    private bool IngestSurveillance(DecodedMessage r, double now, long? mlatTicks)
    {
        if (r.Icao is not string icao)
        {
            return false;
        }
        var ac = Get(icao);
        ac.LastSeen = now;
        if (mlatTicks is long mt)
        {
            ac.LastSeenMlat = mt;
        }
        ac.MsgCount++;
        if (r.Altitude is int alt)
        {
            // Surveillance altcode is always barometric.
            ac.AltitudeBaro = alt;
        }
        if (r.Squawk is string sq)
        {
            ac.Squawk = sq;
        }
        ApplyCommB(ac, r, now);
        return true;
    }

    /// <summary>
    /// Fan out a disambiguated Comm-B decode into the matching aircraft-state
    /// fields. Only fields from the single matched register are touched, so a
    /// BDS 4,4 decode cannot clobber a previous BDS 5,0 decode's TAS etc.
    /// </summary>
    private static void ApplyCommB(Aircraft ac, DecodedMessage r, double now)
    {
        switch (r.Bds)
        {
            case "4,0":
                ac.SelectedAltitudeMcpFt = r.SelectedAltitudeMcpFt;
                ac.SelectedAltitudeFmsFt = r.SelectedAltitudeFmsFt;
                if (r.QnhHpa is double qnh)
                {
                    ac.QnhHpa = qnh;
                }
                ac.Bds40At = now;
                break;
            case "4,4":
                ac.WindSpeedKt = r.WindSpeedKt;
                ac.WindDirectionDeg = r.WindDirectionDeg;
                if (r.StaticAirTemperatureC is double sat)
                {
                    ac.StaticAirTemperatureC = sat;
                }
                ac.StaticPressureHpa = r.StaticPressureHpa;
                ac.Turbulence = r.Turbulence;
                ac.HumidityPct = r.HumidityPct;
                ac.Bds44At = now;
                break;
            case "5,0":
                ac.RollDeg = r.RollDeg;
                ac.TrueTrackDeg = r.TrueTrackDeg;
                ac.GroundspeedKt = r.GroundspeedKt;
                ac.TrackRateDegPerS = r.TrackRateDegPerS;
                ac.TrueAirspeedKt = r.TrueAirspeedKt;
                ac.Bds50At = now;
                break;
            case "6,0":
                ac.MagneticHeadingDeg = r.MagneticHeadingDeg;
                ac.IndicatedAirspeedKt = r.IndicatedAirspeedKt;
                if (r.Mach is double m)
                {
                    ac.Mach = m;
                }
                ac.BaroVerticalRateFpm = r.BaroVerticalRateFpm;
                ac.InertialVerticalRateFpm = r.InertialVerticalRateFpm;
                ac.Bds60At = now;
                break;
        }
    }

    private void UpdatePosition(Aircraft ac, DecodedMessage r, double now, bool surface)
    {
        if (r.CprFormat is not int oe || r.CprLat is not int cprLat || r.CprLon is not int cprLon)
        {
            return;
        }
        if (oe == 0)
        {
            ac.EvenCprLat = cprLat;
            ac.EvenCprLon = cprLon;
            ac.EvenT = now;
        }
        else if (oe == 1)
        {
            ac.OddCprLat = cprLat;
            ac.OddCprLon = cprLon;
            ac.OddT = now;
        }

        var pos = ResolveNewPosition(ac, oe, cprLat, cprLon, surface);
        if (pos is null)
        {
            return;
        }

        var (newLat, newLon) = pos.Value;
        if (newLat is < -90 or > 90 || newLon is < -180 or > 180)
        {
            return;
        }

        // Teleport guard.
        if (ac.Lat is double oldLat && ac.Lon is double oldLon)
        {
            var distKm = GeoMath.ApproxDistanceKm(oldLat, oldLon, newLat, newLon);
            var elapsedS = ac.LastPositionTime > 0 ? now - ac.LastPositionTime : 0;
            var maxPlausibleKm = Math.Max(10.0, elapsedS * 0.5);
            if (distKm > maxPlausibleKm)
            {
                return;
            }
        }

        var elapsedSincePos = ac.LastPositionTime > 0 ? now - ac.LastPositionTime : 0;
        var gapFromPrev = !surface && ac.LastPositionTime > 0 && elapsedSincePos > SignalLostMinAge;

        // Dead-reckoning correction check — if the plane went silent long
        // enough that we'd been extrapolating, compare the new fix to the
        // extrapolated position. A large delta means the dashed dead-reckon
        // line was misleading; clear the trail so the next coloured segment
        // starts from this fix.
        if (!surface && elapsedSincePos > DeadReckonMinAge
            && ac.Lat is double prevLat && ac.Lon is double prevLon
            && ac.Speed is double speed && ac.Track is double track)
        {
            var (predLat, predLon) = DeadReckoning.Project(prevLat, prevLon, track, speed, elapsedSincePos);
            var errorKm = GeoMath.ApproxDistanceKm(predLat, predLon, newLat, newLon);
            if (errorKm > DeadReckonResumeResetKm)
            {
                ac.Trail.Clear();
                ac.TrailRevision++;
            }
        }

        ac.Lat = newLat;
        ac.Lon = newLon;
        ac.LastPositionTime = now;

        var rounded = new TrailPoint(
            Lat: Math.Round(newLat, 5),
            Lon: Math.Round(newLon, 5),
            Altitude: ac.Altitude,
            Speed: ac.Speed,
            Timestamp: now,
            Gap: gapFromPrev);
        AppendTrail(ac, rounded);

        try
        {
            OnPosition?.Invoke(newLat, newLon);
        }
        catch
        {
            // Callback failures don't propagate — the feed must keep flowing.
        }
    }

    /// <summary>
    /// Run the 3-stage CPR fallback chain: global pair → local-vs-last-known
    /// → local-vs-receiver-ref. Overridable so tests can drive a deterministic
    /// resolver without needing to craft real wire bytes.
    /// </summary>
    protected virtual (double Lat, double Lon)? ResolveNewPosition(
        Aircraft ac, int cprFormat, int cprLat, int cprLon, bool surface)
    {
        var pairFresh = ac.EvenCprLat is not null && ac.OddCprLat is not null
                        && Math.Abs(ac.EvenT - ac.OddT) < PositionPairMaxAge;
        if (pairFresh && (!surface || LatRef is not null))
        {
            var evenIsNewer = ac.EvenT >= ac.OddT;
            if (surface && LatRef is double sLat && LonRef is double sLon)
            {
                var pair = Cpr.SurfacePositionPair(
                    ac.EvenCprLat!.Value, ac.EvenCprLon!.Value,
                    ac.OddCprLat!.Value, ac.OddCprLon!.Value,
                    sLat, sLon, evenIsNewer);
                if (pair is not null)
                {
                    return pair;
                }
            }
            else if (!surface)
            {
                var pair = Cpr.AirbornePositionPair(
                    ac.EvenCprLat!.Value, ac.EvenCprLon!.Value,
                    ac.OddCprLat!.Value, ac.OddCprLon!.Value,
                    evenIsNewer);
                if (pair is not null)
                {
                    return pair;
                }
            }
        }

        if (ac.Lat is double lastLat && ac.Lon is double lastLon)
        {
            return surface
                ? Cpr.SurfacePositionWithRef(cprFormat, cprLat, cprLon, lastLat, lastLon)
                : Cpr.AirbornePositionWithRef(cprFormat, cprLat, cprLon, lastLat, lastLon);
        }

        if (LatRef is double rLat && LonRef is double rLon)
        {
            return surface
                ? Cpr.SurfacePositionWithRef(cprFormat, cprLat, cprLon, rLat, rLon)
                : Cpr.AirbornePositionWithRef(cprFormat, cprLat, cprLon, rLat, rLon);
        }

        return null;
    }

    private static void AppendTrail(Aircraft ac, TrailPoint p)
    {
        ac.Trail.Add(p);
        if (ac.Trail.Count > TrailMaxPoints)
        {
            ac.Trail.RemoveAt(0);
        }
        ac.TrailRevision++;
    }

    /// <summary>Serialise the registry to a JSON-friendly payload for persistence.</summary>
    public StateSnapshotPayload Serialize(double? now = null)
    {
        var payload = new StateSnapshotPayload
        {
            Version = 1,
            SavedAt = now ?? _clock(),
        };
        foreach (var (icao, ac) in _aircraft)
        {
            payload.Aircraft[icao] = new PersistedAircraft
            {
                Icao = ac.Icao,
                Callsign = ac.Callsign,
                Category = ac.Category,
                Lat = ac.Lat,
                Lon = ac.Lon,
                AltitudeBaro = ac.AltitudeBaro,
                AltitudeGeo = ac.AltitudeGeo,
                Track = ac.Track,
                Speed = ac.Speed,
                Vrate = ac.Vrate,
                Squawk = ac.Squawk,
                OnGround = ac.OnGround,
                LastSeen = ac.LastSeen,
                LastPositionTime = ac.LastPositionTime,
                FirstSeen = ac.FirstSeen,
                LastSeenMlat = ac.LastSeenMlat,
                MsgCount = ac.MsgCount,
                SignalPeak = ac.SignalPeak,
                Trail = ac.Trail.Select(t =>
                    new PersistedTrailPoint(t.Lat, t.Lon, t.Altitude, t.Speed, t.Timestamp, t.Gap)).ToList(),
            };
        }
        return payload;
    }

    /// <summary>Restore registry state from a previous <see cref="Serialize"/> output.
    /// Entries whose <c>LastSeen</c> is older than <see cref="PersistMaxAge"/> relative
    /// to <paramref name="now"/> are dropped. Returns the count loaded.</summary>
    public int Restore(StateSnapshotPayload payload, double? now = null)
    {
        if (payload.Version != 1)
        {
            return 0;
        }
        var cutoff = (now ?? _clock()) - PersistMaxAge;
        var loaded = 0;
        foreach (var (icao, entry) in payload.Aircraft)
        {
            if (entry.LastSeen < cutoff)
            {
                continue;
            }
            var ac = new Aircraft
            {
                Icao = icao,
                Callsign = entry.Callsign,
                Category = entry.Category,
                Lat = entry.Lat,
                Lon = entry.Lon,
                AltitudeBaro = entry.AltitudeBaro,
                AltitudeGeo = entry.AltitudeGeo,
                Track = entry.Track,
                Speed = entry.Speed,
                Vrate = entry.Vrate,
                Squawk = entry.Squawk,
                OnGround = entry.OnGround,
                LastSeen = entry.LastSeen,
                LastPositionTime = entry.LastPositionTime,
                FirstSeen = entry.FirstSeen,
                LastSeenMlat = entry.LastSeenMlat,
                MsgCount = entry.MsgCount,
                SignalPeak = entry.SignalPeak,
            };
            foreach (var p in entry.Trail)
            {
                ac.Trail.Add(new TrailPoint(p.Lat, p.Lon, p.Altitude, p.Speed, p.Timestamp, p.Gap));
            }
            _aircraft[icao] = ac;
            loaded++;
        }
        return loaded;
    }

    public void Cleanup(double now)
    {
        var stale = new List<string>();
        foreach (var (icao, ac) in _aircraft)
        {
            if (now - ac.LastSeen > AircraftTimeout)
            {
                stale.Add(icao);
            }
        }
        foreach (var icao in stale)
        {
            _aircraft.Remove(icao);
            _trailCache.Remove(icao);
        }
    }

    public RegistrySnapshot Snapshot(double? now = null)
    {
        var t = now ?? _clock();
        var refLat = Receiver?.Lat;
        var refLon = Receiver?.Lon;

        var aircraft = new List<SnapshotAircraft>(_aircraft.Count);
        var positioned = 0;

        foreach (var ac in _aircraft.Values)
        {
            // Skip tails with no callsign AND no position/speed/altitude — not enough to list.
            if (ac.Callsign is null && ac.Lat is null && ac.Speed is null && ac.Altitude is null)
            {
                continue;
            }

            var dispLat = ac.Lat;
            var dispLon = ac.Lon;
            var positionStale = false;

            // Dead-reckon: project stale airborne positions forward so the
            // marker slides smoothly between position reports.
            if (ac.Lat is double acLat && ac.Lon is double acLon
                && !ac.OnGround
                && ac.Speed is double speed && ac.Track is double track
                && ac.LastPositionTime > 0)
            {
                var age = t - ac.LastPositionTime;
                if (age > DeadReckonMinAge)
                {
                    var (projLat, projLon) = DeadReckoning.Project(acLat, acLon, track, speed, age);
                    dispLat = projLat;
                    dispLon = projLon;
                    positionStale = true;
                }
            }

            double? distanceKm = null;
            if (refLat is double rl && refLon is double ro
                && dispLat is double dl && dispLon is double dn)
            {
                var dlat = Deg2Rad(dl - rl);
                var dlon = Deg2Rad(dn - ro) * Math.Cos(Deg2Rad(rl));
                distanceKm = Math.Round(Math.Sqrt(dlat * dlat + dlon * dlon) * GeoMath.EarthKm, 2);
            }
            if (dispLat is not null)
            {
                positioned++;
            }

            var dbInfo = AircraftDb?.Lookup(ac.Icao);

            // Build the serialised trail list. Reuse the cached version when
            // the trail hasn't mutated since the last snapshot (trail_rev unchanged).
            var trail = BuildSnapshotTrail(ac);

            aircraft.Add(new SnapshotAircraft
            {
                Icao = ac.Icao,
                Callsign = ac.Callsign,
                Category = ac.Category,
                Registration = dbInfo?.Registration,
                TypeIcao = dbInfo?.TypeIcao,
                TypeLong = dbInfo?.TypeLong,
                Lat = dispLat,
                Lon = dispLon,
                PositionStale = positionStale,
                Altitude = ac.Altitude,
                AltitudeBaro = ac.AltitudeBaro,
                AltitudeGeo = ac.AltitudeGeo,
                Track = ac.Track,
                Speed = ac.Speed,
                Vrate = ac.Vrate,
                Squawk = ac.Squawk,
                Emergency = EmergencySquawks.LookupLabel(ac.Squawk),
                OnGround = ac.OnGround,
                LastSeen = ac.LastSeen,
                FirstSeen = ac.FirstSeen > 0 ? ac.FirstSeen : null,
                SignalPeak = ac.SignalPeak,
                MsgCount = ac.MsgCount,
                DistanceKm = distanceKm,
                CommB = CommBFusion.Build(ac, t),
                Trail = trail,
            });
        }

        aircraft.Sort((a, b) => b.LastSeen.CompareTo(a.LastSeen));

        return new RegistrySnapshot(
            Now: t,
            Count: aircraft.Count,
            Positioned: positioned,
            Receiver: Receiver,
            SiteName: SiteName,
            Aircraft: aircraft);
    }

    // Per-aircraft cache of the snapshot trail projection, keyed on TrailRevision.
    private readonly Dictionary<string, (int Rev, IReadOnlyList<SnapshotTrailPoint> Points)> _trailCache = new();

    private IReadOnlyList<SnapshotTrailPoint> BuildSnapshotTrail(Aircraft ac)
    {
        if (_trailCache.TryGetValue(ac.Icao, out var cached) && cached.Rev == ac.TrailRevision)
        {
            return cached.Points;
        }
        var projected = new SnapshotTrailPoint[ac.Trail.Count];
        for (var i = 0; i < ac.Trail.Count; i++)
        {
            var p = ac.Trail[i];
            projected[i] = new SnapshotTrailPoint(p.Lat, p.Lon, p.Altitude, p.Speed, p.Gap);
        }
        _trailCache[ac.Icao] = (ac.TrailRevision, projected);
        return projected;
    }

    private static double Deg2Rad(double d) => d * Math.PI / 180.0;
}
