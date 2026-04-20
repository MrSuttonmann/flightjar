// Singleton floating tooltip for airport codes. Mounted on <body> so it
// can paint over parents with overflow:hidden (the sidebar row's route
// line), and driven by delegated hover + click listeners so it works
// uniformly across desktop and iPad Safari — the native `title`
// attribute is inert on iPad, so we roll our own.

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
    const name = el.dataset.title;
    if (!name) return;
    tip.textContent = name;
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
    hideTimer = persistent ? setTimeout(hide, 3500) : null;
  }

  // Click anywhere: if it's an airport code, show the tooltip (and swallow
  // the event so the enclosing sidebar row doesn't also select the plane).
  // Clicks elsewhere dismiss any open tooltip.
  doc.addEventListener(
    'click',
    (e) => {
      const hit = e.target.closest('.airport-code[data-title]');
      if (hit) {
        e.stopPropagation();
        show(hit, true);
      } else if (!tip.hidden) {
        hide();
      }
    },
    true,
  );

  // Desktop hover: show while the pointer is over a code.
  doc.addEventListener('mouseover', (e) => {
    const hit = e.target.closest('.airport-code[data-title]');
    if (hit) show(hit, false);
  });
  doc.addEventListener('mouseout', (e) => {
    if (e.target.closest('.airport-code[data-title]') && hideTimer === null) {
      hide();
    }
  });

  return { show, hide };
}
