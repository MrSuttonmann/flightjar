// Capture README screenshots using a scripted fake fleet + mocked
// backend endpoints — no live BEAST feed or SRTM downloads needed.
//
//   node scripts/take_screenshots.js [--base http://host:port]
//
// The script boots the FlightJar backend itself (same harness env the
// Playwright tests use: BEAST pointed at an unreachable host, blackspots
// disabled), replaces the browser's WebSocket with a no-op shim so the
// real snapshot pusher can't clobber our injected state, and paints a
// rich fake snapshot + mocked /api/aircraft/<icao>, /api/blackspots,
// /api/stats, /api/coverage, /api/heatmap, /api/polar_heatmap payloads
// so the Stats dialog, blackspot grid, and detail panel all render as
// if this were a real-world install with weeks of recorded data.
//
// Running:
//   (cd dotnet && dotnet build FlightJar.slnx -c Debug)
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

// --- real photo fetch -------------------------------------------------

// Wikimedia Commons 787-9 G-ZBKA (CC BY-SA 2.0). Embedded as a data
// URI so the mocked /api/aircraft response stays self-contained once
// we've paid the upstream fetch cost.
const COMMONS_PHOTO_URL = (
  'https://upload.wikimedia.org/wikipedia/commons/thumb/7/71/'
  + 'British_Airways%2C_G-ZBKA%2C_Boeing_787-9_Dreamliner_%2849596677923%29.jpg/'
  + '960px-British_Airways%2C_G-ZBKA%2C_Boeing_787-9_Dreamliner_%2849596677923%29.jpg'
);

async function fetchPhotoDataUri() {
  try {
    const r = await fetch(COMMONS_PHOTO_URL);
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    const buf = Buffer.from(await r.arrayBuffer());
    return 'data:image/jpeg;base64,' + buf.toString('base64');
  } catch (e) {
    console.warn('photo fetch failed, falling back to SVG placeholder:', e.message);
    return fallbackPhotoSvg();
  }
}

function fallbackPhotoSvg() {
  const svg = `<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 600 400">
  <rect width="600" height="400" fill="#3a4a5e"/>
  <text x="300" y="205" text-anchor="middle" font-family="sans-serif"
        font-size="18" fill="#ffffff" opacity="0.8">aircraft photo unavailable</text>
</svg>`;
  return 'data:image/svg+xml;base64,' + Buffer.from(svg).toString('base64');
}

// --- fake fleet --------------------------------------------------------

function buildFleet() {
  const now = Date.now() / 1000;
  const mk = (
    icao, callsign, lat, lon, track, alt, spd, type, op, opIata, alliance,
    country, registration, typeLong, origin, dest, phase = 'cruise',
    extra = {},
  ) => {
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
      comm_b: extra.comm_b ?? null,
      trail,
    };
  };

  const aircraft = [
    mk('406b31', 'BAW283', 51.78, -0.45, 255, 34000, 470,
      'B789', 'British Airways', 'BA', 'oneworld', 'United Kingdom',
      'G-ZBKA', 'BOEING 787-9', 'EGLL', 'KSFO', 'cruise',
      {
        // Realistic cruise-altitude Comm-B so the Enhanced Mode S panel
        // has values to show in the screenshot. Mix of observed BDS 4,4
        // (wind + SAT) and BDS 5,0 / 6,0 (TAS / Mach / IAS / heading /
        // v-rates) — covers all four registers the decoder handles.
        comm_b: {
          selected_altitude_mcp_ft: 34000,
          qnh_hpa: 1013.2,
          wind_speed_kt: 78, wind_direction_deg: 265,
          static_air_temperature_c: -54.5,
          total_air_temperature_c: -24.6,
          humidity_pct: 8.0,
          turbulence: 1,
          mach: 0.84,
          indicated_airspeed_kt: 288,
          true_airspeed_kt: 481,
          groundspeed_kt: 472,
          magnetic_heading_deg: 259,
          true_track_deg: 255,
          roll_deg: -0.6,
          track_rate_deg_per_s: 0.02,
          baro_vertical_rate_fpm: 0,
          inertial_vertical_rate_fpm: -32,
          static_air_temperature_source: 'observed',
          bds40_at: (Date.now() / 1000) - 1,
          bds44_at: (Date.now() / 1000) - 2,
          bds50_at: (Date.now() / 1000) - 1,
          bds60_at: (Date.now() / 1000),
        },
      }),
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
    count: aircraft.length,
    positioned: aircraft.length,
    receiver: { lat: LAT_REF, lon: LON_REF, anon_km: 0 },
    lat_ref: LAT_REF,
    lon_ref: LON_REF,
    frames: 8_432_117,
    site_name: 'Home receiver',
    aircraft,
    airports,
    events: [],
  };
}

