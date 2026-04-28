using FlightJar.Core.State.Persistence;
using FlightJar.Persistence.State;

namespace FlightJar.Persistence.Tests.State;

public class StateSnapshotStoreTests : IDisposable
{
    private readonly string _tmp;

    public StateSnapshotStoreTests()
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

    private string PathFor(string file = "state.json.gz") => Path.Combine(_tmp, file);

    [Fact]
    public async Task SaveAndLoad_RoundTripsAircraft()
    {
        var store = new StateSnapshotStore(PathFor());
        var payload = new StateSnapshotPayload
        {
            Version = 1,
            SavedAt = 1700000000,
            Aircraft =
            {
                ["abc123"] = new PersistedAircraft
                {
                    Icao = "abc123",
                    Callsign = "FLY1",
                    Lat = 52.1,
                    Lon = -1.1,
                    AltitudeBaro = 30000,
                    Speed = 450,
                    Track = 90,
                    LastSeen = 1700000000,
                    LastPositionTime = 1700000000,
                    Trail =
                    {
                        new PersistedTrailPoint(52.09, -1.09, 29000, 440, 1700000000 - 5, false),
                        new PersistedTrailPoint(52.10, -1.10, 30000, 450, 1700000000, false),
                    },
                },
            },
        };
        await store.SaveAsync(payload);

        var loaded = await store.LoadAsync();
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.Version);
        Assert.True(loaded.Aircraft.ContainsKey("abc123"));
        var ac = loaded.Aircraft["abc123"];
        Assert.Equal("FLY1", ac.Callsign);
        Assert.Equal(52.1, ac.Lat);
        Assert.Equal(2, ac.Trail.Count);
        Assert.Equal(29000, ac.Trail[0].Altitude);
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsNull()
    {
        var store = new StateSnapshotStore(PathFor());
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_CorruptFile_ReturnsNull()
    {
        await File.WriteAllTextAsync(PathFor(), "not-a-gzip-file");
        var store = new StateSnapshotStore(PathFor());
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task Save_IsAtomic()
    {
        var store = new StateSnapshotStore(PathFor());
        // Write an initial payload.
        await store.SaveAsync(new StateSnapshotPayload { Version = 1, SavedAt = 1 });
        // Save a second time — target should still exist, tmp file cleaned up.
        await store.SaveAsync(new StateSnapshotPayload { Version = 1, SavedAt = 2 });
        Assert.True(File.Exists(PathFor()));
        Assert.False(File.Exists(PathFor() + ".tmp"));
    }
}
