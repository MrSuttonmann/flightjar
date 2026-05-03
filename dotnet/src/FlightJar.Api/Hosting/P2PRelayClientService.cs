using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlightJar.Core.Configuration;
using FlightJar.Core.State;
using FlightJar.Persistence.P2P;

namespace FlightJar.Api.Hosting;

/// <summary>
/// Maintains a persistent WebSocket connection to the configured P2P relay
/// server. Every <see cref="AppOptions.P2PPushIntervalS"/> seconds it pushes
/// a privacy-sanitised snapshot (receiver coords and per-aircraft distance
/// stripped). Incoming <c>aggregate</c> messages from the relay are written
/// into <see cref="PeerAircraftCache"/> so the next registry tick can merge
/// them into the broadcast snapshot.
/// <para>The service is always registered; the on/off switch lives in
/// <see cref="P2PConfigStore"/> (UI-toggleable, persisted to
/// <c>/data/p2p.json</c>). When disabled, the loop idles on a
/// <see cref="TaskCompletionSource"/> until the store flips back on; when
/// disabled mid-connection the live socket is closed and the loop returns
/// to idling.</para>
/// </summary>
public sealed class P2PRelayClientService : BackgroundService
{
    private readonly AppOptions _options;
    private readonly CurrentSnapshot _currentSnapshot;
    private readonly PeerAircraftCache _peerCache;
    private readonly P2PConfigStore _configStore;
    private readonly P2PRelayRegistrar _registrar;
    private readonly P2PStatus _status;
    private readonly ILogger<P2PRelayClientService> _logger;

    private CancellationTokenSource? _connectionCts;
    private TaskCompletionSource? _enableSignal;
    private readonly object _signalGate = new();

    private static readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    // Stable per-process instance identifier used to tag this connection at
    // the relay for logging/diagnostics — not exposed to other instances.
    private static readonly string _instanceId = Guid.NewGuid().ToString("N");

    public P2PRelayClientService(
        AppOptions options,
        CurrentSnapshot currentSnapshot,
        PeerAircraftCache peerCache,
        P2PConfigStore configStore,
        P2PRelayRegistrar registrar,
        P2PStatus status,
        ILogger<P2PRelayClientService> logger)
    {
        _options = options;
        _currentSnapshot = currentSnapshot;
        _peerCache = peerCache;
        _configStore = configStore;
        _registrar = registrar;
        _status = status;
        _logger = logger;

        _configStore.Changed += OnConfigChanged;
    }

    public override void Dispose()
    {
        _configStore.Changed -= OnConfigChanged;
        base.Dispose();
    }

    private void OnConfigChanged(P2PConfig cfg)
    {
        if (cfg.Enabled)
        {
            // Wake the idle loop.
            lock (_signalGate)
            {
                _enableSignal?.TrySetResult();
            }
        }
        else
        {
            // Tear down any live connection so the outer loop returns to idle.
            _connectionCts?.Cancel();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var relayUri = new Uri(_options.P2PRelayUrl);
        var backoff = TimeSpan.FromSeconds(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_configStore.Current.Enabled)
            {
                // Park until the user toggles it on (or shutdown).
                await WaitForEnableAsync(stoppingToken);
                if (stoppingToken.IsCancellationRequested) return;
                backoff = TimeSpan.FromSeconds(1);
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _connectionCts = cts;
                await RunConnectionAsync(relayUri, cts.Token);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // User toggled federation off — loop will see Enabled=false and park.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "P2P relay connection lost, reconnecting in {Backoff}s",
                    backoff.TotalSeconds);
                try { await Task.Delay(backoff, stoppingToken); }
                catch (OperationCanceledException) { return; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
            }
            finally
            {
                _connectionCts = null;
            }
        }
    }

    private Task WaitForEnableAsync(CancellationToken stoppingToken)
    {
        TaskCompletionSource tcs;
        lock (_signalGate)
        {
            _enableSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs = _enableSignal;
        }
        // Cancellation completes the task too, so the outer loop exits cleanly on shutdown.
        stoppingToken.Register(() => tcs.TrySetResult());
        return tcs.Task;
    }

