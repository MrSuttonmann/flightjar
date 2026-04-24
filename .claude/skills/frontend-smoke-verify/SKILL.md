---
name: frontend-smoke-verify
description: Quickly verify a frontend change in-browser without regenerating screenshots. Boots the FlightJar backend in the Playwright harness config (BEAST pointed at a dead host, blackspots off), injects a fake snapshot, opens the detail panel, and checks for console errors. Use when the user asks to "test the UI", "smoke-check the panel", "verify this renders correctly", or after any app/static/* change you need to confirm works in a real browser. Do NOT use for capturing screenshots — that's the screenshots skill.
---

# In-browser smoke check for frontend changes

The Playwright test suite already has the right harness: a FlightJar
backend booted with `BEAST_HOST=nonexistent.invalid`, blackspots
disabled, and static files served from the repo. Most frontend changes
can be validated by running a targeted subset of the existing tests
rather than writing new ones. This skill is for the loop: make a
change → verify it in a browser → iterate.

## The tests that cover most UI paths

| Test name | What it covers |
|---|---|
| `aircraft detail panel opens, fits the viewport, and closes` | Panel position, viewport fit, close button reachable |
| `no console errors during a typical UI session` | Opens every dialog + the panel, catches SVG / DOM errors |
| `detail panel renders Enhanced Mode S section when comm_b is present` | Exercises the panel's Comm-B / met rendering path |
| `detail panel metric labels carry help icons with explanations` | Help-icon injection across all metric tiles |
| `every dialog fits inside the viewport and the close button is reachable` | Sizing / overflow audits for every dialog |
| `no horizontal scroll at this viewport` | Layout regression guard |

Each runs on both `desktop` and `mobile` projects, so a pass implies
the change works at both viewport sizes.

## First: run the existing suite with your change

```bash
npx playwright test                              # full suite (~30 s)
npx playwright test --grep "detail panel|help"   # narrowed
npx playwright test --project=desktop            # skip mobile during iteration
npx playwright test --headed                     # watch it actually click
```

If an existing test fails, fix the code. If the change is covered by
an existing test's shape but doesn't fit the existing assertions, add
a test (see `tests/e2e/layout.spec.js` for patterns).

## Second: exploratory check against a live browser

When you need to actually see the panel with realistic data, boot
the backend yourself and point a browser at it. The Playwright tests
shut the backend down between runs, so you can't just peek at the
test URL afterwards.

```bash
# Build once.
(cd dotnet && dotnet build FlightJar.slnx -c Debug)

# Boot in Playwright-harness config. BEAST host is deliberately bad
# — we only want the UI, not live data, so the WebSocket sits in a
# reconnect loop and doesn't deliver snapshots.
BEAST_HOST=nonexistent.invalid BEAST_PORT=1 \
  LAT_REF=51.5 LON_REF=-0.1 BEAST_OUTFILE='' \
  FLIGHT_ROUTES=0 METAR_WEATHER=0 \
  BLACKSPOTS_ENABLED=0 TELEMETRY_ENABLED=0 \
  FLIGHTJAR_STATIC_DIR=$PWD/app/static \
  dotnet dotnet/src/FlightJar.Api/bin/Debug/net10.0/FlightJar.Api.dll \
  --urls http://127.0.0.1:8766 > /tmp/flightjar.log 2>&1 &
echo $! > /tmp/flightjar.pid
until curl -sf -o /dev/null http://127.0.0.1:8766/; do sleep 0.5; done

# Now visit http://127.0.0.1:8766/ in a browser. Use the browser
# console to inject a fake snapshot if you need planes on the map:
#
#   const m = await import('/static/update_loop.js');
#   m.update({
#     now: Date.now()/1000, lat_ref: 51.5, lon_ref: -0.1,
#     aircraft: [{ icao: 'a12345', callsign: 'TEST1', lat: 51.5, lon: -0.1,
#                  altitude: 35000, ground_speed: 450, track: 90,
#                  /* …plus any fields the change reads */ }],
#     airports: {},
#   });
#
# To exercise the detail panel specifically:
#
#   const dp = await import('/static/detail_panel.js');
#   dp.openDetailPanel('a12345');

# Stop the backend when done. `lsof -i :8766` to double-check.
kill $(cat /tmp/flightjar.pid) 2>/dev/null
```

## Third: headless console-error sweep

For a quick sanity check without opening a browser, run the
"no console errors" test with verbose output:

```bash
npx playwright test --grep "no console errors" --reporter=list
```

If you're making repeated iterative edits, narrow further to the
desktop project so you're not paying for the mobile viewport run
each time: `--project=desktop`.

## When to write a new test rather than just running the existing ones

Add a Playwright test when:

- Your change introduces a new DOM structure (`.panel-met`, a new
  dialog) that existing tests don't reach into.
- Your change adds branching on snapshot data that the existing
  `FAKE_SNAPSHOT` doesn't cover (a new nested object, a new
  conditional section).
- The change fixes a specific regression — pin it with a test so
  future refactors re-break visibly.

Pattern: extend `FAKE_SNAPSHOT` in `tests/e2e/layout.spec.js` with
the new fields, inject via `update_loop.js.update()`, assert on the
rendered DOM. See `detail panel renders Enhanced Mode S section when
comm_b is present` for a good template.

## What to assert

- **Section visibility**: `document.querySelector('.panel-foo').hidden === false`.
- **Tile count**: `querySelectorAll('.panel-foo-grid .metric:not([hidden])').length >= N`.
- **Text content**: substring-match on label + value rather than
  exact strings, so prose edits don't break tests.
- **No console errors**: use the page-level `pageerror` / `console`
  handlers from the existing "no console errors" test.

## Do not

- Do not leave the harness backend running after you finish. Kill
  the PID and verify with `lsof -i :8766`. An orphan on 8766 will
  conflict with the next Playwright run.
- Do not commit `/tmp/flightjar.log` or `/tmp/flightjar.pid`.
- Do not assert on exact pixel positions. The Playwright viewport
  sizes vary by project; use relative predicates (fits within
  viewport, close button visible) like the existing tests.
- Do not write a new test that needs live ADS-B data. The harness
  doesn't have a BEAST feed — rely on injected `update()` calls
  for anything data-driven.
- Do not reach for `scripts/take_screenshots.js` to verify a
  change. That script is for regenerating README screenshots and
  requires fetching Wikimedia photos; it's heavier than this
  workflow needs.
