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

// Rebuild the per-aircraft trail as a chain of short segments, each
// coloured by the altitude at that point. Using many small segments with
// round lineJoin gives a smooth curved look without a splines library.
// `drTo`, when set to [lat, lon], is appended as a dotted dark segment
// from the last real trail point to the dead-reckoned current position,
// so viewers can tell observed data from extrapolated data at a glance.
function rebuildTrail(entry, points, drTo) {
  const havePoints = points && points.length >= 2;
  if (!havePoints && !drTo) {
    if (entry.trailFp != null) { entry.trail.clearLayers(); entry.trailFp = null; }
    return;
  }
  // Skip rebuild if nothing changed (common between snapshot ticks when
  // the aircraft hasn't reported a new position). Comparing length alone
  // breaks once the server's trail deque is full and starts rotating —
  // same length, different contents — so fingerprint the endpoints too,
  // plus the dead-reckoned tip so extrapolation movement re-renders.
  const last = havePoints ? points[points.length - 1] : null;
  const first = havePoints ? points[0] : null;
  const drTag = drTo ? `${drTo[0].toFixed(4)},${drTo[1].toFixed(4)}` : '-';
  const fp = havePoints
    ? `${points.length}:${first[0]},${first[1]}:${last[0]},${last[1]}:${drTag}`
    : `dr:${drTag}`;
  if (fp === entry.trailFp) return;
  entry.trailFp = fp;

  entry.trail.clearLayers();
  if (havePoints) {
    for (let i = 1; i < points.length; i++) {
      const p0 = points[i - 1];
      const p1 = points[i];
      const opacity = 0.65 + 0.3 * (i / points.length);
      // p1[4] === true means the segment from p0 to p1 spanned a
      // signal-lost gap on the server — render it as the same
      // dashed near-black line the live dead-reckoning tip uses,
      // so history distinguishes observed flight from inferred.
      if (p1[4]) {
        L.polyline([[p0[0], p0[1]], [p1[0], p1[1]]], {
          renderer: state.trailsCanvas,
          color: '#0b0e14',
          weight: 2,
          opacity,
          dashArray: '2 4',
          lineCap: 'round',
          smoothFactor: 0,
          interactive: false,
        }).addTo(entry.trail);
        continue;
      }
      const alt = p1[2] != null ? p1[2] : p0[2];
      const color = altColor(alt);
      L.polyline([[p0[0], p0[1]], [p1[0], p1[1]]], {
        renderer: state.trailsCanvas,
        color,
        weight: 3,
        opacity,
        lineCap: 'round',
        lineJoin: 'round',
        smoothFactor: 0,
        interactive: false,
      }).addTo(entry.trail);
    }
  }
  if (drTo && last) {
    // Dashed near-black link from the last observed point to the
    // currently-extrapolated position. Dashes imply "inferred, not
    // observed"; a darker colour separates it from the altitude palette.
    L.polyline([[last[0], last[1]], drTo], {
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
}

export function applyTrailsVisibility() {
  for (const [icao, entry] of state.aircraft) {
    if (!state.showTrails) {
      entry.trail.clearLayers();
      entry.trailFp = null;
      continue;
    }
    if (state.selectedIcao && state.selectedIcao !== icao) {
      // A different plane is in focus — hide this one's trail.
      entry.trail.clearLayers();
      entry.trailFp = null;
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
