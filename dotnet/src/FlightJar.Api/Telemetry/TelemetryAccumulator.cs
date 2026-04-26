namespace FlightJar.Api.Telemetry;

/// <summary>
/// Accumulates per-tick samples and discrete counters between PostHog
/// pings. <see cref="RegistryWorker"/> pushes a sample after each
/// snapshot tick (current aircraft count, count of those carrying fresh
/// Comm-B, and how long the tick took); <see cref="BeastConsumerService"/>
/// bumps the reconnect counter on each successful TCP connect.
/// <see cref="TelemetryWorker"/> drains the bag on every ping and the
/// counters reset to zero — so each ping reports stats covering only the
/// window since the previous ping.
/// </summary>
public sealed class TelemetryAccumulator
{
    private readonly object _gate = new();

    private long _aircraftSum;
    private long _samples;
    private int _aircraftMax;
    private long _commBSum;
    private int _commBMax;
    private double _tickMsSum;
    private double _tickMsMax;
    private int _reconnects;

    public void RecordTickSample(int aircraftCount, int commBAircraftCount, double tickDurationMs)
    {
        lock (_gate)
        {
            _aircraftSum += aircraftCount;
            _samples++;
            if (aircraftCount > _aircraftMax) _aircraftMax = aircraftCount;
            _commBSum += commBAircraftCount;
            if (commBAircraftCount > _commBMax) _commBMax = commBAircraftCount;
            _tickMsSum += tickDurationMs;
            if (tickDurationMs > _tickMsMax) _tickMsMax = tickDurationMs;
        }
    }

    public void RecordReconnect()
    {
        lock (_gate) _reconnects++;
    }

    public Snapshot DrainAndReset()
    {
        lock (_gate)
        {
            var samples = _samples;
            var snap = new Snapshot(
                AircraftAvg: samples > 0 ? (double)_aircraftSum / samples : 0,
                AircraftMax: _aircraftMax,
                CommBAvg: samples > 0 ? (double)_commBSum / samples : 0,
                CommBMax: _commBMax,
                TickAvgMs: samples > 0 ? _tickMsSum / samples : 0,
                TickMaxMs: _tickMsMax,
                Reconnects: _reconnects,
                Samples: samples);

            _aircraftSum = 0;
            _samples = 0;
            _aircraftMax = 0;
            _commBSum = 0;
            _commBMax = 0;
            _tickMsSum = 0;
            _tickMsMax = 0;
            _reconnects = 0;
            return snap;
        }
    }

    public sealed record Snapshot(
        double AircraftAvg,
        int AircraftMax,
        double CommBAvg,
        int CommBMax,
        double TickAvgMs,
        double TickMaxMs,
        int Reconnects,
        long Samples);
}
