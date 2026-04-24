---
name: detail-panel-section
description: Add a new section (grid of metric tiles, or custom block) to the aircraft detail panel in app/static/detail_panel.js. Use when the user asks to show additional per-aircraft info in the floating card — fuel state, waypoints, winds-aloft, ACARS, extra Mode S registers, anything per-plane. Covers the DOM placeholder / per-tick mutator split, help-icon wiring, CSS conventions, and the Playwright smoke that catches layout + width regressions. Do NOT use this for sidebar changes, header, or dialogs — those live elsewhere.
---

# Adding a section to the aircraft detail panel

The detail panel is the floating card that opens when you click an aircraft.
It has a **build-once / mutate-per-tick** architecture: `buildPopupContent`
lays down a fixed DOM tree with placeholder elements when an aircraft is
selected, and `updatePopupContent` runs on every WebSocket snapshot to
mutate those placeholders in place. That split is the single most
important convention here — violating it (rebuilding HTML every tick)
makes the aircraft photo flicker, drops focus state, and thrashes
layout. **Always add new markup in `buildPopupContent`, always update it
in `updatePopupContent`.**

## Touchpoints

One section spans four files:

1. **`app/static/detail_panel.js`** —
   - Add the placeholder markup inside `buildPopupContent` at the
     right vertical position (after `.panel-route`, before
     `.panel-meta`, etc.).
   - Add an update function called from the end of `updatePopupContent`.
   - If the section shows metrics with non-obvious meanings, add
     entries to `METRIC_HELP` so `injectHelpIcons` wires up the `?`
     tooltips automatically — the label text of a `.metric > .label`
     is the map key.
2. **`app/static/detail_panel.css`** — Reuse the `.metric` /
   `.panel-mini-label` conventions. New grid containers should mirror
   `.panel-meta` (3 columns, 6px gap) unless you have a specific
   reason to differ.
3. **`dotnet/src/FlightJar.Core/State/RegistrySnapshot.cs`** — If
   the section reads new snapshot fields, add them to
   `SnapshotAircraft` (or a nested record like `SnapshotCommB`).
   Fields serialise as `snake_case` via the global policy — no
   `[JsonPropertyName]` attributes needed.
4. **`tests/e2e/layout.spec.js`** — Extend the `FAKE_SNAPSHOT` +
   add a test that opens the panel against a snapshot with the new
   data populated and asserts the section renders. At minimum
   verify no console errors and that the panel still fits the
   viewport.

## Step 1 — Placeholder DOM

Inside `buildPopupContent`, add your section's HTML to the
`root.innerHTML` string. Use placeholder elements with class names
like `.pop-fuel-something` so `updatePopupContent` can find them. Start
with `hidden` on the top-level section wrapper so the panel doesn't
render empty boxes before the updater runs:

```js
`<div class="panel-fuel" hidden>` +
  `<div class="panel-mini-label">Fuel state</div>` +
  `<div class="panel-fuel-grid">` +
    `<div class="metric pop-fuel-weight" hidden>` +
      `<div class="label">Fuel remaining</div>` +
      `<div class="val"></div></div>` +
    // ...more tiles
  `</div>` +
`</div>` +
```

Label text inside `.metric > .label` should be short (≤ 12 chars) so
it fits on one line at 9px uppercase styling. Longer explanations
belong in `METRIC_HELP` (see Step 3).

## Step 2 — Per-tick updater

Write a dedicated function called from the end of `updatePopupContent`.
Mirror `renderCommBSection`'s shape — a local `set(selector, value)`
helper that hides the tile when `value` is null, sets `innerHTML`
otherwise. End with a "hide the whole section if everything is null"
check so we don't render an empty grid.

```js
function renderFuelSection(root, q, a) {
  const fuel = a.fuel;
  const sec = q('.panel-fuel');
  if (!fuel) { sec.hidden = true; return; }

  const set = (cls, value) => {
    const el = root.querySelector(cls);
    if (value == null) { el.hidden = true; }
    else { el.querySelector('.val').innerHTML = value; el.hidden = false; }
  };

  set('.pop-fuel-weight', fuel.weight_kg != null
    ? `${(fuel.weight_kg / 1000).toFixed(1)} t` : null);
  // ...more tiles

  const grid = q('.panel-fuel-grid');
  const anyVisible = Array.from(grid.children).some((el) => !el.hidden);
  sec.hidden = !anyVisible;
}
```

