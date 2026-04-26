using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FlightJar.Api.Auth;
using FlightJar.Api.Configuration;
using FlightJar.Api.Hosting;
using FlightJar.Api.Telemetry;
using FlightJar.Clients.Adsbdb;
using FlightJar.Clients.Metar;
using FlightJar.Clients.OpenAip;
using FlightJar.Clients.Planespotters;
using FlightJar.Clients.Vfrmap;
using FlightJar.Core;
using FlightJar.Core.Configuration;
using FlightJar.Core.ReferenceData;
using FlightJar.Core.State;
using FlightJar.Core.Stats;
using FlightJar.Decoder.Beast;
using FlightJar.Notifications;
using FlightJar.Notifications.Alerts;
using FlightJar.Persistence.Notifications;
using FlightJar.Persistence.State;
using FlightJar.Persistence.Watchlist;
using FlightJar.Terrain.Srtm;

var builder = WebApplication.CreateBuilder(args);

var options = AppOptionsBinder.FromEnvironment();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);

// Optional shared-secret gate for the notification config + watchlist
// endpoints. No-op when FLIGHTJAR_PASSWORD is unset.
builder.Services.AddSingleton<AuthService>();

// Match Python's API: snake_case property + enum names, nulls omitted.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

// Frame channel: BEAST consumer writes, registry worker reads. Bounded +
// drop-oldest so a stuck registry can't back-pressure the TCP reader.
var frameChannel = Channel.CreateBounded<BeastFrame>(new BoundedChannelOptions(16384)
{
    FullMode = BoundedChannelFullMode.DropOldest,
    SingleReader = true,
    SingleWriter = true,
});
builder.Services.AddSingleton(frameChannel.Writer);
builder.Services.AddSingleton(frameChannel.Reader);

// Data-directory-scoped persistence paths — derived from BEAST_OUTFILE's
// directory, matching the Python app's /data/ conventions.
var dataDir = !string.IsNullOrEmpty(options.JsonlPath)
    ? Path.GetDirectoryName(options.JsonlPath) ?? ""
    : "";

// Reference data: ICAO24 → registration/type lookup, plus airports / navaids
// / airlines. All baked into the image at build time under app/ and
// overridable at runtime via /data/.
builder.Services.AddSingleton<AircraftDb>();
builder.Services.AddSingleton<IAircraftDb>(sp => sp.GetRequiredService<AircraftDb>());
builder.Services.AddSingleton<AirportsDb>();
builder.Services.AddSingleton<NavaidsDb>();
builder.Services.AddSingleton<AirlinesDb>();

// Receiver-stats collectors.
var coveragePath = !string.IsNullOrEmpty(dataDir) ? Path.Combine(dataDir, "coverage.json") : null;
var heatmapPath = !string.IsNullOrEmpty(dataDir) ? Path.Combine(dataDir, "heatmap.json") : null;
var polarHeatmapPath = !string.IsNullOrEmpty(dataDir) ? Path.Combine(dataDir, "polar_heatmap.json") : null;
builder.Services.AddSingleton(sp => new PolarCoverage(
    receiverLat: options.LatRef, receiverLon: options.LonRef,
    cachePath: coveragePath,
    time: sp.GetRequiredService<TimeProvider>(),
    logger: sp.GetService<ILogger<PolarCoverage>>()));
builder.Services.AddSingleton(sp => new TrafficHeatmap(
    cachePath: heatmapPath,
    time: sp.GetRequiredService<TimeProvider>(),
    logger: sp.GetService<ILogger<TrafficHeatmap>>()));
builder.Services.AddSingleton(sp => new PolarHeatmap(
    receiverLat: options.LatRef, receiverLon: options.LonRef,
    cachePath: polarHeatmapPath,
    time: sp.GetRequiredService<TimeProvider>(),
    logger: sp.GetService<ILogger<PolarHeatmap>>()));

// Registry + snapshot state. Picks up AircraftDb so snapshots carry
// registration / type fields when a DB is loaded.
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<AppOptions>();
    return new AircraftRegistry(
        latRef: opts.LatRef,
        lonRef: opts.LonRef,
        receiver: opts.LatRef is double rlat && opts.LonRef is double rlon
            ? new ReceiverInfo(rlat, rlon, opts.ReceiverAnonKm)
            : null,
        siteName: opts.SiteName,
        aircraftDb: sp.GetRequiredService<IAircraftDb>(),
        clock: () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
});
builder.Services.AddSingleton<CurrentSnapshot>();
builder.Services.AddSingleton<SnapshotBroadcaster>();
builder.Services.AddSingleton<BeastConnectionState>();
builder.Services.AddSingleton<IBeastConnectionState>(sp => sp.GetRequiredService<BeastConnectionState>());

// Phase 4: watchlist + notifications + alerts.
var watchlistPath = !string.IsNullOrEmpty(dataDir) ? Path.Combine(dataDir, "watchlist.json") : null;
var notificationsPath = !string.IsNullOrEmpty(dataDir) ? Path.Combine(dataDir, "notifications.json") : null;
builder.Services.AddSingleton(sp => new WatchlistStore(
    watchlistPath, sp.GetRequiredService<TimeProvider>(), sp.GetService<ILogger<WatchlistStore>>()));
