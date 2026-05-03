// Marker icon rendering. Prefers tar1090's per-type silhouette when the
// bundle is loaded and the aircraft's ICAO type code has a match;
// otherwise falls back to a generic triangular arrow.
//
// tar1090_shapes.js is loaded asynchronously (it's a big static file
// and failing to fetch it shouldn't stop the app from running). Until
// it resolves we render the generic arrow for everyone; once it does,
// subsequent planeIcon() calls light up.

let tar1090Shapes = null;
let tar1090TypeIcons = null;

import('./tar1090_shapes.js').then(mod => {
  tar1090Shapes = mod.shapes;
  tar1090TypeIcons = mod.TypeDesignatorIcons;
}).catch(e => {
  console.warn('tar1090 shapes unavailable — using generic arrow', e);
});

function tar1090ShapeFor(typeIcao) {
  if (!tar1090Shapes || !tar1090TypeIcons || !typeIcao) return null;
  const entry = tar1090TypeIcons[typeIcao.toUpperCase()];
  if (!entry) return null;
  const [shapeName, scaleFactor] = entry;
  const shape = tar1090Shapes[shapeName];
  return shape ? { shape, scaleFactor } : null;
}

// Render a tar1090-shaped marker. Follows the layout from tar1090's own
// svgShapeToSVG: stroke width is a small reference (0.5 px in viewBox
// units) multiplied by shape.strokeScale, so the viewBox-unit scale
// varies across shapes but the on-screen stroke stays consistent. Main
// path uses paint-order="stroke" (stroke under fill, so only the
// outline shows). Accent paths render as fill="none" lines.
function tar1090Icon(track, color, selected, emergency, relayed, shape, scaleFactor) {
  const rot = track == null ? 0 : track;
  const target = 32;  // target pixel size of the longer edge at scaleFactor=1
  const longest = Math.max(shape.w, shape.h);
  const scale = (scaleFactor || 1) * (target / longest);
  const w = Math.round(shape.w * scale);
  const h = Math.round(shape.h * scale);
  let baseStroke = 0.5;
  let stroke = selected ? '#ffffff' : '#000000';
  if (emergency) { stroke = '#ef4444'; baseStroke = 0.8; }
  // Relayed (MLAT / TIS-B / ADS-R): dashed amber so the marker reads as
  // "computed or rebroadcast by ground equipment" at a glance. Loses to
  // selection (white) and emergency (red), both louder signals.
  else if (relayed) { stroke = '#facc15'; baseStroke = 0.9; }
  const ss = shape.strokeScale || 1;
  const strokeWidth = 2 * baseStroke * ss;
  const accentWidth = 0.6 * (shape.accentMult ? shape.accentMult * baseStroke : baseStroke) * ss;
  const dashed = relayed && !emergency && !selected;
  const dashAttr = dashed
    ? ` stroke-dasharray="${(2.5 * ss).toFixed(2)} ${(1.5 * ss).toFixed(2)}"`
    : '';

  const paths = Array.isArray(shape.path) ? shape.path : [shape.path];
  let body = paths.map(d =>
    `<path paint-order="stroke" fill="${color}" stroke="${stroke}" ` +
    `stroke-width="${strokeWidth}"${dashAttr} d="${d}"/>`
  ).join('');
  if (shape.accent) {
    const accents = Array.isArray(shape.accent) ? shape.accent : [shape.accent];
    body += accents.map(d =>
      `<path fill="none" stroke="${stroke}" stroke-width="${accentWidth}" d="${d}"/>`
    ).join('');
  }
  const innerTransform = shape.transform ? ` transform="${shape.transform}"` : '';
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" ` +
    `viewBox="${shape.viewBox}"` +
    (shape.noAspect ? ' preserveAspectRatio="none"' : '') +
    ` style="transform: rotate(${rot}deg); transform-origin: center;"` +
    `>` +
    `<g${innerTransform}>${body}</g></svg>`;
  return L.divIcon({
    html: svg,
    className: 'plane-icon',
    iconSize: [w, h],
    iconAnchor: [w / 2, h / 2],
  });
}

// Simple triangular arrow used when tar1090 has no silhouette for this
// aircraft's ICAO type code (or the bundle hasn't loaded yet).
const GENERIC_ARROW_PATH = 'M0,-10 L7,8 L0,4 L-7,8 Z';
const GENERIC_ARROW_SIZE = 26;

export function planeIcon(track, color, selected, emergency, relayed, typeIcao) {
  const tar = tar1090ShapeFor(typeIcao);
  if (tar) return tar1090Icon(track, color, selected, emergency, relayed, tar.shape, tar.scaleFactor);

  const rot = track == null ? 0 : track;
  let stroke = selected ? '#fff' : '#000';
  let sw = selected ? 1.5 : 0.6;
  if (emergency) { stroke = '#ef4444'; sw = 2; }
  else if (relayed) { stroke = '#facc15'; sw = 1.6; }
  const dashed = relayed && !emergency && !selected;
  const dashAttr = dashed ? ' stroke-dasharray="3 2"' : '';
  const size = GENERIC_ARROW_SIZE;
  const half = size / 2;
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}"` +
    ` viewBox="-14 -14 28 28">` +
      `<g transform="rotate(${rot})">` +
        `<path d="${GENERIC_ARROW_PATH}" fill="${color}" stroke="${stroke}" ` +
          `stroke-width="${sw}" stroke-linejoin="round"${dashAttr}/>` +
      `</g>` +
    `</svg>`;
  return L.divIcon({
    html: svg,
    className: 'plane-icon',
    iconSize: [size, size],
    iconAnchor: [half, half],
  });
}

// Update a marker's heading in place without replacing the icon's DOM
// element. Called from update() for track-only changes so Leaflet's
// click detection (mousedown + mouseup must land on the same DOM
// node) doesn't drop clicks when snapshots arrive mid-click.
// tar1090Icon keeps the rotation on the <svg>'s inline style;
// the generic arrow keeps it on an inner <g transform=…>.
export function rotateMarkerIcon(marker, track) {
  const el = marker.getElement();
  if (!el) return;
  const svg = el.querySelector('svg');
  if (!svg) return;
  const rot = track == null ? 0 : track;
  if (svg.style && svg.style.transform) {
    svg.style.transform = `rotate(${rot}deg)`;
    return;
  }
  const g = svg.querySelector('g');
  if (g) g.setAttribute('transform', `rotate(${rot})`);
}
