import { test } from 'node:test';
import assert from 'node:assert/strict';

import { resolveSilhouette } from '../../app/static/silhouette.js';

// Minimal stand-in tables — just enough rows to exercise each fallback
// step in isolation. The real tables come from tar1090_shapes.js +
// icao_aircraft_types.js at build time; what we care about here is the
// dispatch order.
const TABLES = {
  typeDesignatorIcons: {
    B738: ['b738', 1.0],
    A320: ['a320', 1.0],
  },
  typeDescriptionIcons: {
    L1P: ['cessna', 1.0],
    'L2J-M': ['airliner', 1.0],
    L2J: ['airliner_bare', 1.0],
    H: ['helicopter', 1.0],
  },
  categoryIcons: {
    A1: ['cessna', 1.0],
    A2: ['jet_swept', 0.94],
    A5: ['heavy_2e', 0.92],
    A7: ['helicopter', 1.0],
    B2: ['balloon', 1.0],
    B6: ['uav', 1.0],
  },
  typeDesignatorMeta: {
    C172: { desc: 'L1P', wtc: 'L' },
    B738: { desc: 'L2J', wtc: 'M' },
    AS50: { desc: 'H1T', wtc: 'L' }, // 'H1T' not in description table; expect 'H' fallback
  },
};

function resolve(args) {
  return resolveSilhouette({ ...TABLES, ...args });
}

test('step 1 — type designator hits TypeDesignatorIcons directly', () => {
  assert.deepEqual(resolve({ typeIcao: 'B738' }), ['b738', 1.0]);
  // Case-insensitive lookup.
  assert.deepEqual(resolve({ typeIcao: 'b738' }), ['b738', 1.0]);
});

test('step 2 — unknown designator falls back to type description + WTC', () => {
  // B738 IS in TypeDesignatorIcons, so that should win. Force the
  // fallback by removing it from that table.
  const out = resolveSilhouette({
    ...TABLES,
    typeDesignatorIcons: {},
    typeIcao: 'B738',
  });
  assert.deepEqual(out, ['airliner', 1.0]); // L2J-M with WTC suffix
});

test('step 2 — bare description matches when WTC suffix has no entry', () => {
  const out = resolveSilhouette({
    ...TABLES,
    typeDescriptionIcons: {
      L1P: ['cessna', 1.0],
      L2J: ['airliner_bare', 1.0], // no 'L2J-M'
    },
    typeIcao: 'B738',
    typeDesignatorIcons: {},
  });
  assert.deepEqual(out, ['airliner_bare', 1.0]);
});

test('step 2 — kind-letter fallback when neither description nor WTC suffix matches', () => {
  // AS50 -> H1T which we deliberately omitted; expect kind 'H'.
  const out = resolveSilhouette({
    ...TABLES,
    typeIcao: 'AS50',
    typeDesignatorIcons: {},
  });
  assert.deepEqual(out, ['helicopter', 1.0]);
});

test('step 1 — C172 with description but no designator entry falls to L1P', () => {
  // This is the actual real-world case: tar1090-db knows C172 but
  // TypeDesignatorIcons doesn't carry it. typeDesignatorMeta has
  // { desc: "L1P", wtc: "L" }; the description table maps L1P to cessna.
  const out = resolve({ typeIcao: 'C172' });
  assert.deepEqual(out, ['cessna', 1.0]);
});

test('step 3 — unknown type + category falls to CategoryIcons (default Set A)', () => {
  assert.deepEqual(resolve({ typeIcao: 'XXXX', category: 1 }), ['cessna', 1.0]);
  assert.deepEqual(resolve({ typeIcao: null, category: 5 }), ['heavy_2e', 0.92]);
});

test('step 3 — explicit Set B distinguishes balloons / UAVs from Set A planes', () => {
  assert.deepEqual(resolve({ category: 2, categorySet: 'B' }), ['balloon', 1.0]);
  assert.deepEqual(resolve({ category: 6, categorySet: 'B' }), ['uav', 1.0]);
  // Same subcategory bits with no set defaults to A2 (jet_swept), not B2 (balloon).
  assert.deepEqual(resolve({ category: 2 }), ['jet_swept', 0.94]);
});

test('returns null when nothing matches and category is 0/missing', () => {
  assert.equal(resolve({}), null);
  assert.equal(resolve({ category: 0 }), null);
  assert.equal(resolve({ typeIcao: 'ZZZZ', category: null }), null);
});

test('null tables are tolerated (bundles still loading)', () => {
  assert.equal(resolveSilhouette({
    typeIcao: 'B738', category: 5,
    typeDesignatorIcons: null,
    typeDescriptionIcons: null,
    categoryIcons: null,
    typeDesignatorMeta: null,
  }), null);
});
