namespace FlightJar.Core.ReferenceData;

/// <summary>
/// Minimal RFC-4180 CSV reader. Handles double-quoted fields (with
/// <c>""</c> escaping) and CRLF / LF line terminators. Supports ',' and
/// ';' delimiters. No embedded newlines inside quoted fields — not
/// needed for the data files we consume (OurAirports, tar1090-db,
/// OpenFlights).
/// </summary>
internal static class CsvReader
{
    /// <summary>Read rows from <paramref name="reader"/>. Yields each row as a list of fields.</summary>
    public static IEnumerable<List<string>> ReadAll(TextReader reader, char delimiter = ',')
    {
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }
            yield return ParseLine(line, delimiter);
        }
    }

    /// <summary>Async variant; same semantics as <see cref="ReadAll"/>.</summary>
    public static async IAsyncEnumerable<List<string>> ReadAllAsync(
        TextReader reader, char delimiter = ',',
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }
            yield return ParseLine(line, delimiter);
        }
    }

    private static List<string> ParseLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        var i = 0;
        while (i < line.Length)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                        continue;
                    }
                    inQuotes = false;
                    i++;
                    continue;
                }
                sb.Append(ch);
                i++;
                continue;
            }
            if (ch == '"')
            {
                inQuotes = true;
                i++;
                continue;
            }
            if (ch == delimiter)
            {
                fields.Add(sb.ToString());
                sb.Clear();
                i++;
                continue;
            }
            sb.Append(ch);
            i++;
        }
        fields.Add(sb.ToString());
        return fields;
    }

    /// <summary>Read a CSV with a header row, yielding each data row as
    /// a dictionary keyed by column name.</summary>
    public static async IAsyncEnumerable<Dictionary<string, string>> ReadDictAsync(
        TextReader reader, char delimiter = ',',
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        List<string>? header = null;
        await foreach (var row in ReadAllAsync(reader, delimiter, ct))
        {
            if (header is null)
            {
                header = row;
                continue;
            }
            var dict = new Dictionary<string, string>(header.Count, StringComparer.Ordinal);
            for (var i = 0; i < header.Count && i < row.Count; i++)
            {
                dict[header[i]] = row[i];
            }
            yield return dict;
        }
    }
}
