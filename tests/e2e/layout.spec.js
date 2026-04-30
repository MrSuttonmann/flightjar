// Layout + responsive-behaviour smoke, run against every Playwright
// project (see playwright.config.js — desktop + mobile). These tests
// exercise the features every new piece of UI has to survive: no
// document-level horizontal scroll, every <dialog> fits inside the
// visible viewport with its close button reachable, and the aircraft
// detail panel opens over the right region and closes cleanly.
//
// New dialogs get picked up automatically because the test
// auto-discovers `<dialog>` elements; a new launcher button wired up
// as `#{base}-btn` is also auto-discovered. No hard-coded lists.

import { expect, test } from '@playwright/test';

// Matches the real snapshot shape well enough to exercise the
// sidebar row + detail panel renderer. The registration/operator/etc
// fields are optional on the wire but present on everything we
// actually care to look at on screen.
const FAKE_AIRCRAFT = {
  icao: 'a12345',
  callsign: 'BAW123',
  registration: 'G-TEST',
  type_icao: 'A320',
  type_description: 'Airbus A320',
  operator: 'British Airways',
  category: 'A3',
  lat: 51.5,
  lon: -0.1,
  altitude: 35000,
  ground_speed: 450,
  track: 90,
  vertical_rate: 0,
  squawk: '2000',
  msg_count: 42,
  signal_peak: -15,
  last_seen: 0,
  last_position: 0,
  first_seen: 0,
  on_ground: false,
  emergency: false,
  origin: 'EGLL',
  destination: 'EDDF',
  flight_phase: 'cruise',
  airline: 'British Airways',
  airline_iata: 'BA',
  alliance: 'oneworld',
  country: 'United Kingdom',
  track_source: 'adsb',
};

const FAKE_SNAPSHOT = {
  now: Date.now() / 1000,
  lat_ref: 51.5,
  lon_ref: -0.1,
  aircraft: [FAKE_AIRCRAFT],
  airports: {
    EGLL: { name: 'London Heathrow', iata: 'LHR', lat: 51.4706, lon: -0.4619 },
    EDDF: { name: 'Frankfurt', iata: 'FRA', lat: 50.0333, lon: 8.5706 },
  },
};

test.beforeEach(async ({ page }) => {
  await page.goto('/');
  await page.evaluate(() => localStorage.clear());
  await page.reload();
  await page.waitForSelector('#map');
});

test('no horizontal scroll at this viewport', async ({ page }) => {
  // A 1px tolerance covers sub-pixel rounding on HiDPI emulation. Any
  // larger overflow is a layout regression — most commonly a hidden
  // desktop-positioned element that should have been repositioned in
  // a mobile media query.
  const probe = await page.evaluate(() => {
    const doc = document.documentElement;
    const vw = doc.clientWidth;
    const offenders = [];
    document.querySelectorAll('*').forEach((el) => {
      const r = el.getBoundingClientRect();
      if (r.right > vw + 1 && r.width > 0) {
        // Leaflet tile images legitimately extend past the map's
        // right edge — they're clipped by `.leaflet-container`'s own
        // overflow:hidden and don't contribute to doc scroll width.
        if (el.classList?.contains?.('leaflet-tile')) return;
        offenders.push({
          tag: el.tagName,
          id: el.id,
          cls: typeof el.className === 'string' ? el.className : '',
          right: Math.round(r.right),
        });
      }
    });
    return {
      hasHscroll: doc.scrollWidth > doc.clientWidth,
      docScrollW: doc.scrollWidth,
      docClientW: doc.clientWidth,
      offenders: offenders.slice(0, 10),
    };
  });
  expect(probe, JSON.stringify(probe)).toMatchObject({ hasHscroll: false });
  expect(probe.offenders, JSON.stringify(probe.offenders)).toEqual([]);
});

