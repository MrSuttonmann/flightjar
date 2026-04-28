using System.Diagnostics;
using FlightJar.Api.Hosting;
using FlightJar.Core.Configuration;
using FlightJar.Core.Stats;
using FlightJar.Persistence.Notifications;
using FlightJar.Persistence.Watchlist;

namespace FlightJar.Api.Telemetry;

/// <summary>
/// Periodically emits an anonymous "this install is alive" event so the
/// maintainer can count active deployments and see which optional features
/// are in use. Disabled when <c>TELEMETRY_ENABLED=0</c> or when no
/// <c>POSTHOG_API_KEY</c> has been baked in / configured.
/// </summary>
public sealed class TelemetryWorker : BackgroundService
{
    public const string PingEvent = "instance_ping";

    /// <summary>How long to wait after startup before the first ping —
    /// gives reference data + caches time to settle so the first
    /// payload reflects steady-state, not cold-start.</summary>
    public static readonly TimeSpan WarmupDelay = TimeSpan.FromMinutes(5);

    private readonly AppOptions _options;
    private readonly InstanceIdStore _instanceStore;
    private readonly PosthogClient _posthog;
    private readonly CurrentSnapshot _snapshot;
    private readonly SnapshotBroadcaster _broadcaster;
    private readonly NotificationsConfigStore _notifications;
    private readonly TelemetryAccumulator _accumulator;
    private readonly TelemetrySources _sources;
    private readonly TimeProvider _time;
    private readonly ILogger<TelemetryWorker> _logger;

    private readonly DateTimeOffset _startedAt;
    private long _lastFrameCount;
    private DateTimeOffset _lastPingAt;

    private IBeastFrameStats? _frameStats => _sources.FrameStats;
    private WatchlistStore? _watchlist => _sources.Watchlist;
    private PolarCoverage? _polarCoverage => _sources.PolarCoverage;
    private TrafficHeatmap? _trafficHeatmap => _sources.TrafficHeatmap;
    private string? _aircraftDbOverridePath => _sources.AircraftDbOverridePath;

    public TelemetryWorker(
        AppOptions options,
        InstanceIdStore instanceStore,
        PosthogClient posthog,
        CurrentSnapshot snapshot,
        SnapshotBroadcaster broadcaster,
        NotificationsConfigStore notifications,
        TelemetryAccumulator accumulator,
        TimeProvider time,
        ILogger<TelemetryWorker> logger,
        TelemetrySources? sources = null)
    {
        _options = options;
        _instanceStore = instanceStore;
        _posthog = posthog;
        _snapshot = snapshot;
        _broadcaster = broadcaster;
        _notifications = notifications;
        _accumulator = accumulator;
        _sources = sources ?? new TelemetrySources();
        _time = time;
        _logger = logger;
        _startedAt = time.GetUtcNow();
        _lastPingAt = _startedAt;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.TelemetryEnabled)
        {
            _logger.LogInformation("telemetry: disabled (TELEMETRY_ENABLED=0)");
            return;
        }
        if (string.IsNullOrWhiteSpace(TelemetryConfig.ApiKey))
        {
            // No destination baked into this build — silent no-op.
            _logger.LogDebug("telemetry: no destination baked in, skipping");
            return;
        }

        await _instanceStore.LoadOrCreateAsync(stoppingToken);
        _logger.LogInformation(
            "telemetry: enabled, instance {InstanceId}",
            _instanceStore.InstanceId);

        // Register the Person profile with PostHog on cold start.
        // Stable per-install attributes (version, feature flags, coarse
        // region, install shape) go into $set; the install's first-seen
        // timestamp goes into $set_once so it survives version bumps
        // without being overwritten. Runs before the warmup delay so a
        // Person exists in PostHog before any per-tick events land
        // against it.
        await SendIdentifyAsync(stoppingToken);

