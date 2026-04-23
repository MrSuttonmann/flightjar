// Shared Lucide icon library (https://lucide.dev, ISC-licensed).
// One authoritative map of the icons the UI renders, so every module
// shares stroke width / proportions / hover colour behaviour. Each path
// string is the raw <path>/<circle>/<polygon>/<line> body Lucide ships
// at 24×24; the envelope (SVG element, viewBox, fill=none, stroke=
// currentColor, round joins) is added by `lucide()` so callers don't
// have to repeat it.
//
// Why inline rather than a <symbol> sprite? (a) several icons render
// into strings that are later injected via innerHTML; (b) the app has
// no build step, so we can't tree-shake an npm import; (c) 16 icons at
// ~100 bytes each is cheaper than the extra HTTP round-trip.

export const LUCIDE_ICON_PATHS = {
  // UI icons
  'external-link':
    '<path d="M15 3h6v6"/>' +
    '<path d="M10 14 21 3"/>' +
    '<path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/>',
  'star':
    '<path d="M11.525 2.295a.53.53 0 0 1 .95 0l2.31 4.679a2.123 2.123 0 0 0 1.595 1.16l5.166.756a.53.53 0 0 1 .294.904l-3.736 3.638a2.123 2.123 0 0 0-.611 1.878l.882 5.14a.53.53 0 0 1-.771.56l-4.618-2.428a2.122 2.122 0 0 0-1.973 0L6.396 21.01a.53.53 0 0 1-.77-.56l.881-5.139a2.122 2.122 0 0 0-.611-1.879L2.16 9.795a.53.53 0 0 1 .294-.906l5.165-.755a2.122 2.122 0 0 0 1.597-1.16z"/>',
  'chart-column':
    '<path d="M3 3v16a2 2 0 0 0 2 2h16"/>' +
    '<path d="M18 17V9"/><path d="M13 17V5"/><path d="M8 17v-3"/>',
  'bell':
    '<path d="M10.268 21a2 2 0 0 0 3.464 0"/>' +
    '<path d="M3.262 15.326A1 1 0 0 0 4 17h16a1 1 0 0 0 .74-1.673C19.41 13.956 18 12.499 18 8A6 6 0 0 0 6 8c0 4.499-1.411 5.956-2.738 7.326"/>',
  'info':
    '<circle cx="12" cy="12" r="10"/>' +
    '<path d="M12 16v-4"/><path d="M12 8h.01"/>',
  'chevron-left': '<path d="m15 18-6-6 6-6"/>',
  'chevron-up': '<path d="m18 15-6-6-6 6"/>',
  'navigation':
    '<polygon points="3 11 22 2 13 21 11 13 3 11"/>',
  'crosshair':
    '<circle cx="12" cy="12" r="10"/>' +
    '<line x1="22" x2="18" y1="12" y2="12"/>' +
    '<line x1="6" x2="2" y1="12" y2="12"/>' +
    '<line x1="12" x2="12" y1="6" y2="2"/>' +
    '<line x1="12" x2="12" y1="22" y2="18"/>',
  'camera-off':
    '<path d="M14.564 14.558a3 3 0 1 1-4.122-4.121"/>' +
    '<path d="m2 2 20 20"/>' +
    '<path d="M20 20H4a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2h1.997a2 2 0 0 0 .819-.175"/>' +
    '<path d="M9.695 4.024A2 2 0 0 1 10.004 4h3.993a2 2 0 0 1 1.76 1.05l.486.9A2 2 0 0 0 18.003 7H20a2 2 0 0 1 2 2v7.344"/>',
  'check': '<path d="M20 6 9 17l-5-5"/>',
  'x':
    '<path d="M18 6 6 18"/><path d="m6 6 12 12"/>',
  'triangle-alert':
    '<path d="m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3"/>' +
    '<path d="M12 9v4"/><path d="M12 17h.01"/>',
  'sparkles':
    '<path d="M11.017 2.814a1 1 0 0 1 1.966 0l1.051 5.558a2 2 0 0 0 1.594 1.594l5.558 1.051a1 1 0 0 1 0 1.966l-5.558 1.051a2 2 0 0 0-1.594 1.594l-1.051 5.558a1 1 0 0 1-1.966 0l-1.051-5.558a2 2 0 0 0-1.594-1.594l-5.558-1.051a1 1 0 0 1 0-1.966l5.558-1.051a2 2 0 0 0 1.594-1.594z"/>' +
    '<path d="M20 2v4"/><path d="M22 4h-4"/><circle cx="4" cy="20" r="2"/>',
  'eye':
    '<path d="M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0"/>' +
    '<circle cx="12" cy="12" r="3"/>',
  'eye-off':
    '<path d="M10.733 5.076a10.744 10.744 0 0 1 11.205 6.575 1 1 0 0 1 0 .696 10.747 10.747 0 0 1-1.444 2.49"/>' +
    '<path d="M14.084 14.158a3 3 0 0 1-4.242-4.242"/>' +
    '<path d="M17.479 17.499a10.75 10.75 0 0 1-15.417-5.151 1 1 0 0 1 0-.696 10.75 10.75 0 0 1 4.446-5.143"/>' +
    '<path d="m2 2 20 20"/>',
  'list':
    '<path d="M3 12h.01"/><path d="M3 18h.01"/><path d="M3 6h.01"/>' +
    '<path d="M8 12h13"/><path d="M8 18h13"/><path d="M8 6h13"/>',

  // Weather icons (METAR overlay)
  'sun':
    '<circle cx="12" cy="12" r="4"/>' +
    '<path d="M12 2v2"/><path d="M12 20v2"/>' +
    '<path d="m4.93 4.93 1.41 1.41"/><path d="m17.66 17.66 1.41 1.41"/>' +
    '<path d="M2 12h2"/><path d="M20 12h2"/>' +
    '<path d="m6.34 17.66-1.41 1.41"/><path d="m19.07 4.93-1.41 1.41"/>',
  'cloud-sun':
    '<path d="M12 2v2"/><path d="m4.93 4.93 1.41 1.41"/>' +
    '<path d="M20 12h2"/><path d="m19.07 4.93-1.41 1.41"/>' +
    '<path d="M15.947 12.65a4 4 0 0 0-5.925-4.128"/>' +
    '<path d="M13 22H7a5 5 0 1 1 4.9-6H13a3 3 0 0 1 0 6Z"/>',
  'cloud':
    '<path d="M17.5 19H9a7 7 0 1 1 6.71-9h1.79a4.5 4.5 0 1 1 0 9Z"/>',
  'cloud-fog':
    '<path d="M4 14.899A7 7 0 1 1 15.71 8h1.79a4.5 4.5 0 0 1 2.5 8.242"/>' +
    '<path d="M16 17H7"/><path d="M17 21H9"/>',
  'cloud-rain':
    '<path d="M4 14.899A7 7 0 1 1 15.71 8h1.79a4.5 4.5 0 0 1 2.5 8.242"/>' +
    '<path d="M16 14v6"/><path d="M8 14v6"/><path d="M12 16v6"/>',
  'cloud-snow':
    '<path d="M4 14.899A7 7 0 1 1 15.71 8h1.79a4.5 4.5 0 0 1 2.5 8.242"/>' +
    '<path d="M8 15h.01"/><path d="M8 19h.01"/><path d="M12 17h.01"/>' +
    '<path d="M12 21h.01"/><path d="M16 15h.01"/><path d="M16 19h.01"/>',
  'cloud-hail':
    '<path d="M4 14.899A7 7 0 1 1 15.71 8h1.79a4.5 4.5 0 0 1 2.5 8.242"/>' +
    '<path d="M16 14v2"/><path d="M8 14v2"/><path d="M16 20h.01"/>' +
    '<path d="M8 20h.01"/><path d="M12 16v2"/><path d="M12 22h.01"/>',
  'cloud-lightning':
    '<path d="M6 16.326A7 7 0 1 1 15.71 8h1.79a4.5 4.5 0 0 1 .5 8.973"/>' +
    '<path d="m13 12-3 5h4l-3 5"/>',
};

// Render a Lucide icon as an inline SVG string ready for innerHTML. Size
// defaults to 16 px — override per call-site when a tighter visual
// (9 px for the external-link glyph inside a link) or a larger one is
// needed. Class names compose so callers can add hooks for hover tints
// or state toggles (e.g. `watched` star filling).
export function lucide(name, opts = {}) {
  const { size = 16, strokeWidth = 1.8, className = '' } = opts;
  const body = LUCIDE_ICON_PATHS[name];
  if (!body) return '';
  const cls = className ? ` class="${className}"` : '';
  return `<svg${cls} viewBox="0 0 24 24" width="${size}" height="${size}" ` +
    `aria-hidden="true" fill="none" stroke="currentColor" ` +
    `stroke-width="${strokeWidth}" stroke-linecap="round" ` +
    `stroke-linejoin="round">${body}</svg>`;
}
