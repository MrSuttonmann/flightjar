using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using FlightJar.Core.Configuration;

namespace FlightJar.Api.Auth;

/// <summary>
/// Owns the optional shared-secret password gate for the notification +
/// watchlist endpoints. Holds an in-memory session-token table keyed on
/// 32-byte random tokens minted at login time, with sliding 24 h expiry
/// and a background sweep. Tokens never leave the server's memory and
/// the cookie carrying them is HttpOnly, so a tampered localStorage
/// can't forge or steal them.
/// </summary>
public sealed class AuthService : IDisposable
{
    public const string CookieName = "flightjar_session";

    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    // Rate-limit window: per client IP, drop login attempts past the cap
    // inside any rolling minute. Successful logins reset the counter.
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);
    private const int MaxAttemptsPerWindow = 5;

    private readonly AppOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AuthService>? _logger;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RateLimitState> _rateLimits = new(StringComparer.Ordinal);
    private readonly ITimer? _sweepTimer;

    public AuthService(AppOptions options, TimeProvider time, ILogger<AuthService>? logger = null)
    {
        _options = options;
        _time = time;
        _logger = logger;
        if (Required)
        {
            _sweepTimer = _time.CreateTimer(_ => Sweep(), state: null, SweepInterval, SweepInterval);
        }
    }

    /// <summary>True when <c>FLIGHTJAR_PASSWORD</c> is non-empty. When false,
    /// login/logout/status are still served (login returns 404 / status
    /// reports required=false) but the gating filter is a no-op.</summary>
    public bool Required => !string.IsNullOrEmpty(_options.Password);

    /// <summary>Constant-time password compare. Returns false for any input
    /// when no password is configured (defence-in-depth — the filter is the
    /// authoritative gate but a stray <c>VerifyPassword("")</c> must never
    /// succeed accidentally).</summary>
    public bool VerifyPassword(string? candidate)
    {
        if (!Required)
        {
            return false;
        }
        var expected = Encoding.UTF8.GetBytes(_options.Password);
        var actual = Encoding.UTF8.GetBytes(candidate ?? "");

        // FixedTimeEquals requires equal-length buffers, so pad the shorter
        // side to the longer length and OR the length mismatch into the
        // result. The compare itself is constant-time; the length check
        // leaks the *length* bucket of the candidate, not the contents.
        var len = Math.Max(expected.Length, actual.Length);
        var a = new byte[len];
        var b = new byte[len];
        Buffer.BlockCopy(expected, 0, a, 0, expected.Length);
        Buffer.BlockCopy(actual, 0, b, 0, actual.Length);
        var bytesEqual = CryptographicOperations.FixedTimeEquals(a, b);
        return bytesEqual && expected.Length == actual.Length;
    }

    public string MintSession()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var token = Convert.ToBase64String(bytes);
        _sessions[token] = _time.GetUtcNow() + SessionLifetime;
        return token;
    }

    public bool ValidateSession(string? token)
    {
        if (!Required)
        {
            // No password configured → every caller is "anonymous-but-allowed".
            // Filters short-circuit on Required=false instead of calling here,
            // but treat a stray call as not-validated so the meaning is clear.
            return false;
        }
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }
        if (!_sessions.TryGetValue(token, out var exp))
        {
            return false;
        }
        if (exp <= _time.GetUtcNow())
        {
            _sessions.TryRemove(token, out _);
            return false;
        }
        return true;
    }

    public void InvalidateSession(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }
        _sessions.TryRemove(token, out _);
    }

    /// <summary>Drop expired tokens. Called on a 5-minute timer; cheap enough
    /// (single dict scan) that we don't bother with epoch buckets.</summary>
    public void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var kv in _sessions)
        {
            if (kv.Value <= now)
            {
                _sessions.TryRemove(kv.Key, out _);
            }
        }
        // Rate-limit windows older than 2× the window are also dead state.
        var rlCutoff = now - (RateLimitWindow + RateLimitWindow);
        foreach (var kv in _rateLimits)
        {
            if (kv.Value.WindowStart <= rlCutoff)
            {
                _rateLimits.TryRemove(kv.Key, out _);
            }
        }
    }

    /// <summary>Returns true when the caller may attempt a login (under the
    /// cap for the current window). Attempt is recorded regardless of the
    /// eventual success/failure — a successful login then calls <see
    /// cref="ResetRateLimit"/> to clear the bucket.</summary>
    public bool TryRecordLoginAttempt(string clientIp)
    {
        var key = clientIp ?? "";
        var now = _time.GetUtcNow();
        var state = _rateLimits.GetOrAdd(key, _ => new RateLimitState { WindowStart = now });
        lock (state)
        {
            if (now - state.WindowStart > RateLimitWindow)
            {
                state.WindowStart = now;
                state.Count = 0;
            }
            state.Count++;
            return state.Count <= MaxAttemptsPerWindow;
        }
    }

    public void ResetRateLimit(string clientIp)
    {
        _rateLimits.TryRemove(clientIp ?? "", out _);
    }

    /// <summary>Test hook: count of live (non-expired) sessions. Tests use
    /// this to assert that logout / sweep actually evicted the token.</summary>
    public int SessionCount => _sessions.Count;

    public void Dispose()
    {
        _sweepTimer?.Dispose();
    }

    private sealed class RateLimitState
    {
        public DateTimeOffset WindowStart;
        public int Count;
    }
}
