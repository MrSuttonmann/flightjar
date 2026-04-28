using FlightJar.Api.Auth;
using FlightJar.Api.Telemetry;
using FlightJar.Core.Configuration;

namespace FlightJar.Api.Endpoints;

internal static class TelemetryEndpoints
{
    public static IEndpointRouteBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder app)
    {
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

        return app;
    }
}
