// Per-aircraft trail rendering + client-side unbounded trail buffer.
//
// Trails have three display modes, driven by state.showTrails +
// state.selectedIcao:
//   1. Trails off  → no trails rendered anywhere.
//   2. Trails on, no selection → every aircraft shows the server's
//      rolling 300-point trail (entry.data.trail).
//   3. Trails on, a plane selected → only that plane's trail is
//      visible, drawn from its full client-side history
//      (entry.clientTrail). Other planes' trails clear.

import { altColor } from './altitude.js';
import { haversineKm, trailDistanceKm } from './geo.js';
import { matchesActiveFilters } from './filters.js';
import { state } from './state.js';

// Sum of great-circle distance added by appending `newPoints` to a
// trail whose last point is `prev`. Used to maintain
// `entry.trailDistKm` incrementally so the selected-plane "Flown"
// stat and the session longest-trail tracker don't re-sum the full
// unbounded client trail every tick.
function appendedTrailDistanceKm(prev, newPoints) {
  let total = 0;
  let p = prev;
  for (const q of newPoints) {
    if (p && q && p[0] != null && p[1] != null && q[0] != null && q[1] != null) {
      total += haversineKm(p[0], p[1], q[0], q[1]);
    }
    p = q;
  }
  return total;
}

// Merge the server's latest sliding-window trail into the entry's
// unbounded client-side buffer. Server trails cap at TRAIL_MAX_POINTS
// (300 server-side), so its last point will already be in our buffer
// most ticks — we find the overlap by lat/lon and append only the new
// tail points. If there's no overlap at all (a very long gap, or
// first sighting), we take the whole server trail.
export function mergeClientTrail(entry, incoming) {
  if (!Array.isArray(incoming) || incoming.length === 0) return;
  if (entry.clientTrail.length === 0) {
    entry.clientTrail = incoming.slice();
    entry.trailDistKm = trailDistanceKm(entry.clientTrail);
    return;
  }
  const last = entry.clientTrail[entry.clientTrail.length - 1];
  for (let i = incoming.length - 1; i >= 0; i--) {
    if (incoming[i][0] === last[0] && incoming[i][1] === last[1]) {
      if (i + 1 < incoming.length) {
        const added = incoming.slice(i + 1);
        entry.trailDistKm = (entry.trailDistKm || 0)
          + appendedTrailDistanceKm(last, added);
        entry.clientTrail.push(...added);
      }
      return;
    }
  }
  // No overlap — server window has rotated past our tail. Append all.
  entry.trailDistKm = (entry.trailDistKm || 0)
    + appendedTrailDistanceKm(last, incoming);
  entry.clientTrail.push(...incoming);
}

// Identity for an observed trail segment — if this string matches a
// cached segment we can reuse its polyline rather than destroying and
// recreating it.
function segKey(p0, p1) {
  return `${p0[0]},${p0[1]}|${p1[0]},${p1[1]}|${p1[4] ? 'd' : 's'}`;
}

function makeSegmentPolyline(p0, p1, opacity) {
  if (p1[4]) {
    // p1[4] === true means the segment from p0 to p1 spanned a
    // signal-lost gap on the server — render it as the same dashed
    // near-black line the live dead-reckoning tip uses, so history
    // distinguishes observed flight from inferred.
    return L.polyline([[p0[0], p0[1]], [p1[0], p1[1]]], {
      renderer: state.trailsCanvas,
      color: '#0b0e14',
      weight: 2,
      opacity,
      dashArray: '2 4',
      lineCap: 'round',
      smoothFactor: 0,
      interactive: false,
    });
  }
  const alt = p1[2] != null ? p1[2] : p0[2];
  return L.polyline([[p0[0], p0[1]], [p1[0], p1[1]]], {
    renderer: state.trailsCanvas,
    color: altColor(alt),
    weight: 3,
    opacity,
    lineCap: 'round',
    lineJoin: 'round',
    smoothFactor: 0,
    interactive: false,
  });
}

export function resetTrailState(entry) {
  entry.trail.clearLayers();
  entry.trailFp = null;
  entry.trailSegments = null;
  entry.trailDrSegment = null;
  entry.trailLen = 0;
}

