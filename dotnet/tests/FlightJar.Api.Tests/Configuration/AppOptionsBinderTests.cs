using FlightJar.Api.Configuration;
using FlightJar.Core.Configuration;

namespace FlightJar.Api.Tests.Configuration;

public class AppOptionsBinderTests
{
    [Fact]
    public void Defaults_WhenEnvIsEmpty()
    {
        var cfg = AppOptionsBinder.FromEnvironment(Env());
        Assert.Equal("readsb", cfg.BeastHost);
        Assert.Equal(30005, cfg.BeastPort);
        Assert.Null(cfg.LatRef);
        Assert.Null(cfg.LonRef);
        Assert.Equal(0.0, cfg.ReceiverAnonKm);
        Assert.Null(cfg.SiteName);
        Assert.Equal(JsonlRotateMode.Daily, cfg.JsonlRotate);
        Assert.Equal(14, cfg.JsonlKeep);
        Assert.False(cfg.JsonlStdout);
        Assert.Equal(1.0, cfg.SnapshotInterval);
    }

    [Fact]
    public void ParsesHappyPath()
    {
        var cfg = AppOptionsBinder.FromEnvironment(Env(new()
        {
            ["BEAST_HOST"] = "ultrafeeder",
            ["BEAST_PORT"] = "31005",
            ["LAT_REF"] = "52.98",
            ["LON_REF"] = "-1.20",
            ["RECEIVER_ANON_KM"] = "10",
            ["SITE_NAME"] = "Home",
            ["BEAST_OUTFILE"] = "/tmp/x.jsonl",
            ["BEAST_ROTATE"] = "hourly",
            ["BEAST_ROTATE_KEEP"] = "7",
            ["BEAST_STDOUT"] = "1",
            ["SNAPSHOT_INTERVAL"] = "2.5",
        }));

        Assert.Equal("ultrafeeder", cfg.BeastHost);
        Assert.Equal(31005, cfg.BeastPort);
        Assert.Equal(52.98, cfg.LatRef);
        Assert.Equal(-1.20, cfg.LonRef);
        Assert.Equal(10.0, cfg.ReceiverAnonKm);
        Assert.Equal("Home", cfg.SiteName);
        Assert.Equal("/tmp/x.jsonl", cfg.JsonlPath);
        Assert.Equal(JsonlRotateMode.Hourly, cfg.JsonlRotate);
        Assert.Equal(7, cfg.JsonlKeep);
        Assert.True(cfg.JsonlStdout);
        Assert.Equal(2.5, cfg.SnapshotInterval);
    }

    [Fact]
    public void InvalidRotate_Raises()
    {
        var ex = Assert.Throws<ConfigException>(() =>
            AppOptionsBinder.FromEnvironment(Env(new() { ["BEAST_ROTATE"] = "weekly" })));
        Assert.Contains("BEAST_ROTATE", ex.Message);
    }

    [Fact]
    public void InvalidPort_Raises()
    {
        var ex = Assert.Throws<ConfigException>(() =>
            AppOptionsBinder.FromEnvironment(Env(new() { ["BEAST_PORT"] = "70000" })));
        Assert.Contains("BEAST_PORT", ex.Message);
    }

    [Fact]
    public void NonIntegerPort_Raises()
    {
        var ex = Assert.Throws<ConfigException>(() =>
            AppOptionsBinder.FromEnvironment(Env(new() { ["BEAST_PORT"] = "abc" })));
        Assert.Contains("BEAST_PORT", ex.Message);
    }

    [Fact]
    public void NegativeRotateKeep_Raises()
    {
        var ex = Assert.Throws<ConfigException>(() =>
            AppOptionsBinder.FromEnvironment(Env(new() { ["BEAST_ROTATE_KEEP"] = "-1" })));
        Assert.Contains("BEAST_ROTATE_KEEP", ex.Message);
    }

    [Fact]
    public void AircraftDbRefreshHours_DefaultIsZero()
    {
        var cfg = AppOptionsBinder.FromEnvironment(Env());
        Assert.Equal(0.0, cfg.AircraftDbRefreshHours);
    }

    [Fact]
    public void AircraftDbRefreshHours_AcceptsPositiveValue()
    {
        var cfg = AppOptionsBinder.FromEnvironment(Env(new() { ["AIRCRAFT_DB_REFRESH_HOURS"] = "168" }));
        Assert.Equal(168.0, cfg.AircraftDbRefreshHours);
    }

    [Fact]
    public void AircraftDbRefreshHours_RejectsNegative()
    {
        var ex = Assert.Throws<ConfigException>(() =>
            AppOptionsBinder.FromEnvironment(Env(new() { ["AIRCRAFT_DB_REFRESH_HOURS"] = "-1" })));
        Assert.Contains("AIRCRAFT_DB_REFRESH_HOURS", ex.Message);
    }

    [Fact]
    public void ZeroSnapshotInterval_Raises()
    {
        var ex = Assert.Throws<ConfigException>(() =>
            AppOptionsBinder.FromEnvironment(Env(new() { ["SNAPSHOT_INTERVAL"] = "0" })));
        Assert.Contains("SNAPSHOT_INTERVAL", ex.Message);
    }

    [Fact]
    public void MalformedLatRef_IsTolerated()
    {
        var cfg = AppOptionsBinder.FromEnvironment(Env(new() { ["LAT_REF"] = "not-a-number" }));
        Assert.Null(cfg.LatRef);
    }

    [Fact]
    public void EmptySiteName_TreatedAsUnset()
    {
        var cfg = AppOptionsBinder.FromEnvironment(Env(new() { ["SITE_NAME"] = "  " }));
        Assert.Null(cfg.SiteName);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    [InlineData("", false)]
    public void StdoutTruthyValues(string raw, bool expected)
    {
        var cfg = AppOptionsBinder.FromEnvironment(Env(new() { ["BEAST_STDOUT"] = raw }));
        Assert.Equal(expected, cfg.JsonlStdout);
    }

    private static IDictionary<string, string?> Env(Dictionary<string, string?>? overrides = null)
        => (IDictionary<string, string?>)(overrides ?? new Dictionary<string, string?>());
}
