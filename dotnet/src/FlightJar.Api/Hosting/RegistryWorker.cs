using System.Text.Json;
using System.Threading.Channels;
using FlightJar.Clients.Adsbdb;
using FlightJar.Clients.Metar;
using FlightJar.Core.Configuration;
using FlightJar.Core.ReferenceData;
using FlightJar.Core.State;
using FlightJar.Core.Stats;
using FlightJar.Decoder.Beast;
using FlightJar.Notifications.Alerts;
using FlightJar.Persistence.State;
using FlightJar.Persistence.Watchlist;

namespace FlightJar.Api.Hosting;

/// <summary>
/// Owns the single-writer <see cref="AircraftRegistry"/>. Consumes BEAST frames
/// from <see cref="ChannelReader{T}"/> and ticks the snapshot builder at
/// <see cref="AppOptions.SnapshotInterval"/>. Publishes the latest snapshot +
/// its JSON projection to <see cref="CurrentSnapshot"/>.
/// </summary>
public sealed class RegistryWorker : BackgroundService
{
    private readonly ChannelReader<BeastFrame> _frames;
    private readonly AircraftRegistry _registry;
    private readonly CurrentSnapshot _current;
    private readonly SnapshotBroadcaster _broadcaster;
    private readonly AppOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RegistryWorker> _logger;
    private readonly AlertWatcher? _alerts;
    private readonly WatchlistStore? _watchlist;
    private readonly StateSnapshotStore? _statePersister;
    private readonly AdsbdbClient? _adsbdb;
    private readonly MetarClient? _metar;
    private readonly AirportsDb? _airportsDb;
    private readonly AirlinesDb? _airlinesDb;
    private readonly PolarCoverage? _polarCoverage;
    private readonly PolarHeatmap? _polarHeatmap;
    private readonly TrafficHeatmap? _trafficHeatmap;

    /// <summary>Persist the registry every Nth snapshot tick (default 30s at 1 Hz).</summary>
    public int PersistEveryNTicks { get; set; } = 30;
    private int _persistCounter;

    private readonly Channel<Work> _work = Channel.CreateUnbounded<Work>(
        new UnboundedChannelOptions { SingleReader = true });

    private long _frameCount;
    public long FrameCount => Volatile.Read(ref _frameCount);

