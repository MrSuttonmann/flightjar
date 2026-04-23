// Three dialogs: About, Stats, Watchlist. Each exports an init function
// that wires its trigger button + close behaviour, plus a render function
// the update loop can call when relevant.

import { applyWatchStateToPanel, selectAircraft } from './detail_panel.js';
import { countHistory, renderSidebar, setRenderStats } from './sidebar.js';
import { COUNT_HISTORY_LEN, state } from './state.js';
import { escapeHtml } from './format.js';
import { lucide } from './icons_lib.js';
import { relativeAge } from './profile.js';

const WL_REMOVE_ICON = lucide('x', { size: 14, strokeWidth: 2 });
import { uconv } from './units.js';

// ---------------- About dialog ----------------

function aboutDialogEl() { return document.getElementById('about-dialog'); }

async function populateAboutVersion() {
  const el = document.getElementById('about-version');
  if (!el.hidden) return;
  try {
    const r = await fetch('/api/stats');
    if (!r.ok) return;
    const { version } = await r.json();
    if (!version) return;
    const label = /^[0-9a-f]{7,}$/.test(version) ? version.slice(0, 7) : version;
    el.textContent = label;
    el.hidden = false;
  } catch (_) { /* leave the badge hidden */ }
}

// Wright Brothers Day — slips a one-line historical note into the
// About dialog on Dec 17 only. 1903 was the first powered flight.
function maybePrependWrightBrothersNote() {
  const dialog = aboutDialogEl();
  const d = new Date();
  if (d.getMonth() !== 11 || d.getDate() !== 17) return;
  if (dialog.querySelector('.egg-wright-note')) return;
  const years = d.getFullYear() - 1903;
  const note = document.createElement('p');
  note.className = 'egg-wright-note';
  note.textContent =
    `Today marks ${years} years since the Wright brothers' ` +
    `first powered flight at Kitty Hawk. 🛩️`;
  const h2 = dialog.querySelector('h2');
  if (h2 && h2.nextSibling) {
    dialog.insertBefore(note, h2.nextSibling);
  } else {
    dialog.appendChild(note);
  }
}

export function initAboutDialog() {
  const dialog = aboutDialogEl();
  document.getElementById('about-btn').addEventListener('click', async () => {
    await populateAboutVersion();
    maybePrependWrightBrothersNote();
    if (typeof dialog.showModal === 'function') dialog.showModal();
    else dialog.setAttribute('open', '');
  });
  dialog.addEventListener('click', (e) => {
    const r = dialog.getBoundingClientRect();
    const inside = e.clientX >= r.left && e.clientX <= r.right
                && e.clientY >= r.top && e.clientY <= r.bottom;
    if (!inside) dialog.close();
  });
}

// ---------------- Stats dialog ----------------

let statsServerInfo = null;
let statsRefreshTimer = null;
const STATS_REFRESH_INTERVAL_MS = 3000;

function fmtDuration(s) {
  if (s == null) return '—';
  if (s < 60) return `${Math.round(s)}s`;
  if (s < 3600) return `${Math.floor(s / 60)}m ${Math.round(s % 60)}s`;
  if (s < 86400) return `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m`;
  const d = Math.floor(s / 86400);
  return `${d}d ${Math.floor((s % 86400) / 3600)}h`;
}

