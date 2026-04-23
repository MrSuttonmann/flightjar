namespace FlightJar.Clients.Caching;

/// <summary>
/// Thrown by a fetcher when the upstream returned HTTP 429. Caught by
/// <see cref="CachedLookup{TKey, TValue}"/> to update the shared
/// <see cref="HttpThrottle"/> cooldown and fall back to a stale entry
/// without caching a spurious negative.
/// </summary>
public sealed class UpstreamRateLimitedException : Exception
{
    public TimeSpan? RetryAfter { get; }

    public UpstreamRateLimitedException(TimeSpan? retryAfter, Exception? inner = null)
        : base("upstream returned 429", inner)
    {
        RetryAfter = retryAfter;
    }
}
