namespace FlightJar.Clients.Caching;

/// <summary>One cache row: the optional payload plus an expiry timestamp.</summary>
public sealed record CacheEntry<TValue>(TValue? Data, DateTimeOffset ExpiresAt)
    where TValue : class
{
    public bool IsFresh(DateTimeOffset now) => now < ExpiresAt;
}