// --- mocked backend payloads ------------------------------------------

function mockTailRecord(icaoHex, photoDataUri) {
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
    photo_thumbnail: photoDataUri,
    photo_url: photoDataUri,
    photo_credit: 'Anna Zvereva / Wikimedia Commons (CC BY-SA 2.0)',
  };
}

// Dense radial grid of blackspot cells. Uses a seeded pseudo-random
// permutation so bands and clusters are reproducible between runs
// while looking organic (rough NW/NE ridges, softer south / east
// gaps), and picks a couple of "unreachable" cells at the far edges.
function mockBlackspotsGrid(targetAltM) {
  const gridDeg = 0.05;
  // Plausible suburban receiver: ground at ~45 m MSL, antenna on a
  // roof-mounted mast ~13 m above that. Ground MUST stay below antenna
  // — an antenna-MSL lower than ground-MSL surfaces as a negative AGL
  // in the tooltip ("You have X m MSL (-Y m AGL)"), which reads as if
  // the receiver is buried.
  const groundM = 45;
  const antennaMslM = 58;
  const radiusDeg = 1.6; // ~175 km N/S at UK latitudes
  const cells = [];

  // Cheap deterministic hash → [0, 1).
  const rnd = (i, j, salt) => {
    const s = Math.sin((i * 374761 + j * 668265 + salt * 10139) * 0.0001) * 10000;
    return s - Math.floor(s);
  };

  // Base ridges centred NW + NE of the receiver, plus a secondary
  // patch SE. Each ridge is defined as (centre dLat, centre dLon,
  // peak delta m, sigma) — cells inside pick up a delta that tapers
  // off with distance from the ridge centre.
  const ridges = [
    { cLat: 0.32, cLon: -0.55, peak: 95,  sigma: 0.25 },  // NW Chilterns-ish
    { cLat: 0.60, cLon: -0.20, peak: 75,  sigma: 0.32 },  // N Bedfordshire
    { cLat: 0.45, cLon:  0.40, peak: 55,  sigma: 0.28 },  // NE toward Essex coast
    { cLat: -0.35, cLon:  0.55, peak: 65, sigma: 0.28 },  // SE toward Kent Downs
    { cLat: -0.70, cLon: -0.25, peak: 48, sigma: 0.35 },  // S Downs
    { cLat: -0.20, cLon: -0.85, peak: 70, sigma: 0.30 },  // SW ridge
  ];

  for (let dLat = -radiusDeg; dLat <= radiusDeg; dLat += gridDeg) {
    for (let dLon = -radiusDeg * 1.4; dLon <= radiusDeg * 1.4; dLon += gridDeg) {
      const d = Math.hypot(dLat, dLon * 0.7);
      if (d > radiusDeg) continue;
      // Earth-curvature baseline: the further away, the more antenna
      // height you'd need just to clear the bulge. Tiny noise layer
      // keeps cells from forming perfect concentric rings.
      let delta = 0;
      for (const r of ridges) {
        const dd = Math.hypot(dLat - r.cLat, dLon - r.cLon);
        delta += r.peak * Math.exp(-(dd * dd) / (2 * r.sigma * r.sigma));
      }
      const noise = (rnd(Math.round(dLat * 100), Math.round(dLon * 100), 7) - 0.5) * 14;
      delta += noise;
      // Cells with very little blockage stay "visible" — skip them so
      // the grid doesn't paint the whole world. The threshold is the
      // yellow-band boundary from blackspots_format.js.
      if (delta < 8) continue;
      // A sprinkling of "unreachable" cells at the far edges adds the
      // purple band from the legend without covering the overlay in it.
      const unreachable = delta > 130 && rnd(
        Math.round(dLat * 100), Math.round(dLon * 100), 13) < 0.55;
      cells.push({
        lat: LAT_REF + dLat,
        lon: LON_REF + dLon,
        required_antenna_msl_m: unreachable ? null : antennaMslM + delta,
      });
    }
  }
  return {
    enabled: true,
    params: {
      receiver_lat: LAT_REF,
      receiver_lon: LON_REF,
      ground_elevation_m: groundM,
      antenna_msl_m: antennaMslM,
      target_altitude_m: targetAltM,
      radius_km: 180,
      grid_deg: gridDeg,
      max_agl_m: 100,
    },
    computed_at: new Date(Date.now() - 14 * 86400 * 1000).toISOString(),
    tile_count: 12,
    tiles_with_data: 12,
    cells,
  };
}

