// Altitude → colour ramp (ColorBrewer Spectral, reversed). Linear RGB
// interpolation between adjacent stops gives a smooth perceptually-even
// gradient: warm red at low alt, yellow/green mid, cool blue/violet high.

export const ALT_STOPS = [
  [    0, [213,  62,  79]],  // #d53e4f
  [ 4000, [244, 109,  67]],  // #f46d43
  [ 8000, [253, 174,  97]],  // #fdae61
  [13000, [254, 224, 139]],  // #fee08b
  [18000, [230, 245, 152]],  // #e6f598
  [23000, [171, 221, 164]],  // #abdda4
  [28000, [102, 194, 165]],  // #66c2a5
  [33000, [ 50, 136, 189]],  // #3288bd
  [40000, [ 94,  79, 162]],  // #5e4fa2
];

function rgb([r, g, b]) {
  return `rgb(${r},${g},${b})`;
}

export function altColor(alt) {
  if (alt == null) return '#8a94a3';
  if (alt <= ALT_STOPS[0][0]) return rgb(ALT_STOPS[0][1]);
  const last = ALT_STOPS[ALT_STOPS.length - 1];
  if (alt >= last[0]) return rgb(last[1]);
  for (let i = 1; i < ALT_STOPS.length; i++) {
    const [a1, c1] = ALT_STOPS[i];
    if (alt <= a1) {
      const [a0, c0] = ALT_STOPS[i - 1];
      const t = (alt - a0) / (a1 - a0);
      return rgb([
        Math.round(c0[0] + t * (c1[0] - c0[0])),
        Math.round(c0[1] + t * (c1[1] - c0[1])),
        Math.round(c0[2] + t * (c1[2] - c0[2])),
      ]);
    }
  }
  return rgb(last[1]);
}
