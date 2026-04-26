using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using FlightJar.Core.Configuration;
using FlightJar.Decoder.Beast;
using FlightJar.Decoder.ModeS;

namespace FlightJar.Api.Hosting;

/// <summary>
/// One BEAST frame paired with the wall-clock timestamp it was received.
/// Captured at the consumer (not at write time) so the JSONL line's
/// <c>ts_rx</c> reflects when the radio actually saw the message rather
/// than when the writer happened to dequeue it.
/// </summary>
public readonly record struct JsonlFrame(BeastFrame Frame, DateTimeOffset RxAt);

/// <summary>
/// Per-message JSONL log writer. Reads frames from a dedicated channel
/// (separate from the registry channel so a slow disk can't backpressure
/// the decoder), formats each as one JSON object per line, and writes to
/// <see cref="AppOptions.JsonlPath"/> and/or stdout. Optional decoded
/// fields are appended under a <c>decoded</c> sub-object when
/// <see cref="AppOptions.JsonlDecode"/> is on.
///
/// Rotation policy honours <see cref="AppOptions.JsonlRotate"/>:
/// hourly / daily / none. On rotation the live file is renamed with a
/// UTC timestamp suffix (<c>.YYYYMMDD-HH</c> or <c>.YYYYMMDD</c>) and a
/// fresh file is opened at the original path. Rotated files older than
/// <see cref="AppOptions.JsonlKeep"/> are deleted.
/// </summary>
public sealed class JsonlWriterService : BackgroundService
{
    private readonly AppOptions _options;
    private readonly ChannelReader<JsonlFrame> _frames;
    private readonly TimeProvider _time;
    private readonly ILogger<JsonlWriterService> _logger;

    private StreamWriter? _writer;
    private string? _currentRotateKey;
    private long _lastFlushTicks;
    private const long FlushIntervalMs = 250;

    public JsonlWriterService(
        AppOptions options,
        ChannelReader<JsonlFrame> frames,
        TimeProvider time,
        ILogger<JsonlWriterService> logger)
    {
        _options = options;
        _frames = frames;
        _time = time;
        _logger = logger;
    }

