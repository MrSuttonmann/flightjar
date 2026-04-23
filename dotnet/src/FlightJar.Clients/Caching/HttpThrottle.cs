using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace FlightJar.Clients.Caching;

/// <summary>
/// Shared throttle state for a single upstream: a minimum interval between
/// consecutive requests (burst control) plus a global 429 cooldown.
/// </summary>
public sealed class HttpThrottle
{
    public TimeSpan MinInterval { get; }
    public TimeSpan Default429Cooldown { get; }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;

    public HttpThrottle(TimeSpan minInterval, TimeSpan? default429Cooldown = null)
    {
        MinInterval = minInterval;
        Default429Cooldown = default429Cooldown ?? TimeSpan.FromSeconds(60);
    }

    public DateTimeOffset CooldownUntil => _cooldownUntil;

    /// <summary>Has the 429 cooldown expired as of <paramref name="now"/>?</summary>
    public bool IsInCooldown(DateTimeOffset now) => now < _cooldownUntil;

    /// <summary>
    /// Acquire the single-gate lock, wait out the min interval since the last
    /// request, then return a disposable that records the request completion
    /// time on dispose.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireAsync(
        TimeProvider time, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var now = time.GetUtcNow();
        var wait = (_lastRequestAt + MinInterval) - now;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, time, ct);
        }
        return new Releaser(this, time);
    }

    /// <summary>Extend the cooldown using an explicit Retry-After span (or <see cref="Default429Cooldown"/> if null).</summary>
    public void RecordCooldown(TimeSpan? retryAfter, TimeProvider time)
    {
        var cooldown = retryAfter ?? Default429Cooldown;
        _cooldownUntil = time.GetUtcNow().Add(cooldown);
    }

    /// <summary>Parse the Retry-After header from a response. Returns null when absent / unparseable.</summary>
    public static TimeSpan? ParseRetryAfter(HttpResponseMessage response, TimeProvider time)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }
        if (header.Delta is TimeSpan delta)
        {
            return delta;
        }
        if (header.Date is DateTimeOffset date)
        {
            var diff = date - time.GetUtcNow();
            return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
        }
        return null;
    }

    private sealed class Releaser : IAsyncDisposable
    {
        private readonly HttpThrottle _owner;
        private readonly TimeProvider _time;
        public Releaser(HttpThrottle owner, TimeProvider time)
        {
            _owner = owner;
            _time = time;
        }
        public ValueTask DisposeAsync()
        {
            _owner._lastRequestAt = _time.GetUtcNow();
            _owner._gate.Release();
            return ValueTask.CompletedTask;
        }
    }

    public static bool IsTransientStatus(HttpStatusCode code) =>
        code is >= HttpStatusCode.InternalServerError
             or HttpStatusCode.RequestTimeout
             or HttpStatusCode.TooManyRequests;

    internal static string FormatInvariant(double d) => d.ToString("G", CultureInfo.InvariantCulture);
}