test('every dialog fits inside the viewport and the close button is reachable', async ({ page }) => {
  // Auto-discover dialogs so new modals inherit this coverage without
  // editing this spec. If a dialog grows larger than the viewport, it
  // must be internally scrollable (scrollH > clientH) and its close
  // button must be visible without scrolling — the sticky close-form
  // rule in dialogs.css enforces that.
  const dialogIds = await page.$$eval('dialog[id]', (ds) => ds.map((d) => d.id));
  expect(dialogIds.length, 'expected at least one <dialog>').toBeGreaterThan(0);

  for (const id of dialogIds) {
    const base = id.replace(/-dialog$/, '');
    const btn = page.locator(`#${base}-btn`);
    const hasLauncher = (await btn.count()) > 0 && (await btn.isVisible());
    if (hasLauncher) {
      await btn.click();
    } else {
      // Dialogs opened by a Leaflet control (e.g. map-key) — drive
      // them programmatically so layout coverage still applies.
      await page.evaluate((id) => document.getElementById(id).showModal(), id);
    }
    // Stats dialog's click handler is async (awaits /api/stats before
    // calling showModal), so wait for the open state to settle rather
    // than assuming the click was synchronous.
    await page.waitForFunction(
      (id) => document.getElementById(id).open,
      id,
      { timeout: 5000 },
    );

    const report = await page.evaluate((id) => {
      const d = document.getElementById(id);
      const closeBtn = d.querySelector('.about-close, button[value="close"]');
      const cr = closeBtn?.getBoundingClientRect();
      const r = d.getBoundingClientRect();
      return {
        id,
        open: d.open,
        rectTop: r.top,
        rectBottom: r.bottom,
        rectRight: r.right,
        vh: window.innerHeight,
        vw: window.innerWidth,
        scrollsInternally: d.scrollHeight > d.clientHeight + 1,
        fits: r.bottom <= window.innerHeight + 1 && r.right <= window.innerWidth + 1,
        closeBottom: cr?.bottom,
        closeVisible: cr != null && cr.bottom <= window.innerHeight && cr.top >= 0,
      };
    }, id);
    expect(report.open, `${id} didn't open`).toBe(true);
    expect(report.fits, `${id} overflows viewport: ${JSON.stringify(report)}`).toBe(true);
    expect(report.closeVisible, `${id} close button off-screen: ${JSON.stringify(report)}`).toBe(true);

    // Close and settle for the next iteration.
    await page.keyboard.press('Escape');
    await page.waitForTimeout(150);
  }
});

test('aircraft detail panel opens, fits the viewport, and closes', async ({ page }) => {
  // Inject a fake snapshot + select the aircraft in one atomic step
  // so the real 1 Hz WebSocket tick can't race us and evict the fake
  // entry before we mark it as selected. Resolve both imports BEFORE
  // touching state — otherwise the await between update() and
  // openDetailPanel() yields to the event loop long enough for a WS
  // snapshot to land and wipe out the injected aircraft, which CI hit
  // intermittently as `opened === false`.
  const openResult = await page.evaluate(async (snap) => {
    const [uMod, m] = await Promise.all([
      import('/static/update_loop.js'),
      import('/static/detail_panel.js'),
    ]);
    uMod.update(snap);
    m.openDetailPanel('a12345');
    return {
      opened: document.getElementById('detail-panel').classList.contains('open'),
    };
  }, FAKE_SNAPSHOT);
  expect(openResult.opened).toBe(true);
  // The open transition is 0.2s — wait for it to settle before
  // measuring, otherwise we'd read an intermediate transform position.
  await page.waitForTimeout(350);

  const metrics = await page.evaluate(() => {
    const p = document.getElementById('detail-panel');
    const closeBtn = document.getElementById('detail-close');
    const r = p.getBoundingClientRect();
    const cr = closeBtn.getBoundingClientRect();
    return {
      rectRight: r.right,
      rectBottom: r.bottom,
      rectTop: r.top,
      rectLeft: r.left,
      vw: window.innerWidth,
      vh: window.innerHeight,
      fits: r.right <= window.innerWidth + 1 && r.bottom <= window.innerHeight + 1,
      scrollsInternally: p.scrollHeight > p.clientHeight,
      closeVisible: cr.top >= 0 && cr.bottom <= window.innerHeight
        && cr.left >= 0 && cr.right <= window.innerWidth,
    };
  });
  expect(metrics.fits, JSON.stringify(metrics)).toBe(true);
  expect(metrics.closeVisible, JSON.stringify(metrics)).toBe(true);

  // The close button lives in a sticky header; on mobile, Playwright
  // can flag it "not visible" during scroll even though a user can
  // still tap it. Route through el.click() to bypass that check while
  // still exercising the real onclick handler.
  await page.locator('#detail-close').evaluate((el) => el.click());
  await page.waitForTimeout(300);
  const openedAfter = await page.locator('#detail-panel').evaluate((el) => el.classList.contains('open'));
  expect(openedAfter).toBe(false);
});

