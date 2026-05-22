using FlightJar.Persistence.P2P;

namespace FlightJar.Persistence.Tests.P2P;

public class P2PConfigStoreTests : IDisposable
{
    private readonly string _tmp;

    public P2PConfigStoreTests()
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

    private string PathFor() => Path.Combine(_tmp, "p2p.json");

    [Fact]
    public async Task FreshInstall_DefaultsToDisabledNoConsent()
    {
        var store = new P2PConfigStore(PathFor());
        await store.LoadAsync();
        Assert.False(store.Current.Enabled);
        Assert.False(store.Current.ConsentGiven);
        Assert.False(store.Current.ShareSiteName);
    }

    [Fact]
    public async Task V1WithEnabledTrue_IsGrandfathered()
    {
        // v1 had no consent_given and defaulted enabled=true.
        await File.WriteAllTextAsync(PathFor(),
            "{\"version\":1,\"enabled\":true,\"share_site_name\":false}");

        var store = new P2PConfigStore(PathFor());
        await store.LoadAsync();

        Assert.True(store.Current.Enabled);
        Assert.True(store.Current.ConsentGiven);
    }

    [Fact]
    public async Task V1WithEnabledFalse_StaysOff()
    {
        // User had explicitly opted out under v1 — keep them off, no consent.
        await File.WriteAllTextAsync(PathFor(),
            "{\"version\":1,\"enabled\":false,\"share_site_name\":false}");

        var store = new P2PConfigStore(PathFor());
        await store.LoadAsync();

        Assert.False(store.Current.Enabled);
        Assert.False(store.Current.ConsentGiven);
    }

    [Fact]
    public async Task V1Migration_RewritesFileAsV2()
    {
        await File.WriteAllTextAsync(PathFor(),
            "{\"version\":1,\"enabled\":true,\"share_site_name\":false}");

        var first = new P2PConfigStore(PathFor());
        await first.LoadAsync();

        var raw = await File.ReadAllTextAsync(PathFor());
        Assert.Contains("\"version\": 2", raw);
        Assert.Contains("\"consent_given\": true", raw);
    }

    [Fact]
    public async Task V2RoundTrip_PreservesAllFields()
    {
        var first = new P2PConfigStore(PathFor());
        first.Replace(new P2PConfig
        {
            Enabled = true,
            ConsentGiven = true,
            ShareSiteName = true,
        });

        var fresh = new P2PConfigStore(PathFor());
        await fresh.LoadAsync();
        Assert.True(fresh.Current.Enabled);
        Assert.True(fresh.Current.ConsentGiven);
        Assert.True(fresh.Current.ShareSiteName);
    }

    [Fact]
    public async Task MissingVersion_TreatedAsLegacy()
    {
        // No version field at all → treat as v0/v1: grandfather based on enabled.
        await File.WriteAllTextAsync(PathFor(), "{\"enabled\":true}");

        var store = new P2PConfigStore(PathFor());
        await store.LoadAsync();
        Assert.True(store.Current.Enabled);
        Assert.True(store.Current.ConsentGiven);
    }

    [Fact]
    public void Replace_FiresChangedEvent()
    {
        var store = new P2PConfigStore(PathFor());
        P2PConfig? observed = null;
        store.Changed += c => observed = c;

        store.Replace(new P2PConfig { Enabled = true, ConsentGiven = true });

        Assert.NotNull(observed);
        Assert.True(observed!.Enabled);
        Assert.True(observed.ConsentGiven);
    }

    [Fact]
    public void Replace_NoChange_NoEvent()
    {
        var store = new P2PConfigStore(PathFor());
        var count = 0;
        store.Changed += _ => count++;

        store.Replace(new P2PConfig());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task MalformedFile_LeavesDefaults()
    {
        await File.WriteAllTextAsync(PathFor(), "{ this is not json");

        var store = new P2PConfigStore(PathFor());
        await store.LoadAsync();
        Assert.False(store.Current.Enabled);
        Assert.False(store.Current.ConsentGiven);
    }
}
