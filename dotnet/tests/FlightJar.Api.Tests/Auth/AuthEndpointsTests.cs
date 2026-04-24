using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlightJar.Api.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FlightJar.Api.Tests.Auth;

/// <summary>
/// End-to-end tests for the optional shared-secret gate. Two flavours:
/// auth-off (default install — every gated endpoint behaves like before)
/// and auth-on (FLIGHTJAR_PASSWORD set — login mints a cookie, gated
/// endpoints 401 without it). Both flavours boot WebApplicationFactory
/// against a dead BEAST endpoint so nothing else needs to be running.
/// </summary>
public abstract class AuthEndpointTestBase
{
    protected const string Password = "hunter2-correct-horse";
    protected const string WatchlistPath = "/api/watchlist";
    protected const string NotifConfigPath = "/api/notifications/config";
    protected const string NotifTestPath = "/api/notifications/test/abc";
    protected const string LoginPath = "/api/auth/login";
    protected const string LogoutPath = "/api/auth/logout";
    protected const string StatusPath = "/api/auth/status";

    protected static WebApplicationFactory<Program> BuildFactory(string? password)
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("BEAST_HOST", "127.0.0.1");
            b.UseSetting("BEAST_PORT", "1");
            // Env vars are how AppOptionsBinder reads config in production —
            // process-level so the host's startup binding sees them.
            Environment.SetEnvironmentVariable("BEAST_HOST", "127.0.0.1");
            Environment.SetEnvironmentVariable("BEAST_PORT", "1");
            Environment.SetEnvironmentVariable("FLIGHTJAR_PASSWORD", password ?? "");
        });
        return f;
    }

    protected static HttpClient NoRedirectClient(WebApplicationFactory<Program> f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false, // we want raw Set-Cookie values
        });

    protected static StringContent JsonBody(object o) =>
        new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
}

[Collection("SequentialApi")]
public class AuthDisabledTests : AuthEndpointTestBase, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public AuthDisabledTests()
    {
        _factory = BuildFactory(password: "");
    }

    [Fact]
    public async Task Status_ReportsRequiredFalse()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(StatusPath);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("required").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("unlocked").GetBoolean());
    }

    [Fact]
    public async Task Login_Returns404_WhenAuthDisabled()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync(LoginPath, JsonBody(new { password = "anything" }));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GatedEndpoints_AreOpen_WithoutCookie()
    {
        var client = _factory.CreateClient();
        var watchlist = await client.GetAsync(WatchlistPath);
        watchlist.EnsureSuccessStatusCode();
        var notif = await client.GetAsync(NotifConfigPath);
        notif.EnsureSuccessStatusCode();
    }

    public void Dispose() => _factory.Dispose();
}

[Collection("SequentialApi")]
public class AuthEnabledTests : AuthEndpointTestBase, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public AuthEnabledTests()
    {
        _factory = BuildFactory(password: Password);
    }

    [Fact]
    public async Task Status_ReportsRequiredTrue_UnlockedFalseWithoutCookie()
    {
        var client = NoRedirectClient(_factory);
        var resp = await client.GetAsync(StatusPath);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("required").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("unlocked").GetBoolean());
    }

    [Theory]
    [InlineData(WatchlistPath, "GET")]
    [InlineData(WatchlistPath, "POST")]
    [InlineData(NotifConfigPath, "GET")]
    [InlineData(NotifConfigPath, "POST")]
    [InlineData(NotifTestPath, "POST")]
    public async Task GatedEndpoints_Return401_WithoutSession(string path, string method)
    {
        var client = NoRedirectClient(_factory);
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") req.Content = JsonBody(new { });
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task GatedEndpoints_Return401_WithGarbageCookie()
    {
        var client = NoRedirectClient(_factory);
        var req = new HttpRequestMessage(HttpMethod.Get, WatchlistPath);
        req.Headers.Add("Cookie", $"{AuthService.CookieName}=not-a-real-token");
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Login_RejectsWrongPassword()
    {
        var client = NoRedirectClient(_factory);
        var resp = await client.PostAsync(LoginPath, JsonBody(new { password = "wrong" }));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        // No Set-Cookie on failure — a probe must not learn the cookie name
        // from a successful round-trip without the password.
        Assert.False(resp.Headers.TryGetValues("Set-Cookie", out _));
    }

    [Fact]
    public async Task Login_RejectsMissingPasswordField()
    {
        var client = NoRedirectClient(_factory);
        var resp = await client.PostAsync(LoginPath, JsonBody(new { }));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Login_AcceptsCorrectPassword_SetsCookie()
    {
        var client = NoRedirectClient(_factory);
        var resp = await client.PostAsync(LoginPath, JsonBody(new { password = Password }));
        resp.EnsureSuccessStatusCode();

        Assert.True(resp.Headers.TryGetValues("Set-Cookie", out var cookies));
        var setCookie = cookies!.Single(c => c.StartsWith(AuthService.CookieName + "=", StringComparison.Ordinal));
        // The two security-critical attributes: HttpOnly stops JS from reading
        // the cookie (so a tampered localStorage / XSS can't steal the token),
        // SameSite=Strict stops cross-site POSTs from carrying it.
        Assert.Contains("HttpOnly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Strict", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_ThenGatedEndpoint_Succeeds()
    {
        var client = _factory.CreateClient(); // HandleCookies=true so the
                                              // returned cookie auto-replays
        var login = await client.PostAsync(LoginPath, JsonBody(new { password = Password }));
        login.EnsureSuccessStatusCode();

        var watchlist = await client.GetAsync(WatchlistPath);
        watchlist.EnsureSuccessStatusCode();
        var notifConfig = await client.GetAsync(NotifConfigPath);
        notifConfig.EnsureSuccessStatusCode();

        var status = await client.GetAsync(StatusPath);
        using var doc = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("unlocked").GetBoolean());
    }

    [Fact]
    public async Task Logout_InvalidatesSession()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsync(LoginPath, JsonBody(new { password = Password }));
        login.EnsureSuccessStatusCode();

        var logout = await client.PostAsync(LogoutPath, content: null);
        logout.EnsureSuccessStatusCode();

        // Cookie was cleared client-side by the Set-Cookie expiry; the
        // server-side session was also invalidated, so even if the
        // client retained the old cookie value the next gated call 401s.
        var watchlist = await client.GetAsync(WatchlistPath);
        Assert.Equal(HttpStatusCode.Unauthorized, watchlist.StatusCode);
    }

    [Fact]
    public async Task Login_RateLimited_AfterFiveBadAttempts()
    {
        var client = NoRedirectClient(_factory);
        for (var i = 0; i < 5; i++)
        {
            var bad = await client.PostAsync(LoginPath, JsonBody(new { password = "wrong" }));
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }
        var sixth = await client.PostAsync(LoginPath, JsonBody(new { password = "wrong" }));
        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);

        // Even the *correct* password is throttled while the bucket is
        // full — otherwise an attacker could mix in the right password
        // mid-spray and still get a session.
        var seventh = await client.PostAsync(LoginPath, JsonBody(new { password = Password }));
        Assert.Equal(HttpStatusCode.TooManyRequests, seventh.StatusCode);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Environment.SetEnvironmentVariable("FLIGHTJAR_PASSWORD", "");
    }
}
