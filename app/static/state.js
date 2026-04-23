// Shared mutable state for the Flightjar frontend.
//
// Before this split, the whole app was one IIFE where every function had
// closure access to a pile of module-local variables. With the split,
// modules need a central place to read and write that shared state.
// Every mutable field that more than one module touches lives here;
// per-module private state stays inside its own file.
//
// Leaflet objects (map, canvas renderers, proxy layer groups) are null
// at load time and set by map_setup.js during `initMap()`. Consumers
// must not read them before map_setup has run.

import { ageOf } from './format.js';

export const RANGE_RECORD_THROTTLE_MS = 60_000;
export const GO_AROUND_COOLDOWN_MS = 5 * 60_000;
export const COUNT_HISTORY_LEN = 60;
export const FIRST_OF_DAY_KEY = 'flightjar.firstOfDay';
export const DEFAULT_SORT_DIR = {
  callsign: 1, altitude: -1, distance: 1, age: 1,
};

// List-filter toggles — 'mil' / 'emergency' / 'watched'. Empty means
// "show everything" (default). Persisted to localStorage so the last
// chosen filter survives refresh.
export const FILTER_KEYS = ['mil', 'emergency', 'watched'];
const FILTERS_STORAGE_KEY = 'flightjar.filters';

function readFilters() {
  try {
    const raw = localStorage.getItem(FILTERS_STORAGE_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter((k) => FILTER_KEYS.includes(k));
  } catch (_) {
    return [];
  }
}

export function writeFilters(set) {
  try {
    localStorage.setItem(FILTERS_STORAGE_KEY, JSON.stringify([...set]));
  } catch (_) { /* storage disabled */ }
}

export const state = {
  // Leaflet handles — populated by map_setup.initMap().
  map: null,
  trailsCanvas: null,
  airportsCanvas: null,
  airportsLayer: null,
  navaidsLayer: null,
  airspacesLayer: null,
  obstaclesLayer: null,
  reportingLayer: null,
  blackspotsLayer: null,
  labelsProxy: null,
  trailsProxy: null,
  airportsProxy: null,
  navaidsProxy: null,
  airspacesProxy: null,
  obstaclesProxy: null,
  reportingProxy: null,
  blackspotsProxy: null,
  coverageProxy: null,
  receiverLayer: null,
  hoverHalo: null,
  syncOverlay: null,
  buildRangeRings: null,
  renderAltLegend: null,

  // Aircraft bookkeeping.
  aircraft: new Map(),
  selectedIcao: null,
  lastSnap: null,
  lastSnapAt: 0,
  firstUpdate: true,
  pendingDeepLinkIcao: null,

  // Persistent UI toggles (mirrored to localStorage when they flip).
  showLabels: localStorage.getItem('flightjar.labels') !== '0',
  showTrails: localStorage.getItem('flightjar.trails') !== '0',
  followSelected: localStorage.getItem('flightjar.follow') === '1',
  compactMode: localStorage.getItem('flightjar.compact') === '1',
  showAirports: localStorage.getItem('flightjar.airports') === '1',
  showNavaids: localStorage.getItem('flightjar.navaids') === '1',
  showAirspaces: localStorage.getItem('flightjar.airspaces') === '1',
  showObstacles: localStorage.getItem('flightjar.obstacles') === '1',
  showReporting: localStorage.getItem('flightjar.reporting') === '1',
  showCoverage: localStorage.getItem('flightjar.coverage') === '1',
  showBlackspots: localStorage.getItem('flightjar.blackspots') === '1',
  // Tile overlays (OpenAIP + VFRMap) — persistence is keyed on the layer
  // directly; these flags just seed the layers-control starting state.
  showOpenaip: localStorage.getItem('flightjar.openaip') === '1',
  showIfrLow: localStorage.getItem('flightjar.ifr_low') === '1',
  showIfrHigh: localStorage.getItem('flightjar.ifr_high') === '1',

  // Sidebar state.
  searchFilter: '',
  // Active list filters ('mil' | 'emergency' | 'watched'). OR semantics:
  // an aircraft matches when any active filter matches. Empty set means
  // "show everything" — the default.
  activeFilters: new Set(readFilters()),
  hoveredFromListIcao: null,
  hoveredFromMapIcao: null,
  sortKey: 'callsign',
  sortDir: DEFAULT_SORT_DIR.callsign,

  // Session trackers (fed the egg-unlocked session stats card).
  sessionSeenSet: new Set(),
  sessionStartFrames: null,
  sessionMaxRangeKm: 0,
  sessionLongestTrailKm: 0,
  notableAnnounced: new Set(),
  lastRangeRecordToastAt: 0,
  goAroundFiredAt: new Map(),

  // "First contact of the day" icao, persisted across page reloads.
  firstOfDayIcao: null,

  // Server-authoritative last-seen map for watchlisted icaos (refreshed
  // when the watchlist dialog opens).
  watchlistLastSeen: {},

  // Watchlist module handle (createWatchlist() return value); wired in
  // app.js at boot before any module that calls state.watchlist runs.
  watchlist: null,

  // Detail panel DOM + content ref (populated during boot).
  detailPanelEl: null,
  detailContentHost: null,
  detailWatchBtn: null,
  appEl: null,
  detailPanelContent: null,
  aircraftInfoCache: new Map(),
};

export function todaysLocalDate() {
  const d = new Date();
  const m = String(d.getMonth() + 1).padStart(2, '0');
  const day = String(d.getDate()).padStart(2, '0');
  return `${d.getFullYear()}-${m}-${day}`;
}

export function readFirstOfDay() {
  try {
    const raw = localStorage.getItem(FIRST_OF_DAY_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (parsed && parsed.date === todaysLocalDate()) return parsed.icao || null;
  } catch (_) { /* corrupt storage — ignore */ }
  return null;
}

export function claimFirstOfDay(icao) {
  try {
    localStorage.setItem(
      FIRST_OF_DAY_KEY,
      JSON.stringify({ date: todaysLocalDate(), icao }),
    );
  } catch (_) { /* storage disabled */ }
}

export function getSessionStats() {
  const frames = state.lastSnap && state.sessionStartFrames != null
    ? Math.max(0, state.lastSnap.frames - state.sessionStartFrames)
    : 0;
  return {
    planesSeen: state.sessionSeenSet.size,
    messages: frames,
    maxRangeKm: state.sessionMaxRangeKm,
    longestTrailKm: state.sessionLongestTrailKm,
  };
}

export function sortValue(a, key, now) {
  switch (key) {
    case 'callsign': return (a.callsign || '').trim() || null;
    case 'altitude': return a.altitude;
    case 'age':      return ageOf(a, now);
    case 'distance': return a.distance_km;
  }
  return null;
}