// Rebuild the per-aircraft trail as a chain of short segments, each
// coloured by the altitude at that point. Using many small segments with
// round lineJoin gives a smooth curved look without a splines library.
// `drTo`, when set to [lat, lon], is appended as a dotted dark segment
// from the last real trail point to the dead-reckoned current position,
// so viewers can tell observed data from extrapolated data at a glance.
//
// The incremental path is the fast one: append new tail segments and
// (when the server window rotates) drop old head segments, rather than
// tearing down every polyline each tick. We only fall back to a full
// rebuild when the cached chain no longer aligns with the incoming
// points (e.g. a gap of missed updates that skipped past our stored
// range).
function rebuildTrail(entry, points, drTo) {
  const havePoints = points && points.length >= 2;
  if (!havePoints && !drTo) {
    if (entry.trailFp != null) resetTrailState(entry);
    return;
  }
  const last = havePoints ? points[points.length - 1] : null;
  const first = havePoints ? points[0] : null;
  const drTag = drTo ? `${drTo[0].toFixed(4)},${drTo[1].toFixed(4)}` : '-';
  const fp = havePoints
    ? `${points.length}:${first[0]},${first[1]}:${last[0]},${last[1]}:${drTag}`
    : `dr:${drTag}`;
  if (fp === entry.trailFp) return;
  const prevLen = entry.trailLen || 0;
  entry.trailFp = fp;

  if (!havePoints) {
    // Only a DR tip; clear observed segments and fall through to DR.
    if (entry.trailSegments && entry.trailSegments.length) {
      for (const seg of entry.trailSegments) entry.trail.removeLayer(seg.poly);
    }
    entry.trailSegments = [];
    entry.trailLen = 0;
  } else {
    const newLen = points.length - 1;
    const cached = entry.trailSegments || [];

    // Align cached chain with the new points. The common case is a
    // server window that rotated forward by 1 — cached[0] rolled off,
    // so we search for new[0]'s segment inside cached. If cached is
    // empty or has drifted out of range, fall back to a full rebuild.
    let alignOk = true;
    let headDrop = 0;
    if (cached.length === 0) {
      alignOk = false;
    } else {
      const firstNewKey = segKey(points[0], points[1]);
      headDrop = -1;
      for (let i = 0; i < cached.length; i++) {
        if (cached[i].key === firstNewKey) {
          headDrop = i;
          break;
        }
      }
      if (headDrop < 0) alignOk = false;
    }

    if (alignOk) {
      // Verify the overlapping midsection matches. If any divergence,
      // give up and full-rebuild — keeps this code honest against
      // edge cases (e.g. a retroactive dashed-flag flip).
      const overlap = Math.min(cached.length - headDrop, newLen);
      for (let j = 0; j < overlap; j++) {
        const k = segKey(points[j], points[j + 1]);
        if (cached[headDrop + j].key !== k) { alignOk = false; break; }
      }
    }

    if (!alignOk) {
      // Full rebuild path.
      if (cached.length) {
        for (const seg of cached) entry.trail.removeLayer(seg.poly);
      }
      const segs = new Array(newLen);
      for (let i = 0; i < newLen; i++) {
        const p0 = points[i];
        const p1 = points[i + 1];
        const opacity = 0.65 + 0.3 * ((i + 1) / points.length);
        const poly = makeSegmentPolyline(p0, p1, opacity);
        poly.addTo(entry.trail);
        segs[i] = { key: segKey(p0, p1), poly };
      }
      entry.trailSegments = segs;
    } else {
      // Drop leading cached segments that rolled off the server window.
      if (headDrop > 0) {
        for (let j = 0; j < headDrop; j++) {
          entry.trail.removeLayer(cached[j].poly);
        }
        cached.splice(0, headDrop);
      }
      // Trim any surplus tail segments if the incoming trail is
      // shorter than what we had cached (rare — server deque reset).
      if (cached.length > newLen) {
        for (let j = newLen; j < cached.length; j++) {
          entry.trail.removeLayer(cached[j].poly);
        }
        cached.length = newLen;
      }
      // Extend with new tail segments to reach newLen total.
      if (cached.length < newLen) {
        const startIdx = cached.length;
        for (let idx = startIdx; idx < newLen; idx++) {
          const p0 = points[idx];
          const p1 = points[idx + 1];
          const opacity = 0.65 + 0.3 * ((idx + 1) / points.length);
          const poly = makeSegmentPolyline(p0, p1, opacity);
          poly.addTo(entry.trail);
          cached.push({ key: segKey(p0, p1), poly });
        }
      }
      // Refresh per-segment opacity when the trail length changed —
      // every segment's relative position (i / N) has shifted.
      if (points.length !== prevLen && cached.length) {
        const total = points.length;
        for (let j = 0; j < cached.length; j++) {
          cached[j].poly.setStyle({ opacity: 0.65 + 0.3 * ((j + 1) / total) });
        }
      }
      entry.trailSegments = cached;
    }
    entry.trailLen = points.length;
  }

  // DR tip: reuse a single polyline; update endpoints in place when
  // the last observed point moves or the extrapolated tip drifts.
  if (drTo && last) {
    const latlngs = [[last[0], last[1]], drTo];
    if (entry.trailDrSegment) {
      entry.trailDrSegment.setLatLngs(latlngs);
    } else {
      entry.trailDrSegment = L.polyline(latlngs, {
        renderer: state.trailsCanvas,
        color: '#0b0e14',
        weight: 2,
        opacity: 0.85,
        dashArray: '2 4',
        lineCap: 'round',
        smoothFactor: 0,
        interactive: false,
      }).addTo(entry.trail);
    }
  } else if (entry.trailDrSegment) {
    entry.trail.removeLayer(entry.trailDrSegment);
    entry.trailDrSegment = null;
  }
}

