namespace FlightJar.Core.State;

/// <summary>
/// Combine a locally-observed aircraft with a peer's snapshot of the
/// same ICAO24 additively — local data takes precedence wherever both
/// sides have a value, and peer data fills any null gaps. Receiver-
/// specific fields (distance, signal, trail, Comm-B, MLAT-stamped
/// counters) always stay local: they describe THIS receiver's
/// reception, not anything intrinsic to the aircraft.
/// </summary>
public static class PeerMerge
{
    /// <summary>
    /// Extends the snapshot's airports map with origin/destination
    /// references contributed by each aircraft's <c>OriginInfo</c> /
    /// <c>DestInfo</c>. Existing entries are preserved unchanged
    /// (their <c>Metar</c> slot may already have been populated by
    /// the local enrichment pass); new entries are added with
    /// name + coords only. METAR for newly-referenced airports is
    /// the caller's concern — this helper is purely about identity.
    /// </summary>
    /// <remarks>
    /// The frontend reads route names, progress geometry, and METAR
    /// from <c>RegistrySnapshot.Airports</c> keyed on ICAO code.
    /// Without this extension step, peer-only aircraft (whose
    /// origin/destination weren't seen by any locally-tracked flight)
    /// would render only their bare airport codes.
    /// </remarks>
    public static Dictionary<string, SnapshotAirportRef> ExtendAirports(
        IReadOnlyDictionary<string, SnapshotAirportRef>? existing,
        IEnumerable<SnapshotAircraft> aircraft)
    {
        var airports = existing is null
            ? new Dictionary<string, SnapshotAirportRef>(StringComparer.Ordinal)
            : new Dictionary<string, SnapshotAirportRef>(existing, StringComparer.Ordinal);
        foreach (var ac in aircraft)
        {
            Add(airports, ac.OriginInfo);
            Add(airports, ac.DestInfo);
        }
        return airports;

        static void Add(Dictionary<string, SnapshotAirportRef> map, SnapshotAirport? info)
        {
            if (info is null || map.ContainsKey(info.Icao)) return;
            map[info.Icao] = new SnapshotAirportRef(info.Name, info.Lat, info.Lon);
        }
    }

    /// <summary>
    /// Returns a new <see cref="SnapshotAircraft"/> that fills the
    /// caller's null fields from <paramref name="peer"/>. The result
    /// is presented as a local aircraft (<c>Peer = null</c>) — the
    /// user has direct radio contact regardless of who else saw it.
    /// </summary>
    public static SnapshotAircraft Combine(SnapshotAircraft local, SnapshotAircraft peer)
    {
        return local with
        {
            // Identity / DB enrichment — peer fills gaps.
            Callsign = local.Callsign ?? peer.Callsign,
            Category = local.Category ?? peer.Category,
            Registration = local.Registration ?? peer.Registration,
            TypeIcao = local.TypeIcao ?? peer.TypeIcao,
            TypeLong = local.TypeLong ?? peer.TypeLong,

            // Position — keep local's own fix; only fall back to peer's
            // position when we have no fix at all. Mixing positions
            // across receivers within a single trail risks visible
            // jumps, so we never overwrite a live local fix.
            Lat = local.Lat ?? peer.Lat,
            Lon = local.Lon ?? peer.Lon,

            // Altitude / velocity — fill gaps from peer.
            Altitude = local.Altitude ?? peer.Altitude,
            AltitudeBaro = local.AltitudeBaro ?? peer.AltitudeBaro,
            AltitudeGeo = local.AltitudeGeo ?? peer.AltitudeGeo,
            Track = local.Track ?? peer.Track,
            Speed = local.Speed ?? peer.Speed,
            Vrate = local.Vrate ?? peer.Vrate,

            // Squawk / emergency — fill gaps.
            Squawk = local.Squawk ?? peer.Squawk,
            Emergency = local.Emergency ?? peer.Emergency,

            // Route + airline enrichment — fill gaps. Each side's
            // adsbdb / airlines DB cache may have been populated at
            // different times, so combining can produce a richer
            // record than either alone.
            Origin = local.Origin ?? peer.Origin,
            Destination = local.Destination ?? peer.Destination,
            OriginInfo = local.OriginInfo ?? peer.OriginInfo,
            DestInfo = local.DestInfo ?? peer.DestInfo,
            Phase = local.Phase ?? peer.Phase,
            Operator = local.Operator ?? peer.Operator,
            OperatorIata = local.OperatorIata ?? peer.OperatorIata,
            OperatorAlliance = local.OperatorAlliance ?? peer.OperatorAlliance,
            OperatorCountry = local.OperatorCountry ?? peer.OperatorCountry,
            CountryIso = local.CountryIso ?? peer.CountryIso,
            Manufacturer = local.Manufacturer ?? peer.Manufacturer,

            // Receiver-specific values stay local: DistanceKm, SignalPeak,
            // MsgCount, LastSeen, FirstSeen, Trail, PositionStale,
            // PositionSource, CommB, OnGround. These describe THIS
            // receiver's reception of the aircraft.
            // Peer flag stays null — we have direct contact, so this
            // record renders as a local aircraft (no peer styling).
            Peer = null,

            // Relay-computed: how many other peers also report this ICAO.
            // The relay calculated this per-recipient (excluding us), so
            // we just carry the peer record's value through.
            SeenByOthers = peer.SeenByOthers,
        };
    }
}
