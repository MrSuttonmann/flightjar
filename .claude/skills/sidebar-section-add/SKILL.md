---
name: sidebar-section-add
description: Extend the left-hand aircraft sidebar — add a new sort chip, a new list-filter chip, a new per-row metric, or a new header/status element. Covers the fingerprint-cache invariant that makes every per-row change subtle (miss a field and the row stops re-rendering when it changes), the lastX cached-text pattern for header elements, delegated event wiring, and the escapeHtml discipline for user-sourced strings. Use when the user asks for "sort by X", "add a MIL/watched/whatever filter chip", "show field Y in each row", or "add a status pill to the sidebar". Don't use for the aircraft detail panel (that's `detail-panel-section`), dialogs, or the map overlays.
---

# Extending the aircraft sidebar

The sidebar on the left is rebuilt from `renderSidebar(snap)` on
every 1 Hz snapshot tick, **but rows themselves are DOM-diffed by
fingerprint** — each row's HTML is cached keyed on an icao and a
`|`-joined string of every value that affects the rendered
markup. If the fingerprint matches the cached entry, the cached
`Element` is reused verbatim; otherwise the row re-renders once
and goes back in the cache. That design keeps scrolling through
a busy sidebar cheap and prevents focus / selection flicker,
and it is the single most important constraint for anyone
adding a new per-row field: **if your new value isn't in the
fingerprint array, the row won't update when it changes**.

Header / status bits (site name, status text, msg rate) take a
different path — each one has a `lastXxx` module-level cache and
the setter only touches the DOM when the value actually changed.

There are four common extension shapes:

| Shape | Touchpoints | Difficulty |
|---|---|---|
| New sort chip | `index.html` + `state.js` (sortValue + DEFAULT_SORT_DIR) | trivial |
| New list-filter chip | `index.html` + `state.js` (FILTER_KEYS) + `filters.js` (matchesActiveFilters) | trivial |
| New per-row field / metric | `sidebar.js` (HTML + fingerprint), plus `snapshot` source | load-bearing |
| New header / status element | `index.html` + `sidebar.js` (module-level `lastXxx` + mutator) | easy |

## Touchpoints by shape

### Sort chip

1. **`app/static/index.html`** — add `<div class="sort-chip" data-key="yourkey">Label<span class="arrow"></span></div>` inside `#sort-bar`.
2. **`app/static/state.js`** — add `yourkey: 1` (ascending default) or `-1` (descending default) to `DEFAULT_SORT_DIR`, and add a case to `sortValue(a, key, now)` returning the sortable value (string, number, or null).

That's it — `initSidebar` already wires every `.sort-chip` via a
delegated click handler.

### Filter chip

1. **`app/static/index.html`** — add `<button type="button" class="filter-chip" data-filter="yourkey" title="…">Label</button>` inside `#filter-bar`.
2. **`app/static/state.js`** — add `'yourkey'` to the `FILTER_KEYS` array.
3. **`app/static/filters.js`** — add a branch to `matchesActiveFilters(a)` returning `true` when the filter is active *and* the aircraft matches.

Semantics are OR across active filters (matching any chip shows
the aircraft). `FILTER_KEYS` is also the localStorage allow-list
— a filter key that isn't in `FILTER_KEYS` is silently dropped
on read, so forgetting step 2 makes the chip un-persistable.

### Per-row field / metric

Opening `sidebar.js` and hunting for the giant `const html =
\`<div class="${classes}" …>\`` template in `renderSidebar` is
the fastest way in. The existing metrics (`Alt`, `Spd`, `Hdg`,
`Dist`) live in a `.meta` grid at the bottom of each row.
Each metric looks like:

```html
<div class="metric">
  <div class="label">Alt</div>
  <div class="val ${tAlt.cls}">${uconv('alt', a.altitude)}${tAlt.arrow}</div>
</div>
```

Two things to get right:

1. **Escape user-sourced strings** — anything that came from a
   BEAST payload (`callsign`, `registration`, `type_icao`,
   `operator*`, `emergency`, …) goes through `escapeHtml()`
   before being concatenated into the HTML string. Numeric
   fields that go through `fmt()` / `uconv()` are safe — those
   helpers only emit digits and unit suffixes. See
   `app/static/format.js` for the `escapeHtml` helper and the
   Frontend section in CLAUDE.md for the rule.

