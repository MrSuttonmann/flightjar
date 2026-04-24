using System.Reflection;

namespace FlightJar.Api.Telemetry;

/// <summary>
/// Baked-in telemetry destination. The PostHog project key is injected
/// at build time via the MSBuild property <c>PosthogApiKey</c> — set by
/// CI from a repo secret and forwarded to <c>dotnet publish</c> through
/// the Dockerfile's <c>POSTHOG_API_KEY</c> build arg. Local dev builds
/// leave the key empty, so the worker no-ops.
///
/// PostHog project keys (<c>phc_*</c>) are designed to be public-facing
/// in client SDKs, so embedding one in the published assembly is the
/// intended pattern — the secret-store workflow is just so the key
/// doesn't sit in the public source tree.
/// </summary>
internal static class TelemetryConfig
{
    public static readonly string Host = ReadAttr("PosthogHost") ?? "https://eu.i.posthog.com";
    public static readonly string ApiKey = ReadAttr("PosthogApiKey") ?? "";
    public static readonly TimeSpan PingInterval = TimeSpan.FromHours(24);

    private static string? ReadAttr(string key)
    {
        foreach (var attr in typeof(TelemetryConfig).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attr.Key == key && !string.IsNullOrEmpty(attr.Value))
            {
                return attr.Value;
            }
        }
        return null;
    }
}
