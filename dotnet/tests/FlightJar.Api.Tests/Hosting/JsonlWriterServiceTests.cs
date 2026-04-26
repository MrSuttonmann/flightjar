using System.Text.Json;
using System.Threading.Channels;
using FlightJar.Api.Hosting;
using FlightJar.Core.Configuration;
using FlightJar.Decoder.Beast;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FlightJar.Api.Tests.Hosting;

public class JsonlWriterServiceFormatTests
{
    private static JsonlFrame ShortFrame(byte[] bytes, DateTimeOffset rxAt) =>
        new(new BeastFrame(BeastFrameType.ModeSShort, MlatTicks: 12_345, Signal: 200, Message: bytes), rxAt);

    private static JsonlFrame LongFrame(byte[] bytes, DateTimeOffset rxAt) =>
        new(new BeastFrame(BeastFrameType.ModeSLong, MlatTicks: 12_345, Signal: 200, Message: bytes), rxAt);

    [Fact]
    public void FormatLine_RawShape_MatchesDocumentedFields()
    {
        var bytes = new byte[] { 0x8d, 0x4c, 0xa2, 0xd1, 0x58, 0xc9, 0x01, 0xa0, 0xc0, 0xb8, 0xa0, 0xcb, 0xd1, 0xe7 };
        var rxAt = DateTimeOffset.Parse("2026-04-18T10:15:22.413291Z");
        var line = JsonlWriterService.FormatLine(LongFrame(bytes, rxAt), decode: false);

        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.Equal("2026-04-18T10:15:22.413291+00:00", root.GetProperty("ts_rx").GetString());
        Assert.Equal(12_345L, root.GetProperty("mlat_ticks").GetInt64());
        Assert.Equal("mode_s_long", root.GetProperty("type").GetString());
        Assert.Equal(200, root.GetProperty("signal").GetInt32());
        Assert.Equal("8d4ca2d158c901a0c0b8a0cbd1e7", root.GetProperty("hex").GetString());
        // No decoded sub-object when decode is off.
        Assert.False(root.TryGetProperty("decoded", out _));
    }

    [Fact]
    public void FormatLine_DecodeOn_AddsDecodedSubObjectForKnownDf()
    {
        // Same DF17 ADS-B identification message as above (TC = 11 → in
        // the surface-position TC range, but the decoder will populate
        // df + icao at minimum).
        var bytes = new byte[] { 0x8d, 0x4c, 0xa2, 0xd1, 0x58, 0xc9, 0x01, 0xa0, 0xc0, 0xb8, 0xa0, 0xcb, 0xd1, 0xe7 };
        var rxAt = DateTimeOffset.Parse("2026-04-18T10:15:22.413291Z");
        var line = JsonlWriterService.FormatLine(LongFrame(bytes, rxAt), decode: true);

        using var doc = JsonDocument.Parse(line);
        var decoded = doc.RootElement.GetProperty("decoded");
        Assert.Equal(17, decoded.GetProperty("df").GetInt32());
        // ICAO is rendered uppercase by the Mode S decoder — JSONL preserves
        // whatever the decoder produces; downstream consumers can normalize.
        Assert.Equal("4CA2D1", decoded.GetProperty("icao").GetString());
    }