builder.Services.AddSingleton(sp => new NotificationsConfigStore(
    notificationsPath, sp.GetService<ILogger<NotificationsConfigStore>>()));

// Registry state snapshot persister (/data/state.json.gz).
var statePath = !string.IsNullOrEmpty(dataDir) ? Path.Combine(dataDir, "state.json.gz") : null;
if (statePath is not null)
{
    builder.Services.AddSingleton(sp => new StateSnapshotStore(
        statePath, sp.GetService<ILogger<StateSnapshotStore>>()));
}

// Phase 2 external clients (adsbdb, planespotters, METAR, VFRMap). Typed
// HttpClients give each one its own connection pool + dispose hooks.
var adsbdbPath = !string.IsNullOrEmpty(dataDir) ? Path.Combine(dataDir, "flight_routes.json.gz") : null;
var photosPath = !string.IsNullOrEmpty(dataDir) ? Path.Combine(dataDir, "photos.json.gz") : null;
var metarPath = !string.IsNullOrEmpty(dataDir) ? Path.Combine(dataDir, "metar.json.gz") : null;
var vfrmapPath = !string.IsNullOrEmpty(dataDir) ? Path.Combine(dataDir, "vfrmap_cycle.json") : null;
var openaipPath = !string.IsNullOrEmpty(dataDir) ? Path.Combine(dataDir, "openaip.json.gz") : null;

builder.Services.AddHttpClient<AdsbdbClient>();
builder.Services.AddSingleton(sp => new AdsbdbClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(AdsbdbClient)),
    sp.GetRequiredService<ILogger<AdsbdbClient>>(),
    sp.GetRequiredService<TimeProvider>(),
    adsbdbPath,
    enabled: sp.GetRequiredService<AppOptions>().FlightRoutesEnabled));

builder.Services.AddHttpClient<PlanespottersClient>();
builder.Services.AddSingleton(sp => new PlanespottersClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(PlanespottersClient)),
    sp.GetRequiredService<ILogger<PlanespottersClient>>(),
    sp.GetRequiredService<TimeProvider>(),
    photosPath,
    enabled: sp.GetRequiredService<AppOptions>().FlightRoutesEnabled));

builder.Services.AddHttpClient<MetarClient>();
builder.Services.AddSingleton(sp => new MetarClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(MetarClient)),
    sp.GetRequiredService<ILogger<MetarClient>>(),
    sp.GetRequiredService<TimeProvider>(),
    metarPath,
    enabled: sp.GetRequiredService<AppOptions>().MetarEnabled));

builder.Services.AddHttpClient<VfrmapCycle>();
builder.Services.AddSingleton(sp => new VfrmapCycle(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(VfrmapCycle)),
    sp.GetRequiredService<ILogger<VfrmapCycle>>(),
    sp.GetRequiredService<TimeProvider>(),
    vfrmapPath,
    overrideDate: sp.GetRequiredService<AppOptions>().VfrmapChartDate));

builder.Services.AddHttpClient<OpenAipClient>();
builder.Services.AddSingleton(sp => new OpenAipClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OpenAipClient)),
    sp.GetRequiredService<ILogger<OpenAipClient>>(),
    sp.GetRequiredService<TimeProvider>(),
    openaipPath,
    apiKey: sp.GetRequiredService<AppOptions>().OpenAipApiKey));

// SRTM terrain tile store (AWS Open Data — no auth). Backs the blackspots
// worker below.
builder.Services.AddHttpClient<SrtmTileStore>();
builder.Services.AddSingleton(sp => new SrtmTileStore(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(SrtmTileStore)),
    cacheDir: sp.GetRequiredService<AppOptions>().TerrainCacheDir,
    logger: sp.GetRequiredService<ILogger<SrtmTileStore>>()));

// HttpClient for the three notifier types — reused across dispatches.
builder.Services.AddHttpClient<TelegramNotifier>();
builder.Services.AddHttpClient<NtfyNotifier>();
builder.Services.AddHttpClient<WebhookNotifier>();
builder.Services.AddSingleton<INotifier, TelegramNotifier>();
builder.Services.AddSingleton<INotifier, NtfyNotifier>();
builder.Services.AddSingleton<INotifier, WebhookNotifier>();
builder.Services.AddSingleton<NotifierDispatcher>();
builder.Services.AddSingleton<AlertWatcher>();

// Hosted services. Register as singleton first so endpoints can resolve them
// (AddHostedService alone only registers IHostedService, not the concrete type).
builder.Services.AddSingleton<BeastConsumerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BeastConsumerService>());
builder.Services.AddSingleton<RegistryWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RegistryWorker>());
builder.Services.AddHostedService<VfrmapCycleRefresher>();

// Warm the OpenAIP tile cache around the receiver at startup so the first
// map pan doesn't stall on paginated upstream fetches.
builder.Services.AddHostedService<OpenAipPrewarmWorker>();

var blackspotsPath = !string.IsNullOrEmpty(dataDir) ? Path.Combine(dataDir, "blackspots.json.gz") : null;
builder.Services.AddSingleton(sp => new BlackspotsWorker(
    sp.GetRequiredService<AppOptions>(),
    sp.GetRequiredService<SrtmTileStore>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<BlackspotsWorker>>(),
    blackspotsPath));
