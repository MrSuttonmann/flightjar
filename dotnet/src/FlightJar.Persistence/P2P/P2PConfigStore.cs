using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Persistence.P2P;

/// <summary>
/// User-toggleable P2P federation settings. Persisted to
/// <c>/data/p2p.json</c> so a flip from the UI survives restart.
/// First run with no file produces the documented defaults
/// (<c>enabled=true</c>, <c>share_site_name=false</c>) — federation
/// is on out of the box; opt out is one click in the About dialog.
/// </summary>
public sealed class P2PConfigStore
{
    public const int SchemaVersion = 1;

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly ILogger _logger;
    private P2PConfig _config = new();

    public event Action<P2PConfig>? Changed;

    public P2PConfigStore(string? path = null, ILogger<P2PConfigStore>? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger<P2PConfigStore>.Instance;
    }

    public P2PConfig Current
    {
        get { lock (_gate) { return _config; } }
    }

    public P2PConfig Replace(P2PConfig incoming)
    {
        var cleaned = incoming with { };
        bool changed;
        lock (_gate)
        {
            changed = _config != cleaned;
            if (changed)
            {
                _config = cleaned;
            }
        }
        if (changed)
        {
            Persist();
            Changed?.Invoke(cleaned);
        }
        return cleaned;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }
        try
        {
            var raw = await File.ReadAllTextAsync(_path, ct);
            var payload = JsonSerializer.Deserialize<PersistedPayload>(raw, JsonOpts);
            if (payload is null)
            {
                return;
            }
            lock (_gate)
            {
                _config = new P2PConfig
                {
                    Enabled = payload.Enabled ?? true,
                    ShareSiteName = payload.ShareSiteName ?? false,
                };
            }
            _logger.LogInformation(
                "loaded P2P config from {Path} (enabled={Enabled}, share_site_name={Share})",
                _path, _config.Enabled, _config.ShareSiteName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "P2P config unreadable at {Path}", _path);
        }
    }

    private void Persist()
    {
        if (_path is null)
        {
            return;
        }
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            PersistedPayload payload;
            lock (_gate)
            {
                payload = new PersistedPayload
                {
                    Version = SchemaVersion,
                    Enabled = _config.Enabled,
                    ShareSiteName = _config.ShareSiteName,
                };
            }
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, JsonOpts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "couldn't persist P2P config");
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
        public bool? Enabled { get; set; }
        public bool? ShareSiteName { get; set; }
    }
}

public sealed record P2PConfig
{
    public bool Enabled { get; init; } = true;
    public bool ShareSiteName { get; init; }
}