Use the existing `uconv('alt' | 'spd' | 'vrt' | 'dst', value)` helper
for unit-system-aware formatting. Raw-unit fields (Mach, temperature,
degrees, percent) format inline.

## Step 3 — Help icons

For every new `.metric` label, add a `METRIC_HELP['Label text'] = '...'`
entry in the block at the top of `detail_panel.js`. `injectHelpIcons`
(called from `buildPopupContent`) walks every `.metric > .label` in the
panel and appends a `?` icon when a matching entry exists. No extra
plumbing — label text is the key.

Guidelines for help text:
- One or two sentences, ≤ 250 chars.
- Lead with what the value means, then how to interpret it.
- Mention data source at the end ("Decoded from BDS 6,0.",
  "From the ADS-B velocity message.") so readers can cross-reference.

## Step 4 — CSS

New grid containers mirror `.panel-meta`'s shape. Don't invent new
metric tile styling unless there's a strong visual reason —
consistency across the panel matters more than a clever variant. The
`.panel-met` block in `detail_panel.css` is a good template for a
section that wraps its grid in a subtly tinted container.

Keep selectors under the `#detail-panel` parent when they risk
colliding with sidebar or dialog markup (e.g. `.metric` is used in
both places). Panel-local styling like `.panel-meta .metric` is fine;
bare `.metric { … }` is not.

## Step 5 — Check panel width

The detail panel is 420px wide on desktop. A 3-column `.metric` grid
leaves ~130px per tile including padding. Values longer than about
16 characters (e.g. `"-56.8 °C derived"`) wrap to two lines unless
you shrink the value's font or drop to 2 columns for the affected
section. Run the Playwright `detail panel opens, fits the viewport`
test — it checks `fits` and `closeVisible` after a render. If the new
section pushes the panel past the viewport bound on mobile (390px),
drop your grid to 2 columns.

## Step 6 — Playwright smoke

In `tests/e2e/layout.spec.js`, extend `FAKE_SNAPSHOT` with the data
your section reads and add a test that:

1. Injects the snapshot via `update_loop.js.update()` and opens the
   panel.
2. Asserts the section element (`.panel-fuel`) is visible and not
   `hidden`.
3. Asserts at least one expected tile label shows up inside the
   section.

Leave the existing `no console errors during a typical UI session`
test alone — it catches SVG / DOM mistakes and is a good backstop.

## Step 7 — Verify

```bash
cd dotnet && dotnet format FlightJar.slnx --verify-no-changes
dotnet build FlightJar.slnx
dotnet test FlightJar.slnx
cd ..
node --test tests/js/
npx playwright test
```

Frontend formatter drift isn't checked automatically — run
`node --check app/static/detail_panel.js` if you made a large edit and
want a quick syntax verification before the full test run.

## Do not

- Do not rebuild HTML in `updatePopupContent`. Every tick that
  replaces `innerHTML` discards focus state, breaks the photo slot's
  fade-in animation, and re-runs SVG rendering. Mutate placeholders.
- Do not add per-tile event listeners in `updatePopupContent` —
  they'd leak handlers every second. Delegate from the panel root,
  or attach once inside `buildPopupContent`.
- Do not hit a new backend endpoint for per-plane info if the data
  could live in the 1 Hz snapshot. The WebSocket fan-out is cheap;
  per-aircraft HTTP calls on every tick are not.
- Do not add new CSS selectors without the `#detail-panel` prefix if
  the class name is remotely generic (`.metric`, `.label`, `.val`).
- Do not create new tooltip infrastructure. The singleton in
  `tooltip.js` handles both `.airport-code[data-title]` and any
  `[data-help]` trigger — add `data-help` to your new elements and
  it just works.
