import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  UNIT_SYSTEMS,
  getUnitSystem,
  setUnitSystem,
  uconv,
} from '../../app/static/units.js';

test('setUnitSystem rejects unknown systems', () => {
  setUnitSystem('metric');  // put in a known state
  assert.equal(setUnitSystem('furlong'), false);
  assert.equal(getUnitSystem(), 'metric');      // state unchanged
});

test('setUnitSystem switches and getUnitSystem reflects it', () => {
  setUnitSystem('nautical');
  assert.equal(getUnitSystem(), 'nautical');
  setUnitSystem('imperial');
  assert.equal(getUnitSystem(), 'imperial');
});

test('uconv returns em-dash for null / NaN', () => {
  setUnitSystem('nautical');
  assert.equal(uconv('alt', null), '—');
  assert.equal(uconv('spd', undefined), '—');
  assert.equal(uconv('dst', NaN), '—');
});

test('nautical: feet stay feet, knots stay knots, distance in NM', () => {
  setUnitSystem('nautical');
  assert.equal(uconv('alt', 35000), '35000 ft');
  assert.equal(uconv('spd', 450), '450 kt');
  assert.equal(uconv('dst', 100), '54 nm');     // 100 km × 0.539957
});

test('imperial: miles and mph', () => {
  setUnitSystem('imperial');
  assert.equal(uconv('alt', 10000), '10000 ft');
  assert.equal(uconv('spd', 100), '115 mph');
  assert.equal(uconv('dst', 100), '62 mi');
});

test('metric altitude flips from metres to km above 1km', () => {
  setUnitSystem('metric');
  assert.equal(uconv('alt', 3000), '914 m');     // 3000 ft < 1 km
  assert.equal(uconv('alt', 3281), '1.0 km');    // just over 1 km
  assert.equal(uconv('alt', 35000), '10.7 km');  // airliner cruise
});

test('metric: speed km/h, distance km, vrate m/s', () => {
  setUnitSystem('metric');
  assert.equal(uconv('spd', 450), '833 km/h');   // 450 kt × 1.852
  assert.equal(uconv('dst', 100), '100 km');
  assert.equal(uconv('vrt', 1000), '5.1 m/s');   // 1000 fpm × 0.00508
});

test('UNIT_SYSTEMS exposes all three variants', () => {
  assert.ok(UNIT_SYSTEMS.metric);
  assert.ok(UNIT_SYSTEMS.imperial);
  assert.ok(UNIT_SYSTEMS.nautical);
});
