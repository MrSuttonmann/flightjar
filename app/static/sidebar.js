// Sidebar + sort bar. renderSidebar() is called on every snapshot tick
// plus on a handful of state-change events (search input, sort chip
// click, watchlist toggle, etc.). Rows are DOM-diffed per icao: each
// row's rendered Element is cached and reused while its fingerprint
// holds, and DOM order is patched in place with insertBefore so a
// steady-cruise tick doesn't touch DOM that didn't actually change.

import { compassIcon, escapeHtml, flagIcon, fmt } from './format.js';
import { isNotable, militaryLabel } from './notable_aircraft.js';
import { matchesActiveFilters } from './filters.js';
import { routeLabel, selectAircraft } from './detail_panel.js';
import { applyFilterVisibility, setHoverHalo } from './trails.js';
import {
  COUNT_HISTORY_LEN,
  DEFAULT_SORT_DIR,
  FILTER_KEYS,
  sortValue,
  state,
  writeFilters,
} from './state.js';
import { signalBars } from './profile.js';
import { trendInfo } from './trend.js';
import { uconv } from './units.js';

// Rolling aircraft-count history — fed into the Stats dialog sparkline.
// ~1 minute at 1Hz snapshot rate.
export const countHistory = [];

// Messages/sec from successive snapshots (server ships cumulative frame
// counter, we diff here). Needs two samples before anything renders.
let prevFrames = null;
let prevFramesAt = null;

function updateMsgRate(snap) {
  const el = document.getElementById('msg-rate');
  if (snap.frames == null || snap.now == null) { el.textContent = ''; return; }
  if (prevFrames != null && snap.now > prevFramesAt) {
    const rate = (snap.frames - prevFrames) / (snap.now - prevFramesAt);
    el.textContent = `${rate.toFixed(rate < 10 ? 1 : 0)} msg/s`;
  }
  prevFrames = snap.frames;
  prevFramesAt = snap.now;
}

// Per-row DOM cache. On a steady-cruise tick the sidebar fingerprint
// of most rows is identical, so we keep the previously-built row
// Elements alive and reuse them — the browser only re-parses HTML for
// rows whose fingerprint actually changed, and the order is patched in
// place with insertBefore rather than blowing away list.innerHTML.
const rowElementCache = new Map();  // icao -> { fp, el }
let lastEmptyMsg = null;
let lastStatusText = '';
let lastSiteName = '';
let lastDocTitle = '';

// Scratch host used to parse a row's HTML string into a real Element.
// Reused across calls — the parsed node becomes detached as soon as we
// overwrite innerHTML, so callers who've stashed the reference keep
// their own detached node intact.
const rowBuilder = document.createElement('div');
function buildRowElement(html) {
  rowBuilder.innerHTML = html;
  return rowBuilder.firstElementChild;
}

// Lets dialogs / etc. call renderStats when the stats dialog is open
// without importing dialogs.js (which imports *us* for countHistory).
let _renderStats = () => {};
export function setRenderStats(fn) { _renderStats = fn; }

export function renderSortBar() {
  document.querySelectorAll('.sort-chip').forEach(el => {
    const active = el.dataset.key === state.sortKey;
    el.classList.toggle('active', active);
    el.querySelector('.arrow').textContent =
      active ? (state.sortDir === 1 ? '↑' : '↓') : '';
  });
}

function renderFilterBar() {
  document.querySelectorAll('.filter-chip').forEach(el => {
    el.classList.toggle('active', state.activeFilters.has(el.dataset.filter));
  });
}

