// Capture README screenshots using a scripted fake fleet + mocked
// backend endpoints — no live BEAST feed or SRTM downloads needed.
//
//   node scripts/take_screenshots.js
//
// The script boots the FlightJar backend itself (same harness env the
// Playwright tests use: BEAST pointed at an unreachable host, blackspots
// disabled), replaces the browser's WebSocket with a no-op shim so the
// real snapshot pusher can't clobber our injected state, and paints a
// rich fake snapshot + mocked /api/aircraft/<icao> + /api/blackspots
// payload. Writes desktop + mobile PNGs for main, detail-panel, stats,
// compact, and blackspots into docs/screenshots/.
//
// Running:
//   npm install --no-save playwright      # one-off
//   node scripts/take_screenshots.js
//
// Add `--base http://localhost:8080` to skip the bundled backend and
// point at an existing instance (e.g. `docker compose up` in a terminal).

import { spawn } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(__dirname, '..');
const outDir = join(repoRoot, 'docs', 'screenshots');
mkdirSync(outDir, { recursive: true });

// --- args ---------------------------------------------------------------

const argv = process.argv.slice(2);
let baseUrl = null;
for (let i = 0; i < argv.length; i++) {
  if (argv[i] === '--base' && argv[i + 1]) { baseUrl = argv[++i]; }
}

const PORT = 8766;
const LAT_REF = 51.5;
const LON_REF = -0.1;

// --- backend -----------------------------------------------------------

async function waitForBackend(url, timeoutMs = 60_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const r = await fetch(url);
      if (r.ok || r.status === 503) return;
    } catch (_) { /* not up yet */ }
    await new Promise(r => setTimeout(r, 500));
  }
  throw new Error(`backend never came up at ${url}`);
}