builder.Services.AddHostedService(sp => sp.GetRequiredService<BlackspotsWorker>());

// Anonymous telemetry. No-op unless TELEMETRY_ENABLED=1 (default) AND a
// POSTHOG_API_KEY is set — without a key the worker never makes a request.
var telemetryPath = !string.IsNullOrEmpty(dataDir) ? Path.Combine(dataDir, "telemetry.json") : null;
builder.Services.AddSingleton(sp => new InstanceIdStore(
    telemetryPath,
    sp.GetRequiredService<TimeProvider>(),
    sp.GetService<ILogger<InstanceIdStore>>()));
// Always-on accumulator — RegistryWorker / BeastConsumerService push
// per-tick samples + reconnect events into it whether telemetry is
// enabled or not (cost is one lock + a handful of adds per tick). The
// drain only happens from inside TelemetryWorker, which no-ops when
// telemetry is off, so the bag just keeps accumulating harmlessly.
builder.Services.AddSingleton<TelemetryAccumulator>();
builder.Services.AddHttpClient<PosthogClient>();
builder.Services.AddSingleton(sp => new PosthogClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(PosthogClient)),
    sp.GetService<ILogger<PosthogClient>>()));
// Register the worker as a singleton AND a hosted service so the reset
// endpoint can call SendIdentifyAsync directly after rotating the id.
var aircraftDbOverridePath = !string.IsNullOrEmpty(dataDir)
    ? Path.Combine(dataDir, "aircraft_db.csv.gz")
    : null;
builder.Services.AddSingleton(sp => new TelemetryWorker(
    sp.GetRequiredService<AppOptions>(),
    sp.GetRequiredService<InstanceIdStore>(),
    sp.GetRequiredService<PosthogClient>(),
    sp.GetRequiredService<CurrentSnapshot>(),
    sp.GetRequiredService<SnapshotBroadcaster>(),
    sp.GetRequiredService<NotificationsConfigStore>(),
    sp.GetRequiredService<TelemetryAccumulator>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<TelemetryWorker>>(),
    sp.GetService<RegistryWorker>(),
    sp.GetService<WatchlistStore>(),
    sp.GetService<PolarCoverage>(),
    sp.GetService<TrafficHeatmap>(),
    aircraftDbOverridePath));
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryWorker>());

var app = builder.Build();

// Load persisted state synchronously before the host kicks off background services.
await app.Services.GetRequiredService<WatchlistStore>().LoadAsync();
await app.Services.GetRequiredService<NotificationsConfigStore>().LoadAsync();
// Eager-load the install ID so /api/telemetry_config can serve it
// before TelemetryWorker has had a chance to run.
await app.Services.GetRequiredService<InstanceIdStore>().LoadOrCreateAsync();

// Warm the external-client disk caches in parallel so adsbdb / planespotters
// / METAR lookups can skip the upstream round-trip on early snapshots.
await Task.WhenAll(
    app.Services.GetRequiredService<AdsbdbClient>().LoadCacheAsync(),
    app.Services.GetRequiredService<PlanespottersClient>().LoadCacheAsync(),
    app.Services.GetRequiredService<MetarClient>().LoadCacheAsync(),
    app.Services.GetRequiredService<VfrmapCycle>().LoadCacheAsync(),
    app.Services.GetRequiredService<OpenAipClient>().LoadCacheAsync());

// Reference data — runtime override under /data/ first, baked-in fallback
// under the published output's app/ directory second.
static List<string> RefDataCandidates(string dataDir, string fileName)
{
    var list = new List<string>();
    if (!string.IsNullOrEmpty(dataDir))
    {
        list.Add(Path.Combine(dataDir, fileName));
    }
    list.Add(Path.Combine(AppContext.BaseDirectory, fileName));
    list.Add(Path.Combine(AppContext.BaseDirectory, "app", fileName));
    return list;
}
await Task.WhenAll(
    app.Services.GetRequiredService<AircraftDb>().LoadFirstAvailableAsync(
        RefDataCandidates(dataDir, "aircraft_db.csv.gz")),
    app.Services.GetRequiredService<AirportsDb>().LoadFirstAvailableAsync(
        RefDataCandidates(dataDir, "airports.csv")),
    app.Services.GetRequiredService<NavaidsDb>().LoadFirstAvailableAsync(
        RefDataCandidates(dataDir, "navaids.csv")),
    app.Services.GetRequiredService<AirlinesDb>().LoadFirstAvailableAsync(
        RefDataCandidates(dataDir, "airlines.dat")),
    app.Services.GetRequiredService<PolarCoverage>().LoadAsync(),
    app.Services.GetRequiredService<TrafficHeatmap>().LoadAsync(),
    app.Services.GetRequiredService<PolarHeatmap>().LoadAsync());

