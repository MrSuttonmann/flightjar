// Unit system: metric / imperial / nautical. Each entry gives the per-kind
// multiplier (from canonical feet / knots / fpm / km) plus suffix and digits.

export const UNIT_SYSTEMS = {
  metric: {
    alt: { mul: 0.3048,  suf: ' m',    digits: 0 },
    spd: { mul: 1.852,   suf: ' km/h', digits: 0 },
    vrt: { mul: 0.00508, suf: ' m/s',  digits: 1 },
    dst: { mul: 1,       suf: ' km',   digits: 0 },
  },
  imperial: {
    alt: { mul: 1,         suf: ' ft',  digits: 0 },
    spd: { mul: 1.15078,   suf: ' mph', digits: 0 },
    vrt: { mul: 1,         suf: ' fpm', digits: 0 },
    dst: { mul: 0.621371,  suf: ' mi',  digits: 0 },
  },
  nautical: {
    alt: { mul: 1,         suf: ' ft',  digits: 0 },
    spd: { mul: 1,         suf: ' kt',  digits: 0 },
    vrt: { mul: 1,         suf: ' fpm', digits: 0 },
    dst: { mul: 0.539957,  suf: ' nm',  digits: 0 },
  },
};

let unitSystem = 'nautical';

export function getUnitSystem() {
  return unitSystem;
}

// Returns true on success, false for unknown system names.
export function setUnitSystem(v) {
  if (!UNIT_SYSTEMS[v]) return false;
  unitSystem = v;
  return true;
}

export function uconv(kind, value) {
  if (value == null || isNaN(value)) return '—';
  // Metric altitude flips to km above 1 km so airliners read "10.7 km" not "10668 m".
  if (unitSystem === 'metric' && kind === 'alt') {
    const m = Number(value) * 0.3048;
    return m >= 1000 ? (m / 1000).toFixed(1) + ' km' : m.toFixed(0) + ' m';
  }
  const u = UNIT_SYSTEMS[unitSystem][kind];
  return (Number(value) * u.mul).toFixed(u.digits) + u.suf;
}
