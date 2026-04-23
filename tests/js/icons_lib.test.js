import { test } from 'node:test';
import assert from 'node:assert/strict';

import { LUCIDE_ICON_PATHS, lucide } from '../../app/static/icons_lib.js';

test('every Lucide path entry is a non-empty string', () => {
  for (const [name, body] of Object.entries(LUCIDE_ICON_PATHS)) {
    assert.ok(typeof body === 'string' && body.length > 0,
      `expected path body for ${name}`);
  }
});

test('lucide() wraps a known icon in an SVG with currentColor stroke', () => {
  const html = lucide('star');
  assert.match(html, /<svg /);
  assert.match(html, /viewBox="0 0 24 24"/);
  assert.match(html, /width="16" height="16"/);
  assert.match(html, /stroke="currentColor"/);
  assert.match(html, /aria-hidden="true"/);
});

test('lucide() honours size, strokeWidth, className overrides', () => {
  const html = lucide('x', { size: 22, strokeWidth: 2.5, className: 'close-btn' });
  assert.match(html, /class="close-btn"/);
  assert.match(html, /width="22" height="22"/);
  assert.match(html, /stroke-width="2.5"/);
});

test('lucide() returns an empty string for an unknown icon name', () => {
  assert.equal(lucide('no-such-icon'), '');
});

test('covers the weather icons that profile.js picks via weatherIconKey', () => {
  // Defence against accidentally removing a weather path the classifier
  // still points at.
  for (const name of [
    'sun', 'cloud-sun', 'cloud', 'cloud-fog',
    'cloud-rain', 'cloud-snow', 'cloud-hail', 'cloud-lightning',
  ]) {
    assert.ok(LUCIDE_ICON_PATHS[name], `expected weather icon ${name}`);
  }
});
