// Playwright config. The FastAPI backend is booted as an ephemeral web
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

  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
  ],

  webServer: {
    command:
      `uvicorn app.main:app --host 127.0.0.1 --port ${PORT} --no-access-log`,
    // /healthz returns 503 while BEAST is disconnected (which it is
    // in the test harness), so wait on / instead — it always returns
    // 200 once the FastAPI app is listening.
    url: `http://127.0.0.1:${PORT}/`,
    reuseExistingServer: !process.env.CI,
    timeout: 30_000,
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
    },
  },
});