    /// <summary>True when the user has asked for any kind of JSONL output.</summary>
    public static bool IsConfigured(AppOptions options) =>
        !string.IsNullOrWhiteSpace(options.JsonlPath) || options.JsonlStdout;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsConfigured(_options))
        {
            _logger.LogInformation("jsonl: disabled (BEAST_OUTFILE empty and BEAST_STDOUT off)");
            return;
        }

        _logger.LogInformation(
            "jsonl: writing to {Path} (rotate={Rotate}, keep={Keep}, stdout={Stdout}, decode={Decode})",
            _options.JsonlPath, _options.JsonlRotate, _options.JsonlKeep, _options.JsonlStdout, _options.JsonlDecode);

        try
        {
            await foreach (var jf in _frames.ReadAllAsync(stoppingToken))
            {
                WriteOne(jf);
                MaybeFlush();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { _writer?.Flush(); _writer?.Dispose(); } catch { /* swallow on shutdown */ }
            _writer = null;
        }
    }

    private void WriteOne(JsonlFrame jf)
    {
        try
        {
            EnsureWriterFor(jf.RxAt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "jsonl: failed to open output file");
            return;
        }

        var line = FormatLine(jf, _options.JsonlDecode);

        try
        {
            _writer?.WriteLine(line);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "jsonl: write failed");
            try { _writer?.Dispose(); } catch { /* swallow */ }
            _writer = null;
            _currentRotateKey = null;
        }

        if (_options.JsonlStdout)
        {
            Console.Out.WriteLine(line);
        }
    }

    private void MaybeFlush()
    {
        // Buffered writes keep up with high frame rates without
        // calling write() per line. Flush periodically so a crash
        // doesn't lose more than ~250 ms of data.
        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        if (nowMs - _lastFlushTicks >= FlushIntervalMs)
        {
            try { _writer?.Flush(); } catch { /* swallow */ }
            _lastFlushTicks = nowMs;
        }
    }

    private void EnsureWriterFor(DateTimeOffset rxAt)
    {
        if (string.IsNullOrWhiteSpace(_options.JsonlPath))
        {
            // stdout-only: no file writer needed.
            _writer = null;
            return;
        }

        var key = RotateKey(rxAt);
        if (_writer is not null && key == _currentRotateKey)
        {
            return;
        }

        // Rotation needed (or first open).
        if (_writer is not null && _currentRotateKey is not null
            && _options.JsonlRotate != JsonlRotateMode.None)
        {
            try { _writer.Flush(); _writer.Dispose(); } catch { /* swallow */ }
            _writer = null;
            try
            {
                if (File.Exists(_options.JsonlPath))
                {
                    var rotated = _options.JsonlPath + "." + _currentRotateKey;
                    if (File.Exists(rotated)) File.Delete(rotated);
                    File.Move(_options.JsonlPath, rotated);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "jsonl: rotation rename failed");
            }
            SweepOldFiles();
        }

        var dir = Path.GetDirectoryName(_options.JsonlPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        // Append + UTF8 (no BOM) + buffered. Shared so external tools
        // can `tail -f` the file without breaking the writer.
        var stream = new FileStream(
            _options.JsonlPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete,
            bufferSize: 8192,
            FileOptions.SequentialScan);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            NewLine = "\n",
        };
        _currentRotateKey = key;
    }

    private string RotateKey(DateTimeOffset rxAt) => _options.JsonlRotate switch
    {
        JsonlRotateMode.Hourly => rxAt.UtcDateTime.ToString("yyyyMMdd-HH", CultureInfo.InvariantCulture),
        JsonlRotateMode.Daily => rxAt.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        _ => "static",
    };

    private void SweepOldFiles()
    {
        if (_options.JsonlKeep <= 0) return;
        if (string.IsNullOrWhiteSpace(_options.JsonlPath)) return;

        try
        {
            var dir = Path.GetDirectoryName(_options.JsonlPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            var baseName = Path.GetFileName(_options.JsonlPath);
            // Match "<base>.<suffix>" — anything with the rotation prefix.
            var rotated = Directory.EnumerateFiles(dir, baseName + ".*")
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .Skip(_options.JsonlKeep)
                .ToList();
            foreach (var fi in rotated)
            {
                try { fi.Delete(); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "jsonl: couldn't delete {Path}", fi.FullName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "jsonl: sweep failed");
        }
    }

    /// <summary>
    /// Format one frame as a JSON line. Pure function (no side effects)
    /// so unit tests can assert on the output without spinning up the
    /// service.
    /// </summary>
    public static string FormatLine(JsonlFrame jf, bool decode)
    {
        var sb = new StringBuilder(192);
        sb.Append('{');
        sb.Append("\"ts_rx\":\"")
          .Append(jf.RxAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture))
          .Append("+00:00\"");
        sb.Append(",\"mlat_ticks\":").Append(jf.Frame.MlatTicks.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"type\":\"").Append(TypeLabel(jf.Frame.Type)).Append('"');
        sb.Append(",\"signal\":").Append(jf.Frame.Signal.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"hex\":\"").Append(BytesToHex(jf.Frame.Message.Span)).Append('"');

        if (decode && jf.Frame.Type is BeastFrameType.ModeSShort or BeastFrameType.ModeSLong)
        {
            var hex = BytesToHex(jf.Frame.Message.Span);
            DecodedMessage? decoded = null;
            try { decoded = MessageDecoder.Decode(hex); } catch { /* malformed → skip */ }
            if (decoded is not null)
            {
                sb.Append(",\"decoded\":")
                  .Append(JsonSerializer.Serialize(decoded, DecodedJsonOpts));
            }
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static string TypeLabel(BeastFrameType type) => type switch
    {
        BeastFrameType.ModeAc => "mode_ac",
        BeastFrameType.ModeSShort => "mode_s_short",
        BeastFrameType.ModeSLong => "mode_s_long",
        _ => "unknown",
    };

    private static string BytesToHex(ReadOnlySpan<byte> bytes)
    {
        Span<char> buf = stackalloc char[bytes.Length * 2];
        const string hex = "0123456789abcdef";
        for (var i = 0; i < bytes.Length; i++)
        {
            buf[i * 2] = hex[bytes[i] >> 4];
            buf[i * 2 + 1] = hex[bytes[i] & 0xF];
        }
        return new string(buf);
    }

    private static readonly JsonSerializerOptions DecodedJsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
