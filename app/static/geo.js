// Great-circle distance between two lat/lon points, in kilometres.
// Mirrors app/coverage.py::_haversine_km on the backend so the client and
// server agree on "how far is this plane from that airport".
const EARTH_KM = 6371.0;

export function haversineKm(lat1, lon1, lat2, lon2) {
  const phi1 = lat1 * Math.PI / 180;
  const phi2 = lat2 * Math.PI / 180;
  const dphi = (lat2 - lat1) * Math.PI / 180;
  const dlam = (lon2 - lon1) * Math.PI / 180;
  const a = Math.sin(dphi / 2) ** 2
          + Math.cos(phi1) * Math.cos(phi2) * Math.sin(dlam / 2) ** 2;
  return 2 * EARTH_KM * Math.asin(Math.min(1, Math.sqrt(a)));
}

// Sum of great-circle distances between consecutive trail points.
// Accepts trail entries of shape `[lat, lon, ...]` — extra fields are
// ignored. Gap-flagged segments (spanning a signal-lost period) are
// included because the endpoints are real fixes and the straight-line
// between them is the best available estimate of the ground path
// flown during the silence. Returns 0 for empty / single-point trails.
export function trailDistanceKm(trail) {
  if (!Array.isArray(trail) || trail.length < 2) return 0;
  let total = 0;
  for (let i = 1; i < trail.length; i++) {
    const a = trail[i - 1];
    const b = trail[i];
    if (!a || !b) continue;
    const la = a[0], oa = a[1], lb = b[0], ob = b[1];
    if (la == null || oa == null || lb == null || ob == null) continue;
    total += haversineKm(la, oa, lb, ob);
  }
  return total;
}

// Returns { pct, etaMinutes, flownKm, remainingKm } for a flight between two
// airports given the current position and groundspeed. Returns null when any
// input is missing or the plane is going too slowly to compute a meaningful
// ETA (e.g. on the ground, or a decoded-but-stale speed of 0).
//
// speed_kn is in knots (1 knot = 1.852 km/h). Flying time = remaining_km /
// (speed_kn * 1.852) hours.
export function flightProgress(originLat, originLon, destLat, destLon, curLat, curLon, speedKn, minSpeedKn = 50) {
  if (originLat == null || originLon == null
      || destLat == null || destLon == null
      || curLat == null || curLon == null) return null;
  if (!(speedKn > minSpeedKn)) return null;
  const total = haversineKm(originLat, originLon, destLat, destLon);
  if (!(total > 0)) return null;
  const flown = haversineKm(originLat, originLon, curLat, curLon);
  const remaining = haversineKm(curLat, curLon, destLat, destLon);
  // Clamp progress to [0, 1]; the plane can be slightly off the great-circle
  // so flown+remaining rarely exactly equals total.
  const pct = Math.max(0, Math.min(1, flown / total));
  const etaMinutes = Math.round(remaining / (speedKn * 1.852) * 60);
  return { pct, etaMinutes, flownKm: flown, remainingKm: remaining };
}

// Point-in-polygon helpers below operate on GeoJSON-style coordinates
// — i.e. `[lon, lat]` ordering, NOT Leaflet's `[lat, lng]`. Call sites
// dealing with Leaflet `latlng` objects must pass `(latlng.lng,
// latlng.lat, geom)`. The 2D ray-cast is fine at our latitudes; an
// airspace polygon spanning enough degrees for spherical curvature to
// matter would also be too large to render usefully. Antimeridian
// crossings are not represented in the OpenAIP data we serve (UK / EU
// region); a polygon that wraps ±180° would silently misclassify here.

// Even-odd ray-cast against a single ring. Boundary behaviour is
// implementation-defined (a point lying exactly on a vertex / edge may
// classify either way) — fine for our use, which doesn't care about
// sub-metre precision at airspace boundaries.
export function pointInRing(lon, lat, ring) {
  if (!Array.isArray(ring) || ring.length < 3) return false;
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const xi = ring[i][0], yi = ring[i][1];
    const xj = ring[j][0], yj = ring[j][1];
    const intersect = (yi > lat) !== (yj > lat)
      && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi;
    if (intersect) inside = !inside;
  }
  return inside;
}

// GeoJSON Polygon: `rings[0]` is the outer boundary, `rings[1..]` are
// holes. Inside iff inside the outer ring AND not inside any hole.
export function pointInPolygon(lon, lat, rings) {
  if (!Array.isArray(rings) || rings.length === 0) return false;
  if (!pointInRing(lon, lat, rings[0])) return false;
  for (let i = 1; i < rings.length; i++) {
    if (pointInRing(lon, lat, rings[i])) return false;
  }
  return true;
}

// Dispatch on a GeoJSON geometry's `type`. Returns false for any
// type other than Polygon / MultiPolygon (LineString, Point, …).
export function pointInGeometry(lon, lat, geom) {
  if (!geom || !geom.type) return false;
  if (geom.type === 'Polygon') {
    return pointInPolygon(lon, lat, geom.coordinates);
  }
  if (geom.type === 'MultiPolygon') {
    const polys = geom.coordinates;
    if (!Array.isArray(polys)) return false;
    for (const rings of polys) {
      if (pointInPolygon(lon, lat, rings)) return true;
    }
    return false;
  }
  return false;
}

// Axis-aligned bounding box `[minLon, minLat, maxLon, maxLat]` of a
// Polygon or MultiPolygon. Computed once per fetch in openaip.js so
// the per-mousemove slice query can cheaply reject most airspaces
// before running the full ring scan. Returns null for unsupported
// types.
export function bboxOfGeometry(geom) {
  if (!geom || !geom.type) return null;
  let minLon = Infinity, minLat = Infinity;
  let maxLon = -Infinity, maxLat = -Infinity;
  function consumeRing(ring) {
    for (const pt of ring) {
      const x = pt[0], y = pt[1];
      if (x < minLon) minLon = x;
      if (x > maxLon) maxLon = x;
      if (y < minLat) minLat = y;
      if (y > maxLat) maxLat = y;
    }
  }
  if (geom.type === 'Polygon') {
    if (!Array.isArray(geom.coordinates) || geom.coordinates.length === 0) return null;
    consumeRing(geom.coordinates[0]);
  } else if (geom.type === 'MultiPolygon') {
    if (!Array.isArray(geom.coordinates)) return null;
    for (const rings of geom.coordinates) {
      if (Array.isArray(rings) && rings.length > 0) consumeRing(rings[0]);
    }
  } else {
    return null;
  }
  if (!isFinite(minLon)) return null;
  return [minLon, minLat, maxLon, maxLat];
}

// 4-compare bbox containment test. Boundary inclusive.
export function bboxContains(bbox, lon, lat) {
  if (!bbox) return false;
  return lon >= bbox[0] && lon <= bbox[2]
      && lat >= bbox[1] && lat <= bbox[3];
}
