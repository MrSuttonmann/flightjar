using System.IO.Compression;
using System.Text.Json;
using FlightJar.Core.State.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Persistence.State;

/// <summary>
/// Atomic gzipped-JSON save/load for <c>AircraftRegistry</c> state. Mirrors
/// <c>app/persistence.py</c>. Writes go to <c>.tmp</c> then rename, so readers
/// never see a half-written file.
/// </summary>
public sealed class StateSnapshotStore
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public StateSnapshotStore(string path, ILogger<StateSnapshotStore>? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger<StateSnapshotStore>.Instance;
    }

    public async Task SaveAsync(StateSnapshotPayload payload, CancellationToken ct = default)
    {
        await _saveGate.WaitAsync(ct);
        string? tmp = null;
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            // Per-call tmp filename so a crash leaves at most a single
            // orphan behind, and concurrent saves never collide.
            tmp = $"{_path}.{Guid.NewGuid():N}.tmp";
            await using (var file = File.Create(tmp))
            await using (var gz = new GZipStream(file, CompressionLevel.Optimal))
            {
                await JsonSerializer.SerializeAsync(gz, payload, JsonOpts, ct);
            }
            File.Move(tmp, _path, overwrite: true);
            tmp = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "couldn't persist state snapshot to {Path}", _path);
        }
        finally
        {
            if (tmp is not null && File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            }
            _saveGate.Release();
        }
    }

    public async Task<StateSnapshotPayload?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }
        try
        {
            await using var file = File.OpenRead(_path);
            await using var gz = new GZipStream(file, CompressionMode.Decompress);
            return await JsonSerializer.DeserializeAsync<StateSnapshotPayload>(gz, JsonOpts, ct);
        }
        catch (JsonException ex)
        {
            // Older / incompatible schema — start fresh quietly.
            _logger.LogInformation(
                "persisted state at {Path} has an incompatible schema, starting fresh ({Reason})",
                _path, ex.Message);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "couldn't read persisted state at {Path}", _path);
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
