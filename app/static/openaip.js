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

import { AIRSPACE_GROUPS, airspaceGroup } from './airspace_groups.js';
import { escapeHtml } from './format.js';
import { bboxOfGeometry } from './geo.js';
import {
  OBSTACLE_ICON,
  REPORTING_COMPULSORY_ICON,
  REPORTING_NONCOMPULSORY_ICON,
} from './point_icons.js';
import { state } from './state.js';

export { AIRSPACE_GROUPS, airspaceGroup };

const FETCH_DEBOUNCE_MS = 300;

// Optional listeners owned by other modules. The airspace vertical
// slice tooltip subscribes to both: visibility flips show/hide it,
// cache updates re-query the stack under a stationary cursor.
let onAirspaceVisibilityChanged = null;
let onAirspaceCacheUpdated = null;
export function setAirspaceVisibilityListener(fn) { onAirspaceVisibilityChanged = fn; }
export function setAirspaceCacheListener(fn) { onAirspaceCacheUpdated = fn; }

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

export function formatLimit(ft, datum) {
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
  // Cache the raw rows so the filter dialog can re-render without
  // re-hitting the backend. We cache even the unfiltered subset that's
  // been excluded by the current group toggles — a user who re-enables a
  // group should see those features pop back instantly.
  state.airspacesCache = rows;
  // Pre-compute axis-aligned bboxes so the airspace_slice mousemove
  // handler can cheap-reject most polygons before the full ring scan.
  // One pass per fetch (a few hundred polygons at most); cleared with
  // the cache when a new fetch completes.
  for (const a of rows) {
    if (a && a.geometry && !a._bbox) a._bbox = bboxOfGeometry(a.geometry);
  }
  const enabled = state.airspaceCategories;
  for (const a of rows) {
    if (!a.geometry) continue;
    if (!enabled.has(airspaceGroup(a))) continue;
    const gj = L.geoJSON(
      { type: 'Feature', geometry: a.geometry, properties: {} },
      { style: airspaceLeafletStyle(a), interactive: true },
    );
    gj.bindTooltip(airspaceTooltip(a), { direction: 'top', sticky: true });
    gj.addTo(layer);
  }
  onAirspaceCacheUpdated?.();
}

// Re-render airspaces from the in-memory cache without re-fetching.
// Called from the subcategory filter dialog after a group is toggled.
export function reapplyAirspaceFilters() {
  const layer = state.airspacesLayer;
  if (!layer) return;
  if (!state.showAirspaces) return;
  layer.clearLayers();
  renderAirspaces(state.airspacesCache || [], layer);
}

// Count of features per group in the current cache, for the filter
// dialog's "(N)" annotations. Returns a plain object keyed by group key.
export function airspaceGroupCounts() {
  const counts = Object.fromEntries(AIRSPACE_GROUPS.map((g) => [g.key, 0]));
  for (const a of state.airspacesCache || []) {
    if (!a.geometry) continue;
    counts[airspaceGroup(a)] = (counts[airspaceGroup(a)] || 0) + 1;
  }
  return counts;
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
    if (kind === 'airspaces') onAirspaceVisibilityChanged?.(value);
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
  ensureAirspaceFilterButton();
}

// Airspace row gets a tiny filter icon next to the label that opens
// the subcategory dialog. Leaflet rebuilds the layers-control DOM on
// every overlay toggle (same reason updateZoomAvailability has to be
// re-applied), so we re-inject the button on each call and rely on
// idempotency: `data-fj-filter-btn` guards against duplicate buttons
// inside one render.
let openAirspaceFilters = null;
export function setAirspaceFiltersOpener(fn) { openAirspaceFilters = fn; }

function ensureAirspaceFilterButton() {
  const entry = findOverlayLabel(OVERLAYS.airspaces.controlLabel);
  if (!entry) return;
  if (entry.label.querySelector('[data-fj-filter-btn]')) return;
  const btn = document.createElement('button');
  btn.type = 'button';
  btn.dataset.fjFilterBtn = '1';
  btn.className = 'airspace-filter-btn';
  btn.setAttribute('aria-label', 'Airspace filters');
  btn.title = 'Filter airspace subcategories';
  // lucide: sliders-horizontal — matches the rest of the map chrome.
  btn.innerHTML =
    '<svg viewBox="0 0 24 24" width="13" height="13" aria-hidden="true" '
    + 'fill="none" stroke="currentColor" stroke-width="2" '
    + 'stroke-linecap="round" stroke-linejoin="round">'
    + '<line x1="21" y1="4" x2="14" y2="4"/><line x1="10" y1="4" x2="3" y2="4"/>'
    + '<line x1="21" y1="12" x2="12" y2="12"/><line x1="8" y1="12" x2="3" y2="12"/>'
    + '<line x1="21" y1="20" x2="16" y2="20"/><line x1="12" y1="20" x2="3" y2="20"/>'
    + '<line x1="14" y1="2" x2="14" y2="6"/><line x1="8" y1="10" x2="8" y2="14"/>'
    + '<line x1="16" y1="18" x2="16" y2="22"/></svg>';
  // Clicking inside the label would otherwise toggle the checkbox — stop
  // the event so pressing the filter icon doesn't also disable the layer.
  const swallow = (e) => {
    e.preventDefault();
    e.stopPropagation();
  };
  btn.addEventListener('click', (e) => {
    swallow(e);
    openAirspaceFilters?.();
  });
  // Leaflet also listens on mousedown/dblclick for control interactions.
  btn.addEventListener('mousedown', (e) => e.stopPropagation());
  btn.addEventListener('dblclick', swallow);
  entry.label.appendChild(btn);
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
