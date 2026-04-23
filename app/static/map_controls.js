// Everything toggle-shaped in the map UI: layer visibility setters
// (labels, trails, airports, coverage), the compact-mode sidebar hide,
// the Follow + Home buttons, the unit-system switch, the filters
// (search + sort) panel collapse, and overlay fetchers for the
// airports + polar-coverage layers.

import { applyFollowState, setFollow } from './detail_panel.js';
import { applyLabelsVisibility } from './labels.js';
import { applyTrailsVisibility } from './trails.js';
import { escapeHtml } from './format.js';
import { lucide } from './icons_lib.js';
import { getUnitSystem, setUnitSystem, uconv } from './units.js';
import { initBlackspotsOverlay } from './blackspots.js';
import { initOpenaipOverlays } from './openaip.js';
import { renderSidebar } from './sidebar.js';
import { state } from './state.js';
import { updatePopupContent } from './detail_panel.js';

// ---- labels + trails ----

export function setLabels(value) {
  state.showLabels = value;
  try { localStorage.setItem('flightjar.labels', value ? '1' : '0'); } catch (_) {}
  applyLabelsVisibility();
}

export function setTrails(value) {
  state.showTrails = value;
  try { localStorage.setItem('flightjar.trails', value ? '1' : '0'); } catch (_) {}
  applyTrailsVisibility();
}

// ---- compact sidebar ----

function applyCompactMode() {
  document.body.classList.toggle('compact-mode', state.compactMode);
  const toggleBtn = document.getElementById('sidebar-toggle');
  toggleBtn.title = state.compactMode ? 'Show sidebar (C)' : 'Hide sidebar (C)';
  state.map.invalidateSize({ pan: false });
}

export function setCompact(value) {
  state.compactMode = value;
  try { localStorage.setItem('flightjar.compact', value ? '1' : '0'); } catch (_) {}
  applyCompactMode();
}

// ---- airports overlay ----

let airportsFetchPending = null;
let airportsDebounce = null;

function refreshAirports() {
  if (!state.showAirports) { state.airportsLayer.clearLayers(); return; }
  const b = state.map.getBounds();
  const url = `/api/airports?min_lat=${b.getSouth()}&min_lon=${b.getWest()}`
            + `&max_lat=${b.getNorth()}&max_lon=${b.getEast()}&limit=2000`;
  if (airportsFetchPending) airportsFetchPending.abort?.();
  const ctrl = new AbortController();
  airportsFetchPending = ctrl;
  fetch(url, { signal: ctrl.signal })
    .then(r => r.ok ? r.json() : [])
    .then(rows => {
      if (!state.showAirports) return;
      state.airportsLayer.clearLayers();
      for (const a of rows) {
        const m = L.circleMarker([a.lat, a.lon], {
          renderer: state.airportsCanvas,
          radius: a.type === 'large_airport' ? 4 : a.type === 'medium_airport' ? 3 : 2,
          color: '#0e1116', weight: 1,
          fillColor: '#fbbf24', fillOpacity: 0.9,
        });
        m.bindTooltip(`${escapeHtml(a.name)} (${escapeHtml(a.icao)})`,
          { direction: 'top', sticky: true });
        m.addTo(state.airportsLayer);
      }
    })
    .catch((e) => { if (e.name !== 'AbortError') console.warn('airports fetch', e); });
}

function scheduleAirportRefresh() {
  clearTimeout(airportsDebounce);
  airportsDebounce = setTimeout(refreshAirports, 250);
}

function applyAirportsToggle() {
  if (state.showAirports) {
    if (!state.map.hasLayer(state.airportsLayer)) state.airportsLayer.addTo(state.map);
    refreshAirports();
  } else {
    state.map.removeLayer(state.airportsLayer);
    state.airportsLayer.clearLayers();
  }
  state.syncOverlay(state.airportsProxy, state.showAirports);
}

export function setAirports(value) {
  state.showAirports = value;
  try { localStorage.setItem('flightjar.airports', value ? '1' : '0'); } catch (_) {}
  applyAirportsToggle();
}

