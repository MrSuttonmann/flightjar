using System.IO.Compression;
using System.Text;
using FlightJar.Core.ReferenceData;

namespace FlightJar.Core.Tests.ReferenceData;

public class AircraftDbTests : IDisposable
{
    private readonly string _tmp;

    public AircraftDbTests()
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

    private string WriteGzippedCsv(string content, string fileName = "aircraft_db.csv.gz")
    {
        var path = Path.Combine(_tmp, fileName);
        using var file = File.Create(path);
        using var gz = new GZipStream(file, CompressionLevel.Fastest);
        gz.Write(Encoding.UTF8.GetBytes(content));
        return path;
    }

    [Fact]
    public async Task LoadFrom_ParsesSemicolonDelimitedRows()
    {
        var csv = string.Join("\n",
            "abc123;G-EZAN;A319;;Airbus A319-111;;",
            "def456;D-AIXX;A350;;Airbus A350-900;;",
            "",
            "bad-row-no-icao");
        var path = WriteGzippedCsv(csv);
        var db = new AircraftDb();
        var count = await db.LoadFromAsync(path);
        Assert.Equal(2, count);

        var entry = db.Lookup("abc123");
        Assert.NotNull(entry);
        Assert.Equal("G-EZAN", entry!.Value.Registration);
        Assert.Equal("A319", entry.Value.TypeIcao);
        Assert.Equal("Airbus A319-111", entry.Value.TypeLong);
    }

    [Fact]
    public async Task Lookup_IsCaseInsensitive()
    {
        var path = WriteGzippedCsv("abc123;G-EZAN;A319;;Airbus A319-111;;");
        var db = new AircraftDb();
        await db.LoadFromAsync(path);
        Assert.NotNull(db.Lookup("ABC123"));
        Assert.NotNull(db.Lookup("Abc123"));
        Assert.NotNull(db.Lookup("abc123"));
    }

    [Fact]
    public async Task LoadFrom_SkipsRowsWithAllEmptyFields()
    {
        // ICAO present but registration / type_icao / type_long all empty.
        var csv = "abc123;;;;;;";
        var path = WriteGzippedCsv(csv);
        var db = new AircraftDb();
        var count = await db.LoadFromAsync(path);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task LoadFirstAvailable_UsesFirstExistingPath()
    {
        var path = WriteGzippedCsv("abc123;G-TEST;A320;;Airbus A320;;");
        var db = new AircraftDb();
        var missing = Path.Combine(_tmp, "missing.csv.gz");
        var count = await db.LoadFirstAvailableAsync(new[] { missing, path });
        Assert.Equal(1, count);
        Assert.Equal("G-TEST", db.Lookup("abc123")!.Value.Registration);
    }

    [Fact]
    public async Task LoadFirstAvailable_ReturnsZero_WhenNothingExists()
    {
        var db = new AircraftDb();
        var count = await db.LoadFirstAvailableAsync(new[]
        {
            Path.Combine(_tmp, "nope1.csv.gz"),
            Path.Combine(_tmp, "nope2.csv.gz"),
        });
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task LoadFrom_AtomicSwap_ReplacesPreviousEntries()
    {
        var first = WriteGzippedCsv("abc123;OLD;A319;;Old;;", "first.csv.gz");
        var second = WriteGzippedCsv("def456;NEW;A320;;New;;", "second.csv.gz");
        var db = new AircraftDb();
        await db.LoadFromAsync(first);
        Assert.NotNull(db.Lookup("abc123"));
        await db.LoadFromAsync(second);
        Assert.Null(db.Lookup("abc123"));
        Assert.NotNull(db.Lookup("def456"));
    }
}
