using System.Net;
using System.Text;
using FlightJar.Api.Hosting;
using FlightJar.Core.Configuration;
using FlightJar.Persistence.P2P;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Api.Tests.P2P;

public class P2PRelayRegistrarTests : IDisposable
{
    private readonly string _tmp;

    public P2PRelayRegistrarTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"flightjar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp))
        {
            Directory.Delete(_tmp, recursive: true);
        }
    }

    private string CredsPath() => Path.Combine(_tmp, "p2p_credentials.json");

    private static AppOptions BaseOptions(string? token = null) => new()
    {
        P2PRelayUrl = "wss://relay.example/ws",
        P2PRelayToken = token,
    };

    private static (P2PRelayRegistrar registrar, MockHandler handler, P2PRelayCredentialsStore creds)
        Build(AppOptions options, string? credsPath, Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var creds = new P2PRelayCredentialsStore(credsPath);
        var handler = new MockHandler { Responder = respond };
        var http = new HttpClient(handler);
        var registrar = new P2PRelayRegistrar(options, creds, http, NullLogger<P2PRelayRegistrar>.Instance);
        return (registrar, handler, creds);
    }

    [Fact]
    public async Task EnvOverride_ShortCircuitsRegistration()
    {
        var (registrar, handler, creds) = Build(BaseOptions(token: "env-token"), CredsPath());
        var token = await registrar.EnsureTokenAsync(CancellationToken.None);
        Assert.Equal("env-token", token);
        Assert.Equal(0, handler.CallCount);
        Assert.Null(creds.Token);
    }

    [Fact]
    public async Task PersistedToken_UsedWithoutRegistration()
    {
        var creds = new P2PRelayCredentialsStore(CredsPath());
        await creds.SetTokenAsync("disk-token");

        var handler = new MockHandler();
        var http = new HttpClient(handler);
        var registrar = new P2PRelayRegistrar(BaseOptions(), creds, http, NullLogger<P2PRelayRegistrar>.Instance);
        await creds.LoadAsync();

        var token = await registrar.EnsureTokenAsync(CancellationToken.None);
        Assert.Equal("disk-token", token);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task NoTokenAnywhere_RegistersAndPersists()
    {
        var (registrar, handler, creds) = Build(BaseOptions(), CredsPath(),
            req =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Equal("https://relay.example/register", req.RequestUri!.ToString());
                return JsonResponse("""{"token":"fresh-token"}""");
            });

        var token = await registrar.EnsureTokenAsync(CancellationToken.None);
        Assert.Equal("fresh-token", token);
        Assert.Equal("fresh-token", creds.Token);
        Assert.Equal(1, handler.CallCount);

        // Second call uses persisted token, no new HTTP request.
        var second = await registrar.EnsureTokenAsync(CancellationToken.None);
        Assert.Equal("fresh-token", second);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task RegisterFails_PropagatesAndLeavesCredsClear()
    {
        var (registrar, _, creds) = Build(BaseOptions(), CredsPath(),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => registrar.EnsureTokenAsync(CancellationToken.None));
        Assert.Null(creds.Token);
    }

    [Fact]
    public async Task RegisterReturnsEmptyToken_Throws()
    {
        var (registrar, _, _) = Build(BaseOptions(), CredsPath(),
            _ => JsonResponse("""{"token":""}"""));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registrar.EnsureTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InvalidateAsync_ClearsPersistedToken()
    {
        var (registrar, _, creds) = Build(BaseOptions(), CredsPath(),
            _ => JsonResponse("""{"token":"abc"}"""));
        await registrar.EnsureTokenAsync(CancellationToken.None);
        Assert.Equal("abc", creds.Token);

        await registrar.InvalidateAsync(CancellationToken.None);
        Assert.Null(creds.Token);
    }

    [Fact]
    public async Task InvalidateAsync_NoOpUnderEnvOverride()
    {
        // Operator picked the token explicitly via env — don't quietly drop it
        // even if the relay rejects, since clearing wouldn't actually change
        // what we send next.
        var creds = new P2PRelayCredentialsStore(CredsPath());
        await creds.SetTokenAsync("disk-token");
        var handler = new MockHandler();
        var http = new HttpClient(handler);
        var registrar = new P2PRelayRegistrar(BaseOptions(token: "env-token"), creds, http,
            NullLogger<P2PRelayRegistrar>.Instance);

        await registrar.InvalidateAsync(CancellationToken.None);
        Assert.Equal("disk-token", creds.Token);
    }

    [Theory]
    [InlineData("wss://relay.example/ws", "https://relay.example/register")]
    [InlineData("ws://localhost:8787/ws", "http://localhost:8787/register")]
    [InlineData("https://relay.example/anything", "https://relay.example/register")]
    [InlineData("wss://relay.example:8443/ws", "https://relay.example:8443/register")]
    public void ToRegisterUrl_DerivesCorrectly(string relayUrl, string expected)
    {
        Assert.Equal(expected, P2PRelayRegistrar.ToRegisterUrl(relayUrl));
    }

    [Fact]
    public void ToRegisterUrl_RejectsUnsupportedScheme()
    {
        Assert.Throws<ArgumentException>(() => P2PRelayRegistrar.ToRegisterUrl("ftp://relay.example/ws"));
    }

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class MockHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(Responder?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
