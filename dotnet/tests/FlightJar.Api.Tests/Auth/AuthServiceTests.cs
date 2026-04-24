using FlightJar.Api.Auth;
using FlightJar.Core.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace FlightJar.Api.Tests.Auth;

public class AuthServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static AuthService Make(string password, FakeTimeProvider? time = null)
    {
        var opts = new AppOptions { Password = password };
        return new AuthService(opts, time ?? new FakeTimeProvider(T0));
    }

    [Fact]
    public void Required_FollowsPassword()
    {
        Assert.False(Make("").Required);
        Assert.True(Make("hunter2").Required);
    }

    [Fact]
    public void VerifyPassword_AcceptsExact()
    {
        var auth = Make("hunter2");
        Assert.True(auth.VerifyPassword("hunter2"));
    }

    [Fact]
    public void VerifyPassword_RejectsMismatch()
    {
        var auth = Make("hunter2");
        Assert.False(auth.VerifyPassword("Hunter2"));
        Assert.False(auth.VerifyPassword("hunter"));
        Assert.False(auth.VerifyPassword("hunter2 "));
        Assert.False(auth.VerifyPassword(""));
        Assert.False(auth.VerifyPassword(null));
    }

    [Fact]
    public void VerifyPassword_AlwaysFalse_WhenAuthDisabled()
    {
        var auth = Make("");
        // No password set must mean every candidate (including the empty
        // string) is rejected — defence-in-depth against a stray check
        // that bypasses the Required gate.
        Assert.False(auth.VerifyPassword(""));
        Assert.False(auth.VerifyPassword("anything"));
    }

    [Fact]
    public void MintSession_ReturnsUniqueValidatableTokens()
    {
        var auth = Make("hunter2");
        var t1 = auth.MintSession();
        var t2 = auth.MintSession();
        Assert.NotEqual(t1, t2);
        Assert.True(auth.ValidateSession(t1));
        Assert.True(auth.ValidateSession(t2));
    }

    [Fact]
    public void ValidateSession_RejectsUnknownAndEmpty()
    {
        var auth = Make("hunter2");
        Assert.False(auth.ValidateSession(""));
        Assert.False(auth.ValidateSession(null));
        Assert.False(auth.ValidateSession("forged"));
    }

    [Fact]
    public void ValidateSession_ExpiresAfterLifetime()
    {
        var time = new FakeTimeProvider(T0);
        var auth = Make("hunter2", time);
        var token = auth.MintSession();

        time.Advance(AuthService.SessionLifetime - TimeSpan.FromSeconds(1));
        Assert.True(auth.ValidateSession(token));

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(auth.ValidateSession(token));
        // Expired tokens are evicted on the read path so they can't pile up.
        Assert.Equal(0, auth.SessionCount);
    }

    [Fact]
    public void InvalidateSession_DropsToken()
    {
        var auth = Make("hunter2");
        var token = auth.MintSession();
        Assert.True(auth.ValidateSession(token));
        auth.InvalidateSession(token);
        Assert.False(auth.ValidateSession(token));
    }

    [Fact]
    public void InvalidateSession_TolerantOfEmpty()
    {
        var auth = Make("hunter2");
        auth.InvalidateSession(null);
        auth.InvalidateSession("");
        // Just doesn't throw.
    }

    [Fact]
    public void Sweep_DropsExpiredTokens()
    {
        var time = new FakeTimeProvider(T0);
        var auth = Make("hunter2", time);
        var t1 = auth.MintSession();              // exp = T0 + lifetime
        time.Advance(TimeSpan.FromHours(1));
        var t2 = auth.MintSession();              // exp = T0 + 1h + lifetime
        // Push to t1's exact expiry boundary: t1 evicts (exp <= now), t2 still alive.
        time.Advance(AuthService.SessionLifetime - TimeSpan.FromHours(1));
        auth.Sweep();
        Assert.False(auth.ValidateSession(t1));
        Assert.True(auth.ValidateSession(t2));
    }

    [Fact]
    public void RateLimit_AllowsUnderCap_RejectsOverCap()
    {
        var auth = Make("hunter2");
        for (var i = 0; i < 5; i++)
        {
            Assert.True(auth.TryRecordLoginAttempt("1.2.3.4"));
        }
        Assert.False(auth.TryRecordLoginAttempt("1.2.3.4"));
        Assert.False(auth.TryRecordLoginAttempt("1.2.3.4"));
    }

    [Fact]
    public void RateLimit_PerIp_NotShared()
    {
        var auth = Make("hunter2");
        for (var i = 0; i < 5; i++)
        {
            Assert.True(auth.TryRecordLoginAttempt("1.2.3.4"));
        }
        // Different IP gets its own bucket.
        Assert.True(auth.TryRecordLoginAttempt("5.6.7.8"));
    }

    [Fact]
    public void RateLimit_WindowResets()
    {
        var time = new FakeTimeProvider(T0);
        var auth = Make("hunter2", time);
        for (var i = 0; i < 5; i++)
        {
            auth.TryRecordLoginAttempt("1.2.3.4");
        }
        Assert.False(auth.TryRecordLoginAttempt("1.2.3.4"));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(auth.TryRecordLoginAttempt("1.2.3.4"));
    }

    [Fact]
    public void RateLimit_ExplicitReset_ClearsCounter()
    {
        var auth = Make("hunter2");
        for (var i = 0; i < 5; i++)
        {
            auth.TryRecordLoginAttempt("1.2.3.4");
        }
        auth.ResetRateLimit("1.2.3.4");
        for (var i = 0; i < 5; i++)
        {
            Assert.True(auth.TryRecordLoginAttempt("1.2.3.4"));
        }
    }

    [Fact]
    public void MintSession_ProducesNonGuessableTokens()
    {
        // Sanity check: 64 mint calls must produce 64 distinct base64
        // strings of >= 32-byte entropy. A weak RNG would collide
        // long before that.
        var auth = Make("hunter2");
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 64; i++)
        {
            var t = auth.MintSession();
            Assert.DoesNotContain(' ', t);
            Assert.True(t.Length >= 32);
            Assert.True(tokens.Add(t));
        }
    }
}