function renderStatsSparkline() {
  const el = document.getElementById('stats-sparkline');
  if (countHistory.length < 2) { el.innerHTML = ''; return; }
  const W = 420, H = 80, PAD = 4;
  const max = Math.max(1, ...countHistory);
  const step = (W - 2 * PAD) / Math.max(1, COUNT_HISTORY_LEN - 1);
  const offset = W - PAD - (countHistory.length - 1) * step;
  const pts = countHistory.map((c, i) => {
    const x = offset + i * step;
    const y = H - PAD - (c / max) * (H - 2 * PAD);
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(' ');
  el.innerHTML =
    `<svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none">` +
    `<polyline points="${pts}" fill="none" stroke="var(--accent)" ` +
    `stroke-width="1.6" stroke-linejoin="round" stroke-linecap="round" ` +
    `vector-effect="non-scaling-stroke"/></svg>`;
}

async function refreshServerStats() {
  try {
    const [s, c, h, p] = await Promise.all([
      fetch('/api/stats').then(r => r.ok ? r.json() : null),
      fetch('/api/coverage').then(r => r.ok ? r.json() : null),
      fetch('/api/heatmap').then(r => r.ok ? r.json() : null),
      fetch('/api/polar_heatmap').then(r => r.ok ? r.json() : null),
    ]);
    statsServerInfo = { stats: s, coverage: c, heatmap: h, polarHeatmap: p };
  } catch (e) {
    console.warn('stats fetch failed', e);
  }
}

function renderTrafficHeatmap(data) {
  const el = document.getElementById('stats-heatmap');
  if (!el) return;
  const grid = data?.grid;
  const labels = data?.day_labels || ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
  if (!Array.isArray(grid) || grid.length !== 7) {
    el.innerHTML = '<div class="heatmap-empty">awaiting traffic data</div>';
    return;
  }
  const max = Math.max(1, ...grid.flat());
  const CELL_W = 14, CELL_H = 14, GAP = 2;
  const LABEL_W = 30, TOP = 14;
  const W = LABEL_W + 24 * (CELL_W + GAP);
  const H = TOP + 7 * (CELL_H + GAP);
  let out =
    `<svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="xMidYMid meet" ` +
    `width="100%" height="auto">`;
  for (let h = 0; h < 24; h += 3) {
    const x = LABEL_W + h * (CELL_W + GAP) + CELL_W / 2;
    out +=
      `<text x="${x}" y="10" text-anchor="middle" ` +
      `font-size="8" fill="currentColor" opacity="0.55">${h}</text>`;
  }
  for (let d = 0; d < 7; d++) {
    const y = TOP + d * (CELL_H + GAP);
    out +=
      `<text x="${LABEL_W - 4}" y="${y + CELL_H - 3}" text-anchor="end" ` +
      `font-size="9" fill="currentColor" opacity="0.55">${labels[d]}</text>`;
    for (let h = 0; h < 24; h++) {
      const count = grid[d][h] || 0;
      const x = LABEL_W + h * (CELL_W + GAP);
      const intensity = count / max;
      const alpha = count === 0 ? 0.06 : (0.15 + intensity * 0.85).toFixed(2);
      out +=
        `<rect x="${x}" y="${y}" width="${CELL_W}" height="${CELL_H}" ` +
        `rx="2" ry="2" fill="var(--accent)" fill-opacity="${alpha}">` +
        `<title>${labels[d]} ${String(h).padStart(2, '0')}:00 — ${count}</title>` +
        `</rect>`;
    }
  }
  out += '</svg>';
  el.innerHTML = out;
}

// Polar heatmap: concentric bands (distance from receiver) x radial wedges
// (bearing buckets). Each cell is a filled SVG path built from the two
// radii + two bearings that bound it, with opacity scaled to count.
// North is up; bearings increase clockwise (0°=N, 90°=E), matching the
// server's bearing convention.
function renderPolarHeatmap(data) {
  const el = document.getElementById('stats-polar-heatmap');
  if (!el) return;
  const grid = data?.grid;
  const buckets = grid?.length || 0;
  const bands = grid?.[0]?.length || 0;
  if (!Array.isArray(grid) || buckets === 0 || bands === 0 || !data?.total) {
    el.innerHTML = '<div class="heatmap-empty">awaiting position fixes</div>';
    return;
  }
  const bucketDeg = data.bucket_deg || (360 / buckets);
  const bandKm = data.band_km || 25;
  const SIZE = 320;
  const PAD = 18;
  const CX = SIZE / 2;
  const CY = SIZE / 2;
  const R = SIZE / 2 - PAD;
  let max = 0;
  for (const row of grid) for (const c of row) if (c > max) max = c;
  max = Math.max(1, max);

  // SVG polar-coord helper: 0°=North (up), clockwise, degrees.
  const pt = (r, deg) => {
    const rad = (deg - 90) * Math.PI / 180;
    return [CX + r * Math.cos(rad), CY + r * Math.sin(rad)];
  };

  let out =
    `<svg viewBox="0 0 ${SIZE} ${SIZE}" preserveAspectRatio="xMidYMid meet" ` +
    `width="100%" height="auto">`;

  // Filled wedges — one per (bucket, band). Innermost band first so the
  // stroke antialiasing between rings looks right, though with fill-only
  // the order barely matters.
  for (let band = 0; band < bands; band++) {
    const r0 = R * (band / bands);
    const r1 = R * ((band + 1) / bands);
    for (let b = 0; b < buckets; b++) {
      const count = grid[b][band] || 0;
      const alpha = count === 0 ? 0.04 : (0.12 + (count / max) * 0.88).toFixed(3);
      const a0 = b * bucketDeg;
      const a1 = (b + 1) * bucketDeg;
      const [x0, y0] = pt(r0, a0);
      const [x1, y1] = pt(r1, a0);
      const [x2, y2] = pt(r1, a1);
      const [x3, y3] = pt(r0, a1);
      // Sweep flag 1 = clockwise, matches our 0°=N-clockwise convention.
      const d =
        `M ${x0.toFixed(2)} ${y0.toFixed(2)} ` +
        `L ${x1.toFixed(2)} ${y1.toFixed(2)} ` +
        `A ${r1.toFixed(2)} ${r1.toFixed(2)} 0 0 1 ${x2.toFixed(2)} ${y2.toFixed(2)} ` +
        `L ${x3.toFixed(2)} ${y3.toFixed(2)} ` +
        (r0 > 0
          ? `A ${r0.toFixed(2)} ${r0.toFixed(2)} 0 0 0 ${x0.toFixed(2)} ${y0.toFixed(2)} `
          : '') +
        'Z';
      const midA = (a0 + a1) / 2;
      const dLo = Math.round(band * bandKm);
      const dHi = Math.round((band + 1) * bandKm);
      const bandLbl = band === bands - 1 ? `${dLo}+ km` : `${dLo}–${dHi} km`;
      out +=
        `<path d="${d}" fill="var(--accent)" fill-opacity="${alpha}">` +
        `<title>${Math.round(midA)}° · ${bandLbl} — ${count}</title>` +
        `</path>`;
    }
  }

  // Reference grid: concentric rings for band boundaries.
  for (let band = 1; band <= bands; band++) {
    const r = R * (band / bands);
    out +=
      `<circle cx="${CX}" cy="${CY}" r="${r.toFixed(2)}" ` +
      `fill="none" stroke="currentColor" stroke-opacity="0.15" ` +
      `stroke-width="0.6"/>`;
  }
  // Cardinal crosshair for orientation.
  for (const deg of [0, 90, 180, 270]) {
    const [x, y] = pt(R, deg);
    out +=
      `<line x1="${CX}" y1="${CY}" x2="${x.toFixed(2)}" y2="${y.toFixed(2)}" ` +
      `stroke="currentColor" stroke-opacity="0.2" stroke-width="0.6"/>`;
  }
  // Compass labels (N/E/S/W) just outside the outer ring.
  const labels = [
    { t: 'N', d: 0 }, { t: 'E', d: 90 }, { t: 'S', d: 180 }, { t: 'W', d: 270 },
  ];
  for (const { t, d } of labels) {
    const [lx, ly] = pt(R + 10, d);
    out +=
      `<text x="${lx.toFixed(2)}" y="${ly.toFixed(2)}" text-anchor="middle" ` +
      `dominant-baseline="middle" font-size="10" fill="currentColor" ` +
      `opacity="0.6">${t}</text>`;
  }
  // Outer distance label so the reader knows the scale, plus the rolling
  // window length if the server returned one.
  const outerKm = Math.round(bands * bandKm);
  const windowDays = data.window_days;
  const caption = windowDays
    ? `outer ring = ${outerKm} km · last ${windowDays} days`
    : `outer ring = ${outerKm} km`;
  out +=
    `<text x="${CX}" y="${SIZE - 4}" text-anchor="middle" ` +
    `font-size="9" fill="currentColor" opacity="0.5">` +
    `${caption}</text>`;

  out += '</svg>';
  el.innerHTML = out;
}

export function renderStats(snap) {
  document.getElementById('stats-tracked').textContent = snap?.count ?? '—';
  document.getElementById('stats-positioned').textContent = snap?.positioned ?? '—';
  renderStatsSparkline();

  const s = statsServerInfo?.stats;
  if (s) {
    document.getElementById('stats-uptime').textContent = fmtDuration(s.uptime_s);
    document.getElementById('stats-frames').textContent = s.frames.toLocaleString();
    document.getElementById('stats-ws').textContent = s.websocket_clients;
    document.getElementById('stats-beast-target').textContent = s.beast_target;
    document.getElementById('stats-beast-connected').textContent =
      s.beast_connected ? 'Connected' : 'Disconnected';
    document.getElementById('stats-beast-connected').className =
      'stat-val ' + (s.beast_connected ? 'stat-ok' : 'stat-bad');
  }
  document.getElementById('stats-rate').textContent =
    document.getElementById('msg-rate').textContent || '—';

  renderTrafficHeatmap(statsServerInfo?.heatmap);
  renderPolarHeatmap(statsServerInfo?.polarHeatmap);

  const bearings = statsServerInfo?.coverage?.bearings ?? [];
  if (bearings.length > 0) {
    const dists = bearings.map(b => b.dist_km);
    const maxD = Math.max(...dists);
    const avgD = dists.reduce((a, b) => a + b, 0) / dists.length;
    document.getElementById('stats-max-range').textContent = uconv('dst', maxD);
    document.getElementById('stats-avg-range').textContent = uconv('dst', avgD);
    document.getElementById('stats-sectors').textContent =
      `${bearings.length} / 36`;
  } else {
    document.getElementById('stats-max-range').textContent = '—';
    document.getElementById('stats-avg-range').textContent = '—';
    document.getElementById('stats-sectors').textContent = '—';
  }
}

function stopStatsRefresh() {
  if (statsRefreshTimer != null) {
    clearInterval(statsRefreshTimer);
    statsRefreshTimer = null;
  }
}

export function initStatsDialog() {
  const dialog = document.getElementById('stats-dialog');
  // Let renderSidebar call renderStats without importing us (avoids a
  // dep cycle since we also import countHistory from sidebar.js).
  setRenderStats(renderStats);

  dialog.addEventListener('close', stopStatsRefresh);

  document.getElementById('stats-btn').addEventListener('click', async () => {
    await refreshServerStats();
    renderStats(state.lastSnap);
    if (typeof dialog.showModal === 'function') dialog.showModal();
    else dialog.setAttribute('open', '');
    stopStatsRefresh();
    statsRefreshTimer = setInterval(async () => {
      if (!dialog.open) { stopStatsRefresh(); return; }
      await refreshServerStats();
      renderStats(state.lastSnap);
    }, STATS_REFRESH_INTERVAL_MS);
  });
  dialog.addEventListener('click', (e) => {
    const r = dialog.getBoundingClientRect();
    const inside = e.clientX >= r.left && e.clientX <= r.right
                && e.clientY >= r.top && e.clientY <= r.bottom;
    if (!inside) dialog.close();
  });
}

// ---------------- Watchlist dialog ----------------

async function fetchAcInfoCached(icao) {
  if (state.aircraftInfoCache.has(icao)) return state.aircraftInfoCache.get(icao);
  try {
    const r = await fetch(`/api/aircraft/${icao}`);
    if (!r.ok) return null;
    const info = await r.json();
    state.aircraftInfoCache.set(icao, info);
    return info;
  } catch (_) {
    return null;
  }
}

function watchlistRowHtml(icao, snap, info) {
  const inRange = !!snap;
  const reg = (snap?.registration || info?.registration || '').trim();
  const type = (snap?.type_long || info?.type || info?.type_icao || '').trim();
  const label = [reg, type].filter(Boolean).join(' · ');
  const rangeCls = inRange ? 'wl-range wl-range-on' : 'wl-range';
  const rangeTitle = inRange ? 'In range right now' : 'Not currently in range';
  const nowSec = Date.now() / 1000;
  const lastSeenTs = inRange ? (snap.last_seen || nowSec) : state.watchlistLastSeen[icao];
  let lastSeenHtml = '';
  if (inRange) {
    lastSeenHtml = '<span class="wl-last-seen wl-live">Live</span>';
  } else if (lastSeenTs) {
    lastSeenHtml =
      `<span class="wl-last-seen" title="${escapeHtml(new Date(lastSeenTs * 1000).toLocaleString())}">` +
      `${escapeHtml(relativeAge(lastSeenTs, nowSec))}</span>`;
  } else {
    lastSeenHtml = '<span class="wl-last-seen wl-last-seen-none">—</span>';
  }
  return `
    <li class="watchlist-entry${inRange ? ' in-range' : ''}" data-icao="${escapeHtml(icao)}">
      <span class="${rangeCls}" title="${rangeTitle}"></span>
      <code class="wl-icao">${escapeHtml(icao.toUpperCase())}</code>
      <span class="wl-label">${escapeHtml(label)}</span>
      ${lastSeenHtml}
      <button type="button" class="wl-remove" data-icao="${escapeHtml(icao)}"
              aria-label="Remove from watchlist" title="Remove">${WL_REMOVE_ICON}</button>
    </li>
  `;
}

function wireWatchlistRowHandlers() {
  const watchlistEntriesEl = document.getElementById('watchlist-entries');
  const dialog = document.getElementById('watchlist-dialog');
  watchlistEntriesEl.querySelectorAll('.wl-remove').forEach((btn) => {
    btn.addEventListener('click', (e) => {
      e.stopPropagation();
      const icao = btn.dataset.icao;
      if (!icao) return;
      state.watchlist.remove(icao);
      renderWatchlistDialog();
      if (state.lastSnap) renderSidebar(state.lastSnap);
      applyWatchStateToPanel();
    });
  });
  watchlistEntriesEl.querySelectorAll('.watchlist-entry.in-range').forEach((row) => {
    row.addEventListener('click', () => {
      const icao = row.dataset.icao;
      if (!icao) return;
      dialog.close();
      selectAircraft(icao);
    });
  });
}

async function renderWatchlistDialog() {
  const watchlistEntriesEl = document.getElementById('watchlist-entries');
  const watchlistEmptyEl = document.getElementById('watchlist-empty');
  const watchlistCountEl = document.getElementById('watchlist-count');
  const watchlistNotifyEl = document.getElementById('watchlist-notify');
  const watchlistDialog = document.getElementById('watchlist-dialog');

  const entries = state.watchlist.list().sort();
  watchlistCountEl.textContent = entries.length ? `· ${entries.length}` : '';
  watchlistEmptyEl.hidden = entries.length > 0;
  if (typeof Notification === 'undefined') {
    watchlistNotifyEl.disabled = true;
    watchlistNotifyEl.checked = false;
  } else {
    watchlistNotifyEl.disabled = false;
    watchlistNotifyEl.checked = state.watchlist.isNotifyEnabled();
  }
  if (!entries.length) {
    watchlistEntriesEl.innerHTML = '';
    return;
  }
  // Refresh server-tracked last-seen map before rendering.
  try {
    const r = await fetch('/api/watchlist', {
      headers: { Accept: 'application/json' },
    });
    if (r.ok) {
      const body = await r.json();
      if (body && typeof body.last_seen === 'object' && body.last_seen) {
        state.watchlistLastSeen = body.last_seen;
      }
    }
  } catch (_) { /* offline — use what we already have */ }
  const snapMap = new Map(
    (state.lastSnap?.aircraft || []).map((a) => [a.icao.toLowerCase(), a]),
  );
  watchlistEntriesEl.innerHTML = entries
    .map((icao) => watchlistRowHtml(icao, snapMap.get(icao), state.aircraftInfoCache.get(icao)))
    .join('');
  wireWatchlistRowHandlers();
  for (const icao of entries) {
    if (snapMap.has(icao) || state.aircraftInfoCache.has(icao)) continue;
    fetchAcInfoCached(icao).then((info) => {
      if (!watchlistDialog.open) return;
      const row = watchlistEntriesEl.querySelector(
        `.watchlist-entry[data-icao="${CSS.escape(icao)}"]`,
      );
      if (!row) return;
      row.outerHTML = watchlistRowHtml(icao, null, info);
      wireWatchlistRowHandlers();
    });
  }
}

export function initWatchlistDialog() {
  const dialog = document.getElementById('watchlist-dialog');
  const addForm = document.getElementById('watchlist-add-form');
  const addInput = document.getElementById('watchlist-add-input');
  const addErr = document.getElementById('watchlist-add-error');
  const notifyEl = document.getElementById('watchlist-notify');

  addForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    const raw = addInput.value.trim().toLowerCase();
    if (!/^[0-9a-f]{6}$/.test(raw)) {
      addErr.textContent = 'ICAO24 must be exactly 6 hex characters (0–9, a–f).';
      addErr.hidden = false;
      return;
    }
    addErr.hidden = true;
    const added = state.watchlist.add(raw);
    addInput.value = '';
    // First add in a session: prompt for notification permission so
    // notifications actually fire. Idempotent after the first.
    if (added) await state.watchlist.setNotifyEnabled(true);
    await renderWatchlistDialog();
    if (added && state.lastSnap) renderSidebar(state.lastSnap);
  });

  notifyEl.addEventListener('change', async () => {
    const ok = await state.watchlist.setNotifyEnabled(notifyEl.checked);
    notifyEl.checked = ok;
  });

  document.getElementById('watchlist-btn').addEventListener('click', async () => {
    await renderWatchlistDialog();
    if (typeof dialog.showModal === 'function') dialog.showModal();
    else dialog.setAttribute('open', '');
    addInput.focus();
  });
  dialog.addEventListener('click', (e) => {
    const r = dialog.getBoundingClientRect();
    const inside = e.clientX >= r.left && e.clientX <= r.right
                && e.clientY >= r.top && e.clientY <= r.bottom;
    if (!inside) dialog.close();
  });
}
