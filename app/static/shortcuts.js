// Global keyboard shortcuts. Wired once at boot; most shortcuts suppress
// themselves while an input / textarea has focus so typing a callsign
// into the search box doesn't toggle the trails overlay.

import {
  goHome,
  setAirports,
  setCompact,
  setFiltersCollapsed,
  setLabels,
  setTrails,
} from './map_controls.js';
import { getUnitSystem } from './units.js';
import { state } from './state.js';
import { writeDeepLink } from './detail_panel.js';

export function initShortcuts() {
  const searchInput = document.getElementById('search');

  document.addEventListener('keydown', (e) => {
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    const tag = (e.target && e.target.tagName) || '';
    const inField = tag === 'INPUT' || tag === 'TEXTAREA' || e.target?.isContentEditable;
    if (e.key === '/' && !inField) {
      e.preventDefault();
      // Expand the filters panel if it's collapsed (mobile default).
      setFiltersCollapsed(false);
      searchInput.focus();
      searchInput.select();
      return;
    }
    if (e.key === 'Escape') {
      if (inField) { searchInput.blur(); return; }
      state.map.closePopup();
      state.selectedIcao = null;
      writeDeepLink(null);
      document.querySelectorAll('.ac-item').forEach(el => el.classList.remove('selected'));
      return;
    }
    if (inField) return;
    if (e.key === 'l' || e.key === 'L') {
      setLabels(!state.showLabels);
    } else if (e.key === 't' || e.key === 'T') {
      setTrails(!state.showTrails);
    } else if (e.key === 'c' || e.key === 'C') {
      setCompact(!state.compactMode);
    } else if (e.key === 'a' || e.key === 'A') {
      setAirports(!state.showAirports);
    } else if (e.key === 'h' || e.key === 'H') {
      goHome();
    } else if (e.key === 'f' || e.key === 'F') {
      const pts = [];
      for (const entry of state.aircraft.values()) pts.push(entry.marker.getLatLng());
      if (pts.length) state.map.fitBounds(L.latLngBounds(pts).pad(0.2), { maxZoom: 10 });
    } else if (e.key === 'u' || e.key === 'U') {
      const order = ['metric', 'imperial', 'nautical'];
      const next = order[(order.indexOf(getUnitSystem()) + 1) % order.length];
      document.querySelector(`.unit-btn[data-unit="${next}"]`)?.click();
    }
  });
}