    private async Task RunConnectionAsync(Uri relayUri, CancellationToken ct)
    {
        var token = await _registrar.EnsureTokenAsync(ct);

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        socket.Options.SetRequestHeader("X-Instance-Id", _instanceId);

        _logger.LogInformation("P2P connecting to relay {Uri}", relayUri);
        try
        {
            await socket.ConnectAsync(relayUri, ct);
        }
        catch (WebSocketException wse) when (LooksLikeAuthRejection(wse))
        {
            // Persisted token rejected — most likely the relay evicted it
            // after the 90d TTL. Drop it so the next loop iteration mints
            // a fresh one. Then rethrow so the outer loop applies backoff.
            _logger.LogWarning("P2P relay rejected token, will re-register on next attempt");
            await _registrar.InvalidateAsync(CancellationToken.None);
            throw;
        }
        _logger.LogInformation("P2P connected to relay");
        _status.SetConnected();

        // Run send and receive concurrently; if either exits (disconnect /
        // error), cancel the other and let the outer loop reconnect.
        using var innerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sendTask = SendLoopAsync(socket, innerCts.Token);
        var receiveTask = ReceiveLoopAsync(socket, innerCts.Token);

        try
        {
            await Task.WhenAny(sendTask, receiveTask);
            await innerCts.CancelAsync();
            try { await Task.WhenAll(sendTask, receiveTask); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "P2P relay connection closed");
            }
        }
        finally
        {
            _status.SetDisconnected();
        }
    }

    private async Task SendLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.P2PPushIntervalS));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (socket.State != WebSocketState.Open) return;
            var payload = BuildSanitisedPayload();
            await SendTextAsync(socket, payload, ct);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            ms.SetLength(0);
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    ms.Write(buffer, 0, result.Count);
                }
            }
            while (!result.EndOfMessage);

            if (ms.Length > 0)
            {
                ProcessMessage(ms.ToArray());
            }
        }
    }

    private void ProcessMessage(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;

            switch (typeProp.GetString())
            {
                case "aggregate" when root.TryGetProperty("aircraft", out var aircraftProp):
                    var aircraft = JsonSerializer.Deserialize<List<SnapshotAircraft>>(
                        aircraftProp.GetRawText(), _jsonOpts);
                    if (aircraft is { Count: > 0 })
                    {
                        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                        _peerCache.Update(aircraft, nowUnix);
                    }
                    if (root.TryGetProperty("peers", out var peersProp)
                        && peersProp.ValueKind == JsonValueKind.Number
                        && peersProp.TryGetInt32(out var peers))
                    {
                        _status.UpdatePeers(peers);
                    }
                    break;

                case "auth_fail":
                    var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : "unknown";
                    _logger.LogError("P2P relay rejected authentication: {Reason}", reason);
                    throw new InvalidOperationException($"P2P relay auth failed: {reason}");
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "P2P relay: malformed message ignored");
        }
    }

    private string BuildSanitisedPayload()
    {
        var snap = _currentSnapshot.Snapshot;
        var aircraft = new List<SnapshotAircraft>(snap.Aircraft.Count);
        foreach (var ac in snap.Aircraft)
        {
            if (ac.Peer == true) continue; // don't echo back what we received
            // Strip receiver-specific + relay-computed fields:
            // - DistanceKm describes our reception, not the aircraft itself.
            // - SeenByOthers is filled by the relay per-recipient; if we
            //   echoed it back the relay would treat it as our reading.
            aircraft.Add(ac with { DistanceKm = null, SeenByOthers = null });
        }

        // Explicitly null out receiver location — never share it with the relay.
        var sanitised = new SanitisedSnapshot(
            Type: "snapshot",
            SiteName: _configStore.Current.ShareSiteName ? snap.SiteName : null,
            Aircraft: aircraft);
        return JsonSerializer.Serialize(sanitised, _jsonOpts);
    }

    private static async Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static bool LooksLikeAuthRejection(WebSocketException wse)
    {
        // ClientWebSocket surfaces a non-101 handshake response as a
        // WebSocketException; the message includes the status code. We
        // match on 401 specifically rather than any error so genuine
        // network failures still hit the normal backoff path.
        var msg = wse.Message;
        return msg.Contains("401", StringComparison.Ordinal)
            || msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SanitisedSnapshot(
        string Type,
        string? SiteName,
        IReadOnlyList<SnapshotAircraft> Aircraft);
}
