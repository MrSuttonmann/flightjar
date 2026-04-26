// Verifies that map layers whose backing config isn't set up render in
// the layers control as disabled rows with a why/how info popover —
// instead of being silently hidden.
//
// The Playwright harness leaves OPENAIP_API_KEY unset and pins
// BLACKSPOTS_ENABLED=0, so the OpenAIP-backed layers and the terrain
// blackspots layer are deterministically disabled. The VFRMap chart
// cycle is auto-discovered from the network on startup, so its
// enabled/disabled state isn't deterministic and we don't assert on
// the IFR layers specifically — only the shape of the response.

import { expect, test } from '@playwright/test';

test.beforeEach(async ({ page }) => {
  await page.goto('/');
  await page.evaluate(() => localStorage.clear());
  await page.reload();
});

test('/api/map_config returns a layer_status entry per gate', async ({ request }) => {
  const r = await request.get('/api/map_config');
  expect(r.ok()).toBeTruthy();
  const body = await r.json();
  expect(body).toHaveProperty('layer_status');
  for (const gate of ['openaip', 'vfrmap', 'blackspots']) {
    const entry = body.layer_status[gate];
    expect(typeof entry.enabled).toBe('boolean');
    if (!entry.enabled) {
      expect(typeof entry.reason).toBe('string');
      expect(entry.reason.length).toBeGreaterThan(20);
    }
  }
  // OPENAIP_API_KEY isn't set in the Playwright harness, so the
  // openaip gate is reliably closed and its reason should be present
  // and actionable.
  expect(body.layer_status.openaip.enabled).toBe(false);
  expect(body.layer_status.openaip.reason).toMatch(/OPENAIP_API_KEY/);
});

test('OpenAIP-gated rows render disabled with an info button', async ({ page }) => {
  await page.locator('.leaflet-control-layers').hover();
  // OpenAIP basemap is a base-layer entry; airspaces / obstacles /
  // reporting points are overlays. All of them gate on OPENAIP_API_KEY.
  const labels = [
    'Aeronautical (OpenAIP)',
    'Airspaces',
    'Obstacles',
    'Reporting points',
  ];
  for (const text of labels) {
    const row = page.locator('.leaflet-control-layers label', { hasText: text }).first();
    await expect(row).toHaveClass(/overlay-disabled/);
    await expect(row.locator('input')).toBeDisabled();
    await expect(row.locator('.overlay-info-btn')).toBeVisible();
  }
});

test('Terrain blackspots row renders disabled with an info button', async ({ page }) => {
  await page.locator('.leaflet-control-layers').hover();
  const row = page.locator('.leaflet-control-layers label', {
    hasText: 'Terrain blackspots',
  }).first();
  await expect(row).toHaveClass(/overlay-disabled/);
  await expect(row.locator('input')).toBeDisabled();
});

test('clicking the info button opens an actionable popover', async ({ page }) => {
  await page.locator('.leaflet-control-layers').hover();
  const row = page.locator('.leaflet-control-layers label', {
    hasText: 'Aeronautical (OpenAIP)',
  }).first();
  await row.locator('.overlay-info-btn').click();
  const popover = page.locator('.overlay-info-popover');
  await expect(popover).toBeVisible();
  await expect(popover).toContainText(/OPENAIP_API_KEY/);
  // Pressing Escape closes it.
  await page.keyboard.press('Escape');
  await expect(popover).toBeHidden();
});

test('hovering the info button opens the popover', async ({ page }) => {
  await page.locator('.leaflet-control-layers').hover();
  const btn = page.locator('.leaflet-control-layers label', {
    hasText: 'Aeronautical (OpenAIP)',
  }).first().locator('.overlay-info-btn');
  await btn.hover();
  const popover = page.locator('.overlay-info-popover');
  await expect(popover).toBeVisible();
  await expect(popover).toContainText(/OPENAIP_API_KEY/);
  // Moving the cursor away from the icon-and-popover region closes it.
  await page.locator('#header h1').hover();
  await expect(popover).toBeHidden();
});

test('blackspots info popover names the relevant env var', async ({ page }) => {
  await page.locator('.leaflet-control-layers').hover();
  const row = page.locator('.leaflet-control-layers label', {
    hasText: 'Terrain blackspots',
  }).first();
  await row.locator('.overlay-info-btn').click();
  const popover = page.locator('.overlay-info-popover');
  await expect(popover).toBeVisible();
  // Harness sets BLACKSPOTS_ENABLED=0, so the reason names that var.
  // (If the harness changes to leave receiver coords unset instead,
  // the reason would name LAT_REF — accept either.)
  await expect(popover).toContainText(/BLACKSPOTS_ENABLED|LAT_REF/);
});
