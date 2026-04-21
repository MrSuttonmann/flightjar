// One-off smoke test for the client-side easter eggs against a running
// Flightjar. Exits non-zero if anything we can validate here fails.
//
//   node scripts/verify_eggs.js [base-url]
//
// Covers: Konami code -> .egg-party + toast, logo x 7 -> session-stats
// card, barrel-roll typed in #search -> #map.egg-barrel-roll.
// Skipped (need fixture data or a specific calendar date): notable
// tails, military chip, range-record toast, go-around detector,
// Wright Brothers note, Christmas snow.

import { chromium } from 'playwright';

const baseUrl = process.argv[2] || 'http://localhost:8080';
const results = [];

function record(name, passed, detail = '') {
  results.push({ name, passed, detail });
  const mark = passed ? '✓' : '✗';
  const line = detail ? `${name} — ${detail}` : name;
  console.log(`${mark} ${line}`);
}

const browser = await chromium.launch();
const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
const page = await ctx.newPage();
try {
  console.log(`loading ${baseUrl}`);
  await page.goto(baseUrl, { waitUntil: 'networkidle' });
  // Give the WebSocket a moment to connect so nothing is mid-init.
  await page.waitForTimeout(1000);

  // --- Konami code ---
  // Click the map once so focus isn't in any input (which would short-
  // circuit the handler). The handler explicitly bails when target is
  // INPUT/TEXTAREA.
  await page.locator('#map').click({ position: { x: 300, y: 300 } });
  const keys = ['ArrowUp', 'ArrowUp', 'ArrowDown', 'ArrowDown',
                'ArrowLeft', 'ArrowRight', 'ArrowLeft', 'ArrowRight', 'b', 'a'];
  for (const k of keys) await page.keyboard.press(k);
  // Body class lands immediately on match, toast animates in over ~220ms.
  await page.waitForSelector('body.egg-party', { timeout: 1000 });
  const partyToast = await page.locator('.toast-egg').first().innerText()
    .catch(() => '');
  record('Konami code triggers party mode',
         partyToast.toLowerCase().includes('cheat code'),
         partyToast ? `toast: "${partyToast}"` : 'no toast found');
  // Wait for the party class to clear so it doesn't leak into later checks.
  await page.waitForFunction(() => !document.body.classList.contains('egg-party'),
                             { timeout: 6000 });

  // --- Logo × 7 clicks ---
  const logo = page.locator('#header h1');
  for (let i = 0; i < 7; i++) await logo.click();
  await page.waitForSelector('#egg-session-stats', { timeout: 2000 });
  const cardText = await page.locator('#egg-session-stats').innerText();
  const hasStats = /Planes seen|Messages|Max range/i.test(cardText);
  record('Logo click x 7 opens session stats', hasStats,
         hasStats ? 'card present with expected rows' : 'card text unexpected');
  // Dismiss the card so it doesn't interfere with the next step.
  await page.locator('.egg-session-close').click();
  await page.waitForTimeout(150);

  // --- Barrel roll ---
  // The handler listens on input events — fill() synthesises one.
  await page.locator('#search').fill('barrel roll');
  let rolled = false;
  try {
    await page.waitForFunction(
      () => document.getElementById('map').classList.contains('egg-barrel-roll'),
      { timeout: 1500 },
    );
    rolled = true;
  } catch (_) { /* timed out */ }
  record('Typing "barrel roll" spins the map', rolled,
         rolled ? 'map got .egg-barrel-roll' : 'map never animated');
  // The JS side clears #search after triggering; confirm that too.
  const searchCleared = await page.locator('#search').inputValue();
  record('Barrel-roll trigger clears the search box',
         searchCleared === '',
         `search value: "${searchCleared}"`);

  // --- Summary ---
  const failed = results.filter((r) => !r.passed);
  console.log(`\n${results.length - failed.length} / ${results.length} passed`);
  if (failed.length) process.exitCode = 1;
} finally {
  await browser.close();
}
