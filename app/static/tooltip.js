// Singleton floating tooltip. Handles two classes of trigger with the
// same DOM + event machinery:
//
//   1. `.airport-code[data-title]` — short airport-name labels on route
//      tickets, shown on hover and persistent-on-tap (for iPad Safari
//      where the native `title` attribute is inert).
//
//   2. `[data-help]` — help-icon-style explanations for metrics in the
//      detail panel. Longer strings; rendered on the same tooltip with
//      a wider max-width courtesy of the `.tip-help` class.
//
// Mounted on <body> so it can paint over parents with overflow:hidden.
// One singleton across both surfaces keeps the event listeners and
// positioning logic from drifting out of sync.

// Two independent selectors rather than a compound so unit tests can
// stub `closest()` against either one without tracking the combined
// string. `hitOf(e)` runs both and returns whichever matches first.
const TITLE_SELECTOR = '.airport-code[data-title]';
const HELP_SELECTOR = '[data-help]';
function hitOf(target) {
  if (!target) return null;
  return target.closest?.(TITLE_SELECTOR) || target.closest?.(HELP_SELECTOR) || null;
}

export function initAirportTooltip(doc = document) {
  const tip = doc.createElement('div');
  tip.id = 'airport-tooltip';
  tip.hidden = true;
  doc.body.appendChild(tip);
  let hideTimer = null;

  function hide() {
    tip.hidden = true;
    if (hideTimer !== null) {
      clearTimeout(hideTimer);
      hideTimer = null;
    }
  }

  function show(el, persistent) {
    // `[data-help]` wins when both are set, since an explanatory help
    // string is always more informative than a name label.
    const text = el.dataset.help || el.dataset.title;
    if (!text) return;
    tip.textContent = text;
    tip.classList.toggle('tip-help', el.hasAttribute('data-help'));
    tip.hidden = false;
    // Measure after unhiding so offsetWidth is meaningful.
    const r = el.getBoundingClientRect();
    const tw = tip.offsetWidth;
    const th = tip.offsetHeight;
    const left = Math.max(4, Math.min(
      (typeof window !== 'undefined' ? window.innerWidth : 1024) - tw - 4,
      r.left + r.width / 2 - tw / 2,
    ));
    const top = r.top - th - 6 >= 4 ? r.top - th - 6 : r.bottom + 6;
    tip.style.left = `${left}px`;
    tip.style.top = `${top}px`;
    if (hideTimer !== null) clearTimeout(hideTimer);
    hideTimer = persistent ? setTimeout(hide, 6000) : null;
  }

  // Click anywhere: if it hits a trigger, show the tooltip (and swallow
  // the event so the enclosing sidebar row doesn't also select the plane).
  // Clicks elsewhere dismiss any open tooltip.
  doc.addEventListener(
    'click',
    (e) => {
      const hit = hitOf(e.target);
      if (hit) {
        e.stopPropagation();
        show(hit, true);
      } else if (!tip.hidden) {
        hide();
      }
    },
    true,
  );

  // Desktop hover: show while the pointer is over a trigger.
  doc.addEventListener('mouseover', (e) => {
    const hit = hitOf(e.target);
    if (hit) show(hit, false);
  });
  doc.addEventListener('mouseout', (e) => {
    if (hitOf(e.target) && hideTimer === null) {
      hide();
    }
  });

  return { show, hide };
}
