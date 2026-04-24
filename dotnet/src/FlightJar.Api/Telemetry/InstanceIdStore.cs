using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Api.Telemetry;

/// <summary>
/// Persists a stable random per-install identifier used as the PostHog
/// <c>distinct_id</c>. Generated on first run, then reused across restarts
/// so the maintainer can count active installs without correlating to any
/// real user identity.
/// </summary>
public sealed class InstanceIdStore
{
    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private string? _instanceId;
    private DateTimeOffset? _firstSeen;

    public InstanceIdStore(string? path, TimeProvider? time = null, ILogger<InstanceIdStore>? logger = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<InstanceIdStore>.Instance;
    }

    public string InstanceId
    {
        get { lock (_gate) { return _instanceId ?? throw new InvalidOperationException("InstanceIdStore.LoadOrCreateAsync not yet called"); } }
    }

    public DateTimeOffset FirstSeen
    {
        get { lock (_gate) { return _firstSeen ?? throw new InvalidOperationException("InstanceIdStore.LoadOrCreateAsync not yet called"); } }
    }

    public async Task LoadOrCreateAsync(CancellationToken ct = default)
    {
        if (_path is null)
        {
            // No persistence configured (test / ephemeral mode). Mint a
            // fresh ID per process — fine since nothing reads it across
            // restarts in that case.
            lock (_gate)
            {
                _instanceId = Guid.NewGuid().ToString("N");
                _firstSeen = _time.GetUtcNow();
            }
            return;
        }

        if (File.Exists(_path))
        {
            try
            {
                using var stream = File.OpenRead(_path);
                var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = doc.RootElement;
                var id = root.TryGetProperty("instance_id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;
                var firstSeen = root.TryGetProperty("first_seen", out var fsEl) && fsEl.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.TryParse(fsEl.GetString(), out var parsed) ? parsed : (DateTimeOffset?)null
                    : null;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    lock (_gate)
                    {
                        _instanceId = id;
                        _firstSeen = firstSeen ?? _time.GetUtcNow();
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "telemetry: failed to read {Path}, regenerating instance id", _path);
            }
        }

        var newId = Guid.NewGuid().ToString("N");
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _instanceId = newId;
            _firstSeen = now;
        }
        await PersistAsync(newId, now, ct);
    }

    /// <summary>
    /// Mints a fresh instance id and resets <see cref="FirstSeen"/> to now,
    /// then persists the change. The previous PostHog Person is left in
    /// place upstream — only the install's link to it is severed, so future
    /// pings + frontend events register against the new id instead.
    /// </summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        var newId = Guid.NewGuid().ToString("N");
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _instanceId = newId;
            _firstSeen = now;
        }
        await PersistAsync(newId, now, ct);
    }

    private async Task PersistAsync(string id, DateTimeOffset firstSeen, CancellationToken ct)
    {
        if (_path is null) return;

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var tmp = _path + ".tmp";
            await using (var fs = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(fs, new { instance_id = id, first_seen = firstSeen }, cancellationToken: ct);
            }
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "telemetry: failed to persist instance id to {Path}", _path);
        }
    }
}