// Plausible /api/stats payload: a week-old install with a few million
// frames logged, a steady WebSocket client count, and the aircraft
// counts taken from the current snapshot.
function mockStatsPayload(snapshot) {
  return {
    site_name: snapshot.site_name,
    beast_host: 'readsb.home.lan',
    beast_port: 30005,
    beast_target: 'readsb.home.lan:30005',
    beast_connected: true,
    frames: snapshot.frames,
    websocket_clients: 2,
    aircraft: snapshot.count,
    positioned: snapshot.positioned,
    uptime_s: 6 * 86400 + 13 * 3600 + 42 * 60 + 18,
    version: 'git-demo',
  };
}

// Synthetic polar coverage ring: 36 buckets, max-range per bearing.
// Bigger to the east/south (over sea) than to the west/north (where the
// receiver overlooks hills). Small bucket-to-bucket noise keeps it from
// looking too clean.
function mockCoveragePayload() {
  const buckets = 36;
  const bucketDeg = 360 / buckets;
  const bearings = [];
  for (let i = 0; i < buckets; i++) {
    const angle = i * bucketDeg + bucketDeg / 2;
    const rad = (angle * Math.PI) / 180;
    // Ellipse-ish shape: long axis east-south-east (~110°).
    const primary = 380 + 110 * Math.cos(rad - (110 * Math.PI) / 180);
    const dip = 60 * Math.sin(rad * 3); // little ripple
    const noise = 30 * (Math.sin(angle * 0.31) + 0.5 * Math.cos(angle * 0.83));
    const d = Math.max(95, Math.round(primary + dip + noise));
    bearings.push({ angle, dist_km: d });
  }
  return {
    receiver: { lat: LAT_REF, lon: LON_REF },
    bucket_deg: bucketDeg,
    bearings,
  };
}

function mockHeatmapPayload() {
  // 7×24 traffic heatmap. Morning + evening arrival/departure peaks,
  // weekends slightly lighter, nights quiet. Absolute numbers don't
  // matter for the visual — the renderer scales to the max.
  const labels = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
  const grid = labels.map((_, d) => {
    const row = new Array(24).fill(0);
    for (let h = 0; h < 24; h++) {
      const morning = Math.exp(-((h - 8) ** 2) / 6) * 280;
      const evening = Math.exp(-((h - 18) ** 2) / 10) * 320;
      const midday = Math.exp(-((h - 13) ** 2) / 40) * 120;
      const weekendFactor = (d === 5 || d === 6) ? 0.8 : 1.0;
      const jitter = (Math.sin((d + 1) * (h + 1) * 1.31) + 1) * 30;
      row[h] = Math.round((morning + evening + midday + jitter) * weekendFactor);
    }
    return row;
  });
  return { grid, day_labels: labels };
}

function mockPolarHeatmapPayload() {
  // 36 buckets × 12 bands (25 km each = 300 km). Fade from bright near
  // the receiver to dim at the edges; stronger lobe to the south-east.
  const buckets = 36;
  const bands = 12;
  const bucketDeg = 360 / buckets;
  const bandKm = 25;
  const grid = [];
  let total = 0;
  for (let b = 0; b < buckets; b++) {
    const angle = b * bucketDeg + bucketDeg / 2;
    const rad = (angle * Math.PI) / 180;
    const azimuthLobe = 1 + 0.9 * Math.cos(rad - (120 * Math.PI) / 180);
    const row = new Array(bands).fill(0);
    for (let band = 0; band < bands; band++) {
      // Gaussian decay with distance plus the azimuthal lobe.
      const dist = band * bandKm + bandKm / 2;
      const decay = Math.exp(-((dist - 60) ** 2) / 18000);
      const base = 1400 * decay * azimuthLobe;
      const noise = Math.sin((b + 1) * (band + 1) * 0.93) * 40;
      const v = Math.max(0, Math.round(base + noise));
      row[band] = v;
      total += v;
    }
    grid.push(row);
  }
  return {
    receiver: { lat: LAT_REF, lon: LON_REF },
    bucket_deg: bucketDeg,
    band_km: bandKm,
    bands,
    window_days: 7,
    grid,
    total,
  };
}

// --- capture -----------------------------------------------------------

const DEVICES = [
  { suffix: '',        viewport: { width: 1440, height: 900 }, isMobile: false },
  { suffix: '-mobile', viewport: { width: 390,  height: 844 }, isMobile: true },
];

