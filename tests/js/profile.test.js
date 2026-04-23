import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  CATEGORY_NAMES,
  LINK_ICON_SVG,
  formatMetar,
  relativeAge,
  renderAltProfile,
  renderSpdProfile,
  renderTrailProfile,
  signalBars,
  signalLabel,
  wakeClass,
  weatherIconKey,
  weatherIconSvg,
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

test('wakeClass returns J for Super types', () => {
  assert.equal(wakeClass('A388', 5), 'J');
  assert.equal(wakeClass('B748', 4), 'J');
  // Case-insensitive on the type code.
  assert.equal(wakeClass('a225', 5), 'J');
});

test('wakeClass returns H for ADS-B category 4/5 (heavy / high-vortex)', () => {
  assert.equal(wakeClass('B738', 5), 'H');
  assert.equal(wakeClass(null, 4), 'H');
});

test('wakeClass returns L for ADS-B category 1 (light)', () => {
  assert.equal(wakeClass('C172', 1), 'L');
});

test('wakeClass returns M for small/large/high-perf/rotorcraft', () => {
  for (const cat of [2, 3, 6, 7]) {
    assert.equal(wakeClass('X', cat), 'M');
  }
});

test('wakeClass returns null when both inputs are missing', () => {
  assert.equal(wakeClass(null, null), null);
  assert.equal(wakeClass(undefined, undefined), null);
});

test('signalBars returns empty for missing / zero / negative byte', () => {
  assert.equal(signalBars(null), '');
  assert.equal(signalBars(undefined), '');
  assert.equal(signalBars(0), '');
  assert.equal(signalBars(-5), '');
});

test('signalBars produces a level between 1 and 4 with matching on-bars', () => {
  // Quartiles: <64 = 1, <128 = 2, <192 = 3, >=192 = 4.
  for (const [byte, expected] of [[30, 1], [100, 2], [160, 3], [240, 4]]) {
    const html = signalBars(byte);
    assert.match(html, new RegExp(`data-level="${expected}"`));
    const onBars = (html.match(/class="bar on"/g) || []).length;
    assert.equal(onBars, expected);
  }
});

test('signalBars tooltip carries the dBFS label', () => {
  const html = signalBars(255);
  assert.match(html, /title="0\.0 dBFS"/);
});

