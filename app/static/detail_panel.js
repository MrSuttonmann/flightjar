// Per-aircraft detail panel plus everything that flows from selecting a
// plane: the detail DOM (build once per selection, mutate placeholders
// on every tick), panel open/close lifecycle, "follow" state, deep-link
// helpers, lost-aircraft signalling, and the selectAircraft() entry
// point that both marker clicks and sidebar clicks call.

import { altColor } from './altitude.js';
import { ageOf, compassIcon, escapeHtml, flagIcon, fmt } from './format.js';
import { flightProgress, trailDistanceKm } from './geo.js';
import { lucide } from './icons_lib.js';
import { planeIcon } from './icons.js';
import { isNotable, militaryLabel } from './notable_aircraft.js';
import {
  CATEGORY_NAMES,
  LINK_ICON_SVG,
  formatMetar,
  relativeAge,
  renderAltProfile,
  renderSpdProfile,
  signalLabel,
  wakeClass,
} from './profile.js';
import { state } from './state.js';
import { applyTrailsVisibility } from './trails.js';
import { trendInfo } from './trend.js';
import { uconv } from './units.js';

// Forward reference to renderSidebar, wired in boot() after sidebar.js
// has exported it. A direct import would create a cycle because
// sidebar.js needs routeLabel from this module.
let _renderSidebar = () => {};
export function setRenderSidebar(fn) { _renderSidebar = fn; }

