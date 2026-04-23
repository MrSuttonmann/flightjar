import { test } from 'node:test';
import assert from 'node:assert/strict';

import { AIRSPACE_GROUPS, airspaceGroup } from '../../app/static/airspace_groups.js';

test('airspaceGroup maps type_name to the expected group', () => {
  assert.equal(airspaceGroup({ type_name: 'Prohibited' }), 'restricted');
  assert.equal(airspaceGroup({ type_name: 'Restricted' }), 'restricted');
  assert.equal(airspaceGroup({ type_name: 'TRA' }), 'restricted');
  assert.equal(airspaceGroup({ type_name: 'Danger' }), 'danger');
  assert.equal(airspaceGroup({ type_name: 'Warning' }), 'danger');
  assert.equal(airspaceGroup({ type_name: 'Alert' }), 'danger');
  assert.equal(airspaceGroup({ type_name: 'CTR' }), 'controlled');
  assert.equal(airspaceGroup({ type_name: 'CTA' }), 'controlled');
  assert.equal(airspaceGroup({ type_name: 'TMA' }), 'controlled');
  assert.equal(airspaceGroup({ type_name: 'ATZ' }), 'atz_matz');
  assert.equal(airspaceGroup({ type_name: 'MATZ' }), 'atz_matz');
  assert.equal(airspaceGroup({ type_name: 'TMZ' }), 'mandatory');
  assert.equal(airspaceGroup({ type_name: 'RMZ' }), 'mandatory');
  assert.equal(airspaceGroup({ type_name: 'Gliding' }), 'gliding');
  assert.equal(airspaceGroup({ type_name: 'FIR' }), 'airways');
  assert.equal(airspaceGroup({ type_name: 'Airway' }), 'airways');
});

test('airspaceGroup falls back to class when type_name is missing or unknown', () => {
  // Plain Class E / F zones have no type_name — class fallback kicks in.
  assert.equal(airspaceGroup({ class: 'A' }), 'controlled');
  assert.equal(airspaceGroup({ class: 'D' }), 'controlled');
  assert.equal(airspaceGroup({ class: 'E' }), 'class_ef');
  assert.equal(airspaceGroup({ class: 'F' }), 'class_ef');
  assert.equal(airspaceGroup({ class: 'G' }), 'airways');
  // An explicit type_name always wins over class — a "Danger" zone tagged
  // Class G is still danger, not airways.
  assert.equal(airspaceGroup({ type_name: 'Danger', class: 'G' }), 'danger');
});

test('airspaceGroup routes unrecognised rows to "other"', () => {
  // Types that don't fit a named group fall through.
  assert.equal(airspaceGroup({ type_name: 'TIZ' }), 'other');
  assert.equal(airspaceGroup({ type_name: 'VFR' }), 'other');
  assert.equal(airspaceGroup({ type_name: 'Other' }), 'other');
  // Completely unknown type_name still returns a valid group (no throws,
  // no undefined) — future OpenAIP additions stay visible by default.
  assert.equal(airspaceGroup({ type_name: 'SomeFutureType' }), 'other');
  // Missing everything is still safe.
  assert.equal(airspaceGroup({}), 'other');
  assert.equal(airspaceGroup(null), 'other');
});

test('AIRSPACE_GROUPS covers every group key that airspaceGroup can return', () => {
  // The filter dialog builds checkboxes from AIRSPACE_GROUPS, so if
  // airspaceGroup ever returns a key that isn't in the list, those
  // airspaces would become unfilterable. Exhaust the known type+class
  // domain and assert every returned key is represented.
  const sampleInputs = [
    { type_name: 'Prohibited' }, { type_name: 'Restricted' }, { type_name: 'TRA' }, { type_name: 'TSA' },
    { type_name: 'Danger' }, { type_name: 'Warning' }, { type_name: 'Alert' },
    { type_name: 'CTR' }, { type_name: 'CTA' }, { type_name: 'TMA' },
    { type_name: 'ATZ' }, { type_name: 'MATZ' }, { type_name: 'HTZ' },
    { type_name: 'TMZ' }, { type_name: 'RMZ' }, { type_name: 'ADIZ' },
    { type_name: 'Airway' }, { type_name: 'FIR' }, { type_name: 'UIR' },
    { type_name: 'Gliding' }, { type_name: 'Aerial Sporting' },
    { class: 'A' }, { class: 'E' }, { class: 'G' },
    {},
  ];
  const validKeys = new Set(AIRSPACE_GROUPS.map((g) => g.key));
  for (const a of sampleInputs) {
    const key = airspaceGroup(a);
    assert.ok(validKeys.has(key), `group '${key}' not in AIRSPACE_GROUPS for ${JSON.stringify(a)}`);
  }
});
