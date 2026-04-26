---
name: posthog-telemetry
description: Add or change anonymous PostHog telemetry — extend the per-tick `instance_ping` payload, add a new property to the `$identify` `$set`, fire a backend event from a hot path, or wire a frontend `track()` call. Covers the privacy posture (no exact coords / IPs / tokens / per-aircraft IDs, $geoip_disable, coarse 10° region, bucketed receiver-fingerprint values), the file map, the build-time `PosthogApiKey` MSBuild prop, the `TELEMETRY_ENABLED=0` opt-out, and the tests + README to update. Use when the user asks "add X to telemetry", "send Y to PostHog", "track when the user does Z", "is N safe to log", or anything touching `app/static/telemetry.js` / `FlightJar.Api/Telemetry/`. Don't use for the `/metrics` Prometheus endpoint or `/api/stats` — those are operator-facing and unrelated.
---

# PostHog telemetry

FlightJar emits two streams to PostHog:

- **Backend** — one `$identify` on cold start + one `instance_ping`
  every 24h (`TelemetryConfig.PingInterval`). Owned by
  `TelemetryWorker` in the API project.
- **Frontend** — one `$pageview` on map load + ad-hoc `track(event, props)`
  calls from user-driven UI (panel opens, watchlist mutations,
  channel creation, map-layer toggles, blackspots slider commit).
  Owned by `app/static/telemetry.js`.

Both share the same per-install distinct ID (random GUID stored in
`/data/telemetry.json`, served to the frontend by
`/api/telemetry_config`) so events from both streams roll up against
the same PostHog Person.

## Privacy posture (read first)

These rules are non-negotiable. The whole point of opting users into
telemetry is that the maintainer learns about *aggregates* and the
user reveals *nothing identifying*.

- **Never send**: exact `LAT_REF` / `LON_REF`, `SITE_NAME`,
  `BEAST_HOST` (could be a public IP), the `Password`, any
  notification tokens (Telegram bot/chat, ntfy auth, webhook URLs),
  any aircraft ICAO24 / callsign / registration / squawk, watchlist
  contents.
- **Always set on every event** (the `Add*` helpers in
  `TelemetryPayloadBuilder` already do this — don't forget it for
  ad-hoc events):
  - `$geoip_disable: true` and `$ip: ""` — without this, PostHog
    rewrites the Person's location every time the source IP geo-
    resolves to a different city.
  - On the frontend the same is registered globally via
    `posthog.register({ $geoip_disable: true, $ip: "" })`.
- **Coarsen anything that could fingerprint a single install**:
  - Receiver location → `region_lat_10` / `region_lon_10` (rounded
    to nearest 10°).
  - Working-set RSS → bucketed to 50 MB steps.
  - Watchlist size → `"0" | "1-10" | "11-50" | "51-200" | "200+"`.
- **Send precise where the value alone is non-identifying**:
  - Antenna height (MSL or AGL) is exact integer metres — there are
    too many installs at any given altitude inside a 10° box for the
    value to identify anyone, and the maintainer needs the resolution
    to see whether mounting profiles cluster.

Before adding a new property, ask: *if I joined this property with
`region_lat_10`, the install ID, and the version, could it pin a
single user?* If yes, coarsen / bucket / drop.

## File map

| File | Purpose |
|---|---|
| `dotnet/src/FlightJar.Api/Telemetry/TelemetryConfig.cs` | Build-time host + API key. Reads `[AssemblyMetadata("PosthogApiKey")]` attributes injected by CI. |
| `…/InstanceIdStore.cs` | Per-install GUID + first-seen timestamp, persisted to `/data/telemetry.json`. Rotatable via `ResetAsync` (called from the `/api/telemetry/reset` endpoint). |
| `…/PosthogClient.cs` | Minimal HTTP client for `/capture/`. `CaptureAsync` (events) and `IdentifyAsync` (Person attributes). Drops on failure — telemetry must never break the host. |
| `…/TelemetryAccumulator.cs` | Always-on bag for per-tick samples (aircraft / Comm-B / tick duration) + reconnect counter. `RegistryWorker` and `BeastConsumerService` push into it; `TelemetryWorker` drains on each ping. |
| `…/TelemetryPayloadBuilder.cs` | Pure functions — `Build` (per-event) and `BuildIdentify` (Person `$set` + `$set_once`). Shared `AddInstallShape` + `AddRegionAndAntenna` helpers. |
| `…/TelemetryWorker.cs` | Background service that ticks the worker loop. Pulls inputs from registry / accumulator / coverage / heatmap / watchlist / notifications and hands them to the builder. |
| `app/static/telemetry.js` | Frontend bootstrap + `track(event, props)`. Reads `/api/telemetry_config`, loads the PostHog snippet, registers global `$geoip_disable`, exposes `track` as a safe no-op when telemetry is off. |
| `dotnet/src/FlightJar.Api/Program.cs` | DI wiring + the `/api/telemetry_config` and `/api/telemetry/reset` endpoints. |

