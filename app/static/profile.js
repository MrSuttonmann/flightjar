// Pure helpers for the detail-panel bottom section: the altitude + speed
// trail sparklines, the signal-strength label, the relative "first seen"
// formatter, and a few small constants shared between the panel skeleton
// and the update path. No DOM ownership beyond writing innerHTML to the
// SVGs the caller hands in, so this whole module is safe to unit-test.

import { altColor } from './altitude.js';

// Maps ADS-B emitter category (TC 4) bytes to human-readable names.
// Keys < 1 or > 7 are not surfaced to the user.
export const CATEGORY_NAMES = {
  1: 'Light', 2: 'Small', 3: 'Large', 4: 'High-vortex',
  5: 'Heavy', 6: 'High-performance', 7: 'Rotorcraft',
};

// ICAO types classified as Super (WTC "J") by regulators. The ADS-B
// emitter category doesn't express Super separately, so this tiny
// override set is the only way to distinguish A380s / B748s / An-225
// from ordinary Heavies.
export const WTC_SUPER = new Set(['A388', 'A38F', 'B748', 'B74R', 'A225']);

// Derive wake-turbulence category from an aircraft's type and ADS-B
// category. Returns one of 'L', 'M', 'H', 'J', or null when we have
// neither input. Drives the category-badge colour cue in the detail
// panel — heavier traffic gets a warmer accent so users can
// scan-filter the wake class without reading the label.
export function wakeClass(typeIcao, category) {
  if (typeIcao && WTC_SUPER.has(typeIcao.toUpperCase())) return 'J';
  if (category === 4 || category === 5) return 'H';
  if (category === 1) return 'L';
  if (category === 2 || category === 3 || category === 6 || category === 7) return 'M';
  return null;
}

// Return an HTML <span> rendering a 4-step signal-strength indicator
// from the BEAST signal byte (0-255). Empty string when the byte is
// missing / zero. `signal_peak` is the per-aircraft max from the
// backend, so this reads as "how strong was the best fix for this
// tail" rather than a jittery per-tick value. Bars are CSS-styled.
export function signalBars(byte) {
  if (byte == null || byte <= 0) return '';
  // Even quartiles of the byte range.
  const level = byte >= 192 ? 4 : byte >= 128 ? 3 : byte >= 64 ? 2 : 1;
  let bars = '';
  for (let i = 1; i <= 4; i++) {
    bars += `<span class="bar${i <= level ? ' on' : ''}"></span>`;
  }
  return `<span class="signal-bars" data-level="${level}" title="${signalLabel(byte)}">${bars}</span>`;
}

// Small "opens-in-new-tab" glyph baked into every external-tracker link.
// currentColor so each button's link icon inherits its text tone.
export const LINK_ICON_SVG =
  `<svg class="link-icon" viewBox="0 0 16 16" width="9" height="9" ` +
    `aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.5" ` +
    `stroke-linecap="round" stroke-linejoin="round">` +
    `<path d="M10 3h3v3"/>` +
    `<path d="M13 3l-6 6"/>` +
    `<path d="M12 9v3a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1h3"/>` +
  `</svg>`;