// Build the panel DOM once per aircraft, with placeholder children for
// every field that changes on snapshot tick. Subsequent ticks call
// updatePopupContent() to mutate those placeholders in place — this
// leaves the .ac-photo slot untouched between ticks, so the aircraft
// photograph doesn't flicker in and out every second the way it did
// when we rebuilt everything from an HTML string each tick.
export function buildPopupContent(a, now, airports) {
  const root = document.createElement('div');
  root.className = 'ac-panel';
  root.innerHTML =
    `<div class="ac-photo" data-icao="${escapeHtml(a.icao)}"></div>` +
    `<div class="panel-head">` +
      `<div class="panel-title-row">` +
        `<span class="pop-flag"></span>` +
        `<b class="pop-callsign"></b>` +
        `<code class="pop-icao"></code>` +
        `<span class="pop-emergency"></span>` +
        `<span class="pop-ground" hidden>ON GROUND</span>` +
        `<span class="pop-signal-lost" hidden>SIGNAL LOST</span>` +
        `<span class="pop-notable" hidden></span>` +
        `<span class="pop-mil mil-chip" hidden>MIL</span>` +
        `<span class="pop-first-today" hidden title="First contact today">🌅 First today</span>` +
      `</div>` +
      `<div class="panel-subline pop-reg-line" hidden><span class="pop-reg"></span></div>` +
      `<div class="panel-subline pop-manuf-line" hidden><span class="pop-manuf"></span></div>` +
      `<div class="panel-subline pop-op-line" hidden><span class="pop-op"></span></div>` +
      `<div class="panel-badges">` +
        `<span class="pop-type-badge badge" hidden></span>` +
        `<span class="pop-cat-badge badge" hidden></span>` +
        `<span class="pop-phase-badge badge" hidden></span>` +
      `</div>` +
    `</div>` +
    `<div class="panel-route pop-route-line" hidden>` +
      `<div class="panel-mini-label">Route</div>` +
      `<div class="route-ticket">` +
        `<div class="route-end">` +
          `<div class="route-code pop-origin-code"></div>` +
          `<div class="route-name pop-origin-name"></div>` +
        `</div>` +
        `<div class="route-arrow">→</div>` +
        `<div class="route-end">` +
          `<div class="route-code pop-dest-code"></div>` +
          `<div class="route-name pop-dest-name"></div>` +
        `</div>` +
      `</div>` +
      `<div class="pop-progress" hidden>` +
        `<div class="pop-progress-track"><div class="pop-progress-fill"></div></div>` +
        `<div class="pop-progress-label">` +
          `<span class="pop-progress-pct"></span>` +
          `<span class="pop-progress-eta"></span>` +
        `</div>` +
      `</div>` +
      `<div class="pop-wx" hidden>` +
        `<div class="pop-wx-orig wx-cell" hidden></div>` +
        `<div class="pop-wx-dest wx-cell" hidden></div>` +
      `</div>` +
    `</div>` +
    `<div class="panel-live-indicator">` +
      `<span class="live-dot"></span>` +
      `<span class="live-label">Live</span>` +
    `</div>` +
    `<div class="panel-meta">` +
      `<div class="metric"><div class="label">Altitude</div>` +
        `<div class="val pop-alt-wrap"><b class="pop-alt"></b>` +
        `<span class="pop-alt-trend"></span></div></div>` +
      `<div class="metric"><div class="label">Speed</div>` +
        `<div class="val pop-spd-wrap"><span class="pop-spd"></span>` +
        `<span class="pop-spd-trend"></span></div></div>` +
      `<div class="metric"><div class="label">Heading</div>` +
        `<div class="val"><span class="pop-hdg"></span>` +
        `<span class="pop-compass"></span></div></div>` +
      `<div class="metric"><div class="label">V.Rate</div>` +
        `<div class="val pop-vrate"></div></div>` +
      `<div class="metric"><div class="label">Squawk</div>` +
        `<div class="val pop-squawk"></div></div>` +
      `<div class="metric"><div class="label">Distance</div>` +
        `<div class="val pop-dist"></div></div>` +
      `<div class="metric"><div class="label">Latitude</div>` +
        `<div class="val pop-lat"></div></div>` +
      `<div class="metric"><div class="label">Longitude</div>` +
        `<div class="val pop-lon"></div></div>` +
      `<div class="metric"><div class="label">Flown</div>` +
        `<div class="val pop-path"></div></div>` +
    `</div>` +
    `<div class="panel-profile">` +
      `<div class="panel-mini-label">Altitude / speed profile (last 5 min)</div>` +
      `<svg class="pop-alt-profile" viewBox="0 0 200 40" ` +
        `preserveAspectRatio="none" aria-hidden="true"></svg>` +
      `<svg class="pop-spd-profile" viewBox="0 0 200 24" ` +
        `preserveAspectRatio="none" aria-hidden="true"></svg>` +
    `</div>` +
    `<div class="panel-links">` +
      `<a class="pop-link-fa" target="_blank" rel="noopener">` +
        `<span>FlightAware</span>${LINK_ICON_SVG}</a>` +
      `<a class="pop-link-fr24" target="_blank" rel="noopener">` +
        `<span>Flightradar24</span>${LINK_ICON_SVG}</a>` +
      `<a class="pop-link-airnav" target="_blank" rel="noopener">` +
        `<span>AirNav Radar</span>${LINK_ICON_SVG}</a>` +
      `<a class="pop-link-ps" target="_blank" rel="noopener">` +
        `<span>Planespotters</span>${LINK_ICON_SVG}</a>` +
    `</div>` +
    `<div class="panel-stats">` +
      `<div class="stat"><span class="stat-label">Messages</span>` +
        `<span class="stat-val pop-msgs"></span></div>` +
      `<div class="stat"><span class="stat-label">Peak signal</span>` +
        `<span class="stat-val pop-signal"></span></div>` +
      `<div class="stat"><span class="stat-label">First seen</span>` +
        `<span class="stat-val pop-first-seen"></span></div>` +
    `</div>`;
  updatePopupContent(root, a, now, airports);
  return root;
}

