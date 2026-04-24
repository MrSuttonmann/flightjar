// Playwright config. The .NET backend is booted as an ephemeral web
// server for the duration of the test run, pointed at a non-existent
// BEAST host so the UI loads and sits in the "Disconnected" state.
// That's enough to exercise every test in tests/e2e — they all cover
// UI paths that don't require live ADS-B data.
//
// For the data-driven paths (detail panel, map markers, trails) we'd
// need a feed mock; that's out of scope for the initial smoke suite.

import { defineConfig, devices } from '@playwright/test';

const PORT = 8765;

export default defineConfig({
  testDir: './tests/e2e',
  timeout: 20_000,
  fullyParallel: false, // single shared backend → serialise
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? 'github' : 'list',

  use: {
    baseURL: `http://127.0.0.1:${PORT}`,
    trace: 'on-first-retry',
  },

  // Run every spec in tests/e2e against both a desktop and a phone
  // viewport. The mobile project keeps the layout spec honest when
  // CSS or markup changes would reintroduce overflow / cut-off bugs.
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'] } },
    {
      name: 'mobile',
      use: {
        ...devices['Desktop Chrome'],
        // A fixed CSS viewport (no isMobile emulation) so layout tests
        // measure the same numbers a real phone would report for CSS
        // pixels. 390×844 is the iPhone 13 / Pixel 7 ballpark; small
        // enough to flush out tight-layout bugs without being fringe.
        viewport: { width: 390, height: 844 },
        hasTouch: true,
      },
    },
  ],

  webServer: {
    // Run the .NET backend via `dotnet run` for local dev (reuses the
    // cached build), or from a pre-published directory in CI where we
    // `dotnet publish` into dotnet-publish/ first.
    command: process.env.CI
      ? `dotnet dotnet-publish/FlightJar.Api.dll --urls http://127.0.0.1:${PORT}`
      : `dotnet run --project dotnet/src/FlightJar.Api --urls http://127.0.0.1:${PORT}`,
    // /healthz returns 503 while BEAST is disconnected (which it is in
    // the test harness), so wait on / instead — it always returns 200
    // once Kestrel is listening and the static root has been resolved.
    url: `http://127.0.0.1:${PORT}/`,
    reuseExistingServer: !process.env.CI,
    timeout: 60_000,
    // Keep the backend quiet: no BEAST host to reach, no on-disk
    // artefacts, no file output. LAT/LON are present so receiver-
    // anchored UI (range rings, coverage) can initialise.
    env: {
      BEAST_HOST: 'nonexistent.invalid',
      BEAST_PORT: '1',
      LAT_REF: '51.5',
      LON_REF: '-0.1',
      BEAST_OUTFILE: '',
      FLIGHT_ROUTES: '0',
      // Blackspots would otherwise try to fetch SRTM tiles from AWS on
      // every test run. Disable it here — the layer still registers in
      // the frontend (testing that is the point of the e2e assertion).
      BLACKSPOTS_ENABLED: '0',
      FLIGHTJAR_STATIC_DIR: `${process.cwd()}/app/static`,
    },
  },
});
