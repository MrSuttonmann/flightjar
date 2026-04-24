---
name: map-layer-add
description: Add a new toggleable overlay to the Leaflet map (layers control checkbox on the top-right). Handles the proxy-overlay pattern (dispatching to a setter that manages data loading + rendering), wires persistence of the checkbox state, and plugs into the existing layers-control registry. Use when the user asks to add a map overlay — weather radar, flight paths, wind barbs, surface winds, METAR stations, MLAT coverage, custom GeoJSON. For changes to base tiles (OpenStreetMap / Satellite / Topographic) see the `baseLayers` block in map_setup.js directly.
---

# Adding a map overlay

Leaflet overlays are driven by a small registry in `map_setup.js` —
adding a new toggle is one entry in `PROXY_OVERLAYS` plus a setter
function that manages the actual rendering. This skill documents the
pattern so new overlays integrate cleanly with the layers control,
the keyboard shortcuts, and the "which overlays are on" persistence
without copy-pasting boilerplate into unrelated files.

## Which pattern to use

Two overlay styles are supported:

| Style | When | Example |
|---|---|---|
| **Proxy overlay** | Overlay is rendered by a dedicated JS module that fetches + draws data (`blackspots.js`, `openaip.js`) and owns its own L.layerGroup. The layers-control checkbox dispatches to a `setX(on)` handler. | Airports, Navaids, Blackspots, Coverage, Airspaces |
| **Tile overlay** | Overlay is just a `L.tileLayer` pointed at a raster source — Leaflet's native add/remove handles the lifecycle. | IFR Low (US), IFR High (US) |

Pick **proxy** for anything data-driven (GeoJSON, canvas markers, line
work). Pick **tile** for raster map tiles from an external service.

## Touchpoints (proxy overlay)

1. **`app/static/<name>_layer.js`** (new file) — the module that
   owns the overlay: layer group, data fetch, canvas rendering,
   `setFoo(on)` enable/disable function.
2. **`app/static/map_setup.js`** — create the proxy layerGroup, add
   one row to `PROXY_OVERLAYS`.
3. **`app/static/app.js`** — import `setFoo` and pass it into
   `initMap({ …, setFoo })`.
4. **`app/static/shortcuts.js`** (optional) — bind a keyboard
   shortcut (`F` = follow, `L` = labels, `T` = trails, etc.) if the
   overlay earns one. Skip for niche overlays.
5. **Backend** — if the overlay needs server data, add an endpoint
   like `/api/<name>?bbox=…` and a record type in `Program.cs` +
   the matching `FlightJar.Clients/…` client if the source is
   external.
6. **Tests** — `tests/e2e/smoke.spec.js` has a template test for
   overlay wiring (`Terrain blackspots overlay is wired into the
   layers control`). Duplicate it for the new overlay.
7. **README** — add to the "Map layers" bullet under "Using the map".

## Step 1 — Write the layer module

Create `app/static/<name>_layer.js`. The module owns one
`L.layerGroup` that sits in the registry, a fetch function that pulls
data from the backend, and a `setFoo(on)` entry point:

```js
import { state } from './state.js';

const layer = L.layerGroup();
let loaded = false;
let inflight = null;

async function load() {
  if (inflight) return inflight;
  inflight = (async () => {
    const r = await fetch('/api/foo');
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    const data = await r.json();
    layer.clearLayers();
    for (const item of data.items || []) {
      L.circleMarker([item.lat, item.lon], { radius: 4, color: '#4da6ff' })
        .bindTooltip(item.label).addTo(layer);
    }
    loaded = true;
  })();
  try { await inflight; } finally { inflight = null; }
}

export function setFoo(on) {
  if (on) {
    state.map.addLayer(layer);
    if (!loaded) load().catch((e) => console.warn('foo layer load', e));
  } else {
    state.map.removeLayer(layer);
  }
}

// Optional: refresh on bbox change (see airports.js for the pattern).
// Optional: refresh when a relevant snapshot field changes — subscribe
// via state.subscribe(...) rather than polling.
```