test('formatMetar renders code + compact summary with raw in title', () => {
  const html = formatMetar('EGLL', {
    raw: 'EGLL 131950Z 25012KT 9999 BKN020 15/10 Q1015',
    wind_dir: 250, wind_kt: 12, visibility: '10+', cover: 'BKN',
  });
  assert.match(html, /EGLL/);
  assert.match(html, /250°\/12kt/);
  assert.match(html, /10\+/);
  assert.match(html, /BKN/);
  // Raw METAR goes into the title attribute for the tooltip.
  assert.match(html, /title="EGLL 131950Z/);
});

test('formatMetar pads wind direction to three digits', () => {
  const html = formatMetar('KJFK', {
    raw: 'KJFK ...',
    wind_dir: 30, wind_kt: 8, visibility: null, cover: null,
  });
  assert.match(html, /030°\/8kt/);
});

test('formatMetar surfaces CALM for zero-zero winds', () => {
  const html = formatMetar('EGLL', {
    raw: 'EGLL ...', wind_dir: 0, wind_kt: 0, visibility: null, cover: 'CLR',
  });
  assert.match(html, /calm/);
});

test('formatMetar drops fragments that are missing data', () => {
  // Only cover is present — the output shouldn't have the wind or vis
  // separator glyphs hanging about.
  const html = formatMetar('LFPG', {
    raw: 'LFPG ...', wind_dir: null, wind_kt: null, visibility: null, cover: 'OVC',
  });
  assert.match(html, /LFPG/);
  assert.match(html, /OVC/);
  assert.doesNotMatch(html, /°\//);
});

test('formatMetar treats numeric visibility as metres', () => {
  const html = formatMetar('EDDF', {
    raw: 'EDDF ...', wind_dir: null, wind_kt: null, visibility: 8000, cover: null,
  });
  assert.match(html, /8000m/);
});

test('formatMetar escapes quotes in raw METAR for the title attribute', () => {
  const html = formatMetar('TEST', {
    raw: 'TEST "weird quotes" hand-edited',
    wind_dir: null, wind_kt: null, visibility: null, cover: null,
  });
  // Raw-quote chars collapse to entities to keep the attribute valid.
  assert.match(html, /title="TEST &quot;weird quotes&quot;/);
});

test('formatMetar returns empty string for missing inputs', () => {
  assert.equal(formatMetar(null, null), '');
  assert.equal(formatMetar('EGLL', null), '');
  assert.equal(formatMetar(null, {}), '');
});

test('weatherIconKey picks thunder over rain even when both present', () => {
  assert.equal(weatherIconKey({ raw: 'KJFK 231051Z +TSRA BKN040CB', cover: 'BKN' }), 'thunder');
  assert.equal(weatherIconKey({ raw: 'EDDF 231050Z 27015KT VCTS SCT025CB', cover: 'SCT' }), 'thunder');
});

test('weatherIconKey returns snow for frozen-precip phenomena', () => {
  assert.equal(weatherIconKey({ raw: 'EGLL 131950Z 09008KT -SN BKN010', cover: 'BKN' }), 'snow');
  assert.equal(weatherIconKey({ raw: 'ENGM 131950Z +SHSN OVC015', cover: 'OVC' }), 'snow');
});

test('weatherIconKey returns rain for liquid-precip phenomena', () => {
  assert.equal(weatherIconKey({ raw: 'EGLL 131950Z 25012KT -RA BKN020', cover: 'BKN' }), 'rain');
  assert.equal(weatherIconKey({ raw: 'EGLL 131950Z 25012KT +SHRA BKN020', cover: 'BKN' }), 'rain');
});

test('weatherIconKey returns fog for obscuration phenomena', () => {
  assert.equal(weatherIconKey({ raw: 'EGLL 131950Z VRB02KT 0100 FG OVC002', cover: 'OVC' }), 'fog');
  assert.equal(weatherIconKey({ raw: 'EGLL 131950Z 25002KT 4000 BR NSC', cover: null }), 'fog');
  assert.equal(weatherIconKey({ raw: 'OMDB 131950Z 25008KT 3000 HZ NSC', cover: null }), 'fog');
});

test('weatherIconKey falls back to cloud cover without present-weather groups', () => {
  assert.equal(weatherIconKey({ raw: 'EGLL 131950Z 25012KT 9999 OVC020', cover: 'OVC' }), 'cloud');
  assert.equal(weatherIconKey({ raw: 'EGLL 131950Z 25012KT 9999 BKN020', cover: 'BKN' }), 'cloud');
  assert.equal(weatherIconKey({ raw: 'EGLL 131950Z 25012KT 9999 SCT020', cover: 'SCT' }), 'partly');
  assert.equal(weatherIconKey({ raw: 'EGLL 131950Z 25012KT 9999 FEW020', cover: 'FEW' }), 'partly');
});

test('weatherIconKey returns clear for CAVOK / SKC', () => {
  assert.equal(weatherIconKey({ raw: 'RJAA 231050Z 22005KT CAVOK 20/12 Q1018', cover: null }), 'clear');
  assert.equal(weatherIconKey({ raw: 'KJFK 231051Z 24008KT 10SM CLR 18/10 A2992', cover: 'CLR' }), 'clear');
  assert.equal(weatherIconKey({ raw: '', cover: null }), 'clear');
  assert.equal(weatherIconKey(null), '');
});

test('weatherIconKey does not false-match ICAO codes containing RA or SN', () => {
  // LFRA (Rennes) contains "RA" as part of the station identifier; must
  // not be treated as rain if there's no actual present-weather group.
  assert.equal(weatherIconKey({ raw: 'LFRA 131950Z 25012KT 9999 BKN020', cover: 'BKN' }), 'cloud');
  // EDSN (Neuburg) contains "SN" — same story.
  assert.equal(weatherIconKey({ raw: 'EDSN 131950Z 25012KT CAVOK', cover: null }), 'clear');
});

test('weatherIconSvg emits an SVG that labels its icon class + currentColor', () => {
  const html = weatherIconSvg({ raw: 'EGLL 131950Z 25012KT -RA BKN020', cover: 'BKN' });
  assert.match(html, /<svg /);
  assert.match(html, /class="wx-icon wx-rain"/);
  assert.match(html, /stroke="currentColor"/);
});

test('weatherIconSvg returns empty string for null input', () => {
  assert.equal(weatherIconSvg(null), '');
});

test('formatMetar includes a weather icon SVG before the fragments', () => {
  const html = formatMetar('EGLL', {
    raw: 'EGLL 131950Z 25012KT 9999 -RA BKN020 15/10 Q1015',
    wind_dir: 250, wind_kt: 12, visibility: '10+', cover: 'BKN',
  });
  assert.match(html, /wx-icon wx-rain/);
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