async function startBackend() {
  const proc = spawn(
    'dotnet',
    ['run', '--project', 'dotnet/src/FlightJar.Api', '--urls', `http://127.0.0.1:${PORT}`],
    {
      cwd: repoRoot,
      env: {
        ...process.env,
        BEAST_HOST: 'nonexistent.invalid',
        BEAST_PORT: '1',
        LAT_REF: String(LAT_REF),
        LON_REF: String(LON_REF),
        BEAST_OUTFILE: '',
        FLIGHT_ROUTES: '0',
        METAR_WEATHER: '0',
        BLACKSPOTS_ENABLED: '0',
        TELEMETRY_ENABLED: '0',
        FLIGHTJAR_STATIC_DIR: join(repoRoot, 'app', 'static'),
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    },
  );
  proc.stdout.on('data', () => {}); // drain
  proc.stderr.on('data', (d) => process.stderr.write(d));
  await waitForBackend(`http://127.0.0.1:${PORT}/`);
  return proc;
}

// --- fake fleet --------------------------------------------------------

function buildFleet() {
  // Positions scattered around the receiver at LAT_REF/LON_REF (London
  // area). Altitudes spread across the altColor ramp so trails and
  // markers show every band. Trail is [lat, lon, alt, ts].
  const now = Date.now() / 1000;
  const mk = (
    icao, callsign, lat, lon, track, alt, spd, type, op, opIata, alliance,
    country, registration, typeLong, origin, dest, phase = 'cruise',
    extra = {},
  ) => {
    // Seed a short trail trailing behind the aircraft along its track
    // heading; altitude walks gently so the trail shows a smooth colour
    // gradient rather than a flat band.
    const trail = [];
    const trackRad = (track * Math.PI) / 180;
    for (let i = 8; i > 0; i--) {
      const dx = -i * 0.04 * Math.sin(trackRad);
      const dy = -i * 0.04 * Math.cos(trackRad);
      trail.push([lat + dy, lon + dx, Math.max(0, alt - i * 200), now - i * 30]);
    }
    trail.push([lat, lon, alt, now]);
    return {
      icao,
      callsign,
      registration,
      type_icao: type,
      type_long: typeLong,
      manufacturer: '',
      operator: op,
      operator_iata: opIata,
      operator_alliance: alliance,
      operator_country: country,
      country_iso: (country === 'United Kingdom') ? 'GB'
        : (country === 'United States') ? 'US'
        : (country === 'Germany') ? 'DE'
        : (country === 'Ireland') ? 'IE'
        : (country === 'France') ? 'FR'
        : (country === 'Netherlands') ? 'NL'
        : (country === 'Qatar') ? 'QA'
        : '',
      category: 'A3',
      lat,
      lon,
      altitude: alt,
      altitude_baro: alt,
      altitude_geo: alt + 50,
      speed: spd,
      ground_speed: spd,
      track,
      vrate: 0,
      vertical_rate: 0,
      squawk: extra.squawk ?? '2000',
      msg_count: 120 + (icao.charCodeAt(2) % 50),
      signal_peak: -12 - (icao.charCodeAt(1) % 8),
      last_seen: now,
      last_position: now,
      first_seen: now - 300,
      on_ground: false,
      emergency: extra.emergency ?? null,
      origin,
      destination: dest,
      phase,
      flight_phase: phase,
      airline: op,
      airline_iata: opIata,
      alliance,
      country,
      track_source: 'adsb',
      distance_km: Math.round(
        Math.hypot((lat - LAT_REF) * 111, (lon - LON_REF) * 70) * 10) / 10,
      trail,
    };
  };

  const aircraft = [
    mk('406b31', 'BAW283', 51.78, -0.45, 255, 34000, 470,
      'B789', 'British Airways', 'BA', 'oneworld', 'United Kingdom',
      'G-ZBKA', 'BOEING 787-9', 'EGLL', 'KSFO', 'cruise'),
    mk('4ca7b4', 'RYR4FP', 51.30, 0.32, 90, 12000, 330,
      'B738', 'Ryanair', 'FR', null, 'Ireland',
      'EI-DYM', 'BOEING 737-800', 'EGSS', 'LIRA', 'climb'),
    mk('3c6671', 'DLH438', 51.92, 0.15, 120, 38000, 455,
      'A21N', 'Lufthansa', 'LH', 'star', 'Germany',
      'D-AIMA', 'AIRBUS A321neo', 'EDDF', 'EGLL', 'descent'),
    mk('a5f2c8', 'UAL901', 51.12, -0.92, 285, 39000, 490,
      'B77W', 'United Airlines', 'UA', 'star', 'United States',
      'N2749U', 'BOEING 777-300ER', 'KORD', 'EGLL', 'descent'),
    mk('06a1b3', 'QTR17', 51.55, 0.54, 340, 41000, 480,
      'A388', 'Qatar Airways', 'QR', 'oneworld', 'Qatar',
      'A7-APA', 'AIRBUS A380-800', 'OTHH', 'EGLL', 'descent'),
    mk('484af6', 'KLM28G', 51.88, -0.72, 45, 7500, 280,
      'E190', 'KLM', 'KL', 'skyteam', 'Netherlands',
      'PH-EZA', 'EMBRAER 190', 'EHAM', 'EGLL', 'descent'),
    mk('3b7a9d', 'AFR82T', 51.42, -0.88, 165, 22000, 420,
      'A320', 'Air France', 'AF', 'skyteam', 'France',
      'F-HEPI', 'AIRBUS A320', 'LFPG', 'EGLL', 'descent'),
    mk('a11d4c', 'N512BK', 51.08, -0.08, 10, 4500, 180,
      'C172', null, null, null, 'United States',
      'N512BK', 'CESSNA 172', null, null, 'cruise'),
    mk('40011f', 'BAW64', 51.62, -0.35, 215, 2200, 165,
      'A320', 'British Airways', 'BA', 'oneworld', 'United Kingdom',
      'G-EUXA', 'AIRBUS A320', 'EDDM', 'EGLL', 'approach'),
    mk('4ca9de', 'EIN56Z', 51.30, -0.60, 340, 28000, 440,
      'A320', 'Aer Lingus', 'EI', 'oneworld', 'Ireland',
      'EI-DVN', 'AIRBUS A320', 'EIDW', 'EGLL', 'descent'),
  ];

  const airports = {
    EGLL: { name: 'London Heathrow',        iata: 'LHR', lat: 51.4706, lon: -0.4619 },
    EGSS: { name: 'London Stansted',        iata: 'STN', lat: 51.885,  lon: 0.235 },
    EHAM: { name: 'Amsterdam Schiphol',     iata: 'AMS', lat: 52.308,  lon: 4.764 },
    EDDF: { name: 'Frankfurt am Main',      iata: 'FRA', lat: 50.033,  lon: 8.570 },
    EDDM: { name: 'Munich',                 iata: 'MUC', lat: 48.354,  lon: 11.786 },
    LFPG: { name: 'Paris Charles de Gaulle', iata: 'CDG', lat: 49.009, lon: 2.547 },
    EIDW: { name: 'Dublin',                 iata: 'DUB', lat: 53.421,  lon: -6.270 },
    LIRA: { name: 'Rome Ciampino',          iata: 'CIA', lat: 41.799,  lon: 12.594 },
    KSFO: { name: 'San Francisco',          iata: 'SFO', lat: 37.619,  lon: -122.375 },
    KORD: { name: "Chicago O'Hare",         iata: 'ORD', lat: 41.978,  lon: -87.904 },
    OTHH: { name: 'Doha Hamad',             iata: 'DOH', lat: 25.273,  lon: 51.608 },
  };

  return {
    now,
    positioned: aircraft.length,
    receiver: { lat: LAT_REF, lon: LON_REF, anon_km: 0 },
    lat_ref: LAT_REF,
    lon_ref: LON_REF,
    frames: 123_456,
    aircraft,
    airports,
    events: [],
  };
}

// --- mocked backend payloads ------------------------------------------

// Nice-looking inline SVG as a data URI — gives the detail panel a
// "real photograph" aesthetic without hotlinking anything, so the
// screenshots are self-contained and reproducible.
function fakePhotoDataUri() {
  const svg = `<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 600 400">
  <defs>
    <linearGradient id="sky" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0%" stop-color="#5d8ec8"/>
      <stop offset="60%" stop-color="#a9c8e8"/>
      <stop offset="100%" stop-color="#d9e3ed"/>
    </linearGradient>
    <linearGradient id="fuselage" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0%" stop-color="#f6f7fa"/>
      <stop offset="100%" stop-color="#c7cad2"/>
    </linearGradient>
  </defs>
  <rect width="600" height="400" fill="url(#sky)"/>
  <ellipse cx="110" cy="130" rx="70" ry="14" fill="#ffffff" opacity="0.5"/>
  <ellipse cx="470" cy="90" rx="90" ry="18" fill="#ffffff" opacity="0.4"/>
  <g transform="translate(120,170)">
    <path d="M0,40 Q20,10 110,10 L330,10 Q365,10 370,40 Q365,60 330,60 L110,60 Q20,60 0,40 Z" fill="url(#fuselage)" stroke="#2c3a4a" stroke-width="1.5"/>
    <path d="M100,10 L70,-22 L150,-12 L180,10 Z" fill="#e1e5ec" stroke="#2c3a4a" stroke-width="1.2"/>
    <path d="M310,10 L330,-14 L345,-4 L340,10 Z" fill="#e1e5ec" stroke="#2c3a4a" stroke-width="1.2"/>
    <rect x="120" y="60" width="40" height="14" fill="#b4bac5" stroke="#2c3a4a" stroke-width="1"/>
    <rect x="230" y="60" width="40" height="14" fill="#b4bac5" stroke="#2c3a4a" stroke-width="1"/>
    <g fill="#4c93d5">
      <circle cx="50"  cy="40" r="2.5"/><circle cx="70"  cy="40" r="2.5"/>
      <circle cx="90"  cy="40" r="2.5"/><circle cx="110" cy="40" r="2.5"/>
      <circle cx="130" cy="40" r="2.5"/><circle cx="150" cy="40" r="2.5"/>
      <circle cx="170" cy="40" r="2.5"/><circle cx="190" cy="40" r="2.5"/>
      <circle cx="210" cy="40" r="2.5"/><circle cx="230" cy="40" r="2.5"/>
      <circle cx="250" cy="40" r="2.5"/><circle cx="270" cy="40" r="2.5"/>
      <circle cx="290" cy="40" r="2.5"/>
    </g>
  </g>
</svg>`;
  return 'data:image/svg+xml;base64,' + Buffer.from(svg).toString('base64');
}

function mockTailRecord(icaoHex) {
  const photo = fakePhotoDataUri();
  return {
    icao: icaoHex,
    registration: 'G-ZBKA',
    type_icao: 'B789',
    type_long: 'BOEING 787-9',
    manufacturer: 'Boeing',
    operator: 'British Airways',
    operator_iata: 'BA',
    operator_country: 'United Kingdom',
    country: 'United Kingdom',
    country_iso: 'GB',
    photo_thumbnail: photo,
    photo_url: photo,
    photo_credit: 'Flightjar demo',
  };
}

function mockBlackspotsGrid(targetAltM) {
  // Pepper terrain-blocked cells around the receiver with varied
  // required-antenna heights so every legend band shows up. Two dense
  // ridges NW + SE of the receiver plus a smaller patch south; the
  // coordinates don't track real terrain — this is purely a visual.
  const gridDeg = 0.05;
  const groundM = 72;
  const antennaMslM = 25;
  const cells = [];
  const add = (dLat, dLon, delta) => cells.push({
    lat: LAT_REF + dLat,
    lon: LON_REF + dLon,
    required_antenna_msl_m: delta == null ? null : antennaMslM + delta,
  });
  // Ridge NW: dense diagonal strip from ~50 to ~120 km away.
  const nwPoints = [
    [0.10, -0.40, 12], [0.14, -0.42, 18], [0.12, -0.36, 20],
    [0.18, -0.48, 28], [0.22, -0.50, 34], [0.20, -0.44, 22],
    [0.24, -0.56, 45], [0.28, -0.58, 55], [0.26, -0.52, 40],
    [0.30, -0.62, 62], [0.32, -0.68, 78], [0.34, -0.60, 58],
    [0.30, -0.54, 48], [0.36, -0.72, 95], [0.38, -0.78, 115],
    [0.32, -0.78, 85], [0.40, -0.80, 130], [0.42, -0.74, null],
    [0.44, -0.68, null], [0.36, -0.66, 68], [0.28, -0.64, 70],
    [0.20, -0.56, 32], [0.16, -0.38, 14],
  ];
  for (const p of nwPoints) add(...p);
  // Ridge SE: another dense chain on the other side of the receiver.
  const sePoints = [
    [-0.12, 0.30, 14], [-0.18, 0.34, 22], [-0.22, 0.38, 30],
    [-0.26, 0.42, 42], [-0.30, 0.48, 55], [-0.32, 0.52, 68],
    [-0.28, 0.44, 48], [-0.24, 0.40, 35], [-0.20, 0.36, 25],
    [-0.34, 0.58, 85], [-0.36, 0.62, 110], [-0.38, 0.66, null],
    [-0.32, 0.46, 55], [-0.26, 0.36, 20], [-0.30, 0.40, 28],
    [-0.16, 0.28, 12], [-0.22, 0.32, 18], [-0.28, 0.50, 52],
    [-0.14, 0.36, 15],
  ];
  for (const p of sePoints) add(...p);
  // Small patch due south for variety.
  for (const p of [
    [-0.42, -0.18, 22], [-0.46, -0.14, 38], [-0.40, -0.10, 18],
    [-0.50, -0.08, 52], [-0.44, -0.22, 25],
  ]) add(...p);
  return {
    enabled: true,
    params: {
      receiver_lat: LAT_REF,
      receiver_lon: LON_REF,
      ground_elevation_m: groundM,
      antenna_msl_m: antennaMslM,
      target_altitude_m: targetAltM,
      radius_km: 150,
      grid_deg: gridDeg,
      max_agl_m: 100,
    },
    computed_at: new Date().toISOString(),
    tile_count: 4,
    tiles_with_data: 4,
    cells,
  };
}

// --- capture -----------------------------------------------------------

const DEVICES = [
  { suffix: '',        viewport: { width: 1440, height: 900 }, isMobile: false },
  { suffix: '-mobile', viewport: { width: 390,  height: 844 }, isMobile: true },
];

async function setupContext(browser, device) {
  const context = await browser.newContext({
    viewport: device.viewport,
    deviceScaleFactor: 1,
    isMobile: device.isMobile,
    hasTouch: device.isMobile,
  });

  // Replace WebSocket with a no-op shim so the real 1 Hz snapshot pusher
  // can't overwrite our injected fake state mid-screenshot.
  await context.addInitScript(() => {
    class NoopWebSocket {
      constructor(url) {
        this.url = url; this.readyState = 1;
        setTimeout(() => this.onopen && this.onopen({}), 0);
      }
      send() {}
      close() { this.readyState = 3; this.onclose && this.onclose({}); }
      addEventListener(k, fn) { this['on' + k] = fn; }
      removeEventListener(k) { this['on' + k] = null; }
    }
    window.WebSocket = NoopWebSocket;
  });

  // Intercept the endpoints we care about. /api/aircraft/<hex> drives
  // the detail-panel photo slot; /api/blackspots drives the shaded
  // grid (the harness disables the feature, so without the route the
  // layer would render nothing).
  await context.route(/\/api\/aircraft\/[0-9a-fA-F]+/, (route) => {
    const hex = route.request().url().split('/').pop();
    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(mockTailRecord(hex)),
    });
  });
  await context.route(/\/api\/blackspots(\?|$)/, (route) => {
    const url = new URL(route.request().url());
    const alt = Number(url.searchParams.get('target_alt_m') || 3048);
    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(mockBlackspotsGrid(alt)),
    });
  });
  await context.route(/\/api\/blackspots\/progress/, (route) => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ active: false, progress: 0 }),
  }));

  return context;
}

