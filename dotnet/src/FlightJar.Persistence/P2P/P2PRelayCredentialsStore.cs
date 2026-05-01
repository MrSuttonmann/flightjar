using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Persistence.P2P;

/// <summary>
/// Persists the bearer token issued by the relay's <c>POST /register</c>
/// endpoint. Kept separate from <see cref="P2PConfigStore"/> because the
/// token is a credential — never appears in <c>/api/p2p/config</c> response
/// bodies and is set automatically on first connect to the relay.
/// </summary>
public sealed class P2PRelayCredentialsStore
{
    public const int SchemaVersion = 1;

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly ILogger _logger;
    private string? _token;

    public P2PRelayCredentialsStore(string? path = null, ILogger<P2PRelayCredentialsStore>? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger<P2PRelayCredentialsStore>.Instance;
    }

    public string? Token
    {
        get { lock (_gate) { return _token; } }
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var raw = await File.ReadAllTextAsync(_path, ct);
            var payload = JsonSerializer.Deserialize<PersistedPayload>(raw, JsonOpts);
            if (payload?.Token is { Length: > 0 } token)
            {
                lock (_gate) { _token = token; }
                _logger.LogInformation("loaded P2P relay credentials from {Path}", _path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "P2P relay credentials unreadable at {Path}", _path);
        }
    }

    public async Task SetTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token)) throw new ArgumentException("token cannot be empty", nameof(token));
        lock (_gate) { _token = token; }
        await PersistAsync(token, ct);
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        lock (_gate) { _token = null; }
        if (_path is not null && File.Exists(_path))
        {
            try { File.Delete(_path); }
            catch (Exception ex) { _logger.LogWarning(ex, "couldn't delete {Path}", _path); }
        }
        return Task.CompletedTask;
    }

    private async Task PersistAsync(string token, CancellationToken ct)
    {
        if (_path is null) return;
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var payload = new PersistedPayload { Version = SchemaVersion, Token = token };
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(payload, JsonOpts), ct);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "couldn't persist P2P relay credentials");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private sealed class PersistedPayload
    {
        public int Version { get; set; }
        public string? Token { get; set; }
    }
}
