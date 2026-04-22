// Main snapshot handler — called once per WebSocket push. Walks the
// aircraft list, creates/updates markers, merges trails, fires
// server-queued events (e.g. range-record toasts), evicts stale
// entries, and drives the sidebar + detail panel re-renders. All
// cross-module state changes live on `state`.

import { altColor } from './altitude.js';
import { claimFirstOfDay, RANGE_RECORD_THROTTLE_MS, state } from './state.js';
import { GO_AROUND_COOLDOWN_MS } from './state.js';
import { applyTrailsVisibility, mergeClientTrail, peekListItem, setHoverHalo } from './trails.js';
import { isNotable } from './notable_aircraft.js';
import {
  markEntryLost,
  panToFollowed,
  selectAircraft,
  unmarkEntryLost,
  updatePopupContent,
} from './detail_panel.js';
import { planeIcon, rotateMarkerIcon } from './icons.js';
import { pushHistory, trendInfo } from './trend.js';
import { renderReceiver } from './map_setup.js';
import { renderSidebar } from './sidebar.js';
import { showToast } from './toast.js';
import { updateLabelFor } from './labels.js';

export function update(snap) {
  state.lastSnap = snap;
  state.lastSnapAt = Date.now();
  if (state.sessionStartFrames == null && typeof snap.frames === 'number') {
    state.sessionStartFrames = snap.frames;
  }
  renderReceiver(snap.receiver);
  const seen = new Set();

  // Drain server-emitted one-shot events (coverage range records,
  // etc.). Rate-limits inside each case so a burst doesn't avalanche
  // the toast stack.
  if (Array.isArray(snap.events) && snap.events.length) {
    for (const ev of snap.events) {
      if (ev && ev.type === 'range_record') {
        const now = Date.now();
        if (now - state.lastRangeRecordToastAt < RANGE_RECORD_THROTTLE_MS) continue;
        state.lastRangeRecordToastAt = now;
        const angle = String(Math.round(ev.angle ?? 0)).padStart(3, '0');
        showToast(
          `New range record: ${Math.round(ev.dist_km)} km at ${angle}°`,
          { level: 'egg' },
        );
      }
    }
  }

  for (const a of snap.aircraft) {
    seen.add(a.icao);
    state.sessionSeenSet.add(a.icao);
    if (typeof a.distance_km === 'number' && a.distance_km > state.sessionMaxRangeKm) {
      state.sessionMaxRangeKm = a.distance_km;
    }
    // Fire a one-off toast the first time a notable tail enters
    // coverage this session.
    if (!state.notableAnnounced.has(a.icao)) {
      const notable = isNotable(a.icao, a.callsign);
      if (notable) {
        state.notableAnnounced.add(a.icao);
        showToast(`${notable.emoji} ${notable.name} in coverage`, { level: 'egg' });
      }
    }
    // Skip the map marker until we have both a position and a track.
    if (a.lat == null || a.lon == null || a.track == null) continue;

    const color = altColor(a.altitude);
    const isSelected = a.icao === state.selectedIcao;

    // Icon fingerprint deliberately excludes `track` — see the
    // track-only branch below for why. Building the SVG is the
    // expensive step, so we defer it until we know setIcon will
    // actually be called.
    const iconFp = `${color}|${isSelected ? 1 : 0}|${a.emergency ? 1 : 0}|${a.type_icao || ''}`;
    let entry = state.aircraft.get(a.icao);
    if (!entry) {
      const icon = planeIcon(a.track, color, isSelected, !!a.emergency, a.type_icao);
      const marker = L.marker([a.lat, a.lon], { icon }).addTo(state.map);
      const trail = L.layerGroup().addTo(state.map);
      marker.on('click', () => selectAircraft(a.icao));
      marker.on('mouseover', () => peekListItem(a.icao, true));
      marker.on('mouseout',  () => peekListItem(a.icao, false));
      entry = {
        marker, trail, label: null, data: a, trailFp: null,
        iconFp, lastTrack: a.track,
        clientTrail: [],
        hist: { alt: [], spd: [], dst: [] },
        // Pin the earliest first_seen we've ever seen for this ICAO
        // so the stat outlives server-side evictions.
        sessionFirstSeen: a.first_seen || (snap.now || Date.now() / 1000),
        prevPhase: a.phase || null,
      };
      state.aircraft.set(a.icao, entry);
      // First trackable contact of the day: claim the slot if empty.
      if (!state.firstOfDayIcao && a.track != null) {
        state.firstOfDayIcao = a.icao;
        claimFirstOfDay(a.icao);
      }
    } else {
      if (entry.lost) unmarkEntryLost(entry);
      if (a.first_seen && a.first_seen < entry.sessionFirstSeen) {
        entry.sessionFirstSeen = a.first_seen;
      }
      // Go-around heuristic: phase flipped from approach -> climb.
      if (
        entry.prevPhase === 'approach' && a.phase === 'climb'
        && !a.on_ground
      ) {
        const nowMs = Date.now();
        const last = state.goAroundFiredAt.get(a.icao) || 0;
        if (nowMs - last > GO_AROUND_COOLDOWN_MS) {
          state.goAroundFiredAt.set(a.icao, nowMs);
          const label = a.callsign || a.registration || a.icao.toUpperCase();
          showToast(`⚠ Possible go-around: ${label}`, { level: 'warn' });
        }
      }
      entry.prevPhase = a.phase || entry.prevPhase;
      entry.marker.setLatLng([a.lat, a.lon]);
      // setIcon() replaces the icon's DOM element and would drop a
      // mid-flight click. Only rebuild when a prop that actually
      // changes the SVG shape/colour has moved; for track-only
      // updates, rotate the existing element in place.
      if (entry.iconFp !== iconFp) {
        entry.marker.setIcon(
          planeIcon(a.track, color, isSelected, !!a.emergency, a.type_icao)
        );
        entry.iconFp = iconFp;
        entry.lastTrack = a.track;
      } else if (a.track !== entry.lastTrack) {
        rotateMarkerIcon(entry.marker, a.track);
        entry.lastTrack = a.track;
      }
    }
    if (state.hoveredFromListIcao === a.icao && state.hoverHalo) {
      state.hoverHalo.setLatLng([a.lat, a.lon]);
    }

    // Merge the incoming sliding-window trail into our unbounded
    // client-side buffer so the selected-plane view has full history.
    const prevTrailLen = entry.clientTrail.length;
    mergeClientTrail(entry, a.trail);
    if (entry.clientTrail.length !== prevTrailLen) {
      const d = entry.trailDistKm || 0;
      if (d > state.sessionLongestTrailKm) state.sessionLongestTrailKm = d;
    }
    if (a.icao === state.selectedIcao && state.detailPanelContent) {
      updatePopupContent(state.detailPanelContent, a, snap.now, snap.airports);
    }

    entry.data = a;
    pushHistory(entry, a);
    // Cache trend arrows once per tick rather than re-deriving them
    // three times per visible sidebar row.
    entry.trend = {
      alt: trendInfo(entry, 'alt'),
      spd: trendInfo(entry, 'spd'),
      dst: trendInfo(entry, 'dst'),
    };
    updateLabelFor(entry);
  }

  // Single pass across all aircraft now that each has its fresh
  // snapshot data merged — applyTrailsVisibility picks which source
  // to render from (server window vs full client history).
  applyTrailsVisibility();

  // Keep the selected aircraft centred when "Follow" is on.
  if (state.followSelected && state.selectedIcao) {
    const entry = state.aircraft.get(state.selectedIcao);
    if (entry) panToFollowed(entry.marker.getLatLng(), { animate: false });
  }

  // Drop aircraft no longer reported. The currently-selected aircraft
  // gets special treatment: we retain its entry + marker + trail in a
  // "signal lost" state so the detail panel stays open with its last
  // known data.
  for (const [icao, entry] of state.aircraft.entries()) {
    if (!seen.has(icao)) {
      if (state.hoveredFromListIcao === icao) {
        state.hoveredFromListIcao = null;
        setHoverHalo(null);
      }
      if (state.hoveredFromMapIcao === icao) state.hoveredFromMapIcao = null;
      if (state.selectedIcao === icao) {
        markEntryLost(entry);
        continue;
      }
      state.map.removeLayer(entry.marker);
      state.map.removeLayer(entry.trail);
      state.aircraft.delete(icao);
    }
  }
  // Keep the panel's Age/first-seen fields ticking even when no new
  // data is arriving for the selected aircraft.
  if (state.selectedIcao && state.detailPanelContent && !seen.has(state.selectedIcao)) {
    const lostEntry = state.aircraft.get(state.selectedIcao);
    if (lostEntry) {
      updatePopupContent(state.detailPanelContent, lostEntry.data, snap.now, snap.airports);
    }
  }

  state.watchlist.noticeAppearances(snap.aircraft);
  renderSidebar(snap);

  // Resolve any pending #icao= deep link once its aircraft appears.
  if (state.pendingDeepLinkIcao) {
    const hit = snap.aircraft.find(a => a.icao === state.pendingDeepLinkIcao);
    if (hit) {
      selectAircraft(state.pendingDeepLinkIcao);
      state.pendingDeepLinkIcao = null;
    }
  }

  // Auto-fit on first populated update.
  if (state.firstUpdate && snap.positioned > 0) {
    const bounds = L.latLngBounds(
      snap.aircraft.filter(a => a.lat != null).map(a => [a.lat, a.lon])
    );
    if (bounds.isValid()) state.map.fitBounds(bounds.pad(0.2), { maxZoom: 7 });
    state.firstUpdate = false;
  }
}