export function updatePopupContent(root, a, now, airports) {
  const q = (sel) => root.querySelector(sel);

  // Flag — img markup from flagcdn.com. Only rewrite innerHTML when
  // the ISO actually changes; otherwise the <img> is torn down and
  // re-created every snapshot tick, which makes the flag flash.
  const flagEl = q('.pop-flag');
  const iso = a.country_iso || '';
  if (flagEl.dataset.iso !== iso) {
    flagEl.innerHTML = iso ? flagIcon(iso) : '';
    flagEl.dataset.iso = iso;
  }
  flagEl.title = a.operator_country || a.country_iso || '';

  q('.pop-callsign').textContent = a.callsign || '—';
  q('.pop-icao').textContent = a.icao.toUpperCase();

  const emEl = q('.pop-emergency');
  if (a.emergency) {
    emEl.innerHTML =
      `<span class="emergency-label">EMERGENCY · ${escapeHtml(a.emergency)}</span>`;
  } else {
    emEl.textContent = '';
  }
  q('.pop-ground').hidden = !a.on_ground;
  q('.pop-signal-lost').hidden = !a.lost;
  const notableChip = q('.pop-notable');
  const notableHit = isNotable(a.icao, a.callsign);
  if (notableHit) {
    notableChip.textContent = `${notableHit.emoji} ${notableHit.name}`;
    notableChip.hidden = false;
  } else {
    notableChip.hidden = true;
  }
  const milChip = q('.pop-mil');
  const milHit = militaryLabel(a.icao);
  if (milHit) {
    milChip.title = milHit;
    milChip.hidden = false;
  } else {
    milChip.hidden = true;
  }
  q('.pop-first-today').hidden = !(state.firstOfDayIcao && a.icao === state.firstOfDayIcao);

  // "Live" / "Last known" indicator above the telemetry grid. 5 s of
  // silence (or a missing last_seen) flips to "Last known" so normal
  // 1-3 s snapshot jitter doesn't flicker the state.
  const ageS = ageOf(a, now);
  const isLive = ageS != null && ageS < 5;
  const liveIndicator = q('.panel-live-indicator');
  liveIndicator.classList.toggle('is-live', isLive);
  q('.live-label').textContent = isLive
    ? 'Live'
    : `Last known · ${relativeAge(a.last_seen, now)}`;

  const regLine = q('.pop-reg-line');
  if (a.registration || a.type_long) {
    q('.pop-reg').textContent = [a.registration, a.type_long]
      .filter(Boolean).join(' · ');
    regLine.hidden = false;
  } else {
    regLine.hidden = true;
  }

  const manufLine = q('.pop-manuf-line');
  const showManuf =
    a.manufacturer &&
    (!a.type_long ||
      !a.type_long.toLowerCase().includes(a.manufacturer.toLowerCase().split(' ')[0]));
  if (showManuf) {
    q('.pop-manuf').textContent = a.manufacturer;
    manufLine.hidden = false;
  } else {
    manufLine.hidden = true;
  }

  const opLine = q('.pop-op-line');
  const opParts = [];
  if (a.operator) opParts.push(a.operator);
  if (a.operator_country) opParts.push(a.operator_country);
  if (opParts.length) {
    const allianceTitle = {
      star: 'Star Alliance', oneworld: 'oneworld', skyteam: 'SkyTeam',
    }[a.operator_alliance] || '';
    const tagCls = a.operator_alliance
      ? `airline-tag alliance-${a.operator_alliance}`
      : 'airline-tag';
    const titleAttr = allianceTitle ? ` title="${allianceTitle}"` : '';
    const tag = a.operator_iata
      ? `<span class="${tagCls}"${titleAttr}>${escapeHtml(a.operator_iata)}</span> `
      : '';
    q('.pop-op').innerHTML = tag + escapeHtml(opParts.join(' · '));
    opLine.hidden = false;
  } else {
    opLine.hidden = true;
  }

  const typeBadge = q('.pop-type-badge');
  if (a.type_icao) {
    typeBadge.textContent = a.type_icao;
    typeBadge.hidden = false;
  } else {
    typeBadge.hidden = true;
  }
  const catBadge = q('.pop-cat-badge');
  const catName = CATEGORY_NAMES[a.category];
  const wtc = wakeClass(a.type_icao, a.category);
  catBadge.className =
    'pop-cat-badge badge' + (wtc ? ` wtc-${wtc.toLowerCase()}` : '');
  if (catName) {
    catBadge.textContent = catName;
    catBadge.hidden = false;
  } else {
    catBadge.hidden = true;
  }
  const phaseBadge = q('.pop-phase-badge');
  if (a.phase) {
    phaseBadge.textContent = a.phase[0].toUpperCase() + a.phase.slice(1);
    phaseBadge.className = `pop-phase-badge badge phase-${a.phase}`;
    phaseBadge.hidden = false;
  } else {
    phaseBadge.hidden = true;
  }

  const routeLine = q('.pop-route-line');
  if (a.origin || a.destination) {
    const aports = airports || {};
    const originEntry = a.origin ? aports[a.origin] : null;
    const destEntry = a.destination ? aports[a.destination] : null;
    q('.pop-origin-code').textContent = a.origin || '—';
    q('.pop-dest-code').textContent = a.destination || '—';
    q('.pop-origin-name').textContent = originEntry?.name || '';
    q('.pop-dest-name').textContent = destEntry?.name || '';
    const progress = a.on_ground ? null : flightProgress(
      originEntry?.lat, originEntry?.lon,
      destEntry?.lat, destEntry?.lon,
      a.lat, a.lon, a.speed,
    );
    const progressEl = q('.pop-progress');
    if (progress) {
      q('.pop-progress-fill').style.width = `${(progress.pct * 100).toFixed(1)}%`;
      q('.pop-progress-pct').textContent = `${Math.round(progress.pct * 100)}%`;
      q('.pop-progress-eta').textContent = progress.etaMinutes < 60
        ? `ETA ${progress.etaMinutes} min`
        : `ETA ${Math.floor(progress.etaMinutes / 60)}h ${progress.etaMinutes % 60}m`;
      progressEl.hidden = false;
    } else {
      progressEl.hidden = true;
    }
    const wxWrap = q('.pop-wx');
    const origWx = originEntry?.metar;
    const destWx = destEntry?.metar;
    if (origWx || destWx) {
      const origCell = q('.pop-wx-orig');
      const destCell = q('.pop-wx-dest');
      if (origWx && a.origin) {
        origCell.innerHTML = formatMetar(a.origin, origWx);
        origCell.hidden = false;
      } else {
        origCell.hidden = true;
      }
      if (destWx && a.destination) {
        destCell.innerHTML = formatMetar(a.destination, destWx);
        destCell.hidden = false;
      } else {
        destCell.hidden = true;
      }
      wxWrap.hidden = false;
    } else {
      wxWrap.hidden = true;
    }
    routeLine.hidden = false;
  } else {
    routeLine.hidden = true;
  }

  let altLabel = uconv('alt', a.altitude);
  if (a.altitude_baro == null && a.altitude_geo != null) {
    altLabel += ' <span class="alt-tag">geo</span>';
  } else if (
    a.altitude_baro != null && a.altitude_geo != null &&
    Math.abs(a.altitude_baro - a.altitude_geo) > 100
  ) {
    altLabel += ` <span class="alt-tag">baro; geo ${uconv('alt', a.altitude_geo)}</span>`;
  }
  const entry = state.aircraft.get(a.icao);
  const tAlt = entry?.trend?.alt || trendInfo(entry, 'alt');
  const tSpd = entry?.trend?.spd || trendInfo(entry, 'spd');

  q('.pop-alt').innerHTML = altLabel;
  q('.pop-alt-wrap').className = 'val pop-alt-wrap ' + tAlt.cls;
  q('.pop-alt-trend').innerHTML = tAlt.arrow;
  q('.pop-spd').textContent = uconv('spd', a.speed);
  q('.pop-spd-wrap').className = 'val pop-spd-wrap ' + tSpd.cls;
  q('.pop-spd-trend').innerHTML = tSpd.arrow;
  q('.pop-hdg').textContent = fmt(a.track, '°');
  q('.pop-compass').innerHTML = compassIcon(a.track);
  q('.pop-vrate').textContent = uconv('vrt', a.vrate);
  q('.pop-squawk').textContent = a.squawk || '—';
  q('.pop-dist').textContent = uconv('dst', a.distance_km);
  q('.pop-lat').textContent = a.lat != null ? a.lat.toFixed(4) + '°' : '—';
  q('.pop-lon').textContent = a.lon != null ? a.lon.toFixed(4) + '°' : '—';
  const pathKm = entry?.clientTrail?.length
    ? (entry.trailDistKm || 0)
    : trailDistanceKm(a.trail || []);
  q('.pop-path').textContent = uconv('dst', pathKm);

  // Profile SVGs: re-run only when trail length or endpoint shifted —
  // otherwise we're rebuilding identical markup every tick.
  const altSvg = q('.pop-alt-profile');
  const spdSvg = q('.pop-spd-profile');
  const tr = a.trail || [];
  const trailFp = tr.length
    ? `${tr.length}|${tr[0][0]}|${tr[0][1]}|${tr[tr.length - 1][0]}|${tr[tr.length - 1][1]}`
    : '0';
  if (altSvg.dataset.trailFp !== trailFp) {
    renderAltProfile(altSvg, a.trail);
    renderSpdProfile(spdSvg, a.trail);
    altSvg.dataset.trailFp = trailFp;
    spdSvg.dataset.trailFp = trailFp;
  }

  const hexUpper = a.icao.toUpperCase();
  const hexLower = a.icao.toLowerCase();
  q('.pop-link-fa').href = `https://flightaware.com/live/modes/${hexLower}/redirect`;
  q('.pop-link-ps').href = `https://www.planespotters.net/hex/${hexUpper}`;
  const fr24 = q('.pop-link-fr24');
  const airnav = q('.pop-link-airnav');
  if (a.registration) {
    const regLower = a.registration.toLowerCase();
    const regUpper = a.registration.toUpperCase();
    fr24.href = `https://www.flightradar24.com/data/aircraft/${regLower}`;
    airnav.href = `https://www.airnavradar.com/data/registration/${regUpper}`;
    fr24.hidden = false;
    airnav.hidden = false;
  } else {
    fr24.hidden = true;
    airnav.hidden = true;
  }

  q('.pop-msgs').textContent = a.msg_count.toLocaleString();
  q('.pop-signal').textContent = signalLabel(a.signal_peak);
  const firstSeen = entry?.sessionFirstSeen || a.first_seen;
  q('.pop-first-seen').textContent = relativeAge(firstSeen, now);
}

