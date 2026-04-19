import { test } from 'node:test';
import assert from 'node:assert/strict';

import { ageOf, compassIcon, fmt } from '../../app/static/format.js';

test('fmt returns em-dash for null / undefined / NaN', () => {
  assert.equal(fmt(null), '—');
  assert.equal(fmt(undefined), '—');
  assert.equal(fmt(NaN), '—');
});

test('fmt respects suffix and digits', () => {
  assert.equal(fmt(42), '42');
  assert.equal(fmt(42.7, ' ft'), '43 ft');
  assert.equal(fmt(42.765, '', 2), '42.77');
});

test('ageOf returns null when inputs are missing', () => {
  assert.equal(ageOf({}, null), null);
  assert.equal(ageOf({}, 100), null);          // no last_seen
  assert.equal(ageOf({ last_seen: 50 }, null), null);
});

test('ageOf computes non-negative seconds since last_seen', () => {
  assert.equal(ageOf({ last_seen: 50 }, 100), 50);
  assert.equal(ageOf({ last_seen: 100 }, 50), 0);  // clamped to 0
});

test('compassIcon returns empty for null / NaN', () => {
  assert.equal(compassIcon(null), '');
  assert.equal(compassIcon(undefined), '');
  assert.equal(compassIcon(NaN), '');
});

test('compassIcon embeds the rotation angle', () => {
  const svg = compassIcon(42);
  assert.match(svg, /rotate\(42deg\)/);
  assert.match(svg, /<svg[^>]*>/);
  assert.match(svg, /currentColor/);
});
