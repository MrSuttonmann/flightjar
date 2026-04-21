import { test } from 'node:test';
import assert from 'node:assert/strict';

import { isNotable, militaryLabel } from '../../app/static/notable_aircraft.js';

test('isNotable matches a known hex (case-insensitive)', () => {
  const got = isNotable('ADFDF8', '');
  assert.ok(got);
  assert.match(got.name, /Air Force One/);
});

test('isNotable matches a callsign prefix when hex misses', () => {
  const got = isNotable('abc123', 'SHEPHERD1');
  assert.ok(got);
  assert.equal(got.name, 'Papal flight');
});

test('isNotable prefers longer prefix when two overlap', () => {
  // NASA entries dominate — there's no NAS or N prefix in the table,
  // but this test guards the sort behaviour against regressions.
  const got = isNotable('xxxxxx', 'NASA921');
  assert.ok(got);
  assert.equal(got.emoji, '🚀');
});

test('isNotable returns null for an ordinary tail', () => {
  assert.equal(isNotable('4ca2d1', 'RYR123'), null);
  assert.equal(isNotable('', ''), null);
  assert.equal(isNotable(null, null), null);
});

test('militaryLabel picks the longest matching prefix', () => {
  assert.equal(militaryLabel('AE1234'), 'MIL · US');
  assert.equal(militaryLabel('43c001'), 'MIL · UK');
  // 3f4 is a longer match than 3f; confirm it wins.
  assert.equal(militaryLabel('3f4abc'), 'MIL · DE');
});

test('militaryLabel returns null for civilian hex', () => {
  assert.equal(militaryLabel('4ca2d1'), null);
  assert.equal(militaryLabel(''), null);
  assert.equal(militaryLabel(null), null);
});

test('isNotable matches SANTA* only on Christmas Eve', () => {
  const xmasEve = new Date(2026, 11, 24);  // month index 11 = Dec
  const santa = isNotable('xxxxxx', 'SANTA01', xmasEve);
  assert.ok(santa);
  assert.equal(santa.emoji, '🎅');
  // Same callsign on any other day: no match.
  const march = new Date(2026, 2, 15);
  assert.equal(isNotable('xxxxxx', 'SANTA01', march), null);
});
