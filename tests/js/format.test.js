import { test } from 'node:test';
import assert from 'node:assert/strict';

import { ageOf, compassIcon, escapeHtml, flagIcon, fmt } from '../../app/static/format.js';

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

test('escapeHtml neutralises the five HTML special characters', () => {
  assert.equal(
    escapeHtml(`<img src=x onerror='alert("xss")'>&foo;`),
    '&lt;img src=x onerror=&#39;alert(&quot;xss&quot;)&#39;&gt;&amp;foo;',
  );
});

test('escapeHtml returns empty for null / undefined', () => {
  assert.equal(escapeHtml(null), '');
  assert.equal(escapeHtml(undefined), '');
});

test('escapeHtml stringifies non-strings', () => {
  assert.equal(escapeHtml(42), '42');
  assert.equal(escapeHtml(true), 'true');
});

test('flagIcon builds a <span> with a flagcdn.com background-image', () => {
  const gb = flagIcon('GB');
  assert.match(gb, /<span /);
  assert.match(gb, /background-image:url\(https:\/\/flagcdn\.com\/gb\.svg\)/);
  assert.match(gb, /aria-label="GB"/);
  // Case-insensitive on input, but the URL is always lower-case.
  assert.match(flagIcon('us'), /background-image:url\(https:\/\/flagcdn\.com\/us\.svg\)/);
  assert.match(flagIcon('us'), /aria-label="US"/);
});

test('flagIcon returns empty for bad input', () => {
  assert.equal(flagIcon(null), '');
  assert.equal(flagIcon(undefined), '');
  assert.equal(flagIcon(''), '');
  assert.equal(flagIcon('USA'), '');   // too long
  assert.equal(flagIcon('G'), '');     // too short
  assert.equal(flagIcon('G1'), '');    // non-letter
  assert.equal(flagIcon(42), '');      // non-string
});
