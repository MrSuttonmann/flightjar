using FlightJar.Persistence.P2P;

namespace FlightJar.Persistence.Tests.P2P;

public class P2PRelayCredentialsStoreTests : IDisposable
{
    private readonly string _tmp;

    public P2PRelayCredentialsStoreTests()
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

    private string PathFor() => Path.Combine(_tmp, "p2p_credentials.json");

    [Fact]
    public async Task LoadAsync_NoFile_LeavesTokenNull()
    {
        var store = new P2PRelayCredentialsStore(PathFor());
        await store.LoadAsync();
        Assert.Null(store.Token);
    }

    [Fact]
    public async Task SetThenLoad_RoundTrips()
    {
        var first = new P2PRelayCredentialsStore(PathFor());
        await first.SetTokenAsync("abc123");

        var fresh = new P2PRelayCredentialsStore(PathFor());
        await fresh.LoadAsync();
        Assert.Equal("abc123", fresh.Token);
    }

    [Fact]
    public async Task ClearAsync_RemovesPersistedToken()
    {
        var store = new P2PRelayCredentialsStore(PathFor());
        await store.SetTokenAsync("abc123");
        await store.ClearAsync();
        Assert.Null(store.Token);

        var fresh = new P2PRelayCredentialsStore(PathFor());
        await fresh.LoadAsync();
        Assert.Null(fresh.Token);
    }

    [Fact]
    public async Task SetTokenAsync_RejectsEmpty()
    {
        var store = new P2PRelayCredentialsStore(PathFor());
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetTokenAsync(""));
    }

    [Fact]
    public async Task LoadAsync_MalformedFile_LeavesTokenNull()
    {
        await File.WriteAllTextAsync(PathFor(), "{ this is not json");
        var store = new P2PRelayCredentialsStore(PathFor());
        await store.LoadAsync();
        Assert.Null(store.Token);
    }

    [Fact]
    public async Task NoPath_StillUsableInMemory()
    {
        var store = new P2PRelayCredentialsStore(path: null);
        await store.LoadAsync();
        await store.SetTokenAsync("abc123");
        Assert.Equal("abc123", store.Token);
        await store.ClearAsync();
        Assert.Null(store.Token);
    }
}
