// Shared SVG-icon factory for non-aircraft point markers on the map.
// Each layer type gets a distinct shape so a navaid doesn't read like
// a reporting point:
//   - airport  → filled circle (scaled by size class)
//   - VOR/VORTAC/VOR-DME → triangle (anchor of airway networks)
//   - DME / TACAN → square
//   - NDB / NDB-DME → dashed hollow circle (chart convention)
//   - obstacle → inverted triangle with a stem (tower silhouette)
//   - reporting (compulsory) → filled diamond
//   - reporting (non-compulsory) → hollow diamond
// We render as L.divIcon so markers land in markerPane (z-index 600),
// above SVG airspaces in overlayPane (400) — hovering a marker hits
// the marker first, hovering empty map hits the airspace, which fixes
// the "tooltips never fire over points" problem.

const STROKE = '#0e1116';

function svgWrap(contents, size) {
  const half = size / 2;
  return `<svg width="${size}" height="${size}" viewBox="${-half} ${-half} ${size} ${size}"`
       + ` xmlns="http://www.w3.org/2000/svg" overflow="visible">${contents}</svg>`;
}

function icon(contents, size) {
  return L.divIcon({
    html: svgWrap(contents, size),
    className: 'fj-point-icon',
    iconSize: [size, size],
    iconAnchor: [size / 2, size / 2],
    tooltipAnchor: [0, -size / 2],
  });
}

// ---- airports ----

function airportIcon(sizeClass) {
  const r = sizeClass === 'large' ? 5 : sizeClass === 'medium' ? 4 : 3;
  const size = (r + 1) * 2;
  return icon(
    `<circle cx="0" cy="0" r="${r}" fill="#fbbf24" stroke="${STROKE}" stroke-width="1"/>`,
    size,
  );
}
const AIRPORT_ICONS = {
  large_airport: airportIcon('large'),
  medium_airport: airportIcon('medium'),
  small_airport: airportIcon('small'),
};
const AIRPORT_DEFAULT = airportIcon('small');

export function iconForAirport(a) {
  return AIRPORT_ICONS[a.type] || AIRPORT_DEFAULT;
}

// ---- navaids ----

function trianglePoints(r) {
  // Equilateral, pointing up, centred on (0,0).
  const h = r * Math.sqrt(3) / 2;
  return `0,${-r} ${h},${r / 2} ${-h},${r / 2}`;
}
function squarePoints(s) {
  const h = s / 2;
  return `${-h},${-h} ${h},${-h} ${h},${h} ${-h},${h}`;
}
function vorIcon(fill) {
  return icon(
    `<polygon points="${trianglePoints(6)}" fill="${fill}" stroke="${STROKE}" stroke-width="1" stroke-linejoin="round"/>`,
    16,
  );
}
function dmeIcon(fill) {
  return icon(
    `<polygon points="${squarePoints(9)}" fill="${fill}" stroke="${STROKE}" stroke-width="1"/>`,
    14,
  );
}
function ndbIcon(fill) {
  return icon(
    `<circle cx="0" cy="0" r="5" fill="none" stroke="${fill}" stroke-width="1.6" stroke-dasharray="2 1.5"/>`
    + `<circle cx="0" cy="0" r="1.5" fill="${fill}"/>`,
    14,
  );
}
const NAVAID_ICONS = {
  VOR: vorIcon('#16a34a'),
  VORTAC: vorIcon('#15803d'),
  'VOR-DME': vorIcon('#16a34a'),
  DME: dmeIcon('#3b82f6'),
  TACAN: dmeIcon('#3b82f6'),
  NDB: ndbIcon('#f97316'),
  'NDB-DME': ndbIcon('#f97316'),
};
const NAVAID_DEFAULT = icon(
  `<circle cx="0" cy="0" r="3" fill="#9ca3af" stroke="${STROKE}" stroke-width="1"/>`,
  10,
);

export function iconForNavaid(n) {
  return NAVAID_ICONS[n.type] || NAVAID_DEFAULT;
}

// ---- obstacles ----
// Inverted triangle with a short vertical stem — reads as a tower/mast
// silhouette and is hard to confuse with VOR (upright triangle).
export const OBSTACLE_ICON = icon(
  `<line x1="0" y1="-5" x2="0" y2="4" stroke="${STROKE}" stroke-width="1.4"/>`
  + `<polygon points="0,5 -4,-2 4,-2" fill="#ef4444" stroke="${STROKE}" stroke-width="1" stroke-linejoin="round"/>`,
  14,
);

// ---- reporting points ----
// Diamond (45° square) — standard VFR reporting-point shape on charts.
// Compulsory: filled cyan. Non-compulsory: hollow.
function diamondPoints(r) {
  return `0,${-r} ${r},0 0,${r} ${-r},0`;
}
export const REPORTING_COMPULSORY_ICON = icon(
  `<polygon points="${diamondPoints(5)}" fill="#0ea5e9" stroke="${STROKE}" stroke-width="1" stroke-linejoin="round"/>`,
  14,
);
export const REPORTING_NONCOMPULSORY_ICON = icon(
  `<polygon points="${diamondPoints(5)}" fill="#ffffff" stroke="#475569" stroke-width="1.4" stroke-linejoin="round"/>`,
  14,
);
