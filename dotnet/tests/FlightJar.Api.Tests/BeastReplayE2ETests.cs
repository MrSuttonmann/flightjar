using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FlightJar.Api.Tests;

/// <summary>
/// End-to-end: spin up a local TCP server acting as the BEAST feed, point the
/// app at it, send a single DF17 identification frame, and verify the aircraft
/// appears in <c>/api/aircraft</c>. This is the parity gate — if this passes,
/// the consumer → registry → snapshot → HTTP chain works end-to-end.
/// </summary>
[Collection("SequentialApi")]
public class BeastReplayE2ETests
{
    // DF17 TC4 identification: ICAO 4840D6, callsign KLM1023.
    // pyModeS-derived golden vector, already verified in AdsbDecoderTests.
    private static readonly byte[] Df17Identification =
    {
        0x8D, 0x48, 0x40, 0xD6, 0x20, 0x2C, 0xC3, 0x71,
        0xC3, 0x2C, 0xE0, 0x57, 0x60, 0x98,
    };

    [Fact]
    public async Task Replay_SurfacesAircraftInSnapshot()
    {
        // Bind a TCP listener on a free port, hand that to the app as BEAST_PORT.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var clientTcs = new TaskCompletionSource<TcpClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                var tcp = await listener.AcceptTcpClientAsync();
                clientTcs.TrySetResult(tcp);
            }
            catch (Exception ex) { clientTcs.TrySetException(ex); }
        });

        Environment.SetEnvironmentVariable("BEAST_HOST", "127.0.0.1");
        Environment.SetEnvironmentVariable("BEAST_PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("P2P_ENABLED", "0");

        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        // Wait for the consumer to connect.
        var accepted = await clientTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var stream = accepted.GetStream();

        // Send one BEAST frame: 0x1A 0x33 <6B MLAT> <1B sig> <14B msg>.
        var frame = new byte[2 + 6 + 1 + Df17Identification.Length];
        frame[0] = 0x1A;
        frame[1] = 0x33; // mode_s_long
        // MLAT ticks (all zeros fine for the test)
        // Signal byte at index 8
        frame[8] = 0x50;
        Array.Copy(Df17Identification, 0, frame, 9, Df17Identification.Length);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();

        // Poll /api/aircraft until the snapshot contains our plane (or time out).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        string? callsign = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            var resp = await client.GetAsync("/api/aircraft");
            if (!resp.IsSuccessStatusCode) continue;
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var arr = doc.RootElement.GetProperty("aircraft");
            if (arr.GetArrayLength() == 0) continue;
            foreach (var ac in arr.EnumerateArray())
            {
                if (ac.TryGetProperty("icao", out var icao)
                    && string.Equals(icao.GetString(), "4840D6", StringComparison.OrdinalIgnoreCase))
                {
                    callsign = ac.TryGetProperty("callsign", out var cs) ? cs.GetString() : null;
                    break;
                }
            }
            if (callsign is not null) break;
        }

        listener.Stop();
        accepted.Dispose();

        Assert.NotNull(callsign);
        Assert.Equal("KLM1023", callsign);
    }
}
