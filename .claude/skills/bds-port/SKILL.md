---
name: bds-port
description: Port a Mode S Comm-B BDS register decoder from pyModeS into FlightJar.Decoder.ModeS.CommB, wire it into the aircraft registry + snapshot + detail panel, and verify against pyModeS golden vectors. Use when the user asks to add a new BDS register (e.g. "add BDS 4,5 hazard data", "decode the hazard / windshear register", "show pilot-entered MET data"). The four heuristic registers 4,0 / 4,4 / 5,0 / 6,0 are already implemented; remaining candidates are BDS 4,5 (meteorological hazard — opt-in, noisy), and the format-ID registers BDS 1,0 / 1,7 / 2,0 / 3,0 (data link capability, GICB capability report, aircraft identification, ACAS active resolution).
---

# Porting a pyModeS BDS register to FlightJar

FlightJar's Comm-B decoder matches pyModeS 3.x byte-for-byte. When adding
another register, keep the wire behaviour identical so we can cross-check
golden vectors from pyModeS's own test corpus. Every deviation (even a
"cleanup") is a source of silent drift later.

## Touchpoints

A full port spans six files and one frontend + one docs update:

1. **`dotnet/src/FlightJar.Decoder/ModeS/CommB.cs`** — add
   `IsBdsXX(payload)` validator + `DecodeBdsXX(payload)` decoder +
   `BdsXXData` record. Extend the `Candidates` record and `Infer()` to
   include the new register.
2. **`dotnet/src/FlightJar.Decoder/ModeS/DecodedMessage.cs`** — add
   one field per value the register exposes.
3. **`dotnet/src/FlightJar.Decoder/ModeS/MessageDecoder.cs`** — add a
   branch in `InferCommB` that builds a `DecodedMessage` for the new
   register; extend `Merge()` to copy the new fields.
4. **`dotnet/src/FlightJar.Core/State/Aircraft.cs`** — add per-field
   state + one `BdsXXAt` timestamp.
5. **`dotnet/src/FlightJar.Core/State/AircraftRegistry.cs`** — add a
   `case "X,Y":` branch in `ApplyCommB` that writes the fields +
   stamps the timestamp. Extend `BuildCommBSnapshot` to gate the
   fields on freshness and feed the `SnapshotCommB` record.
6. **`dotnet/src/FlightJar.Core/State/RegistrySnapshot.cs`** — add
   the fields to `SnapshotCommB` (nullable, snake-case on the wire).
7. **`app/static/detail_panel.js`** — add metric tiles to the
   `.panel-met-grid` placeholder markup in `buildPopupContent` and
   a `set('.pop-met-xxx', …)` call per field in `renderCommBSection`.
8. **`dotnet/tests/FlightJar.Decoder.Tests/ModeS/CommBTests.cs`** —
   validator accept/reject tests + golden-vector decoder tests using
   hex captures from pyModeS's `tests/test_bds_commb.py`.
9. **`README.md`** — add the new fields to the "Enhanced Mode S air
   data" bullet and flag any register-specific caveats.
10. **`CLAUDE.md`** — update the decoder list + any behavioural
    nuances.

## Step 1 — Fetch the pyModeS reference

pyModeS lives at `junzis/pyModeS` on GitHub (default branch `main`).
BDS decoders live under `src/pyModeS/decoder/bds/bdsXX.py`, helpers
under `_helpers.py`. Fetch into `/tmp/pymodes/` via the GitHub API
(the raw CDN sometimes 404s; the contents API is reliable):

```bash
# List available registers.
curl -sL "https://api.github.com/repos/junzis/pyModeS/contents/src/pyModeS/decoder/bds?ref=main" \
  | grep '"download_url"'

# Fetch a specific register + the shared helpers.
for f in bds45 _helpers _infer; do
  curl -sL "https://raw.githubusercontent.com/junzis/pyModeS/main/src/pyModeS/decoder/bds/$f.py" \
    > /tmp/pymodes/$f.py
done

# And the golden-vector test corpus.
curl -sL "https://raw.githubusercontent.com/junzis/pyModeS/main/tests/test_bds_commb.py" \
  > /tmp/pymodes/test_bds_commb.py
```

## Step 2 — Port the validator + decoder

pyModeS operates on a 56-bit payload as a Python int with `(payload >> (55 - i)) & mask`
indexing (MSB-first from payload bit 0). The C# port keeps **identical**
bit indexing so the ported arithmetic reads one-to-one next to the Python.
Add your new methods inside `public static class CommB` in `CommB.cs`.

Use the existing helpers that are already in `CommB.cs`:

- `WrongStatus(payload, statusBit, valueStart, valueWidth)` — mirrors
  pyModeS `_helpers.wrong_status` (status-bit / value-field consistency).