// Render a tiny history polyline into `svgEl` from the snapshot's
// per-aircraft trail. Segments are coloured via `strokeFn(value, index)`
// so altitude charts can use altColor(alt) and speed charts can use a
// flat accent colour. SVGs use viewBox + preserveAspectRatio=none so
// the rendered width stretches to the panel without per-tick measuring.
export function renderTrailProfile(svgEl, trail, opts) {
  const { pickValue, strokeFn, height, emptyLabel, valueFloor = 0, minSpan = 1 } = opts;
  if (!trail || trail.length < 2) {
    svgEl.innerHTML =
      `<text x="100" y="${height / 2 + 2}" text-anchor="middle" ` +
      `font-size="10" fill="currentColor" opacity="0.5">${emptyLabel}</text>`;
    return;
  }
  const W = 200, PAD = 2;
  const series = trail.map(pickValue);
  const valid = series.map((v, i) => [v, i]).filter(([v]) => v != null);
  if (valid.length < 2) {
    svgEl.innerHTML = '';
    return;
  }
  const vals = valid.map(([v]) => v);
  const minV = valueFloor;
  const maxV = Math.max(...vals, valueFloor + minSpan);
  const span = Math.max(minSpan, maxV - minV);
  const xStep = (W - 2 * PAD) / (trail.length - 1);
  const y = (v) => height - PAD - (v - minV) / span * (height - 2 * PAD);
  let out = '';
  for (let i = 1; i < valid.length; i++) {
    const [v0, idx0] = valid[i - 1];
    const [v1, idx1] = valid[i];
    const x0 = PAD + idx0 * xStep;
    const x1 = PAD + idx1 * xStep;
    out +=
      `<line x1="${x0.toFixed(1)}" y1="${y(v0).toFixed(1)}" ` +
      `x2="${x1.toFixed(1)}" y2="${y(v1).toFixed(1)}" ` +
      `stroke="${strokeFn(v1, i)}" stroke-width="1.6" ` +
      `stroke-linecap="round" vector-effect="non-scaling-stroke"/>`;
  }
  svgEl.innerHTML = out;
}

export function renderAltProfile(svgEl, trail) {
  renderTrailProfile(svgEl, trail, {
    pickValue: (p) => p[2],
    strokeFn: (alt) => altColor(alt),
    height: 40,
    emptyLabel: 'awaiting altitude data',
    minSpan: 1000,
  });
}

export function renderSpdProfile(svgEl, trail) {
  renderTrailProfile(svgEl, trail, {
    pickValue: (p) => p[3],
    strokeFn: () => 'var(--accent)',
    height: 24,
    emptyLabel: 'awaiting speed data',
    minSpan: 100,
  });
}

// Rough BEAST signal byte → dBFS label. The byte is the peak-sample fraction
// of full scale, so 10*log10((b/255)^2) puts 255 at 0 dBFS. Matches the
// convention readsb's own dashboard uses.
export function signalLabel(byte) {
  if (byte == null || byte <= 0) return '—';
  const db = 10 * Math.log10((byte / 255) ** 2);
  return db.toFixed(1) + ' dBFS';
}

// Short relative-age label: "45s ago", "12m ago", "3.4h ago" from a
// unix timestamp. Returns '—' when either timestamp is missing.
export function relativeAge(t, now) {
  if (!t || !now) return '—';
  const s = Math.max(0, now - t);
  if (s < 60) return Math.round(s) + 's ago';
  if (s < 3600) return Math.round(s / 60) + 'm ago';
  return (s / 3600).toFixed(1) + 'h ago';
}

// Compact weather summary from a distilled METAR payload
// ({ raw, wind_dir, wind_kt, visibility, cover, ... }).
// Returns HTML: the airport code as a monospace tag, then space-separated
// fragments ("250°/12kt", "10SM", "BKN"). Fragments that don't have data
// drop out so a quiet METAR like "EGLL 0000Z CAVOK" reads as "EGLL ·
// CAVOK" in practice. Hover tooltip carries the raw METAR string.
export function formatMetar(code, m) {
  if (!code || !m) return '';
  const parts = [];
  if (m.wind_dir != null && m.wind_kt != null) {
    const dir = m.wind_dir === 0 && m.wind_kt === 0
      ? 'calm'
      : `${String(m.wind_dir).padStart(3, '0')}°/${m.wind_kt}kt`;
    parts.push(dir);
  }
  if (m.visibility != null && m.visibility !== '') {
    // Visibility is a number (metres, non-US) or string ("10+" for US).
    const vis = typeof m.visibility === 'number' ? `${m.visibility}m` : `${m.visibility}`;
    parts.push(vis);
  }
  if (m.cover) parts.push(m.cover);
  const tag = `<span class="airline-tag">${code}</span>`;
  const body = parts.length ? parts.join(' · ') : '—';
  const title = m.raw ? ` title="${m.raw.replace(/"/g, '&quot;')}"` : '';
  return `<span class="wx-line"${title}>${tag} ${body}</span>`;
}
