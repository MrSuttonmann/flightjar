// Vertical-slice tooltip: when the Airspaces overlay is on, hovering
// the map shows a floating SVG mini-chart near the cursor with one
// "swim lane" per overlapping airspace at that lat/lon, drawn against
// a dynamic altitude scale. Pure client side — reads the airspace
// cache populated by openaip.js, queries with the PiP helpers in
// geo.js, and listens on Leaflet map events to drive the tip's
// position and contents.

import { airspaceGroup, AIRSPACE_GROUPS } from './airspace_groups.js';
import { bboxContains, pointInGeometry } from './geo.js';
import { formatLimit } from './openaip.js';
import { state } from './state.js';

// Mirror of openaip.js OVERLAYS.airspaces.minZoom — the map clears the
// airspaces layer below this zoom so the slice has nothing to show.
const AIRSPACE_MIN_ZOOM = 5;

// Backend reports null upper-limit for airspaces that genuinely extend
// to the top of available chart-space; render them up to FL660 (the
// top of the dynamic Y axis) and decorate the column with an upward
// chevron so the user reads "open above this".
const OPEN_TOP_FT = 66000;

// Aircraft within this many screen pixels of the cursor are drawn as
// altitude markers on the slice. Pixel distance (rather than
// great-circle km) scales naturally with the user's current zoom —
// you can zoom in for a tight local slice or zoom out to include
// planes over a wider area, and the affordance feels the same at
// every scale.
const AIRCRAFT_NEARBY_PX = 24;
const MAX_AIRCRAFT_MARKERS = 3;

// Y-axis nice-number ladder. yMax = max ceiling among visible airspaces
// rounded up to the next entry; floor at 5000 so an ATZ-only slice
// doesn't visually collapse.
const Y_MAX_LADDER = [5000, 7500, 10000, 15000, 20000, 25000, 35000, 45000, 60000, OPEN_TOP_FT];

// Tick sets per yMax (matches each ladder entry). Values are MSL feet
// for non-FL ticks; entries >= 10_000 are rendered as "FL###" labels
// via formatLimit, matching how the map tooltip shows airspace limits.
function pickTicks(yMaxFt) {
  if (yMaxFt <= 5000) return [0, 1500, 3000, 5000];
  if (yMaxFt <= 7500) return [0, 2500, 5000, 7500];
  if (yMaxFt <= 10000) return [0, 2500, 5000, 7500, 10000];
  if (yMaxFt <= 15000) return [0, 5000, 10000, 15000];
  if (yMaxFt <= 20000) return [0, 5000, 10000, 15000, 20000];
  if (yMaxFt <= 25000) return [0, 5000, 10000, 15000, 25000];
  if (yMaxFt <= 35000) return [0, 10000, 20000, 30000, 35000];
  if (yMaxFt <= 45000) return [0, 10000, 25000, 35000, 45000];
  return [0, 10000, 25000, 45000, 66000];
}

function labelForTick(ft) {
  if (ft === 0) return 'GND';
  if (ft >= 10000) return formatLimit(ft, 'FL');
  return `${(ft / 1000).toFixed(ft % 1000 === 0 ? 0 : 1)}k`;
}

const SVG_NS = 'http://www.w3.org/2000/svg';
const COL_W = 32;
const COL_GAP = 4;
const MAX_COLS = 6;
const PAD_TOP = 16;
const PAD_BOTTOM = 24;
const PAD_LEFT = 38;
const PAD_RIGHT = 10;
const PLOT_H = 238;
const HEIGHT = PAD_TOP + PLOT_H + PAD_BOTTOM;

let tipEl = null;
let svgEl = null;
let headEl = null;
let enabled = false;
let initialised = false;
let inMap = false;
let lastEvent = null;
let lastLatLng = null;
let rafScheduled = false;
let lastSig = '';

function el(tag, attrs) {
  const node = document.createElementNS(SVG_NS, tag);
  if (attrs) {
    for (const k in attrs) node.setAttribute(k, attrs[k]);
  }
  return node;
}