// ---- navaids overlay ----

// Colour + radius per navaid type. VOR family gets the most visual weight
// (biggest, green) because they anchor airways; DME/TACAN and NDBs are
// secondary. Any unknown type falls through to the default.
const NAVAID_STYLE = {
  VORTAC:    { color: '#16a34a', radius: 4 },
  'VOR-DME': { color: '#16a34a', radius: 4 },
  VOR:       { color: '#16a34a', radius: 4 },
  DME:       { color: '#3b82f6', radius: 3 },
  TACAN:     { color: '#3b82f6', radius: 3 },
  'NDB-DME': { color: '#f97316', radius: 3 },
  NDB:       { color: '#f97316', radius: 3 },
};
const NAVAID_STYLE_DEFAULT = { color: '#9ca3af', radius: 2 };

function formatNavaidFrequency(khz, type) {
  if (khz == null) return '';
  // VOR/DME/TACAN broadcast in MHz; NDBs in kHz. The CSV stores everything
  // in kHz, so divide for the VHF family.
  if (type === 'NDB' || type === 'NDB-DME') {
    return `${Math.round(khz)} kHz`;
  }
  const mhz = khz / 1000;
  return `${mhz.toFixed(mhz < 100 ? 3 : 2)} MHz`;
}

let navaidsFetchPending = null;
let navaidsDebounce = null;

function refreshNavaids() {
  if (!state.showNavaids) { state.navaidsLayer.clearLayers(); return; }
  const b = state.map.getBounds();
  const url = `/api/navaids?min_lat=${b.getSouth()}&min_lon=${b.getWest()}`
            + `&max_lat=${b.getNorth()}&max_lon=${b.getEast()}&limit=2000`;
  if (navaidsFetchPending) navaidsFetchPending.abort?.();
  const ctrl = new AbortController();
  navaidsFetchPending = ctrl;
  fetch(url, { signal: ctrl.signal })
    .then(r => r.ok ? r.json() : [])
    .then(rows => {
      if (!state.showNavaids) return;
      state.navaidsLayer.clearLayers();
      for (const n of rows) {
        const style = NAVAID_STYLE[n.type] || NAVAID_STYLE_DEFAULT;
        const m = L.circleMarker([n.lat, n.lon], {
          renderer: state.airportsCanvas,
          radius: style.radius,
          color: '#0e1116', weight: 1,
          fillColor: style.color, fillOpacity: 0.9,
        });
        const freq = formatNavaidFrequency(n.frequency_khz, n.type);
        const tip = `<b>${escapeHtml(n.ident)}</b> · ${escapeHtml(n.type)}`
          + (freq ? ` · ${escapeHtml(freq)}` : '')
          + (n.name ? `<br>${escapeHtml(n.name)}` : '');
        m.bindTooltip(tip, { direction: 'top', sticky: true });
        m.addTo(state.navaidsLayer);
      }
    })
    .catch((e) => { if (e.name !== 'AbortError') console.warn('navaids fetch', e); });
}

function scheduleNavaidRefresh() {
  clearTimeout(navaidsDebounce);
  navaidsDebounce = setTimeout(refreshNavaids, 250);
}

function applyNavaidsToggle() {
  if (state.showNavaids) {
    if (!state.map.hasLayer(state.navaidsLayer)) state.navaidsLayer.addTo(state.map);
    refreshNavaids();
  } else {
    state.map.removeLayer(state.navaidsLayer);
    state.navaidsLayer.clearLayers();
  }
  state.syncOverlay(state.navaidsProxy, state.showNavaids);
}

export function setNavaids(value) {
  state.showNavaids = value;
  try { localStorage.setItem('flightjar.navaids', value ? '1' : '0'); } catch (_) {}
  applyNavaidsToggle();
}

// ---- polar coverage overlay ----

const coverageLayer = L.layerGroup();
let coveragePoly = null;
let coverageFetchInFlight = false;

