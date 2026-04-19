// Rolling-history trend arrows (alt, spd, dst). Pushed once per snapshot.
// Dead zones keep cruise jitter from flicker-classifying as up / down.

export const HIST_LEN = 10;
export const TREND_THRESHOLDS = { alt: 50, spd: 3, dst: 0.5 };  // ft, kt, km

export function pushHistory(entry, a) {
  const h = entry.hist;
  const samples = [
    ['alt', a.altitude],
    ['spd', a.speed],
    ['dst', a.distance_km],
  ];
  for (const [key, value] of samples) {
    const buf = h[key];
    buf.push(value);
    if (buf.length > HIST_LEN) buf.shift();
  }
}

export function trendInfo(entry, key) {
  const buf = entry?.hist?.[key];
  if (!buf || buf.length < 3) return { dir: '', arrow: '', cls: '' };
  // Oldest and newest non-null values — skip gaps at either end.
  let oldVal = null;
  let newVal = null;
  for (let i = 0; i < buf.length; i++) {
    if (buf[i] != null) {
      oldVal = buf[i];
      break;
    }
  }
  for (let i = buf.length - 1; i >= 0; i--) {
    if (buf[i] != null) {
      newVal = buf[i];
      break;
    }
  }
  if (oldVal == null || newVal == null) return { dir: '', arrow: '', cls: '' };
  const delta = newVal - oldVal;
  const th = TREND_THRESHOLDS[key] ?? 0;
  let dir = 'flat';
  let glyph = '—';
  let cls = '';
  if (delta > th) {
    dir = 'up';
    glyph = '↑';
    cls = 'trend-up';
  } else if (delta < -th) {
    dir = 'down';
    glyph = '↓';
    cls = 'trend-down';
  }
  return { dir, arrow: `<span class="trend">${glyph}</span>`, cls };
}