export function renderSidebar(snap) {
  // Push the current count onto the rolling history (consumed by the
  // Stats dialog sparkline when it's open).
  countHistory.push(snap.count);
  if (countHistory.length > COUNT_HISTORY_LEN) countHistory.shift();
  const statsDialog = document.getElementById('stats-dialog');
  if (statsDialog?.open) _renderStats(snap);
  updateMsgRate(snap);

  const statusText = `${snap.count} aircraft · ${snap.positioned} positioned`;
  if (statusText !== lastStatusText) {
    document.getElementById('status-text').textContent = statusText;
    lastStatusText = statusText;
  }
  const site = snap.site_name || '';
  if (site !== lastSiteName) {
    document.getElementById('site-name').textContent = site;
    lastSiteName = site;
  }
  const docTitle = site
    ? `Flightjar — ${site} (${snap.count})`
    : `Flightjar (${snap.count})`;
  if (docTitle !== lastDocTitle) {
    document.title = docTitle;
    lastDocTitle = docTitle;
  }

  const q = state.searchFilter;
  const selIcao = state.selectedIcao;
  const needsFilter = state.activeFilters.size > 0 || !!q || !state.showPeer;
  const filtered = needsFilter
    ? snap.aircraft.filter(a => {
        // The selected aircraft stays visible even when it stops
        // matching — otherwise the detail panel orphans mid-read.
        if (a.icao === selIcao) return true;
        if (!state.showPeer && a.peer) return false;
        if (q && !(
          (a.callsign || '').toLowerCase().includes(q) ||
          a.icao.toLowerCase().includes(q) ||
          (a.registration || '').toLowerCase().includes(q)
        )) return false;
        return matchesActiveFilters(a);
      })
    : snap.aircraft;

  const rows = filtered.slice().sort((a, b) => {
    const av = sortValue(a, state.sortKey, snap.now);
    const bv = sortValue(b, state.sortKey, snap.now);
    if (av == null && bv == null) return a.icao.localeCompare(b.icao);
    if (av == null) return 1;
    if (bv == null) return -1;
    const cmp = typeof av === 'string' ? av.localeCompare(bv) : av - bv;
    return cmp * state.sortDir || a.icao.localeCompare(b.icao);
  });

  const list = document.getElementById('ac-list');
  if (rows.length === 0) {
    const msg = q
      ? 'No matches for this search.'
      : state.activeFilters.size > 0
        ? 'No aircraft match the selected filters.'
        : snap.count === 0
          ? 'Waiting for aircraft…'
          : 'No aircraft have a callsign or position yet.';
    if (msg !== lastEmptyMsg) {
      list.innerHTML = `<div class="ac-empty">${msg}</div>`;
      lastEmptyMsg = msg;
      rowElementCache.clear();
    }
    return;
  }
  lastEmptyMsg = null;

  const desiredEls = new Array(rows.length);
  const desiredIcaos = new Set();
  for (let i = 0; i < rows.length; i++) {
    const a = rows[i];
    desiredIcaos.add(a.icao);
    const entry = state.aircraft.get(a.icao);
    const entryTrend = entry?.trend;
    const tAlt = entryTrend?.alt || trendInfo(entry, 'alt');
    const tSpd = entryTrend?.spd || trendInfo(entry, 'spd');
    const tDst = entryTrend?.dst || trendInfo(entry, 'dst');
    const selected = a.icao === state.selectedIcao;
    const watched = state.watchlist.has(a.icao);
    const isFirstOfDay = state.firstOfDayIcao && a.icao === state.firstOfDayIcao;
    const route = routeLabel(a, snap.airports);

    // Fingerprint every value that affects rendered HTML. Stable rows
    // hit the cache and skip both the template build and DOM parse.
    const fp = [
      selected ? 1 : 0, watched ? 1 : 0, isFirstOfDay ? 1 : 0,
      a.emergency || '', a.callsign || '', a.registration || '',
      a.type_icao || '', a.operator_iata || '', a.operator || '',
      a.operator_alliance || '', a.operator_country || '',
      a.country_iso || '', a.phase || '',
      a.altitude, a.speed, a.track, a.distance_km, a.signal_peak,
      tAlt.cls, tAlt.arrow, tSpd.cls, tSpd.arrow, tDst.cls, tDst.arrow,
      route,
    ].join('|');
    const cached = rowElementCache.get(a.icao);
    if (cached && cached.fp === fp) {
      desiredEls[i] = cached.el;
      continue;
    }

    const classes = [
      'ac-item',
      selected ? 'selected' : '',
      a.emergency ? 'emergency' : '',
      watched ? 'watched' : '',
    ].filter(Boolean).join(' ');
    const emergencyBadge = a.emergency
      ? `<span class="emergency-label">${escapeHtml(a.emergency)}</span>`
      : '';
    const subtitle = [a.registration, a.type_icao]
      .filter(Boolean).map(escapeHtml).join(' · ');
    const allianceTitle = {
      star: 'Star Alliance', oneworld: 'oneworld', skyteam: 'SkyTeam',
    }[a.operator_alliance] || '';
    const tagCls = a.operator_alliance
      ? `airline-tag alliance-${a.operator_alliance}`
      : 'airline-tag';
    const titleAttr = allianceTitle ? ` title="${allianceTitle}"` : '';
    const airlineTag = a.operator_iata
      ? `<span class="${tagCls}"${titleAttr}>${escapeHtml(a.operator_iata)}</span> `
      : '';
    const airline = a.operator
      ? `${airlineTag}${escapeHtml(a.operator)}`
      : '';
    const callsign = a.callsign ? escapeHtml(a.callsign) : '— — — —';
    const icao = escapeHtml(a.icao);
    const flag = flagIcon(a.country_iso);
    const flagTag = flag
      ? `<span class="flag" title="${escapeHtml(a.operator_country || a.country_iso)}">${flag}</span> `
      : '';
    const sigBars = signalBars(a.signal_peak);
    const phaseRaw = a.phase == null ? '' : String(a.phase);
    const phaseText = escapeHtml(phaseRaw);
    const phaseClass = phaseRaw.toLowerCase().replace(/[^a-z0-9_-]/g, '-');
    const phaseChip = phaseRaw
      ? `<span class="phase-chip phase-${phaseClass}">${phaseText}</span>`
      : '';
    const notable = isNotable(a.icao, a.callsign);
    const notableTag = notable
      ? `<span class="notable-tag" title="${escapeHtml(notable.name)}">${notable.emoji}</span> `
      : '';
    const milLabel = militaryLabel(a.icao);
    const milChip = milLabel
      ? `<span class="mil-chip" title="${escapeHtml(milLabel)}">MIL</span>`
      : '';
    const firstOfDayTag = isFirstOfDay
      ? `<span class="first-of-day-tag" title="First contact today">🌅</span> `
      : '';
    const html = `<div class="${classes}" data-icao="${icao}"><div class="row1"><span class="cs">${flagTag}${notableTag}${firstOfDayTag}${callsign} ${emergencyBadge} ${milChip} ${phaseChip}</span><span class="icao">${sigBars}${subtitle || icao.toUpperCase()}</span></div>${airline ? `<div class="airline-row">${airline}</div>` : ''}${route ? `<div class="route-row">${route}</div>` : ''}<div class="meta"><div class="metric"><div class="label">Alt</div><div class="val ${tAlt.cls}">${uconv('alt', a.altitude)}${tAlt.arrow}</div></div><div class="metric"><div class="label">Spd</div><div class="val ${tSpd.cls}">${uconv('spd', a.speed)}${tSpd.arrow}</div></div><div class="metric"><div class="label">Hdg</div><div class="val">${fmt(a.track, '°')}${compassIcon(a.track)}</div></div><div class="metric"><div class="label">Dist</div><div class="val ${tDst.cls}">${uconv('dst', a.distance_km)}${tDst.arrow}</div></div></div></div>`;
    const el = buildRowElement(html);
    rowElementCache.set(a.icao, { fp, el });
    desiredEls[i] = el;
  }

  // Drop rows no longer visible — detach from DOM and forget.
  if (rowElementCache.size > desiredIcaos.size) {
    for (const [icao, cached] of rowElementCache) {
      if (!desiredIcaos.has(icao)) {
        cached.el.remove();
        rowElementCache.delete(icao);
      }
    }
  }

  // Patch DOM order in place. insertBefore on a node already in the
  // document moves it; on a fresh node it inserts. Browsers no-op when
  // the target is already in the requested slot, so steady-state ticks
  // with no reorders touch zero nodes here.
  for (let i = 0; i < desiredEls.length; i++) {
    const el = desiredEls[i];
    if (list.children[i] !== el) {
      list.insertBefore(el, list.children[i] || null);
    }
  }
  while (list.children.length > desiredEls.length) {
    list.lastElementChild.remove();
  }

  if (state.hoveredFromMapIcao) {
    const cached = rowElementCache.get(state.hoveredFromMapIcao);
    if (cached) cached.el.classList.add('peek');
  }
}