// Route string (e.g. "EGLL → KJFK") from snapshot fields.
export function routeLabel(a, airports) {
  if (!a.origin && !a.destination) return '';
  const code = (icao) => {
    if (!icao) return '?';
    const info = airports && airports[icao];
    const name = info && info.name ? info.name : null;
    if (!name) return `<span class="airport-code">${escapeHtml(icao)}</span>`;
    return (
      `<span class="airport-code" data-title="${escapeHtml(name)}">` +
      `${escapeHtml(icao)}</span>`
    );
  };
  return `${code(a.origin)} → ${code(a.destination)}`;
}

// Pan the map so `latlng` sits in the middle of the portion of the map
// NOT covered by the detail panel. On mobile the panel overlays the
// whole screen so this is equivalent to panTo(). On desktop we shift
// right by half the panel's occluded width.
export function panToFollowed(latlng, opts) {
  const map = state.map;
  if (!state.detailPanelEl.classList.contains('open')) {
    map.panTo(latlng, opts);
    return;
  }
  const panelRect = state.detailPanelEl.getBoundingClientRect();
  const mapRect = map.getContainer().getBoundingClientRect();
  if (panelRect.right >= mapRect.right && panelRect.left <= mapRect.left) {
    map.panTo(latlng, opts);
    return;
  }
  const obscured = Math.max(0, Math.min(panelRect.right, mapRect.right) - mapRect.left);
  const offsetX = obscured / 2;
  const planePt = map.latLngToContainerPoint(latlng);
  const shifted = map.containerPointToLatLng(L.point(planePt.x - offsetX, planePt.y));
  map.panTo(shifted, opts);
}