Guidelines:
- Use `state.map` (the shared Leaflet instance) rather than re-importing.
- For dense point sets (> 500), use the shared
  `state.airportsCanvas` renderer passed via `L.canvas({ padding })`
  — thousands of markers need canvas, not SVG.
- Debounce `moveend` fetches (bbox-driven overlays) to 250-300 ms.
- Persist "is the overlay on" state via `localStorage` keyed by
  `flightjar.<name>_on` so checkbox state survives reloads. The
  layers-control wiring in `map_setup.js` does this automatically for
  tile overlays via `storageKey`; proxy overlays need to save it
  inside `setFoo`.

## Step 2 — Register in `map_setup.js`

Add the proxy + row inside `initMap`:

```js
const fooProxy = L.layerGroup();
// ... near the other proxies

const PROXY_OVERLAYS = [
  // ...existing entries
  { label: 'Foo radar', proxy: fooProxy, handler: 'setFoo' },
];
```

The proxy isn't drawn — it exists as a Leaflet-visible handle so the
layers-control checkbox has something to add/remove. The real
add/remove runs inside `setFoo` via the dispatcher at the bottom of
`initMap`.

If the overlay should default to on for new users, add it to the
`initiallyOn` set near the bottom of `initMap`.

## Step 3 — Wire the handler in `app.js`

Import the setter and pass it to `initMap`:

```js
import { setFoo } from './foo_layer.js';

initMap({
  config,
  // ...existing handlers
  setFoo,
});
```

`initMap` spreads everything after `config` into `overlayHandlers`,
so the name you use here must match the `handler:` string in
PROXY_OVERLAYS.

## Step 4 — Backend endpoint (if needed)

If the overlay reads server data, add a minimal-API endpoint in
`Program.cs`. Keep it bbox-aware so the frontend can re-fetch on
pan/zoom without transferring the whole dataset each time:

```csharp
app.MapGet("/api/foo", (double? minLat, double? maxLat, double? minLon, double? maxLon) =>
{
    if (minLat is null || maxLat is null || minLon is null || maxLon is null
        || minLat < -90 || maxLat > 90 || minLon < -180 || maxLon > 180)
    {
        return Results.BadRequest();
    }
    return Results.Ok(new FooPayload(Items: /* … */));
});
```

For external data sources (not bundled), add a client via the
`external-client-add` skill before wiring this endpoint.

## Step 5 — Tests

Smoke test: assert the overlay appears in the layers-control DOM
with the right label. Good template in `tests/e2e/smoke.spec.js`:

```js
test('Foo overlay is wired into the layers control', async ({ page }) => {
  await page.hover('.leaflet-control-layers');  // expand the control
  await expect(
    page.locator('.leaflet-control-layers-overlays label', { hasText: 'Foo radar' })
  ).toBeVisible();
});
```

If the backend endpoint is load-bearing, add an API test in
`FlightJar.Api.Tests` asserting 200 with a valid bbox + 400 on bad
input.

## Step 6 — README

Extend the "Map layers" bullet in the "Using the map" section of
`README.md` with a short description of what the overlay shows and
how it behaves (zoom-gate? requires an env var? expensive to toggle?).

## Do not

- Do not add SVG markers for dense point sets (> 500 points). Leaflet
  renders each as a separate DOM node; scroll perf dies. Use a
  canvas renderer — the shared `airportsCanvas` works for most cases.
- Do not fetch data on every pan. Debounce `moveend` (~300 ms) and
  use bbox-bounded endpoints.
- Do not block map interaction during a fetch. `setFoo(true)` should
  add the empty layer group immediately and async-load the content
  in the background; the user sees markers appear rather than a
  janky pause.
- Do not persist overlay state via a global variable. Use
  `localStorage.setItem('flightjar.<name>_on', '1'|'0')` so a reload
  preserves what the user was looking at.
- Do not forget the zoom-gate check. If your overlay is dense at
  zoom < 5 (continental views), hide markers below a minimum zoom or
  serve a decimated response from the backend. See how airspaces
  (z5+), reporting points (z7+), and obstacles (z9+) do it in
  `openaip.js`.
- Do not add a toggleable overlay for something that's always on (a
  base tile layer, a single receiver dot). Those live outside the
  layers control.
