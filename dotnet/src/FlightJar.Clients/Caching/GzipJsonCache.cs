using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FlightJar.Clients.Caching;

/// <summary>
/// Gzipped-JSON on-disk cache with atomic write-to-temp + rename. The payload
/// type is opaque to this class — callers supply the <see cref="JsonSerializerOptions"/>
/// and bucket dictionaries to save/restore.
/// </summary>
public sealed class GzipJsonCache
{
    private readonly ILogger _logger;

    /// <summary>Per-path save gate so concurrent <c>SaveAsync</c> calls for the
    /// same cache file serialise instead of racing on a shared <c>.tmp</c>.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _saveGates = new();

    public GzipJsonCache(ILogger logger) => _logger = logger;

    /// <summary>
    /// Load a gzipped-JSON payload from <paramref name="path"/>. Returns null
    /// when the file doesn't exist or fails to parse (never throws — the
    /// caller always treats a failed load as "start fresh").
    /// </summary>
    public async Task<TPayload?> LoadAsync<TPayload>(
        string path,
        JsonSerializerOptions? options = null,
        CancellationToken ct = default)
        where TPayload : class
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            await using var file = File.OpenRead(path);
            await using var gz = new GZipStream(file, CompressionMode.Decompress);
            return await JsonSerializer.DeserializeAsync<TPayload>(gz, options, ct);
        }
        catch (JsonException ex)
        {
            // Most likely a cache file written by an older / incompatible
            // schema. Start fresh silently — logging the full JSON path + stack
            // trace would be alarming for a perfectly recoverable condition.
            _logger.LogInformation(
                "cache at {Path} has an incompatible schema, starting fresh ({Reason})",
                path, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "cache at {Path} unreadable — starting fresh", path);
            return null;
        }
    }

    /// <summary>
    /// Atomically persist <paramref name="payload"/> to <paramref name="path"/>:
    /// write to a sibling <c>.tmp</c> then rename over the target. Concurrent
    /// saves of the same path are serialised via a per-path semaphore, and
    /// the <c>.tmp</c> filename carries a per-call suffix so even cross-
    /// process races can't clobber each other's work-in-progress. Swallows
    /// all exceptions after logging them — persistence failures must never
    /// break the caller's main flow.
    /// </summary>
    public async Task SaveAsync<TPayload>(
        string path,
        TPayload payload,
        JsonSerializerOptions? options = null,
        CancellationToken ct = default)
        where TPayload : class
    {
        var gate = _saveGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        string? tmp = null;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            // Unique suffix per attempt so a surprise crash leaves at most a
            // single orphaned .tmp-<suffix> behind rather than colliding with
            // the next write.
            tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            await using (var file = File.Create(tmp))
            await using (var gz = new GZipStream(file, CompressionLevel.Optimal))
            {
                await JsonSerializer.SerializeAsync(gz, payload, options, ct);
            }
            File.Move(tmp, path, overwrite: true);
            tmp = null; // successfully moved; don't delete
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "couldn't persist cache at {Path}", path);
        }
        finally
        {
            if (tmp is not null && File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            }
            gate.Release();
        }
    }
}
