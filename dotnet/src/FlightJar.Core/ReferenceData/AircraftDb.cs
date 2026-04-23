using System.Collections.Frozen;
using System.IO.Compression;
using FlightJar.Core.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Core.ReferenceData;

/// <summary>
/// ICAO24 → registration/type lookup backed by a gzipped semicolon-separated
/// CSV in the <a href="https://github.com/wiedehopf/tar1090-db">tar1090-db /
/// Mictronics</a> shape. Mirrors <c>app/aircraft_db.py</c>. Load is opt-in —
/// if no file is present, lookups no-op and snapshot enrichment stays empty.
/// </summary>
public sealed class AircraftDb : IAircraftDb
{
    private readonly ILogger _logger;
    private FrozenDictionary<string, AircraftDbEntry> _entries = FrozenDictionary<string, AircraftDbEntry>.Empty;

    public AircraftDb(ILogger<AircraftDb>? logger = null)
    {
        _logger = logger ?? NullLogger<AircraftDb>.Instance;
    }

    public int Count => _entries.Count;

    public AircraftDbEntry? Lookup(string icao)
    {
        if (string.IsNullOrEmpty(icao))
        {
            return null;
        }
        return _entries.TryGetValue(icao.ToLowerInvariant(), out var e) ? e : null;
    }

    /// <summary>Parse a gzipped CSV and swap it in atomically on success.</summary>
    public async Task<int> LoadFromAsync(string path, CancellationToken ct = default)
    {
        var fresh = new Dictionary<string, AircraftDbEntry>(StringComparer.Ordinal);
        await using var file = File.OpenRead(path);
        await using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }
            var parts = line.Split(';');
            if (parts.Length < 5 || parts[0].Length == 0)
            {
                continue;
            }
            var icao = parts[0].Trim().ToLowerInvariant();
            if (icao.Length == 0)
            {
                continue;
            }
            var reg = NullIfEmpty(parts[1]);
            var typeIcao = NullIfEmpty(parts[2]);
            var typeLong = NullIfEmpty(parts[4]);
            if (reg is null && typeIcao is null && typeLong is null)
            {
                continue;
            }
            fresh[icao] = new AircraftDbEntry(reg, typeIcao, typeLong);
        }
        _entries = fresh.ToFrozenDictionary(StringComparer.Ordinal);
        return _entries.Count;
    }

    /// <summary>Try each candidate path; load the first one that exists.</summary>
    public async Task<int> LoadFirstAvailableAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        foreach (var p in paths)
        {
            if (!File.Exists(p))
            {
                continue;
            }
            try
            {
                var n = await LoadFromAsync(p, ct);
                _logger.LogInformation("loaded aircraft DB from {Path} ({Count} entries)", p, n);
                return n;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "failed to load aircraft DB from {Path}", p);
            }
        }
        _logger.LogInformation("no aircraft DB found; enrichment disabled");
        return 0;
    }

    private static string? NullIfEmpty(string s)
    {
        var trimmed = s.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
