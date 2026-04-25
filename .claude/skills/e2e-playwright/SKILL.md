---
name: e2e-playwright
description: Add or debug Playwright end-to-end tests under tests/e2e/. Covers the FlightJar harness setup (BEAST pointed at a dead host, deterministic env-var pins, reuseExistingServer caveats), the per-test localStorage reset pattern, how to inspect DOM state via page.evaluate(), how to find a layer/dialog row reliably, and the Leaflet/CDN quirks (most notably L.Control.Layers._checkDisabledLayers clobbering input.disabled). Use when writing a new tests/e2e/*.spec.js, when an existing Playwright test is failing intermittently or in unexpected ways, or when you need to assert on something rendered by a third-party library (Leaflet, Recharts, etc.).
---

# Playwright e2e tests in this repo

Smoke + layout + feature suites live under `tests/e2e/`. Playwright boots
the .NET backend itself via the `webServer` block in `playwright.config.js`
— there's no live BEAST feed, no live aircraft, no internet for some
upstream lookups. The harness is *deliberately stripped* so the tests
run anywhere and exercise paths that don't depend on real-time data.

## Run a single spec

```bash
npx playwright test disabled_layers --project=desktop --reporter=line
```

- `--project=desktop` skips the mobile viewport (run mobile separately
  to catch layout regressions: `--project=mobile`).
- `--reporter=line` is much terser than the default `list` reporter for
  large runs; `--reporter=list` is best for one or two failing tests.
- Filename matching is substring-based — `disabled_layers` matches
  `tests/e2e/disabled_layers.spec.js`.

To run *one* test inside a spec, append `--grep "<title fragment>"`.

## What the harness pins

`playwright.config.js`'s `webServer.env` is the source of truth — read
it before assuming anything about the backend's state. Notable pins:

- **`BEAST_HOST: 'nonexistent.invalid'`** — connection sits in
  "Disconnected, retrying…" forever. Anything that needs aircraft data
  has to be injected client-side (see `update_loop.js.update()` from
  the screenshots skill, or mock `/api/aircraft` per spec).
- **`LAT_REF / LON_REF: '51.5' / '-0.1'`** — receiver coords are set,
  so range rings, polar coverage, and anything that gates on
  `LAT_REF != null` are *enabled*.
- **`BLACKSPOTS_ENABLED: '0'`** — terrain layer is off (otherwise it'd
  hammer AWS for SRTM tiles every test run).
- **`FLIGHT_ROUTES: '0'`** — adsbdb client disabled.
- **`OPENAIP_API_KEY` unset** — OpenAIP layers are reliably gated off.
- **`VFRMAP_CHART_DATE` unset** — *but* `VfrmapCycleRefresher` runs at
  startup and tries to discover the cycle from vfrmap.com. With
  internet the gate ends up *open*; offline it's closed. **Don't write
  a test that asserts vfrmap is disabled** — it'll flake.

### Implication

Any gate whose env var is *unset* in the harness AND whose value isn't
auto-discovered from the network is deterministically closed (e.g.
`OPENAIP_API_KEY`). Gates that *are* auto-discovered (`vfrmap_chart_date`
via `VfrmapCycleRefresher`) are environment-dependent — assert on them
conditionally or skip them.

## reuseExistingServer gotcha

`reuseExistingServer: !process.env.CI` means locally Playwright will
attach to an *already-running* backend on port 8765. If you start the
backend manually (`dotnet run`) then change C# code, **Playwright will
keep using the old binary**. Symptoms: backend tests pass, e2e tests
fail with stale endpoint shape.

Fix: either kill the running backend (`lsof -i:8765`) or restart it
before running the spec. Frontend (JS/CSS) changes are picked up
because static files are served fresh every request (the dev server
sets `Cache-Control: no-cache` with ETag revalidation).

## Per-test scaffolding

Every spec starts with this `beforeEach`:

```js
test.beforeEach(async ({ page }) => {
  await page.goto('/');
  await page.evaluate(() => localStorage.clear());
  await page.reload();
});
```

The first `goto` is required before `localStorage.clear()` runs (the
origin needs to exist for storage access). The reload after the clear
ensures the boot path runs with default state. Don't skip the
beforeEach — units, compact mode, persisted overlay toggles, watchlist
state, and several other features live in localStorage and would leak
between tests otherwise.

## Leaflet specifics

The map UI is the trickiest surface to test. A few patterns:

### Hover the layers control before reading rows

Leaflet's layers control is collapsed by default. Hover (or tap on
mobile) expands it:

```js
await page.locator('.leaflet-control-layers').hover();
await expect(page.locator('.leaflet-control-layers-overlays label'))
  .toContainText(['Terrain blackspots']);
```

### Match a row by display label

Leaflet renders each row as `<label><div><input/><span> Layer name</span></div></label>`.
The space before "Layer name" is intentional. To target a specific row:

```js
const row = page.locator('.leaflet-control-layers label', {
  hasText: 'Aeronautical (OpenAIP)',
}).first();
```

`.first()` is needed because `hasText` does substring matching and
several rows could match a partial name. The leading-space-and-suffix
treatment in `findOverlayLabel` (openaip.js) is for the JS-side
mutator — Playwright's `hasText` handles the trim itself.

### `input.disabled` is unstable on layer-control rows

`L.Control.Layers._checkDisabledLayers` (leaflet-src.js line 5430)
unconditionally rewrites every row's `input.disabled` based on the
layer's `minZoom`/`maxZoom` vs the current map zoom. It runs on
`zoomend`, after every `_update`, and after every `_addItem` — which
means **every** layer add anywhere on the map (aircraft markers, trails,
the receiver) clobbers your custom disabled state.

If you're setting `input.disabled = true` manually (e.g. for the
disabled-map-layers feature), you have to either:

1. Hook the prototype: monkey-patch `_checkDisabledLayers` to call your
   reapply right after Leaflet's own logic. See
   `app/static/map_layer_status.js#patchLeafletDisabledCheck`.
2. Set `minZoom: Infinity` on a *placeholder* layer so Leaflet's own
   logic permanently treats it as out-of-zoom and disables it for you.
   Less invasive but only works if you control the layer's options.

A plain `state.map.on('overlayadd overlayremove', reapply)` listener is
**not enough** — the layeradd / zoomend / aircraft-marker paths bypass
those events.

## Inspect DOM state with `page.evaluate`

When a Locator assertion fails in a way that doesn't make sense, stop
guessing and dump the actual state from inside the page:

```js
const data = await page.evaluate(() => {
  return [...document.querySelectorAll('.some-target')].map(el => ({
    text: el.textContent.replace(/\s+/g, ' ').trim().slice(0, 40),
    classes: el.className,
    inputDisabled: el.querySelector('input')?.disabled,
    inputAttr: el.querySelector('input')?.getAttribute('disabled'),
  }));
});
console.log('PROBE:', JSON.stringify(data, null, 2));
```

Throwaway `tests/e2e/_probe.spec.js`-style files are fine for one-off
debugging — leading underscore keeps them out of accidental
`git add -A` on suite cleanup but Playwright still picks them up. **Delete
the probe before committing.**

## Adding a new spec

1. Drop a new `tests/e2e/<feature>.spec.js`. The default project
   matchers (desktop + mobile) cover it automatically.
2. If the feature requires data the BEAST dead host can't supply, mock
   the relevant `/api/...` endpoint via `page.route()` *before* the
   first `page.goto('/')`. See `scripts/take_screenshots.js` for the
   "inject a fake fleet" pattern with `update_loop.js.update()`.
3. If the feature needs a particular env-var state on the backend,
   add the env to `playwright.config.js`'s `webServer.env` block —
   *don't* set process env from a spec, the backend boots once for the
   whole run.
4. Run `--project=desktop` first; once it passes, run `--project=mobile`
   to catch layout regressions.
5. Commit `tests/e2e/<feature>.spec.js` along with the production
   change in the same PR.

## Common failure modes (symptom → diagnosis)

- **Locator times out, assertion never fires.** Either the layers
  control wasn't expanded (missing hover), or the row text mutates
  (e.g. zoom-gate suffix " (zoom ≥ 5)" was applied) and your `hasText`
  no longer matches. Inspect with `page.evaluate`.
- **Test passes in isolation, fails in the full suite.** Almost always
  cross-test state leak: a previous test left a popover/dialog open,
  a localStorage key set, or a layer toggled on. Add the missing
  cleanup; don't add a `waitForTimeout`.
- **Backend endpoint returns the *old* shape.** `reuseExistingServer`
  attached to a stale backend. Kill it.
- **VFRMap-related assertion flakes between machines.** Auto-discovery
  succeeded on one, failed on the other. Don't assert on vfrmap's
  enabled state; assert on the shape only.
- **`toBeDisabled()` fails despite `input.disabled = true` being set.**
  Leaflet's `_checkDisabledLayers` overran your assignment. See the
  monkey-patch pattern above.
