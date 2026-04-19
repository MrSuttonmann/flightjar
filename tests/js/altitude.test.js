import { test } from 'node:test';
import assert from 'node:assert/strict';

import { ALT_STOPS, altColor } from '../../app/static/altitude.js';

test('altColor returns a neutral tone for null', () => {
  assert.equal(altColor(null), '#8a94a3');
});

test('altColor at stop altitudes matches the palette exactly', () => {
  for (const [alt, [r, g, b]] of ALT_STOPS) {
    assert.equal(altColor(alt), `rgb(${r},${g},${b})`);
  }
});

test('altColor clamps below the lowest stop to the first colour', () => {
  assert.equal(altColor(-1000), altColor(ALT_STOPS[0][0]));
});

test('altColor clamps above the highest stop to the last colour', () => {
  const top = ALT_STOPS[ALT_STOPS.length - 1];
  assert.equal(altColor(999999), altColor(top[0]));
});

test('altColor interpolates linearly between adjacent stops', () => {
  // Midpoint between the first two stops should be the channel-wise midpoint.
  const [a0, c0] = ALT_STOPS[0];
  const [a1, c1] = ALT_STOPS[1];
  const mid = (a0 + a1) / 2;
  const colour = altColor(mid);
  const m = (i) => Math.round((c0[i] + c1[i]) / 2);
  assert.equal(colour, `rgb(${m(0)},${m(1)},${m(2)})`);
});