    public RegistryWorker(
        ChannelReader<BeastFrame> frames,
        AircraftRegistry registry,
        CurrentSnapshot current,
        SnapshotBroadcaster broadcaster,
        AppOptions options,
        TimeProvider time,
        ILogger<RegistryWorker> logger,
        AlertWatcher? alerts = null,
        WatchlistStore? watchlist = null,
        StateSnapshotStore? statePersister = null,
        AdsbdbClient? adsbdb = null,
        MetarClient? metar = null,
        AirportsDb? airportsDb = null,
        AirlinesDb? airlinesDb = null,
        PolarCoverage? polarCoverage = null,
        TrafficHeatmap? trafficHeatmap = null,
        PolarHeatmap? polarHeatmap = null)
    {
        _frames = frames;
        _registry = registry;
        _current = current;
        _broadcaster = broadcaster;
        _options = options;
        _time = time;
        _logger = logger;
        _alerts = alerts;
        _watchlist = watchlist;
        _statePersister = statePersister;
        _adsbdb = adsbdb;
        _metar = metar;
        _airportsDb = airportsDb;
        _airlinesDb = airlinesDb;
        _polarCoverage = polarCoverage;
        _trafficHeatmap = trafficHeatmap;
        _polarHeatmap = polarHeatmap;

        // Wire registry callbacks into stats collectors. These fire from within
        // Ingest() which runs on this worker's thread, so no locking needed.
        if (_trafficHeatmap is not null)
        {
            _registry.OnNewAircraft = (_icao, ts) => _trafficHeatmap.Observe(ts);
        }
        if (_polarCoverage is not null || _polarHeatmap is not null)
        {
            _registry.OnPosition = (lat, lon) =>
            {
                _polarCoverage?.Observe(lat, lon);
                _polarHeatmap?.Observe(lat, lon);
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.SnapshotInterval);
        var ingestTask = IngestLoopAsync(stoppingToken);
        var tickTask = TickLoopAsync(interval, stoppingToken);
        var workTask = WorkLoopAsync(stoppingToken);
        await Task.WhenAll(ingestTask, tickTask, workTask);
    }

    private async Task IngestLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _frames.ReadAllAsync(ct))
            {
                _work.Writer.TryWrite(Work.FromFrame(frame));
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _work.Writer.TryComplete();
        }
    }

    private async Task TickLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(interval, _time);
            while (await timer.WaitForNextTickAsync(ct))
            {
                _work.Writer.TryWrite(Work.Tick());
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task WorkLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _work.Reader.ReadAllAsync(ct))
            {
                try
                {
                    if (item.IsTick)
                    {
                        DoTick();
                    }
                    else
                    {
                        IngestFrame(item.Frame);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "registry worker error");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void IngestFrame(BeastFrame frame)
    {
        if (frame.Type is not (BeastFrameType.ModeSShort or BeastFrameType.ModeSLong))
        {
            return;
        }
        var hex = BytesToHex(frame.Message.Span);
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
        _registry.Ingest(hex, now, frame.MlatTicks, frame.Signal);
        Interlocked.Increment(ref _frameCount);
    }

    private void DoTick()
    {
        var nowSec = _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
        _registry.Cleanup(nowSec);
        var snap = _registry.Snapshot(nowSec);
        snap = EnrichSnapshot(snap) with { Frames = FrameCount };
        var json = JsonSerializer.Serialize(snap, _jsonOpts);
        _current.Set(snap, json);
        _broadcaster.Broadcast(json);

        // Phase 4 hooks — watchlist last-seen + alert fan-out.
        if (_watchlist is not null)
        {
            foreach (var ac in snap.Aircraft)
            {
                if (!string.IsNullOrEmpty(ac.Icao))
                {
                    _watchlist.RecordSeen(ac.Icao, ac.LastSeen);
                }
            }
        }
        if (_alerts is not null)
        {
            // Fire-and-forget so a slow upstream can't stall the 1 Hz tick.
            _ = _alerts.ObserveAsync(snap);
        }

        // Periodic state.json.gz persistence. Serialize runs on this (writer)
        // thread so the dict scan is race-free; the disk write goes off-thread
        // via SaveAsync.
        if (_statePersister is not null && ++_persistCounter >= PersistEveryNTicks)
        {
            _persistCounter = 0;
            var payload = _registry.Serialize(nowSec);
            _ = _statePersister.SaveAsync(payload);
        }

        // Stats: coverage + heatmap debounced persist (at most once / 60 s).
        if (_polarCoverage is not null)
        {
            _ = _polarCoverage.MaybePersistAsync(TimeSpan.FromSeconds(60));
        }
        if (_trafficHeatmap is not null)
        {
            _ = _trafficHeatmap.MaybePersistAsync(TimeSpan.FromSeconds(60));
        }
        if (_polarHeatmap is not null)
        {
            _ = _polarHeatmap.MaybePersistAsync(TimeSpan.FromSeconds(60));
        }
    }

    /// <summary>
    /// Enrich each snapshot aircraft with origin/destination (adsbdb, cached),
    /// airport info (airports DB), flight phase (derived), and airline metadata
    /// (airlines DB). Cache-only — if an adsbdb entry isn't on disk, the fields
    /// stay null and a background fetch is fired so the next tick picks it up.
    /// </summary>
    private RegistrySnapshot EnrichSnapshot(RegistrySnapshot snap)
    {
        if (_adsbdb is null && _airportsDb is null && _airlinesDb is null)
        {
            return snap;
        }

        var aircraft = new List<SnapshotAircraft>(snap.Aircraft.Count);
        var uncachedCallsigns = new HashSet<string>(StringComparer.Ordinal);
        var uncachedIcaos = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ac in snap.Aircraft)
        {
            string? origin = null;
            string? destination = null;
            string? @operator = null;
            string? operatorCountry = null;
            string? countryIso = null;
            string? manufacturer = null;

            if (_adsbdb is not null)
            {
                if (!string.IsNullOrEmpty(ac.Callsign))
                {
                    var routeCached = _adsbdb.LookupCachedRoute(ac.Callsign);
                    if (routeCached.Known && routeCached.Data is not null)
                    {
                        origin = routeCached.Data.Origin;
                        destination = routeCached.Data.Destination;
                    }
                    else if (!routeCached.Known)
                    {
                        uncachedCallsigns.Add(ac.Callsign);
                    }
                }

                var acCached = _adsbdb.LookupCachedAircraft(ac.Icao);
                if (acCached.Known && acCached.Data is not null)
                {
                    @operator = acCached.Data.Operator;
                    operatorCountry = acCached.Data.OperatorCountry;
                    countryIso = acCached.Data.OperatorCountryIso;
                    manufacturer = acCached.Data.Manufacturer;
                }
                else if (!acCached.Known)
                {
                    uncachedIcaos.Add(ac.Icao);
                }
            }

            var originInfo = ToSnapshotAirport(_airportsDb?.Lookup(origin));
            var destInfo = ToSnapshotAirport(_airportsDb?.Lookup(destination));

            // Plausibility cross-check — drop route if the plane's clearly not on it.
            if (originInfo is not null && destInfo is not null)
            {
                var plausible = RoutePlausibility.IsPlausible(
                    acLat: ac.Lat, acLon: ac.Lon, acTrack: ac.Track, onGround: ac.OnGround,
                    origin: new AirportInfo(originInfo.Lat, originInfo.Lon),
                    destination: new AirportInfo(destInfo.Lat, destInfo.Lon));
                if (!plausible)
                {
                    origin = null;
                    destination = null;
                    originInfo = null;
                    destInfo = null;
                }
            }

            var phase = FlightPhase.Classify(
                onGround: ac.OnGround,
                altitude: ac.Altitude,
                verticalRate: ac.Vrate,
                lat: ac.Lat, lon: ac.Lon,
                destination: destInfo is not null ? new AirportInfo(destInfo.Lat, destInfo.Lon) : null);

            string? operatorIata = null;
            string? operatorAlliance = null;
            if (_airlinesDb is not null)
            {
                var airline = _airlinesDb.LookupByCallsign(ac.Callsign);
                if (airline is not null)
                {
                    operatorIata = airline.Iata;
                    operatorAlliance = airline.Alliance;
                    // Airline name from the airlines DB is usually fuller than
                    // adsbdb's "registered owner" — prefer it when present.
                    @operator ??= airline.Name;
                }
            }

            aircraft.Add(ac with
            {
                Origin = origin,
                Destination = destination,
                OriginInfo = originInfo,
                DestInfo = destInfo,
                Phase = phase,
                Operator = @operator,
                OperatorIata = operatorIata,
                OperatorAlliance = operatorAlliance,
                OperatorCountry = operatorCountry,
                CountryIso = countryIso,
                Manufacturer = manufacturer,
            });
        }

        // Fire background fetches for uncached entries so the next tick picks them up.
        if (_adsbdb is not null)
        {
            foreach (var cs in uncachedCallsigns)
            {
                _ = _adsbdb.LookupRouteAsync(cs);
            }
            foreach (var ic in uncachedIcaos)
            {
                _ = _adsbdb.LookupAircraftAsync(ic);
            }
        }

        // Aggregate an ICAO -> {name, lat, lon, metar} map for the top-level
        // snapshot. The frontend reads `state.lastSnap.airports[ICAO]` for
        // route-progress geometry + METAR tooltips, so duplicates across
        // aircraft stay consistent from one place.
        Dictionary<string, SnapshotAirportRef>? airports = null;
        var uncachedMetarCodes = new List<string>();
        if (_airportsDb is not null)
        {
            airports = new Dictionary<string, SnapshotAirportRef>(StringComparer.Ordinal);
            foreach (var ac in aircraft)
            {
                AddAirport(airports, ac.OriginInfo, uncachedMetarCodes);
                AddAirport(airports, ac.DestInfo, uncachedMetarCodes);
            }
        }
        // Fire one batched METAR fetch for any referenced airports we
        // haven't cached yet. Fire-and-forget so a slow upstream can't
        // stall the 1 Hz tick; the next snapshot will surface the result.
        if (_metar is not null && uncachedMetarCodes.Count > 0)
        {
            _ = _metar.LookupManyAsync(uncachedMetarCodes);
        }

        return snap with { Aircraft = aircraft, Airports = airports };
    }

    private void AddAirport(
        Dictionary<string, SnapshotAirportRef> airports,
        SnapshotAirport? info,
        List<string> uncachedMetarCodes)
    {
        if (info is null || airports.ContainsKey(info.Icao))
        {
            return;
        }
        SnapshotMetar? metar = null;
        if (_metar is not null)
        {
            var cached = _metar.LookupCached(info.Icao);
            if (cached.Known && cached.Data is MetarEntry entry)
            {
                metar = new SnapshotMetar
                {
                    Raw = entry.Raw,
                    ObsTime = entry.ObsTime,
                    WindDir = entry.WindDir,
                    WindKt = entry.WindKt,
                    GustKt = entry.GustKt,
                    Visibility = entry.Visibility,
                    TempC = entry.TempC,
                    DewpointC = entry.DewpointC,
                    AltimeterHpa = entry.AltimeterHpa,
                    Cover = entry.Cover,
                };
            }
            else if (!cached.Known)
            {
                uncachedMetarCodes.Add(info.Icao);
            }
        }
        airports[info.Icao] = new SnapshotAirportRef(info.Name, info.Lat, info.Lon)
        {
            Metar = metar,
        };
    }

    private static SnapshotAirport? ToSnapshotAirport(AirportRecord? r) =>
        r is null ? null : new SnapshotAirport(r.Icao, r.Name, r.City, r.Country, r.Lat, r.Lon);

    private static readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static string BytesToHex(ReadOnlySpan<byte> bytes)
    {
        Span<char> buf = stackalloc char[bytes.Length * 2];
        const string hex = "0123456789abcdef";
        for (var i = 0; i < bytes.Length; i++)
        {
            buf[i * 2] = hex[bytes[i] >> 4];
            buf[i * 2 + 1] = hex[bytes[i] & 0xF];
        }
        return new string(buf);
    }

    private readonly struct Work
    {
        public bool IsTick { get; }
        public BeastFrame Frame { get; }

        private Work(bool isTick, BeastFrame frame)
        {
            IsTick = isTick;
            Frame = frame;
        }

        public static Work Tick() => new(true, default);
        public static Work FromFrame(BeastFrame f) => new(false, f);
    }
}