// Restore persisted registry state (fresh-start on missing / corrupt file).
if (app.Services.GetService<StateSnapshotStore>() is StateSnapshotStore persisterRestore)
{
    var persisted = await persisterRestore.LoadAsync();
    if (persisted is not null)
    {
        var n = app.Services.GetRequiredService<AircraftRegistry>().Restore(persisted);
        app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("flightjar.state")
            .LogInformation("restored {Count} aircraft from persisted state", n);
    }
}

app.UseWebSockets();

// Static file serving. Discovery order:
//   1. FLIGHTJAR_STATIC_DIR env var (explicit override)
//   2. <AppContext.BaseDirectory>/static   (image layout)
//   3. <repo-root>/app/static              (dev + tests, walks up from bin/)
// Every asset under /static/ sets Cache-Control: no-cache so the browser
// always revalidates via ETag (304 when unchanged, full re-fetch on
// change). Top-level app.css / app.js references in index.html also get
// content-hash ?v= query strings substituted below; their ES-module
// imports inside app.js, and the OpenAIP/Leaflet CDN sprinkles from the
// browser, still rely on the ETag revalidation path.
var staticRoot = ResolveStaticRoot(options);
if (staticRoot is not null)
{
    var fileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(staticRoot);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = fileProvider,
        RequestPath = "/static",
        OnPrepareResponse = ctx =>
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        },
    });

    // Cache-bust the CSS/JS references in index.html by substituting the
    // `__CSS_V__` / `__JS_V__` placeholders with short SHA-256 prefixes
    // of the files they decorate. Computed once at startup and cached so
    // /serve is a single memcpy. Each occurrence resolves to the hash of
    // the *specific* file it's attached to (dialogs.css, detail_panel.css
    // etc. each get their own hash), so changing one asset invalidates
    // only that one's cached URL.
    var renderedIndex = IndexHtmlRenderer.Render(staticRoot);
    app.MapGet("/", (HttpContext ctx) =>
    {
        if (renderedIndex is null)
        {
            return Results.NotFound();
        }
        ctx.Response.Headers.CacheControl = "no-cache";
        return Results.Content(renderedIndex, "text/html; charset=utf-8");
    });
}

