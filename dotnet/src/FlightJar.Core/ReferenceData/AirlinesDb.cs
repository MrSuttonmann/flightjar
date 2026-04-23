using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Core.ReferenceData;

/// <summary>
/// ICAO airline code → IATA + name + alliance lookup. Reads
/// <a href="https://openflights.org/data.html">OpenFlights</a>
/// <c>airlines.dat</c>, keeps only <c>Active == 'Y'</c> rows with a valid
/// 3-letter ICAO code. Ports <c>app/airlines_db.py</c>.
/// </summary>
public sealed class AirlinesDb
{
    /// <summary>Alliance membership by ICAO airline code. Kept narrow —
    /// mis-labelling is worse than omission.</summary>
    public static readonly IReadOnlyDictionary<string, string> Alliances = new Dictionary<string, string>
    {
        // Star Alliance
        ["UAL"] = "star",
        ["DLH"] = "star",
        ["SIA"] = "star",
        ["ANA"] = "star",
        ["THY"] = "star",
        ["ANZ"] = "star",
        ["SAS"] = "star",
        ["TAP"] = "star",
        ["ETH"] = "star",
        ["ACA"] = "star",
        ["SWR"] = "star",
        ["LOT"] = "star",
        ["EVA"] = "star",
        ["AUA"] = "star",
        ["AVA"] = "star",
        ["AAR"] = "star",
        ["CCA"] = "star",
        ["COP"] = "star",
        ["EGY"] = "star",
        ["BEL"] = "star",
        ["CSZ"] = "star",
        // oneworld
        ["AAL"] = "oneworld",
        ["BAW"] = "oneworld",
        ["CPA"] = "oneworld",
        ["FIN"] = "oneworld",
        ["IBE"] = "oneworld",
        ["JAL"] = "oneworld",
        ["QFA"] = "oneworld",
        ["QTR"] = "oneworld",
        ["MAS"] = "oneworld",
        ["RJA"] = "oneworld",
        ["ALK"] = "oneworld",
        // SkyTeam
        ["AFR"] = "skyteam",
        ["DAL"] = "skyteam",
        ["KLM"] = "skyteam",
        ["AMX"] = "skyteam",
        ["CES"] = "skyteam",
        ["CSN"] = "skyteam",
        ["KAL"] = "skyteam",
        ["SVA"] = "skyteam",
        ["ITY"] = "skyteam",
        ["RAM"] = "skyteam",
        ["KQA"] = "skyteam",
        ["AEA"] = "skyteam",
        ["GIA"] = "skyteam",
        ["MEA"] = "skyteam",
        ["CAL"] = "skyteam",
        ["VIR"] = "skyteam",
    };

    private static readonly FrozenSet<string> NullMarkers = new[]
    {
        "", @"\N", "-", "N/A",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly ILogger _logger;
    private FrozenDictionary<string, AirlineRecord> _byIcao = FrozenDictionary<string, AirlineRecord>.Empty;

    public AirlinesDb(ILogger<AirlinesDb>? logger = null)
    {
        _logger = logger ?? NullLogger<AirlinesDb>.Instance;
    }

    public int Count => _byIcao.Count;

    public AirlineRecord? LookupByIcao(string? icao)
    {
        if (string.IsNullOrEmpty(icao))
        {
            return null;
        }
        return _byIcao.TryGetValue(icao.ToUpperInvariant(), out var r) ? r : null;
    }

    /// <summary>Match an airline-operated callsign by its 3-letter ICAO
    /// prefix. Returns null for GA / military / numeric callsigns.</summary>
    public AirlineRecord? LookupByCallsign(string? callsign)
    {
        if (callsign is null || callsign.Length < 3)
        {
            return null;
        }
        var prefix = callsign[..3].ToUpperInvariant();
        foreach (var c in prefix)
        {
            if (!char.IsLetter(c))
            {
                return null;
            }
        }
        return _byIcao.TryGetValue(prefix, out var r) ? r : null;
    }

    public async Task<int> LoadFromAsync(string path, CancellationToken ct = default)
    {
        using var reader = File.OpenText(path);
        var fresh = new Dictionary<string, AirlineRecord>(StringComparer.Ordinal);
        await foreach (var row in CsvReader.ReadAllAsync(reader, ',', ct))
        {
            if (row.Count < 8)
            {
                continue;
            }
            var name = Clean(row[1]);
            var iata = Clean(row[3]);
            var icao = Clean(row[4]);
            var callsign = Clean(row[5]);
            var country = Clean(row[6]);
            var active = Clean(row[7]);
            if (icao is null || icao.Length != 3 || !IsAllLetters(icao))
            {
                continue;
            }
            if (active != "Y")
            {
                continue;
            }
            var icaoUpper = icao.ToUpperInvariant();
            Alliances.TryGetValue(icaoUpper, out var alliance);
            fresh[icaoUpper] = new AirlineRecord(
                Icao: icaoUpper,
                Iata: iata?.ToUpperInvariant(),
                Name: name,
                Callsign: callsign,
                Country: country,
                Alliance: alliance);
        }
        _byIcao = fresh.ToFrozenDictionary(StringComparer.Ordinal);
        return _byIcao.Count;
    }

    public async Task<int> LoadFirstAvailableAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        foreach (var p in paths)
        {
            if (!File.Exists(p))
            {
                continue;
            }
            try
            {
                var n = await LoadFromAsync(p, ct);
                _logger.LogInformation("loaded airlines DB from {Path} ({Count} entries)", p, n);
                return n;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "failed to load airlines DB from {Path}", p);
            }
        }
        _logger.LogInformation("no airlines DB found; airline enrichment disabled");
        return 0;
    }

    private static string? Clean(string? value)
    {
        if (value is null)
        {
            return null;
        }
        var v = value.Trim();
        return NullMarkers.Contains(v) ? null : v;
    }

    private static bool IsAllLetters(string s)
    {
        foreach (var c in s)
        {
            if (!char.IsLetter(c))
            {
                return false;
            }
        }
        return true;
    }
}

public sealed record AirlineRecord(
    string Icao,
    string? Iata,
    string? Name,
    string? Callsign,
    string? Country,
    string? Alliance);