## How to add a per-tick property to `instance_ping`

Add to the dynamic block inside `TelemetryPayloadBuilder.Build`. If
the value comes from a moving piece of state (a counter, an
aggregate), thread it through `TelemetryInputs` rather than reading
`AppOptions` directly.

1. Add a field to `TelemetryInputs` (record init).
2. Compute the value in `TelemetryWorker.BuildInputs` and assign it.
   For per-tick samples, push from `RegistryWorker.DoTick` into
   `TelemetryAccumulator`, then read the drained snapshot here.
3. In `TelemetryPayloadBuilder.Build`, write `props["my_field"] = inputs.MyField;`
   in the per-event block (after `AddInstallShape` / `AddRegionAndAntenna`
   so install-shape fields stay grouped).
4. Bucket / round if the raw value would fingerprint an install
   (see the privacy section).
5. Add a test: `TelemetryPayloadBuilderTests` in
   `dotnet/tests/FlightJar.Api.Tests/Telemetry/TelemetryTests.cs`.
   Use the existing `MakeInputs(...)` helper.
6. Update the README's "What's included (backend heartbeat)" bullets.

## How to add a stable per-install attribute (`$identify` `$set`)

Static attributes (feature toggles, deployment shape, tuning
knobs) belong in **both** `BuildIdentify` AND `Build` so they
land on the Person profile *and* every event. Don't put drifty
counters or aggregates in `$set`.

1. Add the property in `AddInstallShape` (or `AddRegionAndAntenna`
   for receiver-location-derived fields). That helper is called
   from both `Build` and `BuildIdentify` — one edit covers both
   paths.
2. Test via `Identify_SplitsStableAndOnceAttributes` (assert
   `set.ContainsKey("my_field")` and that drifty fields like
   `uptime_s` stay out of `$set`).

## How to add a backend event (`CaptureAsync`)

Use this for one-shot events that aren't periodic, e.g. a state
transition you want a count for. Examples that would warrant one:
notification dispatch failure, blackspots first-compute timing,
auth lockout. Examples that would NOT — anything that fires per
aircraft / per tick (use the per-tick aggregate instead).

```csharp
_ = await _posthog.CaptureAsync(
    host: TelemetryConfig.Host,
    apiKey: TelemetryConfig.ApiKey,
    @event: "blackspots_compute_done",
    distinctId: _instanceStore.InstanceId,
    properties: new Dictionary<string, object?>
    {
        ["$lib"] = "flightjar",
        ["$geoip_disable"] = true,
        ["$ip"] = "",
        ["altitude_m"] = altM,
        ["compute_ms"] = ms,
    },
    timestamp: _time.GetUtcNow());
```

The `$geoip_disable` + `$ip = ""` pair is critical for ad-hoc
events too — `TelemetryPayloadBuilder` adds them automatically
but a freshly-written `CaptureAsync` call doesn't.

## How to add a frontend `track()` event

`track(event, props)` lives in `app/static/telemetry.js` and is a
safe no-op when telemetry isn't loaded, so the call site doesn't
need to guard. Import it from any module:

```js
import { track } from './telemetry.js';
// ...
track('panel_open');                              // bare event
track('notification_channel_added', { type });    // typed prop
```

Hot-path discipline:
- Don't fire from per-snapshot redraw loops — gate on a real state
  change. `openDetailPanel` only fires `panel_open` when
  `state.selectedIcao !== icao`.