export function applyTrailsVisibility() {
  for (const [icao, entry] of state.aircraft) {
    if (!state.showTrails) {
      resetTrailState(entry);
      continue;
    }
    if (entry.hiddenByFilter) {
      resetTrailState(entry);
      continue;
    }
    if (state.selectedIcao && state.selectedIcao !== icao) {
      // A different plane is in focus — hide this one's trail.
      resetTrailState(entry);
      continue;
    }
    const a = entry.data;
    const source = state.selectedIcao === icao ? entry.clientTrail : a.trail;
    const drTo = a.position_stale && a.lat != null && a.lon != null
      ? [a.lat, a.lon] : null;
    rebuildTrail(entry, source, drTo);
  }
  state.syncOverlay(state.trailsProxy, state.showTrails);
}

// Show/hide markers + trails for aircraft that don't match the active
// list filters. The currently-selected aircraft is exempt — hiding a
// plane the user is actively looking at would orphan the detail panel.
// Called on filter-chip toggle and on every snapshot tick (so a plane
// that newly meets/exits a filter via server data flips correctly).
export function applyFilterVisibility() {
  const active = state.activeFilters;
  const selIcao = state.selectedIcao;
  if (active.size === 0 && state.showPeer) {
    for (const entry of state.aircraft.values()) {
      if (entry.hiddenByFilter) {
        state.map.addLayer(entry.marker);
        entry.hiddenByFilter = false;
      }
    }
    return;
  }
  for (const [icao, entry] of state.aircraft) {
    const isPeer = entry.data?.peer === true;
    const visible = icao === selIcao ||
      ((!isPeer || state.showPeer) && matchesActiveFilters(entry.data));
    if (!visible && !entry.hiddenByFilter) {
      state.map.removeLayer(entry.marker);
      resetTrailState(entry);
      entry.hiddenByFilter = true;
    } else if (visible && entry.hiddenByFilter) {
      state.map.addLayer(entry.marker);
      entry.hiddenByFilter = false;
    }
  }
}

export function setHoverHalo(icao) {
  if (state.hoverHalo) { state.map.removeLayer(state.hoverHalo); state.hoverHalo = null; }
  if (!icao) return;
  const entry = state.aircraft.get(icao);
  if (!entry) return;
  state.hoverHalo = L.circleMarker(entry.marker.getLatLng(), {
    radius: 22, color: '#5fa8ff', weight: 2, opacity: 0.9,
    fill: false, interactive: false,
  }).addTo(state.map);
}

export function peekListItem(icao, on) {
  state.hoveredFromMapIcao = on ? icao : null;
  const el = document.querySelector(`.ac-item[data-icao="${icao}"]`);
  if (!el) return;
  el.classList.toggle('peek', on);
  if (on) el.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
}
