using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Core.Stats;

/// <summary>
/// 7-day × 24-hour grid counting distinct aircraft-tracking events per
/// weekday/hour cell. Each cell resets when it rolls back around a week
/// later — so the grid reads "last Monday's 09:00 count", not a running
/// all-time sum. Ports <c>app/heatmap.py</c>.
/// </summary>
public sealed class TrafficHeatmap
{
    public const int Days = 7;
    public const int Hours = 24;

    public static readonly IReadOnlyList<string> DayLabels = new[]
    {
        "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun",
    };

    private readonly object _gate = new();
    private readonly int[,] _grid = new int[Days, Hours];
    private readonly int[,] _lastDay = new int[Days, Hours];
    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private bool _dirty;
    private DateTimeOffset _lastPersist = DateTimeOffset.MinValue;

    public TrafficHeatmap(
        string? cachePath = null, TimeProvider? time = null,
        ILogger<TrafficHeatmap>? logger = null)
    {
        _path = cachePath;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<TrafficHeatmap>.Instance;
    }

    public void Observe(double unixSeconds)
    {
        var dto = DateTimeOffset.FromUnixTimeMilliseconds((long)(unixSeconds * 1000));
        // .NET's DayOfWeek: Sunday=0; Python's weekday: Monday=0. Map accordingly.
        var weekday = ((int)dto.UtcDateTime.DayOfWeek + 6) % 7;
        var hour = dto.UtcDateTime.Hour;
        if (weekday < 0 || weekday >= Days || hour < 0 || hour >= Hours)
        {
            return;
        }
        var day = (int)(unixSeconds / 86400.0);
        lock (_gate)
        {
            // Same weekday+hour a week apart means the day delta is non-zero
            // → new cycle for this cell, wipe the old count first.
            if (_lastDay[weekday, hour] != 0 && _lastDay[weekday, hour] != day)
            {
                _grid[weekday, hour] = 0;
            }
            _grid[weekday, hour]++;
            _lastDay[weekday, hour] = day;
            _dirty = true;
        }
    }

    public TrafficHeatmapSnapshot SnapshotView()
    {
        var grid = new int[Days][];
        var hoursTotal = new int[Hours];
        var daysTotal = new int[Days];
        int total = 0;
        lock (_gate)
        {
            for (var d = 0; d < Days; d++)
            {
                grid[d] = new int[Hours];
                for (var h = 0; h < Hours; h++)
                {
                    var v = _grid[d, h];
                    grid[d][h] = v;
                    hoursTotal[h] += v;
                    daysTotal[d] += v;
                    total += v;
                }
            }
        }
        return new TrafficHeatmapSnapshot(
            Grid: grid,
            DayLabels: DayLabels,
            Hours: hoursTotal,
            Days: daysTotal,
            Total: total);
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_grid);
            Array.Clear(_lastDay);
            _dirty = true;
        }
        _ = PersistAsync(force: true);
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
            var doc = JsonSerializer.Deserialize<PersistedPayload>(raw, JsonOpts);
            if (doc?.Grid is not null && doc.Grid.Count == Days)
            {
                lock (_gate)
                {
                    for (var d = 0; d < Days; d++)
                    {
                        var row = doc.Grid[d];
                        if (row is null)
                        {
                            continue;
                        }
                        for (var h = 0; h < Hours && h < row.Count; h++)
                        {
                            _grid[d, h] = row[h];
                        }
                    }
                    if (doc.LastDay is not null && doc.LastDay.Count == Days)
                    {
                        for (var d = 0; d < Days; d++)
                        {
                            var row = doc.LastDay[d];
                            if (row is null)
                            {
                                continue;
                            }
                            for (var h = 0; h < Hours && h < row.Count; h++)
                            {
                                _lastDay[d, h] = row[h];
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "heatmap cache unreadable at {Path}", _path);
        }
    }

    public async Task MaybePersistAsync(TimeSpan interval, CancellationToken ct = default)
    {
        bool should;
        lock (_gate)
        {
            should = _dirty && (_time.GetUtcNow() - _lastPersist) >= interval;
        }
        if (should)
        {
            await PersistAsync(force: false, ct: ct);
        }
    }

    private async Task PersistAsync(bool force, CancellationToken ct = default)
    {
        if (_path is null)
        {
            return;
        }
        int[][] gridCopy;
        int[][] lastDayCopy;
        lock (_gate)
        {
            if (!force && !_dirty)
            {
                return;
            }
            gridCopy = new int[Days][];
            lastDayCopy = new int[Days][];
            for (var d = 0; d < Days; d++)
            {
                gridCopy[d] = new int[Hours];
                lastDayCopy[d] = new int[Hours];
                for (var h = 0; h < Hours; h++)
                {
                    gridCopy[d][h] = _grid[d, h];
                    lastDayCopy[d][h] = _lastDay[d, h];
                }
            }
        }
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var tmp = _path + ".tmp";
            var payload = new PersistedPayload
            {
                Grid = gridCopy.Select(r => r.ToList()).ToList(),
                LastDay = lastDayCopy.Select(r => r.ToList()).ToList(),
            };
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(payload, JsonOpts), ct);
            File.Move(tmp, _path, overwrite: true);
            lock (_gate)
            {
                _dirty = false;
                _lastPersist = _time.GetUtcNow();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "couldn't persist heatmap");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed class PersistedPayload
    {
        public List<List<int>>? Grid { get; set; }
        public List<List<int>>? LastDay { get; set; }
    }
}

public sealed record TrafficHeatmapSnapshot(
    IReadOnlyList<IReadOnlyList<int>> Grid,
    IReadOnlyList<string> DayLabels,
    IReadOnlyList<int> Hours,
    IReadOnlyList<int> Days,
    int Total);