- For sliders / continuous controls, listen on `change` (commit)
  not `input` (per-pixel drag). See `app/static/blackspots.js` for
  the pattern.
- For map-layer toggling, hook the *user-action* events
  (`overlayadd` / `overlayremove` filtered by `syncingOverlays`),
  not the `setX` setters — the setters also run during
  localStorage restoration on page load.

Property values are bare strings / numbers / booleans (no PII).
Never include `icao`, callsigns, watchlist contents, or anything
the user typed in.

## Build-time API key

`TelemetryConfig.ApiKey` is read from an `[AssemblyMetadata]`
attribute injected at publish time:

- CI sets the repo variable `vars.POSTHOG_API_KEY` (a `phc_*`
  project key — designed to be public-facing in client SDKs).
- The `dotnet/Dockerfile`'s `POSTHOG_API_KEY` build arg is
  forwarded to `dotnet publish` via the MSBuild property
  `PosthogApiKey`, which the csproj converts into the assembly
  metadata.
- Local dev builds leave the value empty, so the worker no-ops
  (it logs `"telemetry: no destination baked in, skipping"` at
  Debug). To exercise telemetry locally, build with
  `dotnet publish -p:PosthogApiKey=phc_dev_key`.

## Opt-out

`TELEMETRY_ENABLED=0` disables both the backend ping AND the
frontend pageview/track stream. The flag is read from
`AppOptions.TelemetryEnabled` (backend) and surfaced via
`/api/telemetry_config` to the frontend, which checks
`config.enabled` before loading the PostHog snippet.

The accumulator keeps running regardless — its writes are local
and the drain is gated on the worker, so nothing leaves the
process when telemetry is off.

## Tests to update

- **`dotnet/tests/FlightJar.Api.Tests/Telemetry/TelemetryTests.cs`**:
  - Add a `TelemetryPayloadBuilderTests` `[Fact]` for any new
    payload field. Use the `MakeInputs` factory helper.
  - For `$set` vs per-event splits, extend
    `Identify_SplitsStableAndOnceAttributes`.
  - Watchlist / RSS / antenna boundary behaviours all use
    `[Theory]` + `[InlineData]` — match the style.
- **`dotnet/tests/FlightJar.Api.Tests/Telemetry/TelemetryTests.cs`**'s
  `TelemetryAccumulatorTests` if you add a new accumulator metric.
- **`tests/js/telemetry.test.js`** for any new export from
  `app/static/telemetry.js`. Frontend `track()` call sites
  themselves don't need tests — `track` is verified as a safe
  no-op in `'track is a safe no-op before init'`.

## README

The "Anonymous telemetry" section in the root README enumerates
*every* property currently sent. **It is end-user-facing** — they
read it to decide whether to leave telemetry on. Update the bullets
when you add a payload field. Keep the language plain and the
"What's never included" list explicit; that's the trust boundary.

## Diagnostics

- **No events arriving**: check `TELEMETRY_ENABLED` is unset / 1,
  `POSTHOG_API_KEY` was set at build time (`docker logs flightjar`
  will show `"telemetry: enabled, instance <guid>"` on success or
  `"telemetry: no destination baked in"` if the key wasn't baked).
  Frontend: open devtools → Network → look for a POST to
  `eu.i.posthog.com/capture/`.
- **Person profile location is wrong / drifts**: a code path is
  emitting an event without `$geoip_disable: true` and `$ip: ""`.
  Grep for `CaptureAsync(` and `posthog.capture(` outside the
  central helpers and add the pair.
- **Properties missing in PostHog UI**: PostHog rejects events
  silently when the project key is wrong. Check `/api/telemetry_config`
  echoes the expected `api_key` (note: the key is intentionally
  visible to the client — that's how the JS SDK works).
- **Distinct-ID mismatch (frontend events count separate from
  backend pings)**: the frontend bootstrap calls
  `posthog.init(..., bootstrap: { distinctID: config.distinct_id })`.
  If the IDs differ, `/api/telemetry_config` is racing the
  `InstanceIdStore.LoadOrCreateAsync` — confirm `await` ordering in
  `Program.cs`.
