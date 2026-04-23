using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FlightJar.Api.Configuration;
using FlightJar.Api.Hosting;
using FlightJar.Clients.Adsbdb;
using FlightJar.Clients.Metar;
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

var builder = WebApplication.CreateBuilder(args);

var options = AppOptionsBinder.FromEnvironment();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);

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

var app = builder.Build();

// Load persisted state synchronously before the host kicks off background services.
await app.Services.GetRequiredService<WatchlistStore>().LoadAsync();
await app.Services.GetRequiredService<NotificationsConfigStore>().LoadAsync();

// Warm the external-client disk caches in parallel so adsbdb / planespotters
// / METAR lookups can skip the upstream round-trip on early snapshots.
await Task.WhenAll(
    app.Services.GetRequiredService<AdsbdbClient>().LoadCacheAsync(),
    app.Services.GetRequiredService<PlanespottersClient>().LoadCacheAsync(),
    app.Services.GetRequiredService<MetarClient>().LoadCacheAsync(),
    app.Services.GetRequiredService<VfrmapCycle>().LoadCacheAsync());

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
// Mirrors Python's `RevalidatingStaticFiles`: every asset under /static/
// sets Cache-Control: no-cache so the browser always revalidates via ETag
// (304 when unchanged, full re-fetch on change) — essential because
// app.js's ES-module imports don't carry content-hashed query strings.
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

    app.MapGet("/", (HttpContext ctx) =>
    {
        var indexPath = Path.Combine(staticRoot, "index.html");
        if (!File.Exists(indexPath))
        {
            return Results.NotFound();
        }
        ctx.Response.Headers.CacheControl = "no-cache";
        return Results.File(indexPath, "text/html; charset=utf-8");
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

// ----- Watchlist -----

app.MapGet("/api/watchlist", (WatchlistStore store) =>
{
    var snap = store.Snapshot();
    return Results.Json(new { icao24s = snap.Icao24s, last_seen = snap.LastSeen });
});

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
});

// ----- Notifications config -----

app.MapGet("/api/notifications/config", (NotificationsConfigStore store) =>
    Results.Json(new
    {
        version = NotificationsConfigStore.SchemaVersion,
        channels = store.Channels,
    }));

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
});

app.MapPost("/api/notifications/test/{channelId}", async (string channelId, NotifierDispatcher dispatcher, CancellationToken ct) =>
{
    var ok = await dispatcher.TestChannelAsync(channelId, ct);
    return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "unknown channel" });
});

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