    [Fact]
    public void FormatLine_ModeAcType_LabelledCorrectly()
    {
        var jf = new JsonlFrame(
            new BeastFrame(BeastFrameType.ModeAc, 0, 0, new byte[] { 0xab, 0xcd }),
            DateTimeOffset.UtcNow);
        var line = JsonlWriterService.FormatLine(jf, decode: false);
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("mode_ac", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void FormatLine_RxAt_AlwaysFormattedAsUtcWithMicroseconds()
    {
        // Input in a non-UTC zone — the formatted ts_rx should still
        // come out as +00:00 with 6 fractional digits.
        var rxAt = new DateTimeOffset(2026, 4, 18, 12, 15, 22, 413, TimeSpan.FromHours(2))
            .AddTicks(2910);  // 0.000291s extra → ffffff = 413291
        var line = JsonlWriterService.FormatLine(
            ShortFrame(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }, rxAt),
            decode: false);
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("2026-04-18T10:15:22.413291+00:00", doc.RootElement.GetProperty("ts_rx").GetString());
    }

    [Fact]
    public void FormatLine_DecodeIgnored_ForModeAcFrames()
    {
        var jf = new JsonlFrame(
            new BeastFrame(BeastFrameType.ModeAc, 0, 0, new byte[] { 0x12, 0x34 }),
            DateTimeOffset.UtcNow);
        var line = JsonlWriterService.FormatLine(jf, decode: true);
        using var doc = JsonDocument.Parse(line);
        // No decoder for Mode AC — sub-object should be absent.
        Assert.False(doc.RootElement.TryGetProperty("decoded", out _));
    }
}

public class JsonlWriterServiceIoTests
{
    private static AppOptions OptsWith(string path, JsonlRotateMode rotate, int keep) => new()
    {
        JsonlPath = path,
        JsonlRotate = rotate,
        JsonlKeep = keep,
        JsonlStdout = false,
        JsonlDecode = false,
    };

    [Fact]
    public async Task IsConfigured_FollowsPathAndStdoutFlags()
    {
        Assert.False(JsonlWriterService.IsConfigured(new AppOptions { JsonlPath = "", JsonlStdout = false }));
        Assert.True(JsonlWriterService.IsConfigured(new AppOptions { JsonlPath = "/tmp/x.jsonl", JsonlStdout = false }));
        Assert.True(JsonlWriterService.IsConfigured(new AppOptions { JsonlPath = "", JsonlStdout = true }));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WritesAllFrames_ToConfiguredPath_NoRotation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fj-jsonl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "beast.jsonl");
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-26T00:00:00Z"));
        var channel = Channel.CreateUnbounded<JsonlFrame>();
        var svc = new JsonlWriterService(
            OptsWith(path, JsonlRotateMode.None, keep: 5),
            channel.Reader, time, NullLogger<JsonlWriterService>.Instance);

        var task = svc.StartAsync(CancellationToken.None);
        await task;

        var bytes = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
        for (var i = 0; i < 3; i++)
        {
            channel.Writer.TryWrite(new JsonlFrame(
                new BeastFrame(BeastFrameType.ModeSShort, i, 100, bytes),
                time.GetUtcNow()));
        }
        // Let the writer drain.
        await Task.Delay(150);
        channel.Writer.Complete();
        await svc.StopAsync(CancellationToken.None);

        var lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        foreach (var l in lines)
        {
            using var doc = JsonDocument.Parse(l);
            Assert.Equal("mode_s_short", doc.RootElement.GetProperty("type").GetString());
        }

        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task DailyRotation_RenamesFile_WhenDateChanges()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fj-jsonl-rot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "beast.jsonl");
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-25T23:59:00Z"));
        var channel = Channel.CreateUnbounded<JsonlFrame>();
        var svc = new JsonlWriterService(
            OptsWith(path, JsonlRotateMode.Daily, keep: 10),
            channel.Reader, time, NullLogger<JsonlWriterService>.Instance);

        await svc.StartAsync(CancellationToken.None);

        var bytes = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
        channel.Writer.TryWrite(new JsonlFrame(
            new BeastFrame(BeastFrameType.ModeSShort, 1, 100, bytes),
            DateTimeOffset.Parse("2026-04-25T23:59:00Z")));
        // Cross midnight UTC — next frame is "tomorrow", forcing rotation.
        channel.Writer.TryWrite(new JsonlFrame(
            new BeastFrame(BeastFrameType.ModeSShort, 2, 100, bytes),
            DateTimeOffset.Parse("2026-04-26T00:00:01Z")));
        await Task.Delay(150);
        channel.Writer.Complete();
        await svc.StopAsync(CancellationToken.None);

        // The previous-day file should have been renamed with the .YYYYMMDD suffix.
        Assert.True(File.Exists(path), "current-day file should exist");
        Assert.True(File.Exists(path + ".20260425"),
            "previous-day file should have been rotated with date suffix");

        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
