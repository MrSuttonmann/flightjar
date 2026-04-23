// OpenAIP overlays — airspaces (polygons), obstacles (markers),
// reporting points (markers). Each mirrors the airports/navaids pattern
// in map_controls.js: persist to localStorage, fetch the visible bbox
// on toggle-on and on moveend, redraw the layer. Min-zoom gates avoid
// hammering the backend at continental views — OpenAIP would return a
// massive result set the browser can't render usefully anyway.
//
// The /api/openaip/* endpoints snap bbox outward to a 2° grid and cache
// for 7 days server-side, so pans within a region after the first fetch
// are free. We still debounce moveend on the client to avoid firing
// mid-drag.

import { escapeHtml } from './format.js';
import {
  OBSTACLE_ICON,
  REPORTING_COMPULSORY_ICON,
  REPORTING_NONCOMPULSORY_ICON,
} from './point_icons.js';
import { state } from './state.js';

const FETCH_DEBOUNCE_MS = 300;

// Airspace colour rules. OpenAIP `type_name` wins (prohibited/danger
// should read red regardless of ICAO class); ICAO class is the
// fallback for plain controlled airspace. FillOpacity floor is 0.06
// — below that the tint washes out on dark basemaps and the user
// reads the layer as "not working".
const AIRSPACE_TYPE_STYLE = {
  Prohibited: { color: '#dc2626', fill: '#dc2626', fillOpacity: 0.18, dashArray: '6 3' },
  Restricted: { color: '#dc2626', fill: '#dc2626', fillOpacity: 0.14, dashArray: '6 3' },
  Danger:     { color: '#dc2626', fill: '#dc2626', fillOpacity: 0.10, dashArray: '2 4' },
  Warning:    { color: '#f97316', fill: '#f97316', fillOpacity: 0.10, dashArray: '2 4' },
  Alert:      { color: '#f97316', fill: '#f97316', fillOpacity: 0.10 },
  TMZ:        { color: '#f59e0b', fill: '#f59e0b', fillOpacity: 0.09, dashArray: '4 2' },
  RMZ:        { color: '#f59e0b', fill: '#f59e0b', fillOpacity: 0.09, dashArray: '2 2' },
  MATZ:       { color: '#84cc16', fill: '#84cc16', fillOpacity: 0.10 },
  ATZ:        { color: '#2563eb', fill: '#2563eb', fillOpacity: 0.09 },
  CTR:        { color: '#2563eb', fill: '#2563eb', fillOpacity: 0.12 },
  CTA:        { color: '#2563eb', fill: '#2563eb', fillOpacity: 0.07 },
  TMA:        { color: '#1d4ed8', fill: '#1d4ed8', fillOpacity: 0.07 },
  Airway:     { color: '#6b7280', fill: '#6b7280', fillOpacity: 0.06 },
  FIR:        { color: '#9ca3af', fill: '#9ca3af', fillOpacity: 0.06 },
  UIR:        { color: '#9ca3af', fill: '#9ca3af', fillOpacity: 0.06 },
  Gliding:    { color: '#a855f7', fill: '#a855f7', fillOpacity: 0.10 },
};
const AIRSPACE_CLASS_STYLE = {
  A: { color: '#2563eb', fill: '#2563eb', fillOpacity: 0.11 },
  B: { color: '#2563eb', fill: '#2563eb', fillOpacity: 0.10 },
  C: { color: '#2563eb', fill: '#2563eb', fillOpacity: 0.09 },
  D: { color: '#2563eb', fill: '#2563eb', fillOpacity: 0.08, dashArray: '4 2' },
  E: { color: '#16a34a', fill: '#16a34a', fillOpacity: 0.07 },
  F: { color: '#16a34a', fill: '#16a34a', fillOpacity: 0.06, dashArray: '2 3' },
  G: { color: '#6b7280', fill: '#6b7280', fillOpacity: 0.06, dashArray: '2 3' },
};
const AIRSPACE_DEFAULT = { color: '#6b7280', fill: '#6b7280', fillOpacity: 0.06 };

function airspaceLeafletStyle(a) {
  const s = (a.type_name && AIRSPACE_TYPE_STYLE[a.type_name])
    || (a.class && AIRSPACE_CLASS_STYLE[a.class])
    || AIRSPACE_DEFAULT;
  const style = {
    color: s.color,
    weight: 1.4,
    opacity: 0.85,
    fillColor: s.fill,
    fillOpacity: s.fillOpacity,
  };
  if (s.dashArray) style.dashArray = s.dashArray;
  return style;
}

function formatLimit(ft, datum) {
  if (ft == null) return '—';
  if (datum === 'FL') {
    const fl = Math.round(ft / 100).toString().padStart(3, '0');
    return `FL${fl}`;
  }
  if (datum === 'GND' && ft === 0) return 'GND';
  const thousands = (ft / 1000).toFixed(ft % 1000 === 0 ? 0 : 1);
  return `${thousands}k ${datum || 'ft'}`;
}