export function applyFollowState() {
  document.querySelectorAll('.follow-control').forEach(
    el => el.classList.toggle('active', state.followSelected),
  );
  if (state.followSelected && state.selectedIcao) {
    const entry = state.aircraft.get(state.selectedIcao);
    if (entry) panToFollowed(entry.marker.getLatLng(), { animate: true });
  }
}

export function setFollow(value) {
  state.followSelected = value;
  try { localStorage.setItem('flightjar.follow', state.followSelected ? '1' : '0'); } catch (_) {}
  applyFollowState();
}

export function applyWatchStateToPanel() {
  if (!state.selectedIcao) {
    state.detailWatchBtn.classList.remove('watched');
    return;
  }
  const on = state.watchlist.has(state.selectedIcao);
  state.detailWatchBtn.classList.toggle('watched', on);
  state.detailWatchBtn.setAttribute(
    'aria-label', on ? 'Remove from watchlist' : 'Add to watchlist',
  );
  state.detailWatchBtn.title = on
    ? 'Remove from watchlist'
    : 'Add to watchlist (notify when this aircraft reappears)';
}

export function readDeepLink() {
  const m = /[#&]icao=([0-9a-fA-F]{6})/.exec(location.hash);
  return m ? m[1].toLowerCase() : null;
}

export function writeDeepLink(icao) {
  const target = icao ? `#icao=${icao.toUpperCase()}` : '';
  if (location.hash !== target) {
    history.replaceState(null, '', location.pathname + location.search + target);
  }
}

// Fetch the adsbdb aircraft record and drop its photo into the panel's
// .ac-photo slot. The slot renders a shimmering skeleton by default; on
// success we swap in an <img>, on miss we add .no-photo to collapse it.
async function fillAircraftPhoto(icao) {
  const slotSelector = `.ac-photo[data-icao="${icao}"]`;
  const findSlot = () => document.querySelector(slotSelector);
  if (!findSlot()) return;

  let info = state.aircraftInfoCache.get(icao);
  if (info === undefined) {
    try {
      const r = await fetch(`/api/aircraft/${encodeURIComponent(icao)}`);
      if (!r.ok) throw new Error(`HTTP ${r.status}`);
      info = await r.json();
    } catch (e) {
      console.warn('aircraft lookup failed', icao, e);
      info = null;
    }
    state.aircraftInfoCache.set(icao, info);
  }

  const slot = findSlot();
  if (!slot) return;
  if (!info || !info.photo_thumbnail) {
    slot.classList.add('no-photo');
    slot.innerHTML =
      `<div class="no-photo-inner">` +
        lucide('camera-off', { size: 22, strokeWidth: 1.5 }) +
        `<span>No photo available</span>` +
      `</div>`;
    return;
  }
  const thumb = escapeHtml(info.photo_thumbnail);
  const full = info.photo_url ? escapeHtml(info.photo_url) : thumb;
  const credit = info.photo_credit
    ? `<span class="photo-credit">© ${escapeHtml(info.photo_credit)}</span>`
    : '';
  slot.innerHTML =
    `<a href="${full}" target="_blank" rel="noopener">` +
    `<img src="${thumb}" alt="" loading="lazy">${credit}</a>`;
}

export function openDetailPanel(icao) {
  const entry = state.aircraft.get(icao);
  // Fall back to the current snapshot when we have no client entry.
  // That's the case for sidebar-visible planes that we skipped creating
  // a map marker for (no position, or position but no track).
  let a = entry?.data;
  if (!a && state.lastSnap) {
    a = state.lastSnap.aircraft.find((ac) => ac.icao === icao);
  }
  if (!a) return;
  state.selectedIcao = icao;
  writeDeepLink(icao);
  state.detailContentHost.innerHTML = '';
  state.detailPanelContent = buildPopupContent(a, state.lastSnap?.now, state.lastSnap?.airports);
  state.detailContentHost.appendChild(state.detailPanelContent);
  state.detailPanelEl.classList.add('open');
  state.appEl.classList.add('panel-open');
  if (entry) {
    entry.marker.setIcon(
      planeIcon(a.track, altColor(a.altitude), true, !!a.emergency, a.type_icao),
    );
  }
  document.querySelectorAll('.ac-item').forEach(el => {
    el.classList.toggle('selected', el.dataset.icao === icao);
  });
  fillAircraftPhoto(icao);
  applyWatchStateToPanel();
  applyTrailsVisibility();
  // Follow auto-engages on open; user can still toggle off.
  state.followSelected = true;
  applyFollowState();
}

export function closeDetailPanel() {
  const icao = state.selectedIcao;
  if (!icao) return;
  state.selectedIcao = null;
  writeDeepLink(null);
  state.detailPanelEl.classList.remove('open');
  state.appEl.classList.remove('panel-open');
  const prevContent = state.detailPanelContent;
  state.detailPanelContent = null;
  setTimeout(() => {
    if (state.detailPanelContent === null && state.detailContentHost.firstChild === prevContent) {
      state.detailContentHost.innerHTML = '';
    }
  }, 220);
  const entry = state.aircraft.get(icao);
  if (entry) {
    if (entry.lost) {
      // The panel was keeping a timed-out aircraft alive so the user
      // could read its last-known data. Tear down now.
      state.map.removeLayer(entry.marker);
      state.map.removeLayer(entry.trail);
      state.aircraft.delete(icao);
    } else {
      const a = entry.data;
      entry.marker.setIcon(
        planeIcon(a.track, altColor(a.altitude), false, !!a.emergency, a.type_icao),
      );
    }
  }
  document.querySelectorAll('.ac-item').forEach(el => {
    if (el.dataset.icao === icao) el.classList.remove('selected');
  });
  applyTrailsVisibility();
  state.followSelected = false;
  applyFollowState();
}

// Freeze the selected aircraft's client-side state when it times out
// on the server. The marker fades (.ac-stale), Follow disengages, and
// the panel sprouts a SIGNAL LOST pill.
export function markEntryLost(entry) {
  if (entry.lost) return;
  entry.lost = true;
  entry.data = { ...entry.data, lost: true };
  const el = entry.marker.getElement();
  if (el) el.classList.add('ac-stale');
  if (state.followSelected) {
    state.followSelected = false;
    applyFollowState();
  }
}

export function unmarkEntryLost(entry) {
  if (!entry.lost) return;
  entry.lost = false;
  if (entry.data.lost) entry.data = { ...entry.data, lost: false };
  const el = entry.marker.getElement();
  if (el) el.classList.remove('ac-stale');
}

export function selectAircraft(icao) {
  const entry = state.aircraft.get(icao);
  openDetailPanel(icao);
  if (entry) panToFollowed(entry.marker.getLatLng());
}

// While an aircraft is selected (panel open), redirect every zoom
// action to pivot on that aircraft instead of the default pivot
// (map center for buttons/keyboard, mouse cursor for scroll wheel).
// We wrap the instance's setZoom + setZoomAround — zoomIn/zoomOut go
// through setZoom, and scroll/double-click/box zoom go through
// setZoomAround, so between the two we catch every entry point.
function installSelectedZoomPivot(map) {
  function pivotLatLng() {
    if (!state.selectedIcao) return null;
    const entry = state.aircraft.get(state.selectedIcao);
    const a = entry?.data;
    if (!a || a.lat == null || a.lon == null) return null;
    return L.latLng(a.lat, a.lon);
  }
  const origSetZoom = map.setZoom.bind(map);
  const origSetZoomAround = map.setZoomAround.bind(map);
  map.setZoom = function (zoom, options) {
    const p = pivotLatLng();
    if (p && map._loaded) return origSetZoomAround(p, zoom, options);
    return origSetZoom(zoom, options);
  };
  map.setZoomAround = function (latlng, zoom, options) {
    const p = pivotLatLng();
    return origSetZoomAround(p || latlng, zoom, options);
  };
}

// Called by app.js after DOM + watchlist are ready. Captures DOM refs
// into `state` and wires the panel's own event listeners (close button,
// Escape, map background click → close, map drag → disable Follow).
export function initDetailPanel() {
  state.detailPanelEl = document.getElementById('detail-panel');
  state.detailContentHost = document.getElementById('detail-content');
  state.detailWatchBtn = document.getElementById('detail-watch');
  state.appEl = document.getElementById('app');

  state.detailWatchBtn.addEventListener('click', async () => {
    if (!state.selectedIcao) return;
    const nowWatching = state.watchlist.toggle(state.selectedIcao);
    if (nowWatching) await state.watchlist.setNotifyEnabled(true);
    applyWatchStateToPanel();
    if (state.lastSnap) _renderSidebar(state.lastSnap);
  });

  document.getElementById('detail-close').addEventListener('click', closeDetailPanel);
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && state.selectedIcao) closeDetailPanel();
  });
  state.map.on('click', () => {
    if (state.selectedIcao) closeDetailPanel();
  });
  state.map.on('dragstart', () => {
    if (state.followSelected) {
      state.followSelected = false;
      applyFollowState();
    }
  });

  installSelectedZoomPivot(state.map);

  window.addEventListener('hashchange', () => {
    const icao = readDeepLink();
    if (icao && icao !== state.selectedIcao) selectAircraft(icao);
  });

  state.pendingDeepLinkIcao = readDeepLink();
}
