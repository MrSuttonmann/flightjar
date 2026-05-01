using System.Text.Json;
using System.Threading.Channels;
using FlightJar.Api.Auth;
using FlightJar.Api.Configuration;
using FlightJar.Api.Endpoints;
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
using FlightJar.Persistence.P2P;
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

// Optional second channel for the JSONL writer. Same drop-oldest policy
// and capacity as the registry channel — disk I/O latency must never
// backpressure the TCP reader either. Only created when the user has
// asked for any JSONL output (file or stdout).
if (JsonlWriterService.IsConfigured(options))
{
    var jsonlChannel = Channel.CreateBounded<JsonlFrame>(new BoundedChannelOptions(16384)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
    });
    builder.Services.AddSingleton(jsonlChannel.Writer);
    builder.Services.AddSingleton(jsonlChannel.Reader);
    builder.Services.AddHostedService<JsonlWriterService>();
}

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
builder.Services.AddSingleton<PeerAircraftCache>();
var p2pConfigPath = !string.IsNullOrEmpty(dataDir) ? Path.Combine(dataDir, "p2p.json") : null;
builder.Services.AddSingleton(sp => new P2PConfigStore(
    p2pConfigPath, sp.GetService<ILogger<P2PConfigStore>>()));
var p2pCredentialsPath = !string.IsNullOrEmpty(dataDir) ? Path.Combine(dataDir, "p2p_credentials.json") : null;
builder.Services.AddSingleton(sp => new P2PRelayCredentialsStore(
    p2pCredentialsPath, sp.GetService<ILogger<P2PRelayCredentialsStore>>()));
// The relay client is registered when the env-only kill switch is on
// (default). Runtime on/off lives in P2PConfigStore so the user can
// toggle it from the About dialog without restarting; setting
// P2P_ENABLED=0 keeps the BackgroundService out of the process entirely
// — used by the e2e harness so the relay's outbound chatter doesn't
// destabilise timing-sensitive tests.
if (options.P2PEnabled)
{
    builder.Services.AddHttpClient<P2PRelayRegistrar>();
    builder.Services.AddHostedService<P2PRelayClientService>();
}
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

// Optional-dependency bundles for the registry worker. Composed lazily from
// already-registered singletons; null members opt out of their own pass.
builder.Services.AddSingleton(sp => new RegistrySnapshotEnrichers(
    Adsbdb: sp.GetService<AdsbdbClient>(),
    Metar: sp.GetService<MetarClient>(),
    Airports: sp.GetService<AirportsDb>(),
    Airlines: sp.GetService<AirlinesDb>()));
builder.Services.AddSingleton(sp => new RegistryStatsCollectors(
    PolarCoverage: sp.GetService<PolarCoverage>(),
    TrafficHeatmap: sp.GetService<TrafficHeatmap>(),
    PolarHeatmap: sp.GetService<PolarHeatmap>()));

// Hosted services. Register as singleton first so endpoints can resolve them
// (AddHostedService alone only registers IHostedService, not the concrete type).
builder.Services.AddSingleton<BeastConsumerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BeastConsumerService>());
builder.Services.AddSingleton<RegistryWorker>();
builder.Services.AddSingleton<IBeastFrameStats>(sp => sp.GetRequiredService<RegistryWorker>());
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
builder.Services.AddSingleton(sp => new TelemetrySources(
    FrameStats: sp.GetService<IBeastFrameStats>(),
    Watchlist: sp.GetService<WatchlistStore>(),
    PolarCoverage: sp.GetService<PolarCoverage>(),
    TrafficHeatmap: sp.GetService<TrafficHeatmap>(),
    AircraftDbOverridePath: aircraftDbOverridePath));
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
    sp.GetService<TelemetrySources>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryWorker>());

var app = builder.Build();

// Load persisted state synchronously before the host kicks off background services.
await app.Services.GetRequiredService<WatchlistStore>().LoadAsync();
await app.Services.GetRequiredService<NotificationsConfigStore>().LoadAsync();
await app.Services.GetRequiredService<P2PConfigStore>().LoadAsync();
await app.Services.GetRequiredService<P2PRelayCredentialsStore>().LoadAsync();
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

    // Service worker must be served from the root path so its scope covers
    // the whole app. The file lives under /static/ but browsers restrict a
    // SW's scope to the directory it's served from, so we expose it at /sw.js
    // with Service-Worker-Allowed: / to explicitly extend that scope.
    var swPath = Path.Combine(staticRoot, "sw.js");
    app.MapGet("/sw.js", (HttpContext ctx) =>
    {
        if (!File.Exists(swPath))
        {
            return Results.NotFound();
        }
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.Append("Service-Worker-Allowed", "/");
        return Results.File(swPath, "application/javascript; charset=utf-8");
    });
}

// ----- Endpoint groups (live in FlightJar.Api/Endpoints/) -----
app.MapStatsEndpoints();
app.MapMapConfigEndpoints();
app.MapTelemetryEndpoints();
app.MapOpenAipEndpoints();
app.MapStatsHistoryEndpoints();
app.MapBlackspotsEndpoints();
app.MapAircraftEndpoints();
app.MapAuthEndpoints();
app.MapWatchlistEndpoints();
app.MapNotificationsEndpoints();
app.MapWsEndpoint();
app.MapP2PEndpoints();

app.Run();

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
    /// <summary>Process start time. Read by <see cref="StatsEndpoints"/>
    /// for the <c>uptime_s</c> field on <c>/api/stats</c>.</summary>
    internal static readonly DateTime StartedAt = DateTime.UtcNow;
}