function classAbbrev(a) {
  // Prefer the explicit ICAO class if present, otherwise abbreviate
  // the type_name to something readable in a 32 px column.
  if (a.class && a.class !== '?') return a.class;
  const t = a.type_name || '';
  const map = {
    Prohibited: 'PA', Restricted: 'RA',
    Danger: 'DA', Warning: 'WA', Alert: 'AL',
    // SUA = Special Use Airspace — FAA umbrella for prohibited /
    // restricted / MOA / warning / alert / CFA. OpenAIP sometimes
    // exports the bare "SUA" type without a more specific kind, so
    // surface it explicitly rather than letting the slice fallback
    // chop it.
    SUA: 'SUA',
    MOA: 'MOA', CFA: 'CFA', ADIZ: 'ADIZ',
    TRA: 'TRA', TSA: 'TSA',
    TMZ: 'TMZ', RMZ: 'RMZ',
    MATZ: 'MATZ', ATZ: 'ATZ', HTZ: 'HTZ',
    CTR: 'CTR', CTA: 'CTA', TMA: 'TMA', LTA: 'LTA', UTA: 'UTA',
    Airway: 'AWY', FIR: 'FIR', UIR: 'UIR',
    Gliding: 'GLD',
    TIZ: 'TIZ', TIA: 'TIA',
  };
  if (map[t]) return map[t];
  return t.slice(0, 4) || '?';
}

function groupSwatch(a) {
  const key = airspaceGroup(a);
  for (const g of AIRSPACE_GROUPS) {
    if (g.key === key) return g.swatch;
  }
  return AIRSPACE_GROUPS[AIRSPACE_GROUPS.length - 1].swatch;
}

function effectiveLowerFt(a) {
  return a.lower_ft == null ? 0 : a.lower_ft;
}
function effectiveUpperFt(a) {
  return a.upper_ft == null ? OPEN_TOP_FT : a.upper_ft;
}

// Column rendering: returns a <g> containing the rect + the upper /
// lower limit captions and a centered class abbreviation.
function renderColumn(a, x, yMaxFt) {
  const lowerFt = effectiveLowerFt(a);
  const upperFt = effectiveUpperFt(a);
  if (upperFt <= lowerFt) return null;
  const yTop = PAD_TOP + PLOT_H * (1 - Math.min(1, upperFt / yMaxFt));
  const yBot = PAD_TOP + PLOT_H * (1 - Math.min(1, lowerFt / yMaxFt));
  const h = Math.max(2, yBot - yTop);
  const swatch = groupSwatch(a);
  const fillOpacity = Math.min(0.55, swatch.fillOpacity * 3.2);

  const g = el('g');
  // Background rect filled with the group colour
  const rect = el('rect', {
    x, y: yTop, width: COL_W, height: h,
    fill: swatch.fill,
    'fill-opacity': fillOpacity,
    stroke: swatch.color,
    'stroke-width': 1.2,
  });
  if (swatch.dashArray) rect.setAttribute('stroke-dasharray', swatch.dashArray);
  g.appendChild(rect);

  // Class abbreviation centered vertically (or anchored top for very
  // tall columns so it stays in view when the user scans).
  const labelY = h > 24 ? Math.min(yBot - 6, yTop + 12) : (yTop + h / 2 + 3);
  const label = el('text', {
    x: x + COL_W / 2, y: labelY, class: 'slice-col-label',
  });
  label.textContent = classAbbrev(a);
  g.appendChild(label);

  // Upper limit caption above the rect — or "↑ open" when the airspace
  // ceiling is missing.
  const upperLabelEl = el('text', {
    x: x + COL_W / 2, y: yTop - 3, class: 'slice-col-edge',
  });
  upperLabelEl.textContent = a.upper_ft == null
    ? '↑'
    : formatLimit(a.upper_ft, a.upper_datum);
  g.appendChild(upperLabelEl);

  // Lower limit caption below the rect; only render when there's room
  // (>= 10 px between the rect base and the chart bottom).
  if (yBot < PAD_TOP + PLOT_H - 4) {
    const lowerLabelEl = el('text', {
      x: x + COL_W / 2, y: yBot + 9, class: 'slice-col-edge',
    });
    lowerLabelEl.textContent = a.lower_ft == null
      ? 'GND'
      : formatLimit(a.lower_ft, a.lower_datum);
    g.appendChild(lowerLabelEl);
  }

  return g;
}

