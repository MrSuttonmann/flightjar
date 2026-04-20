// Small formatting helpers. No DOM, no Leaflet — safe to unit-test.

// Minimal HTML entity escape for inline-template safety. Every upstream
// string (callsign, registration, type_icao, airport names, origin/dest
// codes, …) goes through this before being interpolated into innerHTML.
const HTML_ESCAPE = {
  '&': '&amp;',
  '<': '&lt;',
  '>': '&gt;',
  '"': '&quot;',
  "'": '&#39;',
};
export function escapeHtml(str) {
  if (str == null) return '';
  return String(str).replace(/[&<>"']/g, (c) => HTML_ESCAPE[c]);
}

export function fmt(n, suffix = '', digits = 0) {
  if (n == null || isNaN(n)) return '—';
  return Number(n).toFixed(digits) + suffix;
}

export function ageOf(a, now) {
  if (now == null || a.last_seen == null) return null;
  return Math.max(0, now - a.last_seen);
}

// ISO 3166 alpha-2 country code → flag emoji (e.g. 'GB' → '🇬🇧') by
// offsetting each letter into the Regional Indicator Symbols block.
// Returns '' for anything that isn't a 2-letter code so callers can
// concat the result unconditionally.
export function flagEmoji(iso) {
  if (!iso || typeof iso !== 'string' || iso.length !== 2) return '';
  const upper = iso.toUpperCase();
  if (!/^[A-Z]{2}$/.test(upper)) return '';
  return String.fromCodePoint(
    127397 + upper.charCodeAt(0),
    127397 + upper.charCodeAt(1),
  );
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
