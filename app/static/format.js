// Small formatting helpers. No DOM, no Leaflet — safe to unit-test.

export function fmt(n, suffix = '', digits = 0) {
  if (n == null || isNaN(n)) return '—';
  return Number(n).toFixed(digits) + suffix;
}

export function ageOf(a, now) {
  if (now == null || a.last_seen == null) return null;
  return Math.max(0, now - a.last_seen);
}

// Small rotated triangle; 0° points up (north). `currentColor` so it
// inherits from the surrounding tone.
export function compassIcon(deg) {
  if (deg == null || isNaN(deg)) return '';
  return (
    `<svg class="compass" viewBox="-6 -6 12 12" width="12" height="12" ` +
    `style="transform: rotate(${Number(deg)}deg)">` +
    `<path d="M0,-5 L3,4 L0,2 L-3,4 Z" fill="currentColor"/></svg>`
  );
}