function buildSvg(hits, planes, yMaxFt, totalHits) {
  const cols = Math.min(hits.length, MAX_COLS);
  // Reserve extra width on the right for plane callsign labels when
  // any nearby aircraft are present.
  const planeLabelW = planes.length > 0 ? 56 : 0;
  const width = PAD_LEFT + cols * (COL_W + COL_GAP) - COL_GAP + PAD_RIGHT + planeLabelW;
  const svg = el('svg', {
    width: String(width), height: String(HEIGHT),
    viewBox: `0 0 ${width} ${HEIGHT}`,
    xmlns: SVG_NS,
  });

  // Plot area extends past the columns when we're reserving room for
  // plane labels — so the gridlines should reach the column edge but
  // not run under the plane callsigns.
  const plotRightX = PAD_LEFT + cols * (COL_W + COL_GAP) - COL_GAP + 2;

  // Y-axis ticks (gridlines + labels).
  const ticks = pickTicks(yMaxFt);
  for (const ft of ticks) {
    const y = PAD_TOP + PLOT_H * (1 - Math.min(1, ft / yMaxFt));
    const line = el('line', {
      x1: PAD_LEFT - 2, x2: plotRightX,
      y1: y, y2: y, class: 'slice-grid',
    });
    svg.appendChild(line);
    const label = el('text', {
      x: PAD_LEFT - 4, y: y + 3, class: 'slice-tick',
      'text-anchor': 'end',
    });
    label.textContent = labelForTick(ft);
    svg.appendChild(label);
  }

  // Columns.
  for (let i = 0; i < cols; i++) {
    const x = PAD_LEFT + i * (COL_W + COL_GAP);
    const g = renderColumn(hits[i], x, yMaxFt);
    if (g) svg.appendChild(g);
  }

  // Nearby-aircraft markers — drawn after the columns so they paint on
  // top. Each is a horizontal dashed line spanning the column area, a
  // small triangle to the right of the columns, and a callsign label.
  for (const p of planes) {
    const y = PAD_TOP + PLOT_H * (1 - Math.min(1, Math.max(0, p.altitude) / yMaxFt));
    const dash = el('line', {
      x1: PAD_LEFT - 2, x2: plotRightX,
      y1: y, y2: y, class: 'slice-plane-line',
    });
    svg.appendChild(dash);
    // Triangle pointing left, anchored just past the column edge.
    const triX = plotRightX + 2;
    const tri = el('polygon', {
      points: `${triX + 6},${y - 4} ${triX + 6},${y + 4} ${triX},${y}`,
      class: 'slice-plane-marker',
    });
    svg.appendChild(tri);
    const label = el('text', {
      x: triX + 9, y: y + 3, class: 'slice-plane-label',
    });
    label.textContent = p.callsign;
    svg.appendChild(label);
  }

  // "+N more" caption when there are more hits than visible columns.
  const overflow = totalHits - cols;
  if (overflow > 0) {
    const more = el('text', {
      x: plotRightX, y: HEIGHT - 6, class: 'slice-more',
    });
    more.textContent = `+${overflow} more`;
    svg.appendChild(more);
  }

  return svg;
}