test('no console errors during a typical UI session', async ({ page }) => {
  // Layout regressions sometimes surface only as console noise — e.g.
  // an invalid attribute on an SVG generated at render time. Catching
  // them here fails CI on what would otherwise be silent bugs.
  const errors = [];
  page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));
  page.on('console', (msg) => {
    if (msg.type() === 'error') errors.push(`console.error: ${msg.text()}`);
  });
  await page.goto('/');
  await page.waitForSelector('#map');
  // Exercise the same surfaces the other layout tests use so the
  // renderers that generate dynamic SVGs / markup are covered.
  const dialogIds = await page.$$eval('dialog[id]', (ds) => ds.map((d) => d.id));
  for (const id of dialogIds) {
    await page.evaluate((id) => document.getElementById(id).showModal(), id);
    await page.waitForTimeout(100);
    await page.evaluate((id) => document.getElementById(id).close(), id);
  }
  await page.evaluate(async (snap) => {
    const [uMod, m] = await Promise.all([
      import('/static/update_loop.js'),
      import('/static/detail_panel.js'),
    ]);
    uMod.update(snap);
    m.openDetailPanel('a12345');
  }, FAKE_SNAPSHOT);
  await page.waitForTimeout(400);
  expect(errors, errors.join('\n')).toEqual([]);
});

test('detail panel renders Enhanced Mode S section when comm_b is present', async ({ page }) => {
  // Inject a fake snapshot with a fully populated comm_b block and
  // assert that the .panel-met section is visible with the expected
  // metric tiles. Guards against schema-drift (e.g. renaming a field
  // like `mach` → `mach_number` on the backend) silently hiding the
  // entire new panel.
  const snapWithMet = {
    ...FAKE_SNAPSHOT,
    aircraft: [{
      ...FAKE_AIRCRAFT,
      comm_b: {
        selected_altitude_mcp_ft: 36000,
        qnh_hpa: 1013.2,
        wind_speed_kt: 72,
        wind_direction_deg: 258,
        static_air_temperature_c: -56.3,
        total_air_temperature_c: -26.1,
        static_pressure_hpa: 214,
        humidity_pct: 8.3,
        turbulence: 1,
        mach: 0.81,
        indicated_airspeed_kt: 287,
        true_airspeed_kt: 471,
        groundspeed_kt: 465,
        magnetic_heading_deg: 92,
        true_track_deg: 95,
        roll_deg: -1.4,
        track_rate_deg_per_s: 0.12,
        baro_vertical_rate_fpm: 0,
        inertial_vertical_rate_fpm: -32,
        bds40_at: Date.now() / 1000,
        bds44_at: Date.now() / 1000,
        bds50_at: Date.now() / 1000,
        bds60_at: Date.now() / 1000,
      },
    }],
  };
  await page.evaluate(async (snap) => {
    const [uMod, m] = await Promise.all([
      import('/static/update_loop.js'),
      import('/static/detail_panel.js'),
    ]);
    uMod.update(snap);
    m.openDetailPanel('a12345');
  }, snapWithMet);
  await page.waitForTimeout(300);

  const visible = await page.evaluate(() => {
    const sec = document.querySelector('.panel-met');
    if (!sec || sec.hidden) return { sectionVisible: false };
    const tiles = Array.from(sec.querySelectorAll('.panel-met-grid .metric'))
      .filter((el) => !el.hidden)
      .map((el) => ({
        label: el.querySelector('.label')?.textContent,
        val: el.querySelector('.val')?.textContent,
      }));
    return { sectionVisible: true, tiles };
  });
  expect(visible.sectionVisible).toBe(true);
  // Sanity: at least the four headline fields the user asked for
  // (wind, OAT, QNH, Mach) must all render. The label text now
  // includes the trailing help-icon text node (empty from the SVG),
  // so `includes` rather than an exact match.
  const labels = visible.tiles.map((t) => (t.label || '').trim());
  expect(labels.some((l) => l.startsWith('Wind'))).toBe(true);
  expect(labels.some((l) => l.startsWith('OAT / SAT'))).toBe(true);
  expect(labels.some((l) => l.startsWith('QNH'))).toBe(true);
  expect(labels.some((l) => l.startsWith('Mach'))).toBe(true);
});

test('mlat-tagged aircraft draw a dashed marker stroke', async ({ page }) => {
  // Inject an MLAT plane WITHOUT opening the detail panel: selection
  // overrides the MLAT stroke (white outline wins over amber dashes),
  // so this test specifically covers the unselected-on-the-map view.
  const mlatSnap = {
    ...FAKE_SNAPSHOT,
    aircraft: [{ ...FAKE_AIRCRAFT, position_source: 'mlat' }],
  };
  await page.evaluate(async (snap) => {
    const uMod = await import('/static/update_loop.js');
    uMod.update(snap);
  }, mlatSnap);

  // Marker rendering happens on the next animation frame after the
  // update, and tile-loading on cold start can push that out a few
  // hundred ms. Use auto-retrying assertions instead of a fixed
  // sleep so the test waits exactly as long as it has to.
  const markerSvg = page.locator('.leaflet-marker-icon svg path').first();
  await expect(markerSvg).toHaveAttribute('stroke-dasharray', /.+/);
  await expect(markerSvg).toHaveAttribute('stroke', '#facc15');
});