// Great-circle destination from (lat,lon) after travelling `distKm` km
// on initial bearing `bearingDeg`. Used to draw the coverage polygon
// as a set of end-points rather than shipping lat/lon from the server.
function destinationPoint(lat, lon, bearingDeg, distKm) {
  const R = 6371;
  const phi1 = lat * Math.PI / 180;
  const lam1 = lon * Math.PI / 180;
  const theta = bearingDeg * Math.PI / 180;
  const d = distKm / R;
  const phi2 = Math.asin(
    Math.sin(phi1) * Math.cos(d) + Math.cos(phi1) * Math.sin(d) * Math.cos(theta),
  );
  const lam2 = lam1 + Math.atan2(
    Math.sin(theta) * Math.sin(d) * Math.cos(phi1),
    Math.cos(d) - Math.sin(phi1) * Math.sin(phi2),
  );
  return [phi2 * 180 / Math.PI, ((lam2 * 180 / Math.PI) + 540) % 360 - 180];
}

async function refreshCoverage() {
  if (coverageFetchInFlight) return;
  coverageFetchInFlight = true;
  try {
    const r = await fetch('/api/coverage');
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    const data = await r.json();
    coverageLayer.clearLayers();
    coveragePoly = null;
    const rx = data.receiver;
    if (!rx || rx.lat == null || rx.lon == null) return;
    if (!data.bearings || data.bearings.length < 3) return;
    const sorted = [...data.bearings].sort((a, b) => a.angle - b.angle);
    const pts = sorted.map(b => destinationPoint(rx.lat, rx.lon, b.angle, b.dist_km));
    coveragePoly = L.polygon(pts, {
      color: '#5fa8ff', weight: 1.5, opacity: 0.75,
      fillColor: '#5fa8ff', fillOpacity: 0.08,
      interactive: false,
    });
    coveragePoly.addTo(coverageLayer);
  } catch (e) {
    console.warn('coverage fetch failed', e);
  } finally {
    coverageFetchInFlight = false;
  }
}

function applyCoverageToggle() {
  if (state.showCoverage) {
    if (!state.map.hasLayer(coverageLayer)) coverageLayer.addTo(state.map);
    refreshCoverage();
  } else {
    state.map.removeLayer(coverageLayer);
    coverageLayer.clearLayers();
    coveragePoly = null;
  }
  state.syncOverlay(state.coverageProxy, state.showCoverage);
}

export function setCoverage(value) {
  state.showCoverage = value;
  try { localStorage.setItem('flightjar.coverage', value ? '1' : '0'); } catch (_) {}
  applyCoverageToggle();
}

// ---- Home control ----

export function goHome() {
  const rx = state.lastSnap?.receiver;
  if (!rx || rx.lat == null || rx.lon == null) return;
  state.map.panTo([rx.lat, rx.lon]);
}

// ---- Filters panel collapse ----

export function setFiltersCollapsed(value) {
  document.body.classList.toggle('filters-collapsed', value);
}

// ---- boot wiring ----

function makeIconControl({ className, title, iconHtml, onClick }) {
  return L.Control.extend({
    options: { position: 'topright' },
    onAdd() {
      const a = L.DomUtil.create('a', 'leaflet-bar leaflet-control ' + className);
      a.href = '#'; a.title = title;
      a.setAttribute('role', 'button');
      a.setAttribute('aria-label', title);
      a.innerHTML = iconHtml;
      L.DomEvent
        .on(a, 'click', (e) => {
          L.DomEvent.preventDefault(e);
          L.DomEvent.stopPropagation(e);
          onClick();
        })
        .on(a, 'dblclick', L.DomEvent.stopPropagation);
      return a;
    },
  });
}

function renderUnitSwitch() {
  // Scoped to #unit-switch so the view toggles (which share .unit-btn for
  // styling) aren't dragged into the unit-switch's active-state bookkeeping.
  document.querySelectorAll('#unit-switch .unit-btn').forEach(el => {
    el.classList.toggle('active', el.dataset.unit === getUnitSystem());
  });
}