        try
        {
            await Task.Delay(WarmupDelay, _time, stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await SendPingAsync(stoppingToken);
            try
            {
                await Task.Delay(TelemetryConfig.PingInterval, _time, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task SendIdentifyAsync(CancellationToken ct)
    {
        var inputs = BuildInputs(drainAccumulator: false);
        var (set, setOnce) = TelemetryPayloadBuilder.BuildIdentify(
            options: _options,
            firstSeen: _instanceStore.FirstSeen,
            inputs: inputs);

        _ = await _posthog.IdentifyAsync(
            host: TelemetryConfig.Host,
            apiKey: TelemetryConfig.ApiKey,
            distinctId: _instanceStore.InstanceId,
            setProperties: set,
            setOnceProperties: setOnce,
            timestamp: _time.GetUtcNow(),
            ct: ct);
    }

    internal async Task SendPingAsync(CancellationToken ct)
    {
        var inputs = BuildInputs(drainAccumulator: true);
        var props = TelemetryPayloadBuilder.Build(
            options: _options,
            firstSeen: _instanceStore.FirstSeen,
            startedAt: _startedAt,
            now: _time.GetUtcNow(),
            inputs: inputs);

        _ = await _posthog.CaptureAsync(
            host: TelemetryConfig.Host,
            apiKey: TelemetryConfig.ApiKey,
            @event: PingEvent,
            distinctId: _instanceStore.InstanceId,
            properties: props,
            timestamp: _time.GetUtcNow(),
            ct: ct);
    }

    private TelemetryInputs BuildInputs(bool drainAccumulator)
    {
        var enabledChannelTypes = _notifications.Channels
            .Where(c => c.Enabled)
            .Select(c => c.Type.ToString().ToLowerInvariant())
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        var (maxKm, p95Km) = ComputePolarStats();
        var busiestHour = ComputeBusiestHourUtc();

        var now = _time.GetUtcNow();
        var currentFrames = _frameStats?.FrameCount ?? 0;
        long framesDelta;
        double secondsDelta;
        if (drainAccumulator)
        {
            framesDelta = Math.Max(0, currentFrames - _lastFrameCount);
            secondsDelta = (now - _lastPingAt).TotalSeconds;
            _lastFrameCount = currentFrames;
            _lastPingAt = now;
        }
        else
        {
            // Identify event runs before the first ping; nothing to derive.
            framesDelta = 0;
            secondsDelta = 0;
        }

        var accSnap = drainAccumulator
            ? _accumulator.DrainAndReset()
            : new TelemetryAccumulator.Snapshot(0, 0, 0, 0, 0, 0, 0, 0);

        return new TelemetryInputs
        {
            AircraftCount = _snapshot.Snapshot.Count,
            AircraftPositioned = _snapshot.Snapshot.Positioned,
            WsSubscribers = _broadcaster.SubscriberCount,
            WatchlistSize = _watchlist?.Count ?? 0,
            EnabledNotificationChannelTypes = enabledChannelTypes,
            FramesSinceLastPing = framesDelta,
            SecondsSinceLastPing = secondsDelta,
            Accumulator = accSnap,
            PolarCoverageMaxKm = maxKm,
            PolarCoverageP95Km = p95Km,
            HeatmapBusiestHourUtc = busiestHour,
            ProcessRssBytes = SafeProcessRss(),
            AircraftDbOverridden = !string.IsNullOrEmpty(_aircraftDbOverridePath)
                && File.Exists(_aircraftDbOverridePath),
        };
    }

    private (double MaxKm, double P95Km) ComputePolarStats()
    {
        if (_polarCoverage is null) return (0, 0);
        var view = _polarCoverage.SnapshotView();
        if (view.Bearings.Count == 0) return (0, 0);

        // Read the per-bucket max distances. Padding zeros for buckets
        // SnapshotView omitted (those with no observed positions yet)
        // matters for p95: a lopsided pattern (e.g. mountains north)
        // is supposed to read as a lower p95 than the absolute max.
        var values = new double[PolarCoverage.Buckets];
        foreach (var b in view.Bearings)
        {
            var idx = (int)Math.Floor(b.Angle / PolarCoverage.BucketDeg) % PolarCoverage.Buckets;
            if (idx >= 0 && idx < values.Length) values[idx] = b.DistKm;
        }
        Array.Sort(values);
        var max = values[^1];
        var p95Idx = Math.Clamp((int)Math.Floor(0.95 * (values.Length - 1)), 0, values.Length - 1);
        return (max, values[p95Idx]);
    }

    private int? ComputeBusiestHourUtc()
    {
        if (_trafficHeatmap is null) return null;
        var view = _trafficHeatmap.SnapshotView();
        if (view.Total == 0) return null;
        int best = 0, bestVal = -1;
        for (var h = 0; h < view.Hours.Count; h++)
        {
            if (view.Hours[h] > bestVal)
            {
                best = h;
                bestVal = view.Hours[h];
            }
        }
        return best;
    }

    private static long SafeProcessRss()
    {
        try
        {
            return Process.GetCurrentProcess().WorkingSet64;
        }
        catch
        {
            // GetCurrentProcess can throw on some constrained sandboxes;
            // a zero RSS here is fine — the bucket will round to 0.
            return 0;
        }
    }
}
