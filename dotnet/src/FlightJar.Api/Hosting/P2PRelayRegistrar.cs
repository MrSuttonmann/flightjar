using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FlightJar.Core.Configuration;
using FlightJar.Persistence.P2P;

namespace FlightJar.Api.Hosting;

/// <summary>
/// Resolves the bearer token used to authenticate with the P2P relay.
/// Precedence: <c>P2P_RELAY_TOKEN</c> env override → token persisted to
/// <c>/data/p2p_credentials.json</c> → fresh registration via
/// <c>POST &lt;relay&gt;/register</c>. Persisted tokens survive restart so
/// the per-IP rate limit on <c>/register</c> isn't burned on every restart.
/// </summary>
public sealed class P2PRelayRegistrar
{
    private readonly AppOptions _options;
    private readonly P2PRelayCredentialsStore _creds;
    private readonly HttpClient _http;
    private readonly ILogger<P2PRelayRegistrar> _logger;

    public P2PRelayRegistrar(
        AppOptions options,
        P2PRelayCredentialsStore creds,
        HttpClient http,
        ILogger<P2PRelayRegistrar> logger)
    {
        _options = options;
        _creds = creds;
        _http = http;
        _logger = logger;
    }

    /// <summary>Returns the token to use for the next relay connect.
    /// Registers against the relay if no env override / persisted token is
    /// present. Throws on registration failure so the caller can apply
    /// connection backoff.</summary>
    public async Task<string> EnsureTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_options.P2PRelayToken))
        {
            return _options.P2PRelayToken!;
        }
        var existing = _creds.Token;
        if (!string.IsNullOrEmpty(existing))
        {
            return existing!;
        }
        var fresh = await RegisterAsync(ct);
        await _creds.SetTokenAsync(fresh, ct);
        return fresh;
    }

    /// <summary>Discards the persisted token (used after the relay rejects
    /// it as evicted/unknown). The next <see cref="EnsureTokenAsync"/> call
    /// will register a fresh one. No-op when an env override is set, since
    /// the operator chose that value explicitly.</summary>
    public async Task InvalidateAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.P2PRelayToken))
        {
            await _creds.ClearAsync(ct);
        }
    }

    private async Task<string> RegisterAsync(CancellationToken ct)
    {
        var registerUrl = ToRegisterUrl(_options.P2PRelayUrl);
        _logger.LogInformation("P2P registering with relay at {Url}", registerUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, registerUrl);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<RegisterPayload>(cancellationToken: ct);
        if (payload?.Token is not { Length: > 0 } token)
        {
            throw new InvalidOperationException("P2P relay /register returned no token");
        }
        return token;
    }

    /// <summary>Convert a <c>wss://host[:port]/ws</c> relay URL into the
    /// matching <c>https://host[:port]/register</c> URL.</summary>
    public static string ToRegisterUrl(string relayUrl)
    {
        var uri = new Uri(relayUrl);
        var scheme = uri.Scheme switch
        {
            "wss" => "https",
            "ws" => "http",
            "http" => "http",
            "https" => "https",
            _ => throw new ArgumentException($"unsupported relay URL scheme: {uri.Scheme}", nameof(relayUrl)),
        };
        var builder = new UriBuilder(uri)
        {
            Scheme = scheme,
            Path = "/register",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        // UriBuilder propagates the original port even after a scheme swap;
        // collapse back to the default port for the new scheme so the URL
        // stays clean (no `:443` on https URLs etc.).
        if (uri.IsDefaultPort) builder.Port = -1;
        return builder.Uri.ToString();
    }

    private sealed record RegisterPayload([property: JsonPropertyName("token")] string? Token);
}