function querySliceHits(latlng) {
  const hits = [];
  const cache = state.airspacesCache;
  if (!Array.isArray(cache) || cache.length === 0) return hits;
  const enabledCats = state.airspaceCategories;
  const lon = latlng.lng;
  const lat = latlng.lat;
  for (const a of cache) {
    if (!a || !a.geometry) continue;
    if (!enabledCats.has(airspaceGroup(a))) continue;
    if (a._bbox && !bboxContains(a._bbox, lon, lat)) continue;
    if (!pointInGeometry(lon, lat, a.geometry)) continue;
    hits.push(a);
  }
  // Sort low-to-high so the leftmost column is the surface-rooted
  // airspace and ceiling-stacked entries fan out to the right.
  hits.sort((p, q) => {
    const pl = effectiveLowerFt(p);
    const ql = effectiveLowerFt(q);
    if (pl !== ql) return pl - ql;
    return effectiveUpperFt(p) - effectiveUpperFt(q);
  });
  return hits;
}

function pickYMax(hits, planes) {
  let max = 0;
  for (const a of hits) {
    const u = effectiveUpperFt(a);
    if (u > max) max = u;
  }
  for (const p of planes) {
    if (p.altitude > max) max = p.altitude;
  }
  for (const cap of Y_MAX_LADDER) {
    if (max <= cap) return cap;
  }
  return OPEN_TOP_FT;
}

// Returns aircraft within AIRCRAFT_NEARBY_PX screen pixels of the
// cursor that have a known altitude (skips ground/pre-altcode tracks).
// Sorted nearest-first; the renderer caps at MAX_AIRCRAFT_MARKERS to
// avoid label collisions. Reads from entry.data — the registry
// stores `{marker, trail, data: <snapshot fields>, …}` entries, not
// raw aircraft records.
function queryNearbyAircraft(latlng) {
  const out = [];
  const planes = state.aircraft;
  if (!planes || typeof planes.values !== 'function' || !state.map) return out;
  const cursorPt = state.map.latLngToContainerPoint(latlng);
  for (const entry of planes.values()) {
    const d = entry && entry.data;
    if (!d || d.lat == null || d.lon == null || d.altitude == null) continue;
    const pt = state.map.latLngToContainerPoint([d.lat, d.lon]);
    const dx = pt.x - cursorPt.x;
    const dy = pt.y - cursorPt.y;
    const px = Math.sqrt(dx * dx + dy * dy);
    if (px > AIRCRAFT_NEARBY_PX) continue;
    out.push({
      icao: d.icao,
      callsign: (d.callsign || '').trim() || (d.icao || '').toUpperCase(),
      altitude: d.altitude,
      pxDistance: px,
    });
  }
  out.sort((p, q) => p.pxDistance - q.pxDistance);
  return out.slice(0, MAX_AIRCRAFT_MARKERS);
}

function position(ev, w, h) {
  const PAD = 8;
  const GAP_X = 14;
  const GAP_Y = 18;
  const vw = window.innerWidth;
  const vh = window.innerHeight;
  let x = ev.clientX + GAP_X;
  let y = ev.clientY + GAP_Y;
  if (x + w + PAD > vw) x = ev.clientX - GAP_X - w;
  if (y + h + PAD > vh) y = ev.clientY - GAP_Y - h;
  x = Math.max(PAD, Math.min(vw - w - PAD, x));
  y = Math.max(PAD, Math.min(vh - h - PAD, y));
  tipEl.style.transform = `translate(${x}px, ${y}px)`;
}

function hide() {
  if (!tipEl) return;
  tipEl.classList.remove('open');
  lastSig = '';
}