// Wire the sort chips, filter chips, search input, and delegated
// click/hover handlers on #ac-list. Called once at boot.
export function initSidebar() {
  document.querySelectorAll('.sort-chip').forEach(el => {
    el.addEventListener('click', () => {
      const key = el.dataset.key;
      if (key === state.sortKey) {
        state.sortDir = -state.sortDir;
      } else {
        state.sortKey = key;
        state.sortDir = DEFAULT_SORT_DIR[key];
      }
      renderSortBar();
      if (state.lastSnap) renderSidebar(state.lastSnap);
    });
  });
  renderSortBar();

  document.querySelectorAll('.filter-chip').forEach(el => {
    const key = el.dataset.filter;
    if (!FILTER_KEYS.includes(key)) return;
    el.addEventListener('click', () => {
      if (state.activeFilters.has(key)) state.activeFilters.delete(key);
      else state.activeFilters.add(key);
      writeFilters(state.activeFilters);
      renderFilterBar();
      if (state.lastSnap) {
        renderSidebar(state.lastSnap);
        applyFilterVisibility();
      }
    });
  });
  renderFilterBar();
  applyFilterVisibility();

  // Peer chip — independent toggle (hide/show aircraft from relay peers).
  const peerChip = document.getElementById('peer-chip');
  if (peerChip) {
    peerChip.classList.toggle('active', state.showPeer);
    peerChip.addEventListener('click', () => {
      state.showPeer = !state.showPeer;
      try { localStorage.setItem('flightjar.showPeer', state.showPeer ? '1' : '0'); }
      catch (_) { /* storage disabled */ }
      peerChip.classList.toggle('active', state.showPeer);
      if (state.lastSnap) {
        renderSidebar(state.lastSnap);
        applyFilterVisibility();
      }
    });
  }

  const searchInput = document.getElementById('search');
  searchInput.addEventListener('input', () => {
    state.searchFilter = searchInput.value.trim().toLowerCase();
    if (state.lastSnap) renderSidebar(state.lastSnap);
  });

  // One-time event delegation on #ac-list: the sidebar rebuilds via
  // innerHTML every tick, so per-row listeners would cost 3×N
  // addEventListener calls per second and generate garbage. Delegating
  // at the container costs one listener total and survives rebuilds.
  const acList = document.getElementById('ac-list');
  acList.addEventListener('click', (e) => {
    const el = e.target.closest('.ac-item');
    if (el) selectAircraft(el.dataset.icao);
  });
  acList.addEventListener('mouseover', (e) => {
    const el = e.target.closest('.ac-item');
    if (!el) return;
    const related = e.relatedTarget && e.relatedTarget.closest?.('.ac-item');
    if (related === el) return;
    state.hoveredFromListIcao = el.dataset.icao;
    setHoverHalo(state.hoveredFromListIcao);
  });
  acList.addEventListener('mouseout', (e) => {
    const el = e.target.closest('.ac-item');
    if (!el) return;
    const related = e.relatedTarget && e.relatedTarget.closest?.('.ac-item');
    if (related === el) return;
    state.hoveredFromListIcao = null;
    setHoverHalo(null);
  });
}
