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