- `Signed(value, width, sign)` — sign-magnitude to signed int (NOT
  two's complement; Mode S splits sign + magnitude bits).
- `NormaliseAngle(deg)` — wrap into `[0, 360)`.

Every range gate in `IsBdsXX` must match pyModeS's validator. If pyModeS
rejects `> 600 kt` but you port it as `>= 600`, you will silently accept
values pyModeS would reject.

## Step 3 — Wire into inference

`CommB.Infer(payload)` returns a `Candidates` record with one bool per
heuristic register. `MessageDecoder.InferCommB(msg)` returns a decoded
message only when **exactly one** candidate validates. This single-match
discipline is intentional: multi-match payloads are ambiguous and
dropped rather than risk polluting aircraft state with fields decoded
against the wrong register. Do not relax it without a replacement
disambiguation strategy (e.g. pyModeS Phase 3 known-state scoring).

BDS 4,5 (meteorological hazard) is opt-in in pyModeS because it
false-positives on non-meteorological payloads. When porting, keep
the validator strict; if ambiguity becomes a problem, add a
`Candidates.Bds45` branch behind a config flag rather than unconditionally
accepting it.

## Step 4 — Extend state + snapshot

For every decoded field:

1. Add a nullable property to `Aircraft` (e.g. `public int? WindshearLevel { get; set; }`).
2. Add an identically-named property to `SnapshotCommB`.
3. In `ApplyCommB`, add a `case "X,Y":` that assigns from the
   `DecodedMessage`; **do not** touch fields from other registers
   (each register owns its own slice of state).
4. In `BuildCommBSnapshot`, compute a `bdsXXFresh` flag using
   `CommBMaxAge` (120 s) and use it to gate every field from the
   register. Include the register's `BdsXXAt` timestamp on the
   snapshot so the frontend can age values out independently.

Naming convention on the wire: snake_case via the global serializer
config in `FlightJar.Api.Configuration`. `MagneticHeadingDeg` becomes
`magnetic_heading_deg` without any extra attributes.

## Step 5 — Extend the frontend panel

The Enhanced Mode S panel is driven entirely by `a.comm_b` in the
snapshot. In `detail_panel.js`:

1. Add placeholder tile markup inside `.panel-met-grid` in
   `buildPopupContent` — same shape as existing tiles
   (`<div class="metric pop-met-xxx" hidden><div class="label">…</div><div class="val"></div></div>`).
2. Add a `set('.pop-met-xxx', value)` call in `renderCommBSection`
   that computes the formatted string or returns `null` to hide the
   tile.

Use the existing `uconv('alt' | 'spd' | 'vrt' | 'dst', value)` helper
for unit-system-aware formatting. Raw-unit values (Mach, temperature,
degrees, percent) format inline.

## Step 6 — Tests

`CommBTests.cs` follows two patterns; use both for the new register:

1. **Validator acceptance + rejection**: pick a golden-vector hex
   from pyModeS's `test_bds_commb.py`, assert `IsBdsXX` accepts it,
   then construct synthetic payloads that should be rejected (all
   zeros, out-of-range values, status-bit / value-bit inconsistency)
   and assert they're rejected.
2. **Decode golden vector**: decode the pyModeS golden hex and
   assert each field matches the pyModeS oracle value to within the
   same `abs=` tolerance the pyModeS test uses.

Also add an end-to-end test in `MessageDecoder_RoutesUnambiguousBdsXXOnDf20`
to prove the `InferCommB` branch wires through.

## Step 7 — Add an integration test

`dotnet/tests/FlightJar.Core.Tests/State/AircraftRegistryTests.cs` +
`FakeDecoder.cs` use fake hex keys (`BD44` → pre-built `DecodedMessage`)
to exercise `ApplyCommB` + `BuildCommBSnapshot` without real wire
bytes. Add a `BDxx` fixture and a test proving the new field lands
in the snapshot and ages out correctly past `CommBMaxAge`.

## Step 8 — Playwright smoke (optional)

The `detail panel renders Enhanced Mode S section when comm_b is present`
test in `tests/e2e/layout.spec.js` injects a fake snapshot with a full
`comm_b` block. If the new register adds a user-visible tile, extend the
fixture and assert the new label appears.

## Step 9 — Verify

```bash
cd dotnet && dotnet format FlightJar.slnx --verify-no-changes
dotnet build FlightJar.slnx
dotnet test FlightJar.slnx
cd ..
node --test tests/js/
npx playwright test
```

All five must pass before the port is done. Formatter drift and
Playwright regressions are the two most common surprises.

## Do not

- Do not rename pyModeS's field names in the decoded record unless
  you have a codebase-wide reason. `static_air_temperature` in pyModeS
  is `StaticAirTemperatureC` in `CommB.cs` — the `C` suffix is the
  only concession, and it's there because our wire convention suffixes
  every physical-unit field.
- Do not persist Comm-B state in `state.json.gz`. The 120 s freshness
  window is far shorter than the 30 s persist cadence + 10 min
  `PersistMaxAge`, so restored Comm-B values would be stale on load.
- Do not bypass `CommB.Infer`'s single-match gate by eagerly decoding
  every register. Multi-match payloads mean one of the decodes is
  wrong; you cannot tell which without an external reference signal,
  so dropping ambiguous payloads is the safe default.
- Do not add register-specific env vars for opt-in registers without
  updating `README.md`'s configuration reference table.
