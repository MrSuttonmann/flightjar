// Smoke tests exercising the stable UI paths that don't require live
// ADS-B data. The backend runs with BEAST_HOST pointed at a
// non-existent host, so the status sits in "Disconnected, retrying…"
// throughout — which is exactly the state we want to assert against
// for these paths (dialog open/close, unit switching, compact mode,
// keyboard shortcuts, clock ticking).

import { expect, test } from '@playwright/test';

test.beforeEach(async ({ page }) => {
  // Clear localStorage so each test starts with the app's default
  // preferences (units = nautical, compact off, etc.). Navigating to
  // "/" first is required before we can touch localStorage.
  await page.goto('/');
  await page.evaluate(() => localStorage.clear());
  await page.reload();
});

test('loads with Flightjar title', async ({ page }) => {
  await expect(page).toHaveTitle(/Flightjar/);
  await expect(page.locator('#header h1')).toContainText('Flightjar');
});

test('status line reflects the live WebSocket state', async ({ page }) => {
  // HTML ships with "Connecting…" as the initial text. Once the WS
  // connects and the first snapshot arrives, the line is rewritten to
  // "<N> aircraft · <N> positioned". Not being stuck on the literal
  // "Connecting…" string is our proof that the WS wired up at all.
  const status = page.locator('#status-text');
  await expect(status).not.toHaveText('Connecting…');
});

test('footer clock renders HH:MM:SS and ticks', async ({ page }) => {
  const clock = page.locator('#footer-clock');
  await expect(clock).toHaveText(/^\d{2}:\d{2}:\d{2}$/);
  const first = await clock.textContent();
  // Wait a bit past a second; clock should advance. Poll to avoid
  // exact-timing flakes.
  await expect.poll(async () => (await clock.textContent()) !== first, {
    timeout: 3000,
  }).toBe(true);
});

test('About dialog opens and closes', async ({ page }) => {
  const dialog = page.locator('#about-dialog');
  await expect(dialog).toBeHidden();
  await page.locator('#about-btn').click();
  await expect(dialog).toBeVisible();
  await expect(dialog.locator('h2')).toContainText('Flightjar');
  // Close via the × button
  await dialog.locator('.about-close').click();
  await expect(dialog).toBeHidden();
});

test('About dialog shows a version badge after opening', async ({ page }) => {
  await page.locator('#about-btn').click();
  const badge = page.locator('#about-version');
  // Either "dev" for the local dev server, or a short git SHA in CI.
  await expect(badge).toBeVisible();
  await expect(badge).toHaveText(/^(dev|[0-9a-f]{7,})$/);
});

test('Stats dialog opens and populates receiver stats', async ({ page }) => {
  const dialog = page.locator('#stats-dialog');
  await expect(dialog).toBeHidden();
  await page.locator('#stats-btn').click();
  await expect(dialog).toBeVisible();
  // Uptime and frame-count cards fill in on open via /api/stats.
  await expect(page.locator('#stats-uptime')).not.toHaveText('—');
  await expect(page.locator('#stats-frames')).not.toHaveText('—');
  // BEAST source string mirrors BEAST_HOST:BEAST_PORT from env.
  await expect(page.locator('#stats-beast-target')).toContainText(
    'nonexistent.invalid:1',
  );
});

test('Unit switcher toggles between metric, imperial, nautical', async ({ page }) => {
  const metric = page.locator('#unit-switch .unit-btn[data-unit="metric"]');
  const imperial = page.locator('#unit-switch .unit-btn[data-unit="imperial"]');
  const nautical = page.locator('#unit-switch .unit-btn[data-unit="nautical"]');
  // Default is nautical.
  await expect(nautical).toHaveClass(/active/);
  await metric.click();
  await expect(metric).toHaveClass(/active/);
  await expect(nautical).not.toHaveClass(/active/);
  await imperial.click();
  await expect(imperial).toHaveClass(/active/);
});

test('Compact mode hides the sidebar via the chevron toggle', async ({ page }) => {
  const body = page.locator('body');
  const sidebar = page.locator('#sidebar');
  await expect(body).not.toHaveClass(/compact-mode/);
  await expect(sidebar).toBeVisible();
  await page.locator('#sidebar-toggle').click();
  await expect(body).toHaveClass(/compact-mode/);
  await expect(sidebar).toBeHidden();
  // Toggle back.
  await page.locator('#sidebar-toggle').click();
  await expect(body).not.toHaveClass(/compact-mode/);
  await expect(sidebar).toBeVisible();
});

test('Keyboard shortcut C toggles compact mode', async ({ page }) => {
  const body = page.locator('body');
  await expect(body).not.toHaveClass(/compact-mode/);
  // Focus the body so the global keydown handler fires rather than
  // being swallowed by some input field.
  await page.locator('body').press('c');
  await expect(body).toHaveClass(/compact-mode/);
});

test('Keyboard shortcut U cycles unit systems', async ({ page }) => {
  // Default: nautical. `U` should advance to metric (order is
  // metric → imperial → nautical → metric …).
  await expect(
    page.locator('#unit-switch .unit-btn[data-unit="nautical"]'),
  ).toHaveClass(/active/);
  await page.locator('body').press('u');
  await expect(
    page.locator('#unit-switch .unit-btn[data-unit="metric"]'),
  ).toHaveClass(/active/);
});
