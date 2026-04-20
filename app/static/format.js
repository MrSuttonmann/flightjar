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

// ISO 3166 alpha-2 country code → HTML `<img>` fragment pointing at
// flagcdn.com's public SVG for that country. We render an image rather
// than a flag emoji because Windows ships no glyph font for regional
// indicator symbols — emoji flags render as letter-pair boxes there.
// The CDN hit is cheap (each SVG is ~1KB and HTTP-cached by the
// browser). Returns '' for anything that isn't a two-letter ISO code
// so callers can concatenate unconditionally.
export function flagIcon(iso) {
  if (!iso || typeof iso !== 'string' || iso.length !== 2) return '';
  const upper = iso.toUpperCase();
  if (!/^[A-Z]{2}$/.test(upper)) return '';
  const lower = upper.toLowerCase();
  // No loading="lazy" / decoding="async": these flags are tiny (~1KB
  // SVG each) and the defer combined with our once-per-second snapshot
  // tick made the sidebar imgs flash in and out on every rebuild.
  return (
    `<img class="flag-icon" src="https://flagcdn.com/${lower}.svg" ` +
    `alt="${upper}" width="16" height="12">`
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
