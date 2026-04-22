// Everything toggle-shaped in the map UI: layer visibility setters
// (labels, trails, airports, coverage), the compact-mode sidebar hide,
// the Follow + Home buttons, the unit-system switch, the filters
// (search + sort) panel collapse, and overlay fetchers for the
// airports + polar-coverage layers.

import { applyFollowState, setFollow } from './detail_panel.js';
import { applyLabelsVisibility } from './labels.js';
import { applyTrailsVisibility } from './trails.js';
import { escapeHtml } from './format.js';
import { getUnitSystem, setUnitSystem, uconv } from './units.js';
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

function makeIconControl({ className, title, pathD, onClick }) {
  return L.Control.extend({
    options: { position: 'topright' },
    onAdd() {
      const a = L.DomUtil.create('a', 'leaflet-bar leaflet-control ' + className);
      a.href = '#'; a.title = title;
      a.setAttribute('role', 'button');
      a.setAttribute('aria-label', title);
      a.innerHTML =
        `<svg viewBox="0 0 20 20" width="16" height="16" aria-hidden="true" ` +
          `fill="none" stroke="currentColor" stroke-width="1.8" ` +
          `stroke-linecap="round" stroke-linejoin="round">${pathD}</svg>`;
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
    pathD:
      `<path d="M10,2 L11.5,9 L18,11 L18,12.5 L11.5,10.5 L11.5,15 ` +
      `L13,17 L13,17.5 L7,17.5 L7,17 L8.5,15 L8.5,10.5 L2,12.5 ` +
      `L2,11 L8.5,9 Z" fill="currentColor" stroke-linejoin="round"/>`,
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

  // Polar coverage overlay — periodic refresh while visible.
  applyCoverageToggle();
  setInterval(() => { if (state.showCoverage) refreshCoverage(); }, 60_000);

  // Home control.
  const HomeControl = L.Control.extend({
    options: { position: 'bottomright' },
    onAdd() {
      const a = L.DomUtil.create('a', 'leaflet-bar leaflet-control home-control');
      a.href = '#';
      a.title = 'Re-centre on receiver (H)';
      a.setAttribute('role', 'button');
      a.setAttribute('aria-label', 'Re-centre on receiver');
      a.innerHTML = (
        '<svg viewBox="0 0 20 20" width="20" height="20" aria-hidden="true">' +
          '<circle cx="10" cy="10" r="6" fill="none" stroke="currentColor" stroke-width="1.6"/>' +
          '<circle cx="10" cy="10" r="2.2" fill="currentColor"/>' +
          '<line x1="10" y1="1" x2="10" y2="4"  stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>' +
          '<line x1="10" y1="16" x2="10" y2="19" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>' +
          '<line x1="1" y1="10" x2="4" y2="10"  stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>' +
          '<line x1="16" y1="10" x2="19" y2="10" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>' +
        '</svg>'
      );
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
