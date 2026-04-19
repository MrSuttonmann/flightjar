import { test } from 'node:test';
import assert from 'node:assert/strict';

import { PLANE_SHAPES, TYPE_SHAPES, silhouette } from '../../app/static/silhouette.js';

test('PLANE_SHAPES covers every silhouette referenced by the type map', () => {
  const families = new Set(Object.values(TYPE_SHAPES));
  for (const family of families) {
    assert.ok(PLANE_SHAPES[family], `missing shape for family ${family}`);
  }
  // And the fallback-only families.
  for (const family of ['generic', 'heli']) {
    assert.ok(PLANE_SHAPES[family], `missing shape for ${family}`);
  }
});

test('silhouette picks by explicit type code when known', () => {
  assert.equal(silhouette({ type_icao: 'B738' }), 'jet');
  assert.equal(silhouette({ type_icao: 'B77W' }), 'widebody');
  assert.equal(silhouette({ type_icao: 'DH8D' }), 'turboprop');
  assert.equal(silhouette({ type_icao: 'C172' }), 'light');
});

test('silhouette falls back to ADS-B category when type is unknown', () => {
  assert.equal(silhouette({ type_icao: 'XXXX', category: 1 }), 'light');
  assert.equal(silhouette({ type_icao: 'ZZZZ', category: 2 }), 'jet');
  assert.equal(silhouette({ type_icao: 'QQQQ', category: 5 }), 'widebody');
  assert.equal(silhouette({ type_icao: 'WWWW', category: 7 }), 'heli');
});

test('silhouette picks heli when type code starts with H', () => {
  assert.equal(silhouette({ type_icao: 'H60' }), 'heli');
  assert.equal(silhouette({ type_icao: 'HUGE' }), 'heli');
});

test('silhouette returns generic when nothing matches', () => {
  assert.equal(silhouette({}), 'generic');
  assert.equal(silhouette({ type_icao: null }), 'generic');
  assert.equal(silhouette({ type_icao: 'XXXX', category: 0 }), 'generic');
});