function render() {
  rafScheduled = false;
  if (!enabled || !lastLatLng || !lastEvent) { hide(); return; }
  if (!state.map || state.map.getZoom() < AIRSPACE_MIN_ZOOM) { hide(); return; }

  const hits = querySliceHits(lastLatLng);
  // No airspaces under the cursor → suppress the slice entirely, even
  // if a plane happens to be nearby. The slice is fundamentally an
  // "what's the airspace stack here" affordance; the plane markers
  // are context inside it, not a reason to open it on their own.
  if (hits.length === 0) { hide(); return; }
  const planes = queryNearbyAircraft(lastLatLng);

  const yMax = pickYMax(hits, planes);
  // Cheap signature: if the visible content is unchanged, only update
  // the position and skip the SVG rebuild. The signature includes the
  // ids + altitude window + chosen yMax + plane altitudes.
  const sig = hits.slice(0, MAX_COLS)
    .map((a) => `${a.id || ''}:${a.lower_ft}:${a.upper_ft}`)
    .join('|') + `#${hits.length}@${yMax}!`
    + planes.map((p) => `${p.icao}:${p.altitude}`).join(',');
  if (sig !== lastSig) {
    const planeNote = planes.length > 0
      ? ` · ${planes.length} plane${planes.length === 1 ? '' : 's'}`
      : '';
    headEl.textContent = `${hits.length} airspace${hits.length === 1 ? '' : 's'}${planeNote}`;
    while (svgEl.firstChild) svgEl.removeChild(svgEl.firstChild);
    const newSvg = buildSvg(hits, planes, yMax, hits.length);
    svgEl.replaceWith(newSvg);
    svgEl = newSvg;
    lastSig = sig;
  }
  if (!tipEl.classList.contains('open')) tipEl.classList.add('open');
  position(lastEvent, tipEl.offsetWidth, tipEl.offsetHeight);
}

function schedule() {
  if (rafScheduled) return;
  rafScheduled = true;
  requestAnimationFrame(render);
}

function onMove(e) {
  if (!enabled) return;
  // Suppress when the cursor is over a Leaflet control (layers panel,
  // zoom buttons, attribution, our inline airspace-filter button).
  // Leaflet still fires `mousemove` on the map while the cursor sits
  // over these floating panels — but the cursor isn't pointing at a
  // geographic location, so the slice would just chase the user
  // around the layer picker.
  const target = e.originalEvent && e.originalEvent.target;
  if (target && target.closest && target.closest('.leaflet-control')) {
    hide();
    return;
  }
  lastEvent = e.originalEvent;
  lastLatLng = e.latlng;
  inMap = true;
  schedule();
}
function onOut() { inMap = false; hide(); }
function onIn() { inMap = true; }
function onMoveStart() { hide(); }
function onMoveEnd() {
  if (!enabled || !inMap || !lastEvent || !state.map) return;
  // The map shifted under a stationary cursor — re-project the last
  // DOM event so the slice queries the new lat/lon, not the pre-pan
  // one cached on the previous mousemove.
  try {
    lastLatLng = state.map.mouseEventToLatLng(lastEvent);
  } catch (_) { /* mouseEventToLatLng can throw if map is detaching */ }
  schedule();
}

export function initAirspaceSlice() {
  if (initialised) return;
  initialised = true;
  tipEl = document.createElement('div');
  tipEl.id = 'airspace-slice-tip';
  headEl = document.createElement('div');
  headEl.className = 'slice-head';
  tipEl.appendChild(headEl);
  // Placeholder svg so the first render's `replaceWith` has a node.
  svgEl = el('svg', { width: '0', height: '0' });
  tipEl.appendChild(svgEl);
  document.body.appendChild(tipEl);

  if (state.map) attachMapListeners();
}

function attachMapListeners() {
  state.map.on('mousemove', onMove);
  state.map.on('mouseout', onOut);
  state.map.on('mouseover', onIn);
  state.map.on('movestart zoomstart', onMoveStart);
  state.map.on('moveend zoomend', onMoveEnd);
}

// Recompute whether the slice should be shown from the two relevant
// state bits and apply the result. Called on boot, after the user
// toggles either the airspaces overlay or the slice feature toggle,
// and from openaip.js when the layer-on state flips.
export function syncSliceEnabled() {
  const want = !!(state.showAirspaces && state.airspaceSliceEnabled);
  enabled = want;
  if (!enabled) { hide(); return; }
  // Render immediately if the cursor is already inside the map.
  if (lastLatLng && lastEvent && inMap) schedule();
}

// Called from openaip.js after a successful renderAirspaces — the
// cache (and `_bbox` annotations) just changed underneath us, so a
// stationary cursor should still see the fresh data.
export function refreshSlice() {
  if (enabled && lastLatLng && lastEvent && inMap) {
    lastSig = '';
    schedule();
  }
}