app.MapGet("/healthz", (IBeastConnectionState state) =>
    state.IsConnected
        ? Results.Ok(new { status = "ok" })
        : Results.Json(new { status = "disconnected" }, statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapGet("/api/aircraft", (CurrentSnapshot current) =>
    Results.Content(current.Json, "application/json; charset=utf-8"));

app.MapGet("/api/stats", (
    [Microsoft.AspNetCore.Mvc.FromServices] AppOptions opts,
    [Microsoft.AspNetCore.Mvc.FromServices] IBeastConnectionState state,
    [Microsoft.AspNetCore.Mvc.FromServices] RegistryWorker worker,
    [Microsoft.AspNetCore.Mvc.FromServices] SnapshotBroadcaster broadcaster,
    [Microsoft.AspNetCore.Mvc.FromServices] CurrentSnapshot current) =>
    Results.Json(new
    {
        site_name = opts.SiteName,
        beast_host = opts.BeastHost,
        beast_port = opts.BeastPort,
        beast_target = $"{opts.BeastHost}:{opts.BeastPort}",
        beast_connected = state.IsConnected,
        frames = worker.FrameCount,
        websocket_clients = broadcaster.SubscriberCount,
        aircraft = current.Snapshot.Count,
        positioned = current.Snapshot.Positioned,
        uptime_s = (int)(DateTime.UtcNow - _startedAt).TotalSeconds,
        version = Environment.GetEnvironmentVariable("FLIGHTJAR_VERSION") ?? "dev",
    }));

app.MapGet("/metrics", (
    [Microsoft.AspNetCore.Mvc.FromServices] IBeastConnectionState state,
    [Microsoft.AspNetCore.Mvc.FromServices] RegistryWorker worker,
    [Microsoft.AspNetCore.Mvc.FromServices] SnapshotBroadcaster broadcaster,
    [Microsoft.AspNetCore.Mvc.FromServices] CurrentSnapshot current) =>
{
    var sb = new StringBuilder();
    sb.Append("# HELP flightjar_beast_connected 1 if the BEAST feed is currently connected\n");
    sb.Append("# TYPE flightjar_beast_connected gauge\n");
    sb.Append("flightjar_beast_connected ").Append(state.IsConnected ? 1 : 0).Append('\n');
    sb.Append("# HELP flightjar_frames_total BEAST frames ingested since startup\n");
    sb.Append("# TYPE flightjar_frames_total counter\n");
    sb.Append("flightjar_frames_total ").Append(worker.FrameCount).Append('\n');
    sb.Append("# HELP flightjar_aircraft Tracked aircraft in the current snapshot\n");
    sb.Append("# TYPE flightjar_aircraft gauge\n");
    sb.Append("flightjar_aircraft ").Append(current.Snapshot.Count).Append('\n');
    sb.Append("# HELP flightjar_ws_clients Connected WebSocket clients\n");
    sb.Append("# TYPE flightjar_ws_clients gauge\n");
    sb.Append("flightjar_ws_clients ").Append(broadcaster.SubscriberCount).Append('\n');
    return Results.Text(sb.ToString(), "text/plain; version=0.0.4");
});

// ----- Map config / airports / navaids -----

app.MapGet("/api/map_config", (AppOptions opts, VfrmapCycle vfrmap) =>
    Results.Json(new
    {
        openaip_api_key = opts.OpenAipApiKey,
        vfrmap_chart_date = vfrmap.CurrentDate ?? opts.VfrmapChartDate,
    }));

// Frontend telemetry init payload. Same opt-out as the backend ping
// (TELEMETRY_ENABLED=0) and same destination (baked phc_* key). Returns
// {enabled:false} when off so the frontend skips loading posthog-js
// entirely; otherwise returns the distinct_id from the install's
// InstanceIdStore so frontend events tie back to the same Person as
// the backend ping.
app.MapGet("/api/telemetry_config", (AppOptions opts, InstanceIdStore instance) =>
{
    if (!opts.TelemetryEnabled || string.IsNullOrWhiteSpace(TelemetryConfig.ApiKey))
    {
        return Results.Json(new { enabled = false });
    }
    return Results.Json(new
    {
        enabled = true,
        host = TelemetryConfig.Host,
        api_key = TelemetryConfig.ApiKey,
        distinct_id = instance.InstanceId,
    });
});

// Rotate the install's PostHog distinct_id. Mints a new instance id +
// resets first_seen to now, persists, then fires a fresh $identify so
// the new Person registers upstream without waiting for the next
// scheduled tick. Gated behind the same auth as the watchlist —
// reset is irreversible and visible to the maintainer's analytics.
app.MapPost("/api/telemetry/reset", async (
    AppOptions opts,
    InstanceIdStore instance,
    TelemetryWorker telemetry,
    CancellationToken ct) =>
{
    await instance.ResetAsync(ct);
    var posthogActive = opts.TelemetryEnabled
        && !string.IsNullOrWhiteSpace(TelemetryConfig.ApiKey);
    if (posthogActive)
    {
        // Fire-and-forget so the response doesn't block on the upstream
        // POST. IdentifyAsync swallows network failures, and the next
        // 24h tick retries anyway.
        _ = telemetry.SendIdentifyAsync(CancellationToken.None);
    }
    return Results.Ok(new
    {
        ok = true,
        distinct_id = instance.InstanceId,
        telemetry_enabled = posthogActive,
    });
}).RequireAuthSession();

app.MapGet("/api/airports", (
    [Microsoft.AspNetCore.Mvc.FromServices] AirportsDb db,
    double? min_lat, double? max_lat, double? min_lon, double? max_lon, int? limit) =>
{
    if (min_lat is not double mnLat || max_lat is not double mxLat
        || min_lon is not double mnLon || max_lon is not double mxLon)
    {
        return Results.BadRequest(new { error = "min_lat/max_lat/min_lon/max_lon required" });
    }
    if (mnLat < -90 || mnLat > 90 || mxLat < -90 || mxLat > 90
        || mnLon < -180 || mnLon > 180 || mxLon < -180 || mxLon > 180)
    {
        return Results.BadRequest(new { error = "lat/lon out of range" });
    }
    var hits = db.Bbox(mnLat, mnLon, mxLat, mxLon, limit: Math.Clamp(limit ?? 2000, 1, 5000));
    return Results.Json(hits);
});

app.MapGet("/api/navaids", (
    [Microsoft.AspNetCore.Mvc.FromServices] NavaidsDb db,
    double? min_lat, double? max_lat, double? min_lon, double? max_lon, int? limit) =>
{
    if (min_lat is not double mnLat || max_lat is not double mxLat
        || min_lon is not double mnLon || max_lon is not double mxLon)
    {
        return Results.BadRequest(new { error = "min_lat/max_lat/min_lon/max_lon required" });
    }
    if (mnLat < -90 || mnLat > 90 || mxLat < -90 || mxLat > 90
        || mnLon < -180 || mnLon > 180 || mxLon < -180 || mxLon > 180)
    {
        return Results.BadRequest(new { error = "lat/lon out of range" });
    }
    var hits = db.Bbox(mnLat, mnLon, mxLat, mxLon, limit: Math.Clamp(limit ?? 2000, 1, 5000));
    return Results.Json(hits);
});

// OpenAIP bbox overlays — backed by OpenAipClient's disk cache. The frontend
// fires one request per moveend and aborts the previous one mid-flight when
// the user pans, which trips the request's cancellation token and bubbles an
// OperationCanceledException out of the throttle's semaphore wait. That's
// expected — swallow it so it doesn't spam Kestrel's error log.
static (double mnLat, double mxLat, double mnLon, double mxLon)? ReadBbox(
    double? minLat, double? maxLat, double? minLon, double? maxLon)
{
    if (minLat is not double mnLat || maxLat is not double mxLat
        || minLon is not double mnLon || maxLon is not double mxLon) return null;
    if (mnLat < -90 || mnLat > 90 || mxLat < -90 || mxLat > 90
        || mnLon < -180 || mnLon > 180 || mxLon < -180 || mxLon > 180) return null;
    return (mnLat, mxLat, mnLon, mxLon);
}

static async Task<IResult> ServeOpenAip<T>(
    Func<double, double, double, double, CancellationToken, Task<IReadOnlyList<T>>> fetch,
    string endpoint,
    ILogger logger,
    double? min_lat, double? max_lat, double? min_lon, double? max_lon,
    CancellationToken ct)
{
    var bbox = ReadBbox(min_lat, max_lat, min_lon, max_lon);
    if (bbox is null)
    {
        logger.LogInformation(
            "openaip /api/openaip/{Endpoint} rejected: bbox missing or out of range",
            endpoint);
        return Results.BadRequest(new { error = "min_lat/max_lat/min_lon/max_lon required, in range" });
    }
    var (mnLat, mxLat, mnLon, mxLon) = bbox.Value;
    try
    {
        var items = await fetch(mnLat, mnLon, mxLat, mxLon, ct);
        logger.LogInformation(
            "openaip /api/openaip/{Endpoint} bbox=({MnLat:0.###},{MnLon:0.###})-({MxLat:0.###},{MxLon:0.###}) → {Count} items",
            endpoint, mnLat, mnLon, mxLat, mxLon, items.Count);
        return Results.Json(items);
    }
    catch (OperationCanceledException)
    {
        // OCE here covers two cases. The usual one is our own `ct` firing
        // — the map pan aborted this request, Kestrel won't be able to
        // write the response anyway. The less-obvious one: OpenAipClient
        // shares an in-flight fetch across concurrent callers via
        // `inflight.GetOrAdd`, so if a *sibling* request's ct cancelled
        // the fetch task, we inherit its OCE even though our own ct is
        // fine. In both cases the client will re-request on the next
        // moveend, so returning Empty is the right recovery — and it
        // keeps Kestrel's error log from filling with benign noise.
        return Results.Empty;
    }
}

app.MapGet("/api/openaip/airspaces", (
    [Microsoft.AspNetCore.Mvc.FromServices] OpenAipClient client,
    [Microsoft.AspNetCore.Mvc.FromServices] ILogger<OpenAipClient> logger,
    double? min_lat, double? max_lat, double? min_lon, double? max_lon,
    CancellationToken ct) =>
    ServeOpenAip<Airspace>(client.GetAirspacesAsync, "airspaces", logger, min_lat, max_lat, min_lon, max_lon, ct));

app.MapGet("/api/openaip/obstacles", (
    [Microsoft.AspNetCore.Mvc.FromServices] OpenAipClient client,
    [Microsoft.AspNetCore.Mvc.FromServices] ILogger<OpenAipClient> logger,
    double? min_lat, double? max_lat, double? min_lon, double? max_lon,
    CancellationToken ct) =>
    ServeOpenAip<Obstacle>(client.GetObstaclesAsync, "obstacles", logger, min_lat, max_lat, min_lon, max_lon, ct));

app.MapGet("/api/openaip/reporting_points", (
    [Microsoft.AspNetCore.Mvc.FromServices] OpenAipClient client,
    [Microsoft.AspNetCore.Mvc.FromServices] ILogger<OpenAipClient> logger,
    double? min_lat, double? max_lat, double? min_lon, double? max_lon,
    CancellationToken ct) =>
    ServeOpenAip<ReportingPoint>(client.GetReportingPointsAsync, "reporting_points", logger, min_lat, max_lat, min_lon, max_lon, ct));

// ----- Coverage + heatmap -----

app.MapGet("/api/coverage", (PolarCoverage coverage) => Results.Json(coverage.SnapshotView()));
app.MapPost("/api/coverage/reset", (PolarCoverage coverage) =>
{
    coverage.Reset();
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/heatmap", (TrafficHeatmap hm) => Results.Json(hm.SnapshotView()));
app.MapPost("/api/heatmap/reset", (TrafficHeatmap hm) =>
{
    hm.Reset();
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/polar_heatmap", (PolarHeatmap ph) => Results.Json(ph.SnapshotView()));
app.MapPost("/api/polar_heatmap/reset", (PolarHeatmap ph) =>
{
    ph.Reset();
    return Results.Ok(new { ok = true });
});

// ----- Blackspots (terrain LOS) -----

// Target altitude comes from the frontend slider; unset means "use the
// default the worker prewarmed at startup" (FL100 / 3048 m MSL). Bounds
// are wide enough for anything from surface GA to high-level airliners.
app.MapGet("/api/blackspots", async (
    BlackspotsWorker worker, double? target_alt_m, CancellationToken ct) =>
{
    if (!worker.Enabled)
    {
        return Results.Json(new { enabled = false, cells = Array.Empty<object>() });
    }
    var alt = target_alt_m ?? BlackspotsWorker.DefaultTargetAltitudeM;
    // 0 is a sentinel for "ground level at each cell" — the grid uses
    // sampled DEM elevation + a 2 m fuselage offset per cell instead of
    // a fixed MSL value. Anything positive is treated as absolute MSL.
    if (alt < 0 || alt > 20_000)
    {
        return Results.BadRequest(new { error = "target_alt_m out of range [0, 20000]" });
    }
    try
    {
        var grid = await worker.GetOrComputeAsync(alt, ct);
        if (grid is null)
        {
            return Results.Json(new { enabled = true, computing = true, cells = Array.Empty<object>() });
        }
        return Results.Json(grid.SnapshotView());
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        return Results.Empty;
    }
});

app.MapPost("/api/blackspots/recompute", (BlackspotsWorker worker) =>
{
    if (!worker.Enabled)
    {
        return Results.Json(new { enabled = false });
    }
    worker.TriggerRecompute();
    return Results.Ok(new { ok = true });
});

// Live progress poll for the currently-running compute at a given altitude.
// Designed to be hit every ~300 ms by the frontend while awaiting a fresh
// grid; returns {active: false} when the altitude is cached / queued /
// disabled so the caller can stop polling.
app.MapGet("/api/blackspots/progress", (BlackspotsWorker worker, double? target_alt_m) =>
{
    if (!worker.Enabled)
    {
        return Results.Json(new { active = false });
    }
    var alt = target_alt_m ?? BlackspotsWorker.DefaultTargetAltitudeM;
    var (active, progress) = worker.GetProgress(alt);
    return Results.Json(new { active, progress });
});

// ----- Flight route + aircraft details (adsbdb + planespotters) -----

app.MapGet("/api/flight/{callsign}", async (
    string callsign, AdsbdbClient adsbdb, CancellationToken ct) =>
{
    var info = await adsbdb.LookupRouteAsync(callsign, ct);
    if (info is null)
    {
        return Results.Json(new
        {
            callsign,
            origin = (string?)null,
            destination = (string?)null,
            error = "no route data",
        });
    }
    return Results.Json(new
    {
        callsign,
        origin = info.Origin,
        destination = info.Destination,
    });
});

app.MapGet("/api/aircraft/{icao24}", async (
    string icao24,
    AdsbdbClient adsbdb,
    PlanespottersClient planespotters,
    CancellationToken ct) =>
{
    if (AdsbdbClient.NormaliseIcao(icao24) is null)
    {
        return Results.BadRequest(new { error = "bad ICAO24" });
    }
    var record = await adsbdb.LookupAircraftAsync(icao24, ct);
    var registration = record?.Registration?.Trim();
    var photo = !string.IsNullOrEmpty(registration)
        ? await planespotters.LookupAsync(registration, ct)
        : null;
    return Results.Json(new
    {
        registration = record?.Registration,
        type = record?.Type,
        icao_type = record?.IcaoType,
        manufacturer = record?.Manufacturer,
        @operator = record?.Operator,
        operator_country = record?.OperatorCountry,
        operator_country_iso = record?.OperatorCountryIso,
        photo_url = photo?.Large ?? record?.PhotoUrl,
        photo_thumbnail = photo?.Thumbnail ?? record?.PhotoThumbnail,
        photo_link = photo?.Link,
        photo_photographer = photo?.Photographer,
    });
});

// ----- Auth (optional shared-secret gate) -----

// Cookie carrying the session token. HttpOnly so JS cannot read it (an
// XSS foothold can't steal a token), SameSite=Strict so cross-site
// POSTs can't smuggle the cookie back, Secure on HTTPS requests so
// browsers refuse to leak it over plain HTTP. Localhost-over-HTTP still
// works (IsHttps is false → cookie set without Secure).
static void SetSessionCookie(HttpContext ctx, string token, TimeSpan lifetime)
{
    var secure = ctx.Request.IsHttps
        || (ctx.Request.Headers.TryGetValue("X-Forwarded-Proto", out var p)
            && string.Equals(p.ToString(), "https", StringComparison.OrdinalIgnoreCase));
    ctx.Response.Cookies.Append(AuthService.CookieName, token, new CookieOptions
    {
        HttpOnly = true,
        Secure = secure,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        MaxAge = lifetime,
    });
}

static void ClearSessionCookie(HttpContext ctx)
{
    var secure = ctx.Request.IsHttps
        || (ctx.Request.Headers.TryGetValue("X-Forwarded-Proto", out var p)
            && string.Equals(p.ToString(), "https", StringComparison.OrdinalIgnoreCase));
    ctx.Response.Cookies.Append(AuthService.CookieName, "", new CookieOptions
    {
        HttpOnly = true,
        Secure = secure,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = DateTimeOffset.UnixEpoch,
    });
}

// {required} tells the UI whether to show a lock indicator at all.
// {unlocked} is informational only — the server never trusts it for
// access control, the gating filter re-checks the cookie on every
// request. Always 200 so the UI can read it on first paint.
app.MapGet("/api/auth/status", (HttpContext ctx, AuthService auth) =>
{
    var cookie = ctx.Request.Cookies[AuthService.CookieName];
    var unlocked = auth.Required && auth.ValidateSession(cookie);
    return Results.Json(new { required = auth.Required, unlocked });
});

app.MapPost("/api/auth/login", async (HttpContext ctx, AuthService auth) =>
{
    if (!auth.Required)
    {
        return Results.NotFound(new { error = "auth not configured" });
    }
    var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
    if (!auth.TryRecordLoginAttempt(clientIp))
    {
        ctx.Response.Headers["Retry-After"] = "60";
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }
    string? candidate = null;
    try
    {
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("password", out var pw)
            && pw.ValueKind == JsonValueKind.String)
        {
            candidate = pw.GetString();
        }
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "expected JSON {password: string}" });
    }
    if (!auth.VerifyPassword(candidate))
    {
        // Don't log the candidate. Log the IP so abuse leaves a trail.
        ctx.RequestServices.GetService<ILogger<Program>>()
            ?.LogWarning("auth: bad password from {Ip}", clientIp);
        return Results.StatusCode(StatusCodes.Status401Unauthorized);
    }
    auth.ResetRateLimit(clientIp);
    var token = auth.MintSession();
    SetSessionCookie(ctx, token, AuthService.SessionLifetime);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/auth/logout", (HttpContext ctx, AuthService auth) =>
{
    var cookie = ctx.Request.Cookies[AuthService.CookieName];
    auth.InvalidateSession(cookie);
    ClearSessionCookie(ctx);
    return Results.Ok(new { ok = true });
});

// ----- Watchlist -----

app.MapGet("/api/watchlist", (WatchlistStore store) =>
{
    var snap = store.Snapshot();
    return Results.Json(new { icao24s = snap.Icao24s, last_seen = snap.LastSeen });
}).RequireAuthSession();

app.MapPost("/api/watchlist", async (HttpContext ctx, WatchlistStore store) =>
{
    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
    var root = doc.RootElement;
    var arr = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("icao24s", out var a)
        ? a
        : root;
    if (arr.ValueKind != JsonValueKind.Array)
    {
        return Results.BadRequest(new { error = "expected array or {icao24s: array}" });
    }
    if (arr.GetArrayLength() > 10_000)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }
    var incoming = new List<string>();
    foreach (var v in arr.EnumerateArray())
    {
        if (v.ValueKind == JsonValueKind.String && v.GetString() is string s)
        {
            incoming.Add(s);
        }
    }
    var snap = store.Replace(incoming);
    return Results.Json(new { icao24s = snap.Icao24s, last_seen = snap.LastSeen });
}).RequireAuthSession();

// ----- Notifications config -----

app.MapGet("/api/notifications/config", (NotificationsConfigStore store) =>
    Results.Json(new
    {
        version = NotificationsConfigStore.SchemaVersion,
        channels = store.Channels,
    })).RequireAuthSession();

app.MapPost("/api/notifications/config", async (HttpContext ctx, NotificationsConfigStore store) =>
{
    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
    var root = doc.RootElement;
    var arr = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("channels", out var a)
        ? a
        : root;
    if (arr.ValueKind != JsonValueKind.Array)
    {
        return Results.BadRequest(new { error = "expected array or {channels: array}" });
    }
    if (arr.GetArrayLength() > 100)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }
    var channels = new List<NotificationChannel?>();
    foreach (var item in arr.EnumerateArray())
    {
        channels.Add(item.Deserialize<NotificationChannel>(JsonOpts));
    }
    var updated = store.Replace(channels);
    return Results.Json(new { channels = updated });
}).RequireAuthSession();

app.MapPost("/api/notifications/test/{channelId}", async (string channelId, NotifierDispatcher dispatcher, CancellationToken ct) =>
{
    var ok = await dispatcher.TestChannelAsync(channelId, ct);
    return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "unknown channel" });
}).RequireAuthSession();