2. **Update the fingerprint** — find the `const fp = [ … ].join('|')`
   block and append every field that affects your new
   markup. Miss one and the row will stop re-rendering when
   that field changes (the cached element gets reused because
   the fingerprint matches) — this is the most common way to
   accidentally break the sidebar. If you rely on a derived
   value like `trendInfo(entry, 'spd').cls`, include the derived
   class/arrow strings in the fingerprint, not the raw inputs.

Sketch of a new "Fuel %" metric that reads `a.fuel_percent`:

```js
// in sidebar.js, inside renderSidebar's per-row block:
const fp = [
  // ...existing entries...
  tAlt.cls, tAlt.arrow, tSpd.cls, tSpd.arrow, tDst.cls, tDst.arrow,
  route,
  a.fuel_percent,          // new — stringifies to '' for null so stable
].join('|');
// ...inside the `const html = ...` template, within the .meta div:
`<div class="metric"><div class="label">Fuel</div>` +
`<div class="val">${fmt(a.fuel_percent, '%')}</div></div>`
```

Label text must stay ≤ 4 chars — the metric tiles are narrow
and overflow wraps into two lines (`Alt`, `Spd`, `Hdg`, `Dist`
are the existing ceiling). Long labels belong in a tooltip on a
chip-style element, not the metric grid.

If the new field's data doesn't exist yet in the snapshot, add
it to `SnapshotAircraft` in
`dotnet/src/FlightJar.Core/State/RegistrySnapshot.cs` — the
global snake_case policy means a `FuelPercent` property surfaces
as `fuel_percent` on the wire with no attributes.

### Header / status element

Above the row list, the sidebar has a header row with site name,
status text, msg rate, and a unit switch. To add a new element:

1. **`app/static/index.html`** — add the DOM inside
   `#header` or `#header-row2`. Give it a unique id, keep the
   markup minimal (usually a `<span>` and maybe a dot).
2. **`app/static/sidebar.js`** — add a module-level
   `let lastYourThing = null;` and inside `renderSidebar`
   compute the new value, compare, and set `textContent` only
   if it changed:

   ```js
   let lastTrafficClass = '';
   // …
   const trafficClass = classifyTraffic(snap.count);
   if (trafficClass !== lastTrafficClass) {
     document.getElementById('traffic-class').textContent = trafficClass;
     lastTrafficClass = trafficClass;
   }
   ```

   Header elements use `textContent` almost exclusively — user
   data displayed here (site_name, status text) has never been
   concatenated into `innerHTML`, and new elements should
   follow suit. `textContent` is XSS-safe by construction.

## Step — Verify

```bash
node --check app/static/sidebar.js  # fast syntax check before the full suite
node --test tests/js/
npx playwright test
```

The Playwright sidebar smoke in `tests/e2e/smoke.spec.js`
renders against a fake snapshot — if your new field is
load-bearing for a test, extend `FAKE_SNAPSHOT` rather than
adding a brand-new spec file.

## Do not

- Do not forget the fingerprint. A new field that isn't in
  `fp` will render correctly the first time, then appear to
  "stick" at its initial value. This is the #1 sidebar
  regression and it's easy to miss because steady-cruise
  tests all pass.
- Do not attach event listeners to rows inside the
  per-row template. The sidebar builds row Elements via
  `buildRowElement` which uses a detached `div` as a parse
  host; listeners on a freshly parsed node survive the
  `insertBefore` into `#ac-list`, but you'd leak one listener
  per icao per cache miss. The existing pattern is a single
  delegated `click` / `mouseover` / `mouseout` listener on
  `#ac-list`, bound once in `initSidebar`. Add your new row
  interactions there.
- Do not interpolate `a.callsign` / `a.registration` /
  `a.operator` / `a.emergency` / `a.type_icao` / any
  upstream-sourced string into an HTML template without
  `escapeHtml(…)`. CodeQL flags these on every PR that
  touches the sidebar. Numeric fields via `fmt` / `uconv` are
  safe.
- Do not add a new row-level filter without putting its key in
  `FILTER_KEYS`. The localStorage reader filters unknown keys
  out — a chip whose key isn't registered will visibly toggle
  but not persist across reloads, which users read as a bug.
- Do not re-sort by re-rendering everything. Sort chip clicks
  set `state.sortDir` / `state.sortKey` and call
  `renderSidebar(state.lastSnap)` — the sort happens inside
  `renderSidebar` against the cached snapshot. Don't build a
  parallel `sortRows()` function; the render path already
  does it.
- Do not add a new `<h2>` / `<details>` block inside
  `#sidebar` without first asking — the list view is
  deliberately one-flat-list and the layout has no affordance
  for collapsed sub-sections. If you need grouping, that's a
  design conversation, not a quick extension.