function airspaceTooltip(a) {
  const head = a.name ? `<b>${escapeHtml(a.name)}</b>` : '<b>Airspace</b>';
  const meta = [];
  if (a.type_name) meta.push(escapeHtml(a.type_name));
  if (a.class && a.class !== '?') meta.push(`Class ${escapeHtml(a.class)}`);
  const limits = `${formatLimit(a.lower_ft, a.lower_datum)} → ${formatLimit(a.upper_ft, a.upper_datum)}`;
  return `${head}${meta.length ? `<br>${meta.join(' · ')}` : ''}<br>${escapeHtml(limits)}`;
}

function renderAirspaces(rows, layer) {
  for (const a of rows) {
    if (!a.geometry) continue;
    const gj = L.geoJSON(
      { type: 'Feature', geometry: a.geometry, properties: {} },
      { style: airspaceLeafletStyle(a), interactive: true },
    );
    gj.bindTooltip(airspaceTooltip(a), { direction: 'top', sticky: true });
    gj.addTo(layer);
  }
}

function renderObstacles(rows, layer) {
  for (const o of rows) {
    const m = L.marker([o.lat, o.lon], { icon: OBSTACLE_ICON, keyboard: false });
    const label = o.name || o.type_name || 'Obstacle';
    const parts = [`<b>${escapeHtml(label)}</b>`];
    if (o.type_name && o.name) parts.push(escapeHtml(o.type_name));
    if (o.height_ft != null) parts.push(`${o.height_ft} ft AGL`);
    m.bindTooltip(parts.join('<br>'), { direction: 'top', sticky: true });
    m.addTo(layer);
  }
}

function renderReporting(rows, layer) {
  for (const r of rows) {
    const m = L.marker([r.lat, r.lon], {
      icon: r.compulsory ? REPORTING_COMPULSORY_ICON : REPORTING_NONCOMPULSORY_ICON,
      keyboard: false,
    });
    const label = r.name ? escapeHtml(r.name) : 'Reporting point';
    const tag = r.compulsory ? ' · Compulsory' : '';
    m.bindTooltip(`<b>${label}</b>${tag}`, { direction: 'top', sticky: true });
    m.addTo(layer);
  }
}

// Per-overlay config table — keeps the three setters + refresh logic
// identical apart from this data. `controlLabel` must match the label
// string used in map_setup.PROXY_OVERLAYS so we can find each overlay's
// <label> element in the layers-control DOM to annotate it when zoom
// is below the threshold.
const OVERLAYS = {
  airspaces: {
    path: '/api/openaip/airspaces',
    minZoom: 5,
    visibleFlag: 'showAirspaces',
    layerKey: 'airspacesLayer',
    proxyKey: 'airspacesProxy',
    storageKey: 'flightjar.airspaces',
    render: renderAirspaces,
    controlLabel: 'Airspaces',
  },
  obstacles: {
    path: '/api/openaip/obstacles',
    minZoom: 9,
    visibleFlag: 'showObstacles',
    layerKey: 'obstaclesLayer',
    proxyKey: 'obstaclesProxy',
    storageKey: 'flightjar.obstacles',
    render: renderObstacles,
    controlLabel: 'Obstacles',
  },
  reporting: {
    path: '/api/openaip/reporting_points',
    minZoom: 7,
    visibleFlag: 'showReporting',
    layerKey: 'reportingLayer',
    proxyKey: 'reportingProxy',
    storageKey: 'flightjar.reporting',
    render: renderReporting,
    controlLabel: 'Reporting points',
  },
};

const fetchState = {
  airspaces: { pending: null, debounce: null },
  obstacles: { pending: null, debounce: null },
  reporting: { pending: null, debounce: null },
};

// Locate the layers-control <label> for an overlay by its display name.
// Leaflet's Control.Layers empties and rebuilds its DOM every time a
// layer is added to or removed from the map (_update() is called from
// _onLayerChange), so any DOM references cached at init go stale the
// first time we flip a checkbox or restore a persisted overlay. Walk
// the live DOM on each call — the overlay list has ~10 entries so the
// lookup is trivial.
function findOverlayLabel(controlLabel) {
  const control = state.layersControl;
  if (!control) return null;
  const labels = control.getContainer()
    .querySelectorAll('.leaflet-control-layers-overlays label');
  for (const lab of labels) {
    const span = lab.querySelector('span > span') || lab.querySelector('span');
    if (!span) continue;
    // Text may have been rewritten by the zoom-gate annotation to e.g.
    // " Airspaces (zoom ≥ 5)", so match on the canonical prefix.
    const text = span.textContent.trim();
    if (text === controlLabel || text.startsWith(controlLabel + ' ')) {
      return { label: lab, span };
    }
  }
  return null;
}