async function seedPage(page, snapshot) {
  // Paint the injected snapshot + wait for the sidebar to populate. Two
  // update() calls feed the 5 s smoothing window inside the detail
  // panel's "Live" indicator, so "Last known" doesn't flip in before
  // we capture.
  await page.evaluate(async (snap) => {
    const u = await import('/static/update_loop.js');
    u.update(snap);
    // Fit to the whole fleet so the shot doesn't land on the world view.
    const bounds = L.latLngBounds(snap.aircraft.map(a => [a.lat, a.lon]));
    if (bounds.isValid()) {
      window._flightjar_state = window._flightjar_state || {};
      // Pull the live state through the module graph so we can operate
      // on state.map from inside the page.
      const s = (await import('/static/state.js')).state;
      s.map.fitBounds(bounds.pad(0.15), { maxZoom: 8, animate: false });
    }
  }, snapshot);
  await page.waitForSelector('.ac-item', { timeout: 5000 });
  await page.waitForTimeout(400);
}

async function capture(browser, device, base) {
  const context = await setupContext(browser, device);
  const page = await context.newPage();
  const file = (name) => join(outDir, `${name}${device.suffix}.png`);

  console.log(`[${device.suffix || 'desktop'}] loading ${base}`);
  await page.goto(base, { waitUntil: 'domcontentloaded' });
  // Clear any lingering localStorage + reload so defaults apply.
  await page.evaluate(() => localStorage.clear());
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForSelector('#map');
  await page.waitForTimeout(500);

  const snapshot = buildFleet();
  await seedPage(page, snapshot);
  // Keep the injected snapshot alive by re-pushing it on an interval —
  // some modules tick against state independently (age readouts etc)
  // and this also refreshes `last_seen` so nothing flips to "lost".
  await page.evaluate((snap) => {
    window.__pushSnap = async () => {
      const u = await import('/static/update_loop.js');
      const bumped = { ...snap, now: Date.now() / 1000 };
      bumped.aircraft = snap.aircraft.map(a => ({
        ...a, last_seen: bumped.now, last_position: bumped.now,
      }));
      u.update(bumped);
    };
    setInterval(() => window.__pushSnap(), 1000);
  }, snapshot);

  console.log(`-> ${file('main')}`);
  await page.screenshot({ path: file('main') });

  // Detail panel — pick the BA 787 as the demo tail because its mocked
  // /api/aircraft/<hex> payload describes a 787 so the panel is coherent.
  await page.evaluate(async () => {
    const m = await import('/static/detail_panel.js');
    m.openDetailPanel('406b31');
  });
  await page.waitForSelector('#detail-panel.open');
  // Fake photo is a data URI so it loads instantly, but the panel still
  // does a micro-layout pass — half a second is plenty.
  await page.waitForTimeout(600);
  console.log(`-> ${file('detail-panel')}`);
  await page.screenshot({ path: file('detail-panel') });

  // Close the detail panel before moving on.
  await page.locator('#detail-close').evaluate((el) => el.click());
  await page.waitForTimeout(400);

  // Stats dialog.
  await page.locator('#stats-btn').click();
  await page.waitForSelector('#stats-dialog[open]', { timeout: 5000 });
  await page.waitForTimeout(700);
  console.log(`-> ${file('stats')}`);
  await page.screenshot({ path: file('stats') });
  await page.locator('#stats-dialog .about-close').click();
  await page.waitForTimeout(300);

  // Collapse the Leaflet layers control — on touch viewports it tends
  // to stay open and would obscure the map in subsequent shots.
  await page.evaluate(async () => {
    const { state } = await import('/static/state.js');
    state.layersControl?.collapse?.();
    document.querySelectorAll('.leaflet-control-layers-expanded')
      .forEach((el) => el.classList.remove('leaflet-control-layers-expanded'));
  });

  // Compact mode (keyboard C toggles on both desktop + mobile). Dispatch
  // the shortcut via the document-level listener rather than pressing
  // against `body` so Playwright's mobile emulation doesn't also tap
  // the map and expand the Leaflet layers control under our button.
  const toggleCompact = () => page.evaluate(() =>
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'c' })));
  await toggleCompact();
  await page.waitForTimeout(500);
  // Belt-and-braces: collapse the layers control again in case anything
  // expanded it during the compact toggle.
  await page.evaluate(async () => {
    const { state } = await import('/static/state.js');
    state.layersControl?.collapse?.();
  });
  console.log(`-> ${file('compact')}`);
  await page.screenshot({ path: file('compact') });
  // Return to the normal layout for the blackspots shot.
  await toggleCompact();
  await page.waitForTimeout(500);

  // Terrain blackspots overlay. The layer's on-activation fetch hits
  // our mocked /api/blackspots route and the slider control slides in.
  // After the cells land, centre on the receiver at a zoom that keeps
  // both clusters on-screen on desktop AND mobile viewports. Mobile's
  // narrower map would otherwise pick a lower zoom via fitBounds and
  // render the 0.05° cells too small to read.
  await page.evaluate(async (receiver) => {
    const bs = await import('/static/blackspots.js');
    const { state } = await import('/static/state.js');
    bs.setBlackspots(true);
    await new Promise((r) => setTimeout(r, 900));
    state.map.setView([receiver.lat, receiver.lon], 8, { animate: false });
  }, { lat: LAT_REF, lon: LON_REF });
  await page.waitForTimeout(700);
  console.log(`-> ${file('blackspots')}`);
  await page.screenshot({ path: file('blackspots') });

  await context.close();
}

// --- main --------------------------------------------------------------

let backend = null;
let finalBase = baseUrl;
try {
  if (!finalBase) {
    console.log(`starting backend on :${PORT} …`);
    backend = await startBackend();
    finalBase = `http://127.0.0.1:${PORT}`;
  } else {
    console.log(`using existing backend at ${finalBase}`);
  }

  const browser = await chromium.launch();
  try {
    for (const device of DEVICES) {
      await capture(browser, device, finalBase);
    }
  } finally {
    await browser.close();
  }
  console.log('done →', outDir);
} finally {
  if (backend) {
    backend.kill('SIGINT');
    await new Promise(r => backend.once('exit', r)).catch(() => {});
  }
}
