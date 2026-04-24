---
name: reference-data-loader-add
description: Add a new in-memory reference-data loader under FlightJar.Core.ReferenceData — CSV/JSON/TSV file baked into the Docker image with an optional /data/ override, loaded once at startup into a FrozenDictionary or similar, exposed via a DI-registered singleton. Use when the user wants to bundle a new lookup set — military-callsign prefixes, type-designator → wake-turbulence category, runway data, FIR boundaries, aircraft silhouettes, ICAO24 allocation blocks. Covers the loader class, the DI wiring, the Dockerfile fetch, the `LoadFirstAvailableAsync` path convention, and the test pattern. Do NOT use for external HTTP data sources (that's `external-client-add`), per-aircraft trails, or anything that changes mid-run.
---

# Adding a reference-data loader

Reference data in FlightJar is static-ish: big tables (ICAO24 →
registration, airport coords, airline names, alliances) baked
into the Docker image at build time, loaded once at startup, and
swapped atomically if the user drops a newer copy into `/data/`.
Four loaders already follow this template under
`FlightJar.Core/ReferenceData`: `AircraftDb`, `AirportsDb`,
`NavaidsDb`, `AirlinesDb`. The shared shape is:

- One class per loader, holding a `FrozenDictionary` (for O(1)
  lookups) or a `FrozenDictionary<string, …>[]` (for small sets
  of hashed indexes).
- `LoadFromAsync(path)` parses a single file and atomically
  swaps the backing dictionary on success.
- `LoadFirstAvailableAsync(paths)` tries candidates in order,
  uses the first one that exists, logs info / warning on
  success / parse failure, and returns the row count.
- `Lookup(…)` / `Bbox(…)` / etc. — read-only queries against the
  frozen backing store.

Good references: `AircraftDb` (simplest — gzipped
semicolon-separated, one-shot dict); `AirportsDb` (CSV via the
`CsvReader.ReadDictAsync` helper + a `Bbox` spatial query);
`AirlinesDb` (two indexes plus a curated alliance side-table).

Reach for **`external-client-add`** instead if the data is
fetched live from an HTTP service — that's a different pattern
with caching, throttling, and TTLs.

## Touchpoints

1. **`dotnet/src/FlightJar.Core/ReferenceData/<Name>Db.cs`** (new) —
   the loader class. Namespace is `FlightJar.Core.ReferenceData`.
2. **`dotnet/src/FlightJar.Api/Program.cs`** —
   `AddSingleton<<Name>Db>()` near the other reference DBs (~L62);
   add a `LoadFirstAvailableAsync(RefDataCandidates(dataDir,
   "your_file.ext"))` to the startup `Task.WhenAll` (~L254).
3. **`dotnet/Dockerfile`** — `ARG <NAME>_DB_URL=…` and
   `RUN curl -fsSL -o /out/<file> "${<NAME>_DB_URL}"` so the
   file gets baked in. Source from `raw.githubusercontent.com`
   — never `github.com/…/raw/…` (documented throughput reason
   in CLAUDE.md).
4. **`dotnet/tests/FlightJar.Core.Tests/ReferenceData/<Name>DbTests.cs`** —
   parse happy path + atomic-swap + `LoadFirstAvailable` behaviour.
5. **`CLAUDE.md`** — one bullet under
   `### External data sources` + one row in the `/data/`
   persistence table if the file is user-overridable.
6. **`README.md`** — mention the new data source in the credits /
   feature bullets.

## Step 1 — Pick the file shape

What you parse dictates the helpers you reach for:

| Shape | Parse | Example |
|---|---|---|
| Gzipped CSV / TSV | `GZipStream` + hand-split (`AircraftDb`) | tar1090-db aircraft.csv.gz |
| Headered CSV, comma-delimited | `CsvReader.ReadDictAsync` | OurAirports airports.csv |
| Positional CSV, no header | `CsvReader.ReadAllAsync` + index into `row[i]` | OpenFlights airlines.dat |
| JSON | `JsonSerializer.DeserializeAsync<T>` | mil-allocation-blocks.json |

Stick to existing helpers unless you're parsing something
genuinely different. If your file is big (> 5 MB), keep it
gzipped on disk and stream-decompress during parse — the
existing `AircraftDb` shows the pattern.

## Step 2 — The loader class

Minimum skeleton, modelled on `AircraftDb`:

```csharp
using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Core.ReferenceData;

/// <summary>
/// <paramref name="key"/> → <paramref name="value"/> lookup backed by
/// <!-- upstream description + licence + file name -->.
/// Load is opt-in — if no file is present, lookups no-op.
/// </summary>
public sealed class YourDb
{
    private readonly ILogger _logger;
    private FrozenDictionary<string, YourRecord> _entries =
        FrozenDictionary<string, YourRecord>.Empty;

    public YourDb(ILogger<YourDb>? logger = null)
    {
        _logger = logger ?? NullLogger<YourDb>.Instance;
    }

    public int Count => _entries.Count;

    public YourRecord? Lookup(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        return _entries.TryGetValue(key.ToUpperInvariant(), out var r) ? r : null;
    }

    public async Task<int> LoadFromAsync(string path, CancellationToken ct = default)
    {
        var fresh = new Dictionary<string, YourRecord>(StringComparer.Ordinal);
        // …parse into `fresh`…
        _entries = fresh.ToFrozenDictionary(StringComparer.Ordinal);
        return _entries.Count;
    }

    public async Task<int> LoadFirstAvailableAsync(
        IEnumerable<string> paths, CancellationToken ct = default)
    {
        foreach (var p in paths)
        {
            if (!File.Exists(p)) continue;
            try
            {
                var n = await LoadFromAsync(p, ct);
                _logger.LogInformation("loaded your DB from {Path} ({Count} entries)", p, n);
                return n;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "failed to load your DB from {Path}", p);
            }
        }
        _logger.LogInformation("no your DB found; <feature> disabled");
        return 0;
    }
}

public sealed record YourRecord(string Key, string Name /* …fields… */);
```

Load-bearing conventions:

- **Atomic swap.** Build the full dict (`fresh`), then assign to
  `_entries` in one statement. Don't mutate the live dict in
  place — a concurrent lookup could observe a half-loaded state.
  `FrozenDictionary` is read-optimised and can't be mutated, so
  this gives you a get-for-free invariant.
- **Key normalisation.** Uppercase (airline ICAO, airport ICAO)
  or lowercase (aircraft ICAO24 hex) should be decided up front
  and enforced in both the insert and the lookup — pick one and
  stay consistent with similar types already in the repo.
  Existing convention: ICAO airport/airline uppercase, ICAO24
  lowercase.
- **Opt-in loading.** The file not being present is not an
  error. Log at info, return 0, let the feature gracefully no-op
  via `Count == 0` or `Lookup(…) == null`. Throwing on a missing
  file breaks users who haven't baked the image themselves.
- **No lock.** `FrozenDictionary` assigned to a plain field is
  safe for concurrent reads when assignment is atomic (reference
  assignment on 64-bit CLR is atomic). Don't add a `lock`
  around reads.

### Spatial queries

If the new type needs a bounding-box query (like `AirportsDb.Bbox`),
iterate `_entries.Values` rather than building a spatial index —
the existing sets are small enough (~70k airports, ~50k navaids)
that linear scans beat anything clever once you add the
antimeridian-wrap handling. Mirror `AirportsDb.Bbox`'s pattern
when `minLon > maxLon` (wraps through the dateline).

## Step 3 — Register in Program.cs

Two edits in `Program.cs`:

```csharp
// near the other reference-data DBs (~line 62):
builder.Services.AddSingleton<YourDb>();
```

```csharp
// inside the startup `await Task.WhenAll(…)` (~line 254):
await Task.WhenAll(
    // ...existing loads...
    app.Services.GetRequiredService<YourDb>().LoadFirstAvailableAsync(
        RefDataCandidates(dataDir, "your_file.csv")),
    // ...
);
```

`RefDataCandidates(dataDir, fileName)` returns the standard
search list: `/data/<file>` (user override) first, then two
baked-in paths inside `AppContext.BaseDirectory`. **Use the
helper** — if you build the candidate list by hand, you'll
diverge from the `/data/` override behaviour everyone expects.

## Step 4 — Dockerfile

Add the download to `dotnet/Dockerfile`, following the existing
block around L65:

```dockerfile
ARG YOUR_DB_URL=https://raw.githubusercontent.com/<owner>/<repo>/<ref>/<path>
RUN mkdir -p /out \
 && curl -fsSL -o /out/your_file.csv "${YOUR_DB_URL}"
```

Rules:

- **`raw.githubusercontent.com` only.** Not `github.com/.../raw/...`
  — CLAUDE.md documents why (lower CI throughput ceiling; we hit
  it on build boxes).
- **Pin the ref.** Use a tag, a commit SHA, or a long-lived
  branch like `main` / `master` that the upstream actually
  maintains. Don't use short-lived branches.
- **Check the licence.** tar1090-db is CC0, OurAirports is
  public domain, OpenFlights is ODbL. Anything copyleft, or a
  source that forbids redistribution, is off the table — a
  bundled-image redistribution is what we do here. If the
  licence allows it, credit the upstream in the README.
- **`-fsSL`** — fail on HTTP error, silent progress, show
  errors, follow redirects. Match the existing flag set exactly.

If the file is large (> 50 MB) or changes daily, consider
keeping it out of the image and fetching it at runtime via a
background refresh worker instead. For anything < 10 MB and
stable, bake it.

## Step 5 — Tests

Add `dotnet/tests/FlightJar.Core.Tests/ReferenceData/<Name>DbTests.cs`.
Use the `IDisposable` + tmp-dir pattern from
`AircraftDbTests`:

```csharp
public class YourDbTests : IDisposable
{
    private readonly string _tmp;

    public YourDbTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"flightjar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    [Fact]
    public async Task LoadFrom_ParsesRowsCorrectly() { … }

    [Fact]
    public async Task LoadFrom_SkipsMalformedRows() { … }

    [Fact]
    public async Task Lookup_NormalisesKey() { … }

    [Fact]
    public async Task LoadFirstAvailable_ReturnsZero_WhenNothingExists() { … }

    [Fact]
    public async Task LoadFrom_AtomicSwap_ReplacesPreviousEntries()
    {
        // Load file A, confirm one key present.
        // Load file B, confirm file A's key is gone and file B's key is present.
    }
}
```

The atomic-swap test is the one newcomers skip — it catches the
case where someone refactors `LoadFromAsync` to mutate
`_entries` in place. Keep it.

## Step 6 — Docs

- **`CLAUDE.md`** — add one bullet under `### External data sources`
  naming the upstream, licence, and file. If the file is
  overridable under `/data/`, add a row to the persistence table
  too (mark the owner as "optional").
- **`README.md`** — one bullet in the "what you get" or "data
  sources" section, plus a link to the upstream if licence
  requires it.

## Step 7 — Verify

```bash
cd dotnet
dotnet format FlightJar.slnx --verify-no-changes
dotnet test tests/FlightJar.Core.Tests/
dotnet test FlightJar.slnx
cd ..

# Docker build exercises the fetch + bake path.
docker compose build flightjar
```

The startup log line (`loaded your DB from … (<count> entries)`)
is the best quick sanity check — spin the container and grep
the log.

## Do not

- Do not rebuild the dictionary on every lookup. Frozen
  dictionary + single atomic swap is the pattern; anything else
  introduces either races or startup-time cost on the hot path.
- Do not iterate the full table for keyed lookups. If you find
  yourself writing `_entries.Values.FirstOrDefault(…)` for a
  keyed query, you want a second `FrozenDictionary` instead.
  `AirlinesDb` keeps only one dict and searches its values when
  needed — that's acceptable for ~6k rows; scale up and it'll
  hurt.
- Do not accept the file size uncritically. A 200 MB CSV at
  startup blows the container's working set and delays the
  first snapshot tick by seconds. If the upstream has a pruned
  / filtered version, prefer it (OurAirports has `type`
  categories; AircraftDb has an ICAO24-hex-only column layout).
- Do not call blocking `File.ReadAllText` inside the hot path.
  Startup loads are async — keep them so the Generic Host can
  parallelise across loaders via `Task.WhenAll`.
- Do not introduce a new parser when `CsvReader` will do. It
  already handles `""`-escaped quoted fields, `,` / `;`
  delimiters, and async streaming. If your file needs
  something `CsvReader` doesn't support, ask before
  handwriting a new parser — the usual answer is "pre-process
  the upstream in the Dockerfile".
- Do not fetch at runtime from a public upstream just to avoid
  a Docker build step. Reference data bakes into the image;
  runtime fetching belongs behind an `external-client-add`
  typed HTTP client with caching + throttling. Mixing the two
  patterns produces a loader with no TTL that silently serves
  stale data forever.