app.Map("/ws", async (HttpContext ctx, SnapshotBroadcaster broadcaster, CurrentSnapshot current, ILogger<Program> log) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        return Results.StatusCode(StatusCodes.Status400BadRequest);
    }
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    var sub = broadcaster.Subscribe();
    try
    {
        await SendAsync(socket, current.Json, ctx.RequestAborted);
        await foreach (var payload in sub.ReadAllAsync(ctx.RequestAborted))
        {
            await SendAsync(socket, payload, ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException ex)
    {
        log.LogDebug(ex, "ws client disconnected");
    }
    finally
    {
        broadcaster.Unsubscribe(sub.Id);
    }
    return Results.Empty;
});

app.Run();

static async Task SendAsync(WebSocket socket, string payload, CancellationToken ct)
{
    var bytes = Encoding.UTF8.GetBytes(payload);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
}

static string? ResolveStaticRoot(AppOptions options)
{
    _ = options; // reserved for future per-options override
    var envDir = Environment.GetEnvironmentVariable("FLIGHTJAR_STATIC_DIR");
    if (!string.IsNullOrEmpty(envDir) && Directory.Exists(envDir))
    {
        return envDir;
    }
    // Published-image layout: <content-root>/static
    var packaged = Path.Combine(AppContext.BaseDirectory, "static");
    if (Directory.Exists(packaged))
    {
        return packaged;
    }
    // Dev / test layout: walk up from bin/ to find repo-root/app/static.
    var cursor = new DirectoryInfo(AppContext.BaseDirectory);
    while (cursor is not null)
    {
        var candidate = Path.Combine(cursor.FullName, "app", "static");
        if (Directory.Exists(candidate))
        {
            return candidate;
        }
        cursor = cursor.Parent;
    }
    return null;
}

public partial class Program
{
    private static readonly DateTime _startedAt = DateTime.UtcNow;
    private static readonly JsonSerializerOptions JsonOpts = BuildJsonOpts();

    private static JsonSerializerOptions BuildJsonOpts()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        opts.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return opts;
    }
}
