using FlightJar.Persistence.Watchlist;
using Microsoft.Extensions.Time.Testing;

namespace FlightJar.Persistence.Tests.Watchlist;

public class WatchlistStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 4, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly string _tmp;

    public WatchlistStoreTests()
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

    private string PathFor(string file = "watchlist.json") => System.IO.Path.Combine(_tmp, file);

    [Fact]
    public void Contains_NormalisesCase()
    {
        var store = new WatchlistStore();
        store.Replace(new[] { "ABC123" });
        Assert.True(store.Contains("abc123"));
        Assert.True(store.Contains("ABC123"));
        Assert.True(store.Contains("  abc123 "));
    }

    [Fact]
    public void Replace_DropsInvalidHex()
    {
        var store = new WatchlistStore();
        var payload = store.Replace(new[] { "abc123", "zzzzzz", "1234", "deadbeef1234" });
        Assert.Single(payload.Icao24s);
        Assert.Equal("abc123", payload.Icao24s[0]);
    }

    [Fact]
    public void RecordSeen_IgnoresNonWatchedTails()
    {
        var store = new WatchlistStore(time: new FakeTimeProvider(T0));
        store.Replace(new[] { "abc123" });
        store.RecordSeen("def456", 1000);
        Assert.Empty(store.Snapshot().LastSeen);
    }

    [Fact]
    public void RecordSeen_IgnoresTimeTravel()
    {
        var time = new FakeTimeProvider(T0);
        var store = new WatchlistStore(time: time);
        store.Replace(new[] { "abc123" });
        store.RecordSeen("abc123", 1000);
        store.RecordSeen("abc123", 500); // older — ignored
        Assert.Equal(1000, store.Snapshot().LastSeen["abc123"]);
    }

    [Fact]
    public void RecordSeen_UpdatesLastSeen()
    {
        var time = new FakeTimeProvider(T0);
        var store = new WatchlistStore(time: time);
        store.Replace(new[] { "abc123" });
        store.RecordSeen("abc123", 1000);
        store.RecordSeen("abc123", 2000);
        Assert.Equal(2000, store.Snapshot().LastSeen["abc123"]);
    }

    [Fact]
    public async Task FirstSighting_PersistsImmediately()
    {
        var time = new FakeTimeProvider(T0);
        var store = new WatchlistStore(path: PathFor(), time: time);
        store.Replace(new[] { "abc123" });
        store.RecordSeen("abc123", 1000);

        // Fresh instance reads the persisted state.
        var fresh = new WatchlistStore(path: PathFor(), time: time);
        await fresh.LoadAsync();
        Assert.Equal(1000, fresh.Snapshot().LastSeen["abc123"]);
    }

    [Fact]
    public async Task DebouncedUpdates_DontImmediatelyPersist()
    {
        var time = new FakeTimeProvider(T0);
        var store = new WatchlistStore(path: PathFor(), time: time);
        store.Replace(new[] { "abc123" });

        store.RecordSeen("abc123", 1000); // persists (first-ever)
        store.RecordSeen("abc123", 1005); // within debounce — stays in memory only

        var intermediate = new WatchlistStore(path: PathFor(), time: time);
        await intermediate.LoadAsync();
        Assert.Equal(1000, intermediate.Snapshot().LastSeen["abc123"]);

        // Advance past the debounce window; next record triggers a flush.
        time.Advance(WatchlistStore.PersistDebounce + TimeSpan.FromSeconds(1));
        store.RecordSeen("abc123", 1100);
        var after = new WatchlistStore(path: PathFor(), time: time);
        await after.LoadAsync();
        Assert.Equal(1100, after.Snapshot().LastSeen["abc123"]);
    }

    [Fact]
    public async Task Replace_PrunesRemovedIcaoLastSeen()
    {
        var store = new WatchlistStore(path: PathFor(), time: new FakeTimeProvider(T0));
        store.Replace(new[] { "abc123", "def456" });
        store.RecordSeen("abc123", 1000);
        store.RecordSeen("def456", 2000);

        store.Replace(new[] { "abc123" });
        Assert.False(store.Snapshot().LastSeen.ContainsKey("def456"));

        var reloaded = new WatchlistStore(path: PathFor());
        await reloaded.LoadAsync();
        Assert.False(reloaded.Snapshot().LastSeen.ContainsKey("def456"));
    }

    [Fact]
    public async Task LoadAsync_HandlesMissingFile()
    {
        var store = new WatchlistStore(path: PathFor(), time: new FakeTimeProvider(T0));
        await store.LoadAsync();
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task LoadAsync_DropsEntriesNotInIcaoList()
    {
        // A corrupted / hand-edited file could have last-seen entries for
        // icaos that aren't in the icao24s list. Drop those on load.
        var payload = """
            {"version":2,"icao24s":["abc123"],"last_seen":{"abc123":1000,"def456":2000}}
            """;
        await File.WriteAllTextAsync(PathFor(), payload);
        var store = new WatchlistStore(path: PathFor(), time: new FakeTimeProvider(T0));
        await store.LoadAsync();
        Assert.True(store.Snapshot().LastSeen.ContainsKey("abc123"));
        Assert.False(store.Snapshot().LastSeen.ContainsKey("def456"));
    }
}
