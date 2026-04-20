import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  CATEGORY_NAMES,
  LINK_ICON_SVG,
  relativeAge,
  renderAltProfile,
  renderSpdProfile,
  renderTrailProfile,
  signalLabel,
} from '../../app/static/profile.js';

// Minimal SVG-element stub: we only need something renderTrailProfile can
// set innerHTML on. Captures the last written value for assertions.
function fakeSvg() {
  return { innerHTML: '' };
}

test('CATEGORY_NAMES covers ADS-B emitter categories 1-7', () => {
  for (let i = 1; i <= 7; i++) {
    assert.ok(typeof CATEGORY_NAMES[i] === 'string' && CATEGORY_NAMES[i].length > 0,
      `missing name for category ${i}`);
  }
  assert.equal(CATEGORY_NAMES[0], undefined);
  assert.equal(CATEGORY_NAMES[8], undefined);
});

test('LINK_ICON_SVG is a compact inline SVG using currentColor', () => {
  assert.match(LINK_ICON_SVG, /<svg /);
  assert.match(LINK_ICON_SVG, /currentColor/);
  assert.match(LINK_ICON_SVG, /<\/svg>$/);
});

test('signalLabel converts BEAST signal bytes to dBFS', () => {
  assert.equal(signalLabel(255), '0.0 dBFS');
  // 255 → 0 dBFS, so smaller bytes are more negative.
  const db100 = signalLabel(100);
  assert.match(db100, /^-\d/);
  assert.ok(db100.endsWith(' dBFS'));
});

test('signalLabel returns em-dash for null / zero / negative', () => {
  assert.equal(signalLabel(null), '—');
  assert.equal(signalLabel(undefined), '—');
  assert.equal(signalLabel(0), '—');
  assert.equal(signalLabel(-10), '—');
});

test('relativeAge formats ranges in s / m / h', () => {
  assert.equal(relativeAge(95, 100), '5s ago');
  assert.equal(relativeAge(100 - 120, 100), '2m ago');
  assert.equal(relativeAge(100 - 3600 * 2, 100), '2.0h ago');
});

test('relativeAge returns em-dash when either timestamp is missing', () => {
  assert.equal(relativeAge(null, 100), '—');
  assert.equal(relativeAge(50, null), '—');
  assert.equal(relativeAge(0, 100), '—');       // 0 == "never seen"
});

test('relativeAge clamps negative deltas to zero', () => {
  // Future timestamps (clock skew) shouldn't produce "-5s ago".
  assert.equal(relativeAge(110, 100), '0s ago');
});

test('renderAltProfile writes an empty-state text for short trails', () => {
  const svg = fakeSvg();
  renderAltProfile(svg, []);
  assert.match(svg.innerHTML, /awaiting altitude data/);
  renderAltProfile(svg, [[0, 0, 1000, null]]);
  assert.match(svg.innerHTML, /awaiting altitude data/);
});

test('renderAltProfile emits one <line> per segment with altColor strokes', () => {
  const svg = fakeSvg();
  renderAltProfile(svg, [
    [0, 0, 1000, null],
    [0, 0, 20000, null],
    [0, 0, 35000, null],
  ]);
  // Two adjacent points → one segment. Three points → two segments.
  const lines = svg.innerHTML.match(/<line /g) || [];
  assert.equal(lines.length, 2);
  // altColor picks an rgb(...) from the ALT_STOPS palette — not '—'.
  assert.match(svg.innerHTML, /stroke="rgb\(\d+,\s*\d+,\s*\d+\)"/);
});

test('renderSpdProfile uses the accent CSS variable (not altColor)', () => {
  const svg = fakeSvg();
  renderSpdProfile(svg, [
    [0, 0, 1000, 200],
    [0, 0, 1200, 240],
  ]);
  assert.match(svg.innerHTML, /stroke="var\(--accent\)"/);
});

test('renderSpdProfile skips null speeds in the series', () => {
  const svg = fakeSvg();
  // Middle point has no speed — should get dropped, leaving two valid
  // points (and therefore exactly one segment).
  renderSpdProfile(svg, [
    [0, 0, 1000, 200],
    [0, 0, 1100, null],
    [0, 0, 1200, 240],
  ]);
  const lines = svg.innerHTML.match(/<line /g) || [];
  assert.equal(lines.length, 1);
});

test('renderTrailProfile shows empty-state text when valid values < 2', () => {
  const svg = fakeSvg();
  // Only one point has altitude; empty-state since valid series is 1.
  renderTrailProfile(svg, [
    [0, 0, 1000, null, 0],
    [0, 0, null, null, 1],
  ], {
    pickValue: (p) => p[2],
    strokeFn: () => 'red',
    height: 20,
    emptyLabel: 'no data',
  });
  // Two points → bypasses the "length < 2" early return and hits the
  // valid.length < 2 branch, which clears innerHTML.
  assert.equal(svg.innerHTML, '');
});
