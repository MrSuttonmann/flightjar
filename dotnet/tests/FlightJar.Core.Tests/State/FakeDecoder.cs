using FlightJar.Core.State;
using FlightJar.Decoder.ModeS;

namespace FlightJar.Core.Tests.State;

/// <summary>
/// Mirror of the Python tests' <c>fake_decode</c> — maps the first 4 chars of
/// the hex input to a canned <see cref="DecodedMessage"/>.
/// </summary>
internal static class FakeDecoder
{
    public static readonly IReadOnlyDictionary<string, DecodedMessage> Messages = new Dictionary<string, DecodedMessage>
    {
        // DF17 identification (TC 4): callsign + category
        ["ID01"] = new()
        {
            Df = 17,
            CrcValid = true,
            Icao = "abc123",
            Typecode = 4,
            Callsign = "FLY123__",
            Category = 3,
        },
        // DF17 airborne baro position (TC 11)
        ["AP01"] = new()
        {
            Df = 17,
            CrcValid = true,
            Icao = "abc123",
            Typecode = 11,
            Altitude = 37000,
            CprFormat = 0,
            CprLat = 0,
            CprLon = 0,
        },
        ["AP02"] = new()
        {
            Df = 17,
            CrcValid = true,
            Icao = "abc123",
            Typecode = 11,
            Altitude = 30000,
            CprFormat = 1,
            CprLat = 0,
            CprLon = 0,
        },
        // DF17 airborne GNSS position (TC 20)
        ["GP01"] = new()
        {
            Df = 17,
            CrcValid = true,
            Icao = "abc123",
            Typecode = 20,
            Altitude = 37100,
            CprFormat = 0,
            CprLat = 0,
            CprLon = 0,
        },
        // DF17 velocity (TC 19)
        ["VL01"] = new()
        {
            Df = 17,
            CrcValid = true,
            Icao = "abc123",
            Typecode = 19,
            Groundspeed = 450,
            Track = 270.0,
            VerticalRate = -600,
        },
        // DF4 altitude reply
        ["AC01"] = new() { Df = 4, Icao = "def456", Altitude = 24000 },
        // DF5 squawk reply
        ["SQ01"] = new() { Df = 5, Icao = "def456", Squawk = "1234" },
        // DF17 with bad CRC — should be dropped
        ["BAD1"] = new() { Df = 17, CrcValid = false, Icao = "aaa111", Typecode = 1 },
        // DF11 all-call with only ICAO — should not pass the snapshot "nothing interesting" filter
        ["DF11"] = new() { Df = 11, Icao = "ddd" },
        // Unknown DF — registry should ignore entirely
        ["XXXX"] = new() { Df = 19 },
    };

    public static Func<string, DecodedMessage?> Decoder => hex =>
    {
        if (string.IsNullOrEmpty(hex) || hex.Length < 4)
        {
            return null;
        }
        return Messages.TryGetValue(hex[..4], out var m) ? m : null;
    };

    /// <summary>Default ResolveNewPosition override: returns (52.1, -1.1).</summary>
    public static (double Lat, double Lon)? DefaultLocal(Aircraft _a, int _cf, int _cl, int _co, bool _s)
        => (52.1, -1.1);
}
