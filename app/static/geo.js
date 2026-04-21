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
