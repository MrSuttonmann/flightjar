// Capture README screenshots against a running Flightjar instance.
//
//   node scripts/take_screenshots.js [base-url]
//
// Defaults to http://localhost:8080. Writes desktop PNGs (main.png,
// detail-panel.png, stats.png, compact.png) plus mobile variants with
// a `-mobile` suffix into docs/screenshots/. Waits for real aircraft
// data to land before capturing so the shots reflect what a live user
// actually sees (altitude-coloured trails, populated sidebar, a
// detail panel with route + METAR when available).

import { mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const __dirname = dirname(fileURLToPath(import.meta.url));
const outDir = join(__dirname, '..', 'docs', 'screenshots');
mkdirSync(outDir, { recursive: true });

const baseUrl = process.argv[2] || 'http://localhost:8080';

// Desktop + a modern-phone mobile viewport. The mobile layout kicks
// in below 600 px (see the @media rule in app.css); 390x844 matches
// an iPhone 13/14/15 so we capture the list-on-top / map-below
// arrangement plus the fullscreen detail panel that overlays on tap.
const DEVICES = [
  { suffix: '', viewport: { width: 1440, height: 900 }, isMobile: false },
  { suffix: '-mobile', viewport: { width: 390, height: 844 }, isMobile: true },
];

// deviceScaleFactor=1 keeps the PNGs under ~1 MB each. The README
// shrinks each shot into a table cell anyway, so retina scaling
// only adds bytes without making a visible difference.
const DSF = 1;

async function capture(browser, device) {
  const context = await browser.newContext({
    viewport: device.viewport,
    deviceScaleFactor: DSF,
    isMobile: device.isMobile,
    hasTouch: device.isMobile,
  });
  const page = await context.newPage();
  const file = (name) => join(outDir, `${name}${device.suffix}.png`);

  console.log(`[${device.suffix || 'desktop'}] loading ${baseUrl}`);
  await page.goto(baseUrl, { waitUntil: 'networkidle' });

  // Wait for a handful of aircraft so the sidebar and map aren't empty.
  await page.waitForFunction(
    () => document.querySelectorAll('.ac-item').length >= 5,
    { timeout: 30000 },
  );
  // Let a few snapshots flow through so trails draw and values stabilise.
  await page.waitForTimeout(3000);

  console.log(`-> ${file('main')}`);
  await page.screenshot({ path: file('main') });

  // Pick the first row that already has a route ticket — that one will
  // yield the most-populated detail panel (photo + route + progress +
  // maybe METAR once the async fetches settle).
  const rowIdx = await page.$$eval('.ac-item', rows =>
    Math.max(0, rows.findIndex(r => r.querySelector('.route-row'))),
  );
  await page.locator('.ac-item').nth(rowIdx).click();
  await page.waitForSelector('#detail-panel.open', { timeout: 5000 });
  // Photo, adsbdb record, and METAR all arrive async — give them room.
  await page.waitForTimeout(8000);

  console.log(`-> ${file('detail-panel')}`);
  await page.screenshot({ path: file('detail-panel') });

  await page.locator('#detail-close').click();
  await page.waitForTimeout(400);

  // Stats dialog — heatmap + coverage numbers make this a good overview shot.
  await page.locator('#stats-btn').click();
  await page.waitForSelector('#stats-dialog[open]', { timeout: 3000 });
  await page.waitForTimeout(1500);
  console.log(`-> ${file('stats')}`);
  await page.screenshot({ path: file('stats') });
  // Native <dialog> — use the close button inside its form.
  await page.locator('#stats-dialog .about-close').click();
  await page.waitForTimeout(400);

  // Compact mode — desktop: sidebar hides, toggle pill sits at the
  // edge. Mobile: the top sidebar strip tucks up, the grab-handle
  // pill anchors to the top of the map. Keyboard shortcut C toggles
  // on both (the keydown handler is global).
  await page.locator('body').press('c');
  await page.waitForTimeout(600);
  console.log(`-> ${file('compact')}`);
  await page.screenshot({ path: file('compact') });

  await context.close();
}

const browser = await chromium.launch();
try {
  for (const device of DEVICES) {
    await capture(browser, device);
  }
} finally {
  await browser.close();
}
console.log('done →', outDir);