function setOverlayLoading(kind, loading) {
  const entry = findOverlayLabel(OVERLAYS[kind].controlLabel);
  if (!entry) return;
  entry.label.classList.toggle('overlay-loading', loading);
}

function refresh(kind) {
  const spec = OVERLAYS[kind];
  const layer = state[spec.layerKey];
  if (!layer) return;
  if (!state[spec.visibleFlag]) { layer.clearLayers(); return; }
  const z = state.map.getZoom();
  if (z < spec.minZoom) {
    layer.clearLayers();
    console.info(`[openaip] ${kind}: zoom ${z} < minZoom ${spec.minZoom}, layer cleared`);
    return;
  }
  const b = state.map.getBounds();
  const url = `${spec.path}?min_lat=${b.getSouth()}&min_lon=${b.getWest()}`
            + `&max_lat=${b.getNorth()}&max_lon=${b.getEast()}`;
  const slot = fetchState[kind];
  if (slot.pending) slot.pending.abort?.();
  const ctrl = new AbortController();
  slot.pending = ctrl;
  setOverlayLoading(kind, true);
  const started = performance.now();
  fetch(url, { signal: ctrl.signal })
    .then(r => {
      if (!r.ok) {
        console.warn(`[openaip] ${kind}: HTTP ${r.status} for ${url}`);
        return [];
      }
      return r.json();
    })
    .then(rows => {
      if (!state[spec.visibleFlag]) return;
      layer.clearLayers();
      spec.render(rows, layer);
      const ms = Math.round(performance.now() - started);
      console.info(`[openaip] ${kind}: ${rows.length} features at zoom ${z} (${ms}ms)`);
    })
    .catch((e) => { if (e.name !== 'AbortError') console.warn(`[openaip] ${kind} fetch failed`, e); })
    .finally(() => {
      // Only clear the spinner if *this* fetch is still the pending one.
      // A rapid pan sequence aborts the old controller and starts a new
      // one before the old promise settles — the stale .finally must not
      // switch the spinner off while the new fetch is still in flight.
      if (slot.pending === ctrl) {
        slot.pending = null;
        setOverlayLoading(kind, false);
      }
    });
}

function schedule(kind) {
  const slot = fetchState[kind];
  clearTimeout(slot.debounce);
  slot.debounce = setTimeout(() => refresh(kind), FETCH_DEBOUNCE_MS);
}

function applyToggle(kind) {
  const spec = OVERLAYS[kind];
  const layer = state[spec.layerKey];
  const proxy = state[spec.proxyKey];
  if (!layer || !proxy) return;
  if (state[spec.visibleFlag]) {
    if (!state.map.hasLayer(layer)) layer.addTo(state.map);
    refresh(kind);
  } else {
    state.map.removeLayer(layer);
    layer.clearLayers();
  }
  state.syncOverlay(proxy, state[spec.visibleFlag]);
}

function makeSetter(kind) {
  const spec = OVERLAYS[kind];
  return (value) => {
    state[spec.visibleFlag] = value;
    try { localStorage.setItem(spec.storageKey, value ? '1' : '0'); } catch (_) {}
    applyToggle(kind);
  };
}

export const setAirspaces = makeSetter('airspaces');
export const setObstacles = makeSetter('obstacles');
export const setReporting = makeSetter('reporting');

// Mark each zoom-gated overlay as available / unavailable at the
// current zoom. When unavailable we append "(zoom ≥ N)" so the user
// sees *how far* they need to zoom, and toggle a CSS class that dims
// the row so it reads as "not actionable right now".
function updateZoomAvailability() {
  const z = state.map.getZoom();
  for (const spec of Object.values(OVERLAYS)) {
    const entry = findOverlayLabel(spec.controlLabel);
    if (!entry) continue;
    const available = z >= spec.minZoom;
    entry.label.classList.toggle('overlay-zoom-unavailable', !available);
    entry.span.textContent = available
      ? ` ${spec.controlLabel}`
      : ` ${spec.controlLabel} (zoom ≥ ${spec.minZoom})`;
  }
}

// Called from initMapControls after state.map exists. Attaches the
// moveend listener + replays persisted toggles.
export function initOpenaipOverlays() {
  state.map.on('moveend', () => {
    for (const kind of Object.keys(OVERLAYS)) {
      if (state[OVERLAYS[kind].visibleFlag]) schedule(kind);
    }
  });
  for (const kind of Object.keys(OVERLAYS)) {
    applyToggle(kind);
  }
  updateZoomAvailability();
  state.map.on('zoomend', updateZoomAvailability);
  // Leaflet re-renders the layers-control DOM on every overlay toggle,
  // stripping our annotations. Re-apply after each change.
  state.map.on('overlayadd overlayremove', updateZoomAvailability);
}
