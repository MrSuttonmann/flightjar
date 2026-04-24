using FlightJar.Api.Hosting;
using FlightJar.Core.Configuration;
using FlightJar.Persistence.Notifications;

namespace FlightJar.Api.Telemetry;

/// <summary>
/// Periodically emits an anonymous "this install is alive" event so the
/// maintainer can count active deployments and see which optional features
/// are in use. Disabled when <c>TELEMETRY_ENABLED=0</c> or when no
/// <c>POSTHOG_API_KEY</c> has been baked in / configured.
/// </summary>
public sealed class TelemetryWorker(
    AppOptions options,
    InstanceIdStore instanceStore,
    PosthogClient posthog,
    CurrentSnapshot snapshot,
    SnapshotBroadcaster broadcaster,
    NotificationsConfigStore notifications,
    TimeProvider time,
    ILogger<TelemetryWorker> logger) : BackgroundService
{
    public const string PingEvent = "instance_ping";

    /// <summary>How long to wait after startup before the first ping —
    /// gives reference data + caches time to settle so the first
    /// payload reflects steady-state, not cold-start.</summary>
    public static readonly TimeSpan WarmupDelay = TimeSpan.FromMinutes(5);

    private readonly DateTimeOffset _startedAt = time.GetUtcNow();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.TelemetryEnabled)
        {
            logger.LogInformation("telemetry: disabled (TELEMETRY_ENABLED=0)");
            return;
        }
        if (string.IsNullOrWhiteSpace(TelemetryConfig.ApiKey))
        {
            // No destination baked into this build — silent no-op.
            logger.LogDebug("telemetry: no destination baked in, skipping");
            return;
        }

        await instanceStore.LoadOrCreateAsync(stoppingToken);
        logger.LogInformation(
            "telemetry: enabled, instance {InstanceId}",
            instanceStore.InstanceId);

        try
        {
            await Task.Delay(WarmupDelay, time, stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await SendPingAsync(stoppingToken);
            try
            {
                await Task.Delay(TelemetryConfig.PingInterval, time, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task SendPingAsync(CancellationToken ct)
    {
        var props = TelemetryPayloadBuilder.Build(
            options: options,
            firstSeen: instanceStore.FirstSeen,
            startedAt: _startedAt,
            now: time.GetUtcNow(),
            aircraftCount: snapshot.Snapshot.Count,
            aircraftPositioned: snapshot.Snapshot.Positioned,
            wsSubscribers: broadcaster.SubscriberCount,
            enabledNotificationChannels: notifications.Channels.Count(c => c.Enabled));

        _ = await posthog.CaptureAsync(
            host: TelemetryConfig.Host,
            apiKey: TelemetryConfig.ApiKey,
            @event: PingEvent,
            distinctId: instanceStore.InstanceId,
            properties: props,
            timestamp: time.GetUtcNow(),
            ct: ct);
    }
}

/// <summary>
/// Pure helper that turns the current app state into a PostHog properties
/// dictionary. Kept separate from the worker so the payload shape can be
/// asserted in unit tests without spinning up a background service.
/// </summary>
public static class TelemetryPayloadBuilder
{
    public static IReadOnlyDictionary<string, object?> Build(
        AppOptions options,
        DateTimeOffset firstSeen,
        DateTimeOffset startedAt,
        DateTimeOffset now,
        int aircraftCount,
        int aircraftPositioned,
        int wsSubscribers,
        int enabledNotificationChannels)
    {
        var props = new Dictionary<string, object?>
        {
            ["$lib"] = "flightjar",
            ["version"] = Environment.GetEnvironmentVariable("FLIGHTJAR_VERSION") ?? "dev",
            ["uptime_s"] = (long)(now - startedAt).TotalSeconds,
            ["first_seen_iso"] = firstSeen.ToString("O"),

            ["feature_flight_routes"] = options.FlightRoutesEnabled,
            ["feature_metar"] = options.MetarEnabled,
            ["feature_openaip"] = !string.IsNullOrWhiteSpace(options.OpenAipApiKey),
            ["feature_blackspots"] = options.BlackspotsEnabled
                && options.LatRef is not null && options.LonRef is not null,
            ["feature_notification_channels"] = enabledNotificationChannels,

            ["aircraft_count"] = aircraftCount,
            ["aircraft_positioned"] = aircraftPositioned,
            ["ws_subscribers"] = wsSubscribers,
        };

        // Coarse 10° region — enough to spot UK vs Europe vs US clustering,
        // not enough to identify a household. Skip when the receiver
        // location isn't configured.
        if (options.LatRef is double lat && options.LonRef is double lon)
        {
            props["region_lat_10"] = (int)Math.Round(lat / 10.0) * 10;
            props["region_lon_10"] = (int)Math.Round(lon / 10.0) * 10;
        }

        return props;
    }
}
