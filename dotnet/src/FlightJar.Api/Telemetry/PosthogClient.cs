using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Api.Telemetry;

/// <summary>
/// Minimal client for PostHog's <c>/capture/</c> endpoint. Single-event
/// POST, no batching, no retries — drop-on-failure is fine here since the
/// next periodic tick will try again.
/// </summary>
public sealed class PosthogClient
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public PosthogClient(HttpClient http, ILogger<PosthogClient>? logger = null)
    {
        _http = http;
        _logger = logger ?? NullLogger<PosthogClient>.Instance;
    }

    public async Task<bool> CaptureAsync(
        string host,
        string apiKey,
        string @event,
        string distinctId,
        IReadOnlyDictionary<string, object?> properties,
        DateTimeOffset timestamp,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return false;

        var url = host.TrimEnd('/') + "/capture/";
        var body = new Dictionary<string, object?>
        {
            ["api_key"] = apiKey,
            ["event"] = @event,
            ["distinct_id"] = distinctId,
            ["properties"] = properties,
            ["timestamp"] = timestamp.ToString("O"),
        };

        try
        {
            using var resp = await _http.PostAsJsonAsync(url, body, JsonOpts, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "telemetry: posthog capture returned {Status} {Reason}",
                    (int)resp.StatusCode, resp.ReasonPhrase);
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Telemetry failures are not user-visible. Log at Debug so a
            // network glitch doesn't spam Information-level container logs.
            _logger.LogDebug(ex, "telemetry: posthog capture failed");
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
