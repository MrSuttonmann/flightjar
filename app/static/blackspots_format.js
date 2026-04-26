// Pure helpers for the blackspots overlay — colour banding, tooltip copy,
// altitude-stop table. Kept separate from blackspots.js so they can be
// imported in Node tests without dragging the Leaflet/state modules into
// the graph.

import { escapeHtml } from './format.js';

// Fixed slider stops in metres MSL (chosen to match standard flight levels
// a pilot or spotter would scan through: surface → low GA → low airway →
// airliner). The backend accepts any value in [0, 20000]; 0 is a sentinel
// that makes the grid use each cell's DEM elevation + 2 m (ground-level
// aircraft). Anything positive is treated as absolute MSL.
export const ALT_STOPS_M = [
  0,      // GND   — planes on the ground (per-cell DEM + 2 m)
  305,    // FL010 — approach / departure low pass
  610,    // FL020 — circuit / pattern altitude
  914,    // FL030 — low GA / training
  1524,   // FL050
  2286,   // FL075
  3048,   // FL100 — default (backend prewarms this one)
  4572,   // FL150
  6096,   // FL200
  7620,   // FL250
  9144,   // FL300
  12192,  // FL400 — top of typical airliner cruise
  15240,  // FL500 — business-jet / high-altitude ceiling
];
// Shifted up by one after inserting GND at the front so the default
// still lands on FL100.
export const DEFAULT_STOP_INDEX = 6;

// Render an altitude in metres as an FL label (feet / 100, zero-padded).
// 0 is the ground-level sentinel — surface the GND annotation pilots use
// on charts rather than a meaningless FL000.
export function flLabel(altM) {
  if (altM <= 0) return 'GND';
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

// Subtle greyscale shading for the blocker-aggregate cells. The intent
// is "see at a glance which terrain is doing the work" without competing
// visually with the coloured shadow shading on top — neutral grey reads
// as "context", not "alert". Opacity scales with `blocked_count`: a
// single-cell offender is barely there; a hundred-cell ridge is a clear
// dark patch. Pure log scaling so the visual difference between 1 and 5
// is similar to between 50 and 250.
export function blockerShade(blockedCount) {
  const n = Math.max(1, blockedCount);
  // log2(1)=0 → opacity 0.10; log2(8)=3 → 0.25; log2(64)=6 → 0.40; cap 0.55.
  const opacity = Math.min(0.55, 0.10 + 0.05 * Math.log2(n));
  return {
    fillColor: '#1f2937',  // neutral slate-800
    fillOpacity: opacity,
    color: '#1f2937',
    weight: 0,             // no stroke — blocker patches abut and a stroke would grid them up
    opacity: 0,
  };
}

export function blockerTooltipFor(blocker, params) {
  const altLabel = flLabel(params.target_altitude_m);
  const elev = Math.round(blocker.max_elev_msl_m);
  const cells = blocker.blocked_count;
  const cellsLabel = cells === 1 ? 'cell' : 'cells';
  return `<b>Obstruction at ${elev} m MSL</b><br>`
    + `Blocking ${cells} ${cellsLabel} at ${escapeHtml(altLabel)}`;
}
