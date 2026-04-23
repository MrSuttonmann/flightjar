// Pure helpers for the blackspots overlay — colour banding, tooltip copy,
// altitude-stop table. Kept separate from blackspots.js so they can be
// imported in Node tests without dragging the Leaflet/state modules into
// the graph.

import { escapeHtml } from './format.js';

// Fixed slider stops in metres MSL (chosen to match standard flight levels
// a pilot or spotter would scan through: low GA → low airway → airliner).
// The backend is happy with any value in (0, 20000] so this table is
// purely a UI concern; change it freely.
export const ALT_STOPS_M = [
  305,    // FL010 — approach / departure low pass
  610,    // FL020 — circuit / pattern altitude
  914,    // FL030 — low GA / training
  1524,   // FL050
  2286,   // FL075
  3048,   // FL100 — default (backend prewarms this one)
  4572,   // FL150
  6096,   // FL200
  9144,   // FL300
  12192,  // FL400 — top of typical airliner cruise
  15240,  // FL500 — business-jet / high-altitude ceiling
];
export const DEFAULT_STOP_INDEX = 5;

// Render an altitude in metres as an FL label (feet / 100, zero-padded).
export function flLabel(altM) {
  const ft = Math.round(altM * 3.28084);
  return `FL${Math.round(ft / 100).toString().padStart(3, '0')}`;
}

// Fill colour bands, keyed on `required_height_m`. Step thresholds so the
// legend and tooltip copy agree. Tweaked for visibility against both the
// default OSM basemap and Carto Dark.
export const HEIGHT_BANDS = [
  { max: 20,   fill: '#fde68a', stroke: '#d97706' },   // pale yellow — just out of reach
  { max: 50,   fill: '#fdba74', stroke: '#c2410c' },   // orange
  { max: 100,  fill: '#f87171', stroke: '#991b1b' },   // red
  { max: Infinity, fill: '#7c3aed', stroke: '#4c1d95' }, // unreachable
];

// `null` (unreachable — LOS still blocked at MAX_AGL) falls through to
// the last band; finite values pick the first band whose `max` covers them.
export function bandFor(requiredHeightM) {
  if (requiredHeightM == null) return HEIGHT_BANDS[HEIGHT_BANDS.length - 1];
  for (const band of HEIGHT_BANDS) {
    if (requiredHeightM <= band.max) return band;
  }
  return HEIGHT_BANDS[HEIGHT_BANDS.length - 1];
}

export function tooltipFor(cell, params) {
  const altLabel = flLabel(params.target_altitude_m);
  const currentMsl = Math.round(params.antenna_msl_m);
  const currentAgl = Math.round(params.antenna_msl_m - params.ground_elevation_m);
  if (cell.required_antenna_msl_m == null) {
    const ceiling = Math.round(params.ground_elevation_m + params.max_agl_m);
    return `<b>Unreachable at ${escapeHtml(altLabel)}</b><br>`
      + `No antenna ≤ ${ceiling} m MSL clears this cell`;
  }
  const neededMsl = Math.round(cell.required_antenna_msl_m);
  const delta = neededMsl - currentMsl;
  return `<b>Blind spot at ${escapeHtml(altLabel)}</b><br>`
    + `Needs antenna ≥ ${neededMsl} m MSL (+${delta} m)<br>`
    + `You have ${currentMsl} m MSL (${currentAgl} m AGL)`;
}
