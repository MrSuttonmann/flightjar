using FlightJar.Clients.Vfrmap;

namespace FlightJar.Api.Hosting;

/// <summary>
/// Kicks off one <see cref="VfrmapCycle.DiscoverAsync"/> at startup, then
/// refreshes every 6 h. Without this the IFR Low / IFR High map overlays
/// would have no cycle date to plug into the tile URL template and the
/// frontend would hide them.
/// </summary>
public sealed class VfrmapCycleRefresher : BackgroundService
{
    private readonly VfrmapCycle _cycle;
    private readonly TimeProvider _time;
    private readonly ILogger<VfrmapCycleRefresher> _logger;

    public VfrmapCycleRefresher(
        VfrmapCycle cycle, TimeProvider time, ILogger<VfrmapCycleRefresher> logger)
    {
        _cycle = cycle;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // One-shot discover at startup so the overlays light up as soon
        // as possible, then refresh on the FAA 28-day cadence granularity.
        try
        {
            await _cycle.DiscoverAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VFRMap cycle initial discover failed");
        }

        try
        {
            using var timer = new PeriodicTimer(VfrmapCycle.RefreshInterval, _time);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _cycle.DiscoverAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "VFRMap cycle refresh failed");
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