export function initMapControls() {
  // Pick up persisted unit system before anything renders.
  setUnitSystem(localStorage.getItem('flightjar.units') || 'nautical');

  // Unit-system buttons.
  document.querySelectorAll('#unit-switch .unit-btn').forEach(el => {
    el.addEventListener('click', () => {
      setUnitSystem(el.dataset.unit);
      try { localStorage.setItem('flightjar.units', getUnitSystem()); } catch (_) {}
      renderUnitSwitch();
      state.renderAltLegend();
      // Range-ring distances change with the unit system — rebuild.
      if (state.lastSnap?.receiver) state.buildRangeRings(state.lastSnap.receiver);
      if (state.lastSnap) {
        renderSidebar(state.lastSnap);
        // Refresh the open detail panel so its units update immediately.
        if (state.selectedIcao && state.detailPanelContent) {
          const entry = state.aircraft.get(state.selectedIcao);
          if (entry) {
            updatePopupContent(
              state.detailPanelContent, entry.data,
              state.lastSnap.now, state.lastSnap.airports,
            );
          }
        }
      }
    });
  });
  renderUnitSwitch();

  // Labels + trails start from their persisted state.
  applyLabelsVisibility();
  applyTrailsVisibility();

  // Follow control.
  const FollowControl = makeIconControl({
    className: 'follow-control',
    title: 'Follow selected aircraft',
    iconHtml: lucide('navigation', { size: 16, strokeWidth: 1.8 }),
    onClick: () => setFollow(!state.followSelected),
  });
  state.map.addControl(new FollowControl());
  applyFollowState();

  // Compact-mode toggle + sidebar handle.
  document.getElementById('sidebar-toggle')
    .addEventListener('click', () => setCompact(!state.compactMode));
  document.getElementById('sidebar-handle')
    .addEventListener('click', () => setCompact(!state.compactMode));
  applyCompactMode();

  // Airports overlay — refresh on pan/zoom when visible.
  state.map.on('moveend', () => { if (state.showAirports) scheduleAirportRefresh(); });
  applyAirportsToggle();

  // Navaids overlay — same refresh-on-moveend pattern.
  state.map.on('moveend', () => { if (state.showNavaids) scheduleNavaidRefresh(); });
  applyNavaidsToggle();

  // Polar coverage overlay — periodic refresh while visible.
  applyCoverageToggle();
  setInterval(() => { if (state.showCoverage) refreshCoverage(); }, 60_000);

  // OpenAIP overlays (airspaces / obstacles / reporting points). Wires
  // its own moveend listener and replays persisted toggles.
  initOpenaipOverlays();

  // Terrain blackspots — static grid, fetched once on first toggle-on.
  initBlackspotsOverlay();

  // Home control.
  const HomeControl = L.Control.extend({
    options: { position: 'bottomright' },
    onAdd() {
      const a = L.DomUtil.create('a', 'leaflet-bar leaflet-control home-control');
      a.href = '#';
      a.title = 'Re-centre on receiver (H)';
      a.setAttribute('role', 'button');
      a.setAttribute('aria-label', 'Re-centre on receiver');
      a.innerHTML = lucide('crosshair', { size: 18, strokeWidth: 1.8 });
      L.DomEvent
        .on(a, 'click', (e) => {
          L.DomEvent.preventDefault(e);
          L.DomEvent.stopPropagation(e);
          goHome();
        })
        .on(a, 'dblclick', L.DomEvent.stopPropagation);
      return a;
    },
  });
  state.map.addControl(new HomeControl());

  // Filters panel — collapsed by default on narrow viewports.
  const narrowMQ = window.matchMedia('(max-width: 600px)');
  if (narrowMQ.matches) document.body.classList.add('filters-collapsed');
  document.getElementById('filters-toggle').addEventListener('click', () => {
    setFiltersCollapsed(!document.body.classList.contains('filters-collapsed'));
  });
}