async function setupContext(browser, device, photoDataUri) {
  const context = await browser.newContext({
    viewport: device.viewport,
    deviceScaleFactor: 1,
    isMobile: device.isMobile,
    hasTouch: device.isMobile,
  });

  // Replace WebSocket with a no-op shim so the real 1 Hz snapshot
  // pusher can't overwrite our injected fake state mid-screenshot.
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

  await context.route(/\/api\/aircraft\/[0-9a-fA-F]+/, (route) => {
    const hex = route.request().url().split('/').pop();
    return route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify(mockTailRecord(hex, photoDataUri)),
    });
  });
  await context.route(/\/api\/blackspots(\?|$)/, (route) => {
    const url = new URL(route.request().url());
    const alt = Number(url.searchParams.get('target_alt_m') || 3048);
    return route.fulfill({
      status: 200, contentType: 'application/json',
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
  // Paint the injected snapshot + seed the count-history array so the
  // Stats dialog sparkline has something to draw on first open (the
  // sidebar normally accretes ~60 samples before we get there).
  await page.evaluate(async (snap) => {
    const u = await import('/static/update_loop.js');
    const sb = await import('/static/sidebar.js');
    u.update(snap);
    if (sb.countHistory.length === 0) {
      for (let i = 0; i < 60; i++) {
        const wobble = Math.round(Math.sin(i * 0.4) * 2 + Math.cos(i * 0.17) * 1);
        sb.countHistory.push(Math.max(1, snap.count + wobble));
      }
    }
  }, snapshot);
  await page.waitForSelector('.ac-item', { timeout: 5000 });
  await page.waitForTimeout(400);
}

async function capture(browser, device, base, photoDataUri, statsRoutes) {
  const context = await setupContext(browser, device, photoDataUri);
  // Install the /api/stats + /api/coverage + /api/heatmap +
  // /api/polar_heatmap mocks. Their payloads are rebuilt per capture
  // because mockStatsPayload bakes in the snapshot frame count.
  for (const [pattern, body] of statsRoutes) {
    await context.route(pattern, (route) => route.fulfill({
      status: 200, contentType: 'application/json', body,
    }));
  }

  const page = await context.newPage();
  const file = (name) => join(outDir, `${name}${device.suffix}.png`);

  console.log(`[${device.suffix || 'desktop'}] loading ${base}`);
  await page.goto(base, { waitUntil: 'domcontentloaded' });
  await page.evaluate(() => localStorage.clear());
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForSelector('#map');
  await page.waitForTimeout(500);

  const snapshot = buildFleet();
  await seedPage(page, snapshot);

  // Keep the injected snapshot alive by re-pushing it on an interval
  // with bumped timestamps so age readouts don't flip to "lost".
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

  // Detail panel — BAW283 / 406b31 is the one whose mocked tail record
  // describes a 787, keeping registration, type, and route coherent.
  await page.evaluate(async () => {
    const m = await import('/static/detail_panel.js');
    m.openDetailPanel('406b31');
  });
  await page.waitForSelector('#detail-panel.open');
  // Give the real photograph time to finish decoding.
  await page.waitForFunction(() => {
    const img = document.querySelector('#detail-panel .ac-photo img');
    return img && img.complete && img.naturalWidth > 0;
  }, null, { timeout: 5000 }).catch(() => {});
  await page.waitForTimeout(400);
  // Grow the viewport vertically so the whole panel fits in a single
  // screenshot. The panel has grown past one viewport-height since
  // the Enhanced Mode S section landed, especially on mobile where
  // the tiles stack in a narrower grid. Measure the panel's
  // scrollHeight and bump the viewport to match (+ a margin).
  const panelHeight = await page.evaluate(() => {
    const p = document.getElementById('detail-panel');
    return p ? p.scrollHeight : 0;
  });
  const targetHeight = Math.max(device.viewport.height, panelHeight + 40);
  if (targetHeight > device.viewport.height) {
    await page.setViewportSize({ width: device.viewport.width, height: targetHeight });
    await page.waitForTimeout(200);
  }
  console.log(`-> ${file('detail-panel')}`);
  await page.screenshot({ path: file('detail-panel') });
  // Restore the default viewport so the subsequent captures (stats,
  // compact, blackspots) use the nominal device height rather than
  // the grown one.
  if (targetHeight > device.viewport.height) {
    await page.setViewportSize(device.viewport);
    await page.waitForTimeout(200);
  }

  await page.locator('#detail-close').evaluate((el) => el.click());
  await page.waitForTimeout(400);

  // Stats dialog. The dialog's click handler is async (fetches
  // /api/stats + /api/coverage + /api/heatmap + /api/polar_heatmap
  // before showModal), so wait for the open state explicitly.
  await page.locator('#stats-btn').click();
  await page.waitForFunction(() =>
    document.getElementById('stats-dialog')?.open, null, { timeout: 5000 });
  await page.waitForTimeout(800);
  // Same viewport-grow trick as the detail panel: the Receiver-stats
  // dialog runs to a BEAST-feed footer that's well past 844 px on
  // mobile, so a default-viewport screenshot truncates the polar
  // heatmap and the BEAST-feed cards. Measure scrollHeight and grow.
  const statsHeight = await page.evaluate(() => {
    const d = document.getElementById('stats-dialog');
    return d ? d.scrollHeight : 0;
  });
  const statsTarget = Math.max(device.viewport.height, statsHeight + 60);
  if (statsTarget > device.viewport.height) {
    await page.setViewportSize({ width: device.viewport.width, height: statsTarget });
    await page.waitForTimeout(200);
  }
  console.log(`-> ${file('stats')}`);
  await page.screenshot({ path: file('stats') });
  if (statsTarget > device.viewport.height) {
    await page.setViewportSize(device.viewport);
    await page.waitForTimeout(200);
  }
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

  // Compact mode. Dispatch via the document-level keydown listener
  // rather than pressing on `body` — Playwright's mobile emulation
  // would otherwise tap the map and re-expand the layers control.
  const toggleCompact = () => page.evaluate(() =>
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'c' })));
  await toggleCompact();
  await page.waitForTimeout(500);
  await page.evaluate(async () => {
    const { state } = await import('/static/state.js');
    state.layersControl?.collapse?.();
  });
  console.log(`-> ${file('compact')}`);
  await page.screenshot({ path: file('compact') });
  await toggleCompact();
  await page.waitForTimeout(500);

  // Terrain blackspots. Enable the layer + wait for cells, then zoom
  // to a framing that shows both the dense cluster AND the tooltip
  // we pop on a representative cell.
  await page.evaluate(async (receiver) => {
    const bs = await import('/static/blackspots.js');
    const { state } = await import('/static/state.js');
    bs.setBlackspots(true);
    await new Promise((r) => setTimeout(r, 1200));
    state.map.setView([receiver.lat, receiver.lon], 8, { animate: false });
  }, { lat: LAT_REF, lon: LON_REF });
  await page.waitForTimeout(600);

  // Pop a tooltip on a cell with a readable delta so the reader can
  // see the "Needs antenna ≥ X m" copy rendered live. Pick the cell
  // whose required-antenna delta is in the orange band and that sits
  // near the receiver (so the tooltip itself stays on-screen).
  await page.evaluate((receiver) => {
    const { state } = window.__flightjar = window.__flightjar || {};
    // Grab state via the module import idempotently.
    return import('/static/state.js').then(({ state }) => {
      let best = null;
      let bestScore = Infinity;
      state.blackspotsLayer.eachLayer((l) => {
        if (typeof l.getBounds !== 'function') return;
        const b = l.getBounds();
        const c = b.getCenter();
        const distSq = (c.lat - receiver.lat) ** 2 + (c.lng - receiver.lon) ** 2;
        // Prefer cells that are close AND have a finite required height
        // (i.e. a tooltip that shows the full "Needs ≥ X m" copy).
        const tip = l.getTooltip?.();
        const html = tip?.getContent?.() || '';
        if (!html.includes('Needs antenna')) return;
        if (distSq < bestScore) { bestScore = distSq; best = l; }
      });
      if (best) best.openTooltip();
    });
  }, { lat: LAT_REF, lon: LON_REF });
  await page.waitForTimeout(500);
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

  // Fetch the real G-ZBKA photo from Wikimedia Commons once — all
  // subsequent captures reuse the base64-encoded copy.
  const photoDataUri = await fetchPhotoDataUri();

  // Stats payloads are the same across devices but need the snapshot's
  // frame count, so they're baked once here.
  const sampleSnapshot = buildFleet();
  const statsRoutes = [
    [/\/api\/stats\b/,         JSON.stringify(mockStatsPayload(sampleSnapshot))],
    [/\/api\/coverage\b/,      JSON.stringify(mockCoveragePayload())],
    [/\/api\/heatmap\b/,       JSON.stringify(mockHeatmapPayload())],
    [/\/api\/polar_heatmap\b/, JSON.stringify(mockPolarHeatmapPayload())],
  ];

  const browser = await chromium.launch();
  try {
    for (const device of DEVICES) {
      await capture(browser, device, finalBase, photoDataUri, statsRoutes);
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
