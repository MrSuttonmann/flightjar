using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Persistence.P2P;

/// <summary>
/// User-toggleable Community Feed settings. Persisted to
/// <c>/data/p2p.json</c> so a flip from the UI survives restart.
/// <para>Fresh installs default to <c>Enabled=false, ConsentGiven=false</c> —
/// the relay client won't dial out until the operator opts in via the
/// Community Feed dialog. Existing installs on schema v1 are grandfathered
/// (<c>ConsentGiven = Enabled</c>) so users who already opted in via the
/// previous default stay opted in across the upgrade.</para>
/// </summary>
public sealed class P2PConfigStore
{
    public const int SchemaVersion = 2;

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
            var version = payload.Version ?? 0;
            var enabled = payload.Enabled ?? false;
            // v1 had no ConsentGiven and defaulted Enabled=true. Grandfather
            // those users in so the upgrade doesn't yank sharing out from
            // under anyone who'd already opted in via the previous default.
            var consent = payload.ConsentGiven ?? (version <= 1 ? enabled : false);
            lock (_gate)
            {
                _config = new P2PConfig
                {
                    Enabled = enabled,
                    ConsentGiven = consent,
                    ShareSiteName = payload.ShareSiteName ?? false,
                };
            }
            _logger.LogInformation(
                "loaded Community Feed config from {Path} (enabled={Enabled}, consent={Consent}, share_site_name={Share})",
                _path, _config.Enabled, _config.ConsentGiven, _config.ShareSiteName);

            // Migrate the on-disk file forward so subsequent reads see v2.
            if (version < SchemaVersion)
            {
                Persist();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Community Feed config unreadable at {Path}", _path);
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
                    ConsentGiven = _config.ConsentGiven,
                    ShareSiteName = _config.ShareSiteName,
                };
            }
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, JsonOpts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "couldn't persist Community Feed config");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private sealed class PersistedPayload
    {
        public int? Version { get; set; }
        public bool? Enabled { get; set; }
        public bool? ConsentGiven { get; set; }
        public bool? ShareSiteName { get; set; }
    }
}

public sealed record P2PConfig
{
    /// <summary>Active on/off state. Only meaningful when <see cref="ConsentGiven"/> is true.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// True iff the operator has explicitly opted in via the Community Feed dialog
    /// (or was grandfathered in from a v1 config that already had Enabled=true).
    /// The relay client refuses to dial out unless this is true.
    /// </summary>
    public bool ConsentGiven { get; init; }

    public bool ShareSiteName { get; init; }
}
