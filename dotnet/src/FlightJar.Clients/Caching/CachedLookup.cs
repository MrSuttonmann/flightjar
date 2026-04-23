using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;

namespace FlightJar.Clients.Caching;

/// <summary>
/// Generic cached lookup: in-flight dedup, positive / negative TTLs, 429
/// cooldown, stale-on-error fallback. One instance per key namespace.
/// </summary>
public sealed class CachedLookup<TKey, TValue>
    where TKey : notnull
    where TValue : class
{
    private readonly ConcurrentDictionary<TKey, CacheEntry<TValue>> _entries = new();
    private readonly ConcurrentDictionary<TKey, Task<TValue?>> _inflight = new();
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly HttpThrottle _throttle;
    private readonly string _name;
    private readonly TimeSpan _positiveTtl;
    private readonly TimeSpan _negativeTtl;
    private readonly int _maxSize;

    public CachedLookup(
        string name,
        TimeSpan positiveTtl,
        TimeSpan negativeTtl,
        int maxSize,
        HttpThrottle throttle,
        TimeProvider time,
        ILogger logger)
    {
        _name = name;
        _positiveTtl = positiveTtl;
        _negativeTtl = negativeTtl;
        _maxSize = maxSize;
        _throttle = throttle;
        _time = time;
        _logger = logger;
    }

    public int Count => _entries.Count;

    /// <summary>
    /// Synchronous cache lookup. Returns <c>(known: true, data: ...)</c> for
    /// a fresh entry (including fresh cached-negatives); <c>(false, null)</c>
    /// means stale or unseen.
    /// </summary>
    public (bool Known, TValue? Data) LookupCached(TKey key)
    {
        if (_entries.TryGetValue(key, out var entry) && entry.IsFresh(_time.GetUtcNow()))
        {
            return (true, entry.Data);
        }
        return (false, null);
    }

    /// <summary>
    /// Cache-aware lookup. Never throws — falls back to a stale cache entry
    /// if the upstream call fails. Concurrent callers on the same key await
    /// the same in-flight task.
    /// </summary>
    public Task<TValue?> GetAsync(TKey key, Func<TKey, CancellationToken, ValueTask<TValue?>> fetcher, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        if (_entries.TryGetValue(key, out var entry) && entry.IsFresh(now))
        {
            return Task.FromResult(entry.Data);
        }
        if (_throttle.IsInCooldown(now))
        {
            return Task.FromResult(entry?.Data);
        }
        return _inflight.GetOrAdd(key, k => FetchAndCacheAsync(k, fetcher, ct));
    }

    private async Task<TValue?> FetchAndCacheAsync(TKey key, Func<TKey, CancellationToken, ValueTask<TValue?>> fetcher, CancellationToken ct)
    {
        try
        {
            TValue? data = null;
            bool fetched = false;
            await using (await _throttle.AcquireAsync(_time, ct))
            {
                // Recheck: another caller may have filled while we were queued.
                var now = _time.GetUtcNow();
                if (_entries.TryGetValue(key, out var recheck) && recheck.IsFresh(now))
                {
                    return recheck.Data;
                }
                if (_throttle.IsInCooldown(now))
                {
                    return recheck?.Data;
                }
                try
                {
                    data = await fetcher(key, ct);
                    fetched = true;
                }
                catch (UpstreamRateLimitedException ex)
                {
                    _throttle.RecordCooldown(ex.RetryAfter, _time);
                    _logger.LogWarning(
                        "{Name} 429 for {Key} — cooling down until {Until:O}",
                        _name, key, _throttle.CooldownUntil);
                    return recheck?.Data;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "{Name} HTTP error for {Key}", _name, key);
                    return recheck?.Data;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "{Name} lookup failed for {Key}", _name, key);
                    return recheck?.Data;
                }
            }
            if (fetched)
            {
                var ttl = data is not null ? _positiveTtl : _negativeTtl;
                _entries[key] = new CacheEntry<TValue>(data, _time.GetUtcNow().Add(ttl));
                Prune();
            }
            return data;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    private void Prune()
    {
        if (_entries.Count <= _maxSize)
        {
            return;
        }
        var keep = _entries
            .OrderByDescending(kv => kv.Value.ExpiresAt)
            .Take(_maxSize)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        _entries.Clear();
        foreach (var (k, v) in keep)
        {
            _entries[k] = v;
        }
    }

    public IReadOnlyDictionary<TKey, CacheEntry<TValue>> SnapshotEntries() =>
        new Dictionary<TKey, CacheEntry<TValue>>(_entries);

    public void LoadEntries(IEnumerable<KeyValuePair<TKey, CacheEntry<TValue>>> entries)
    {
        var now = _time.GetUtcNow();
        foreach (var (k, v) in entries)
        {
            if (v.ExpiresAt > now)
            {
                _entries[k] = v;
            }
        }
    }
}