// One assertion harness, parameterised across the three relayed source
// types — keeps each branch covered in-browser without spawning three
// near-identical tests.
async function assertPositionChip(page, source, expectedText) {
  const snap = {
    ...FAKE_SNAPSHOT,
    aircraft: [{ ...FAKE_AIRCRAFT, position_source: source }],
  };
  await page.evaluate(async (snap) => {
    const [uMod, m] = await Promise.all([
      import('/static/update_loop.js'),
      import('/static/detail_panel.js'),
    ]);
    uMod.update(snap);
    m.openDetailPanel('a12345');
  }, snap);
  await page.waitForTimeout(300);
  const probe = await page.evaluate(() => {
    const chip = document.querySelector('.pop-pos-source');
    return {
      exists: !!chip,
      hidden: chip?.hidden,
      text: chip?.textContent,
      title: chip?.title,
    };
  });
  expect(probe.exists, JSON.stringify(probe)).toBe(true);
  expect(probe.hidden, JSON.stringify(probe)).toBe(false);
  expect(probe.text).toBe(expectedText);
  expect(probe.title?.length, JSON.stringify(probe)).toBeGreaterThan(0);
}

test('mlat-tagged aircraft show the MLAT chip in the detail panel', async ({ page }) => {
  await assertPositionChip(page, 'mlat', 'MLAT');
});

test('tisb-tagged aircraft show the TIS-B chip in the detail panel', async ({ page }) => {
  await assertPositionChip(page, 'tisb', 'TIS-B');
});

test('adsr-tagged aircraft show the ADS-R chip in the detail panel', async ({ page }) => {
  await assertPositionChip(page, 'adsr', 'ADS-R');
});

test('adsb-tagged aircraft hide the position-source chip and draw a solid marker', async ({ page }) => {
  const adsbSnap = {
    ...FAKE_SNAPSHOT,
    aircraft: [{ ...FAKE_AIRCRAFT, position_source: 'adsb' }],
  };
  await page.evaluate(async (snap) => {
    const [uMod, m] = await Promise.all([
      import('/static/update_loop.js'),
      import('/static/detail_panel.js'),
    ]);
    uMod.update(snap);
    m.openDetailPanel('a12345');
  }, adsbSnap);
  await page.waitForTimeout(300);

  const probe = await page.evaluate(() => {
    const chip = document.querySelector('.pop-pos-source');
    const markerSvg = document.querySelector('.leaflet-marker-icon svg path');
    return {
      chipHidden: chip?.hidden,
      markerHasDash: markerSvg?.hasAttribute('stroke-dasharray'),
    };
  });
  expect(probe.chipHidden, JSON.stringify(probe)).toBe(true);
  expect(probe.markerHasDash, JSON.stringify(probe)).toBe(false);
});

test('detail panel metric labels carry help icons with explanations', async ({ page }) => {
  // Open the panel for a plain aircraft (no comm_b needed — every
  // .panel-meta tile should have a help icon regardless of the data).
  await page.evaluate(async (snap) => {
    const [uMod, m] = await Promise.all([
      import('/static/update_loop.js'),
      import('/static/detail_panel.js'),
    ]);
    uMod.update(snap);
    m.openDetailPanel('a12345');
  }, FAKE_SNAPSHOT);
  await page.waitForTimeout(250);

  const audit = await page.evaluate(() => {
    const icons = Array.from(document.querySelectorAll('#detail-panel .help-icon'));
    return {
      count: icons.length,
      sampleHelp: icons.find((el) => el.closest('.metric')?.querySelector('.label')?.textContent?.includes('Altitude'))
        ?.dataset?.help ?? null,
    };
  });
  // Every metric in .panel-meta has a help entry registered, so we
  // expect at least 9 icons (alt/speed/heading/vrate/squawk/distance/
  // lat/lon/flown).
  expect(audit.count).toBeGreaterThanOrEqual(9);
  // The altitude tile's help text should mention "altitude" — loose
  // substring check so future prose edits don't break the test.
  expect(audit.sampleHelp?.toLowerCase()).toContain('altitude');
});
