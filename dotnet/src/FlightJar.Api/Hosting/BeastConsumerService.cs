using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Channels;
using FlightJar.Core;
using FlightJar.Core.Configuration;
using FlightJar.Decoder.Beast;

namespace FlightJar.Api.Hosting;

/// <summary>
/// Connects to the BEAST feed TCP endpoint, parses frames with
/// <see cref="BeastFrameReader"/>, and writes them to the shared frame
/// channel. Reconnects with exponential backoff (1s → 30s) when the
/// connection drops.
/// </summary>
public sealed class BeastConsumerService : BackgroundService
{
    private readonly AppOptions _options;
    private readonly ChannelWriter<BeastFrame> _frames;
    private readonly BeastConnectionState _state;
    private readonly ILogger<BeastConsumerService> _logger;

    public BeastConsumerService(
        AppOptions options,
        ChannelWriter<BeastFrame> frames,
        BeastConnectionState state,
        ILogger<BeastConsumerService> logger)
    {
        _options = options;
        _frames = frames;
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        var maxBackoff = TimeSpan.FromSeconds(30);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation(
                    "connecting to {Host}:{Port}",
                    _options.BeastHost, _options.BeastPort);
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(_options.BeastHost, _options.BeastPort, stoppingToken);
                _logger.LogInformation("connected");
                _state.Set(true);
                backoff = TimeSpan.FromSeconds(1);

                try
                {
                    await using var stream = tcp.GetStream();
                    var pipe = PipeReader.Create(stream);
                    await ConsumeFramesAsync(pipe, stoppingToken);
                }
                finally
                {
                    _state.Set(false);
                }
                _logger.LogWarning("BEAST stream closed by remote");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _state.Set(false);
                _logger.LogWarning(ex, "BEAST connection error");
            }

            try
            {
                await Task.Delay(backoff, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, maxBackoff.TotalSeconds));
        }
    }

    private async Task ConsumeFramesAsync(PipeReader reader, CancellationToken ct)
    {
        var buffered = new List<BeastFrame>(64);
        while (!ct.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;

            buffered.Clear();
            int consumedBytes;
            if (buffer.IsSingleSegment)
            {
                consumedBytes = BeastFrameReader.ParseMany(buffer.FirstSpan, buffered);
            }
            else
            {
                // Copy multi-segment sequences to a contiguous buffer. PipeReader
                // tends to return single-segment for TCP, so this path is rare.
                var contiguous = BuffersExtensions.ToArray(buffer);
                consumedBytes = BeastFrameReader.ParseMany(contiguous, buffered);
            }

            foreach (var frame in buffered)
            {
                // Drop-oldest bounded channel — a stuck registry must not
                // back-pressure the TCP reader and wedge the connection.
                _frames.TryWrite(frame);
            }

            reader.AdvanceTo(buffer.GetPosition(consumedBytes), buffer.End);
            if (result.IsCompleted)
            {
                break;
            }
        }
        await reader.CompleteAsync();
    }
}
