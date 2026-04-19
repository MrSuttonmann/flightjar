// Top-down aircraft silhouettes + type-code mapping. No DOM.
//
// planeIcon() itself (which needs Leaflet's L.divIcon) stays in app.js
// — only the pure shape metadata and the silhouette-picker live here.

export const PLANE_SHAPES = {
  generic: {
    size: 26,
    paths: ['M0,-10 L7,8 L0,4 L-7,8 Z'],
  },
  jet: {
    size: 28,
    paths: [
      'M0,-12 L2,-9 L2,-2 L11,2 L11,4 L2,3 L2,8 L4,11 L4,12 L-4,12 L-4,11 ' +
        'L-2,8 L-2,3 L-11,4 L-11,2 L-2,-2 L-2,-9 Z',
    ],
  },
  widebody: {
    size: 32,
    paths: [
      'M0,-13 L3,-10 L3,-2 L14,3 L14,5 L3,4 L3,10 L6,13 L6,14 L-6,14 L-6,13 ' +
        'L-3,10 L-3,4 L-14,5 L-14,3 L-3,-2 L-3,-10 Z',
    ],
  },
  turboprop: {
    size: 26,
    paths: [
      'M0,-11 L2,-8 L2,-1 L11,-1 L11,1 L2,2 L2,7 L4,10 L4,11 L-4,11 L-4,10 ' +
        'L-2,7 L-2,2 L-11,1 L-11,-1 L-2,-1 L-2,-8 Z',
    ],
  },
  light: {
    size: 22,
    paths: [
      'M0,-9 L1,-6 L1,-1 L9,-1 L9,1 L1,2 L1,6 L3,8 L3,9 L-3,9 L-3,8 L-1,6 ' +
        'L-1,2 L-9,1 L-9,-1 L-1,-1 L-1,-6 Z',
    ],
  },
  heli: {
    size: 28,
    paths: ['M0,-2 L1,1 L1,6 L-1,6 L-1,1 Z'],
    disc: { r: 12, dash: '2 3' },
  },
};

// ICAO type-code → silhouette family. Just the common ones; everything
// else falls through to category heuristics.
export const TYPE_SHAPES = {
  // Wide-body
  B772: 'widebody', B773: 'widebody', B77L: 'widebody', B77W: 'widebody',
  B744: 'widebody', B748: 'widebody', B788: 'widebody', B789: 'widebody',
  B78X: 'widebody', A332: 'widebody', A333: 'widebody', A338: 'widebody',
  A339: 'widebody', A342: 'widebody', A343: 'widebody', A345: 'widebody',
  A346: 'widebody', A359: 'widebody', A35K: 'widebody', A388: 'widebody',
  // Narrow-body jets
  B712: 'jet', B736: 'jet', B737: 'jet', B738: 'jet', B739: 'jet',
  B73G: 'jet', B38M: 'jet', B39M: 'jet', B3XM: 'jet',
  A319: 'jet', A320: 'jet', A321: 'jet', A318: 'jet',
  A19N: 'jet', A20N: 'jet', A21N: 'jet',
  E170: 'jet', E175: 'jet', E190: 'jet', E195: 'jet', E290: 'jet', E295: 'jet',
  CRJ2: 'jet', CRJ7: 'jet', CRJ9: 'jet', CRJX: 'jet',
  // Turboprop
  DH8A: 'turboprop', DH8B: 'turboprop', DH8C: 'turboprop', DH8D: 'turboprop',
  AT42: 'turboprop', AT43: 'turboprop', AT44: 'turboprop', AT45: 'turboprop',
  AT46: 'turboprop', AT72: 'turboprop', AT75: 'turboprop', AT76: 'turboprop',
  BE20: 'turboprop', B350: 'turboprop', BE30: 'turboprop', BE9L: 'turboprop',
  PC12: 'turboprop', TBM9: 'turboprop', TBM8: 'turboprop',
  // Light single / twin piston
  C152: 'light', C172: 'light', C162: 'light', C182: 'light', C206: 'light',
  C210: 'light', PA28: 'light', P28A: 'light', P28R: 'light', PA32: 'light',
  PA34: 'light', PA44: 'light', PA46: 'light', SR20: 'light', SR22: 'light',
  DA40: 'light', DA42: 'light', DA62: 'light', DR40: 'light', AA5: 'light',
};

export function silhouette(a) {
  const t = a.type_icao;
  if (t && TYPE_SHAPES[t]) return TYPE_SHAPES[t];
  // Helicopter type codes typically start with H in the ICAO designator list.
  if (t && t[0] === 'H') return 'heli';
  switch (a.category) {
    case 1: return 'light';    // light < 15500 lb
    case 2: return 'jet';      // small
    case 3: return 'jet';      // large
    case 5: return 'widebody'; // heavy
    case 7: return 'heli';     // rotorcraft
  }
  return 'generic';
}
