// Airspace subcategory filter dialog. Opened from the small filter
// icon inlined next to "Airspaces" in the Leaflet layers control (see
// ensureAirspaceFilterButton in openaip.js). Checkboxes mirror
// state.airspaceCategories; flipping one persists to localStorage and
// re-renders the airspace layer from the in-memory cache so the change
// is instant — no refetch.

import { syncSliceEnabled } from './airspace_slice.js';
import {
  AIRSPACE_GROUPS,
  airspaceGroupCounts,
  reapplyAirspaceFilters,
  setAirspaceFiltersOpener,
} from './openaip.js';
import {
  state,
  writeAirspaceCategories,
  writeAirspaceSliceEnabled,
} from './state.js';

function dialogEl() { return document.getElementById('airspace-filters-dialog'); }
function listEl() { return document.getElementById('airspace-filter-list'); }

function swatchSvg(s) {
  const dash = s.dashArray ? ` stroke-dasharray="${s.dashArray}"` : '';
  return (
    `<svg viewBox="0 0 30 14" width="30" height="14" aria-hidden="true">`
    + `<rect x="1" y="2" width="28" height="10" fill="${s.fill}" `
    + `fill-opacity="${s.fillOpacity}" stroke="${s.color}" `
    + `stroke-width="1.4"${dash}/>`
    + '</svg>'
  );
}

// A question-mark bubble that surfaces the group's explanation on
// hover (desktop) and on focus/tap (touch). Hint text goes into
// `data-hint`; CSS pulls it into a ::after pseudo-element bubble so
// the styling stays under our control instead of relying on the
// browser's native `title` tooltip, which is laggy and doesn't fire
// on touch devices. `tabindex="0"` makes the icon focusable so tap
// + keyboard users get the same bubble.
function helpIcon(hint) {
  const safe = hint.replace(/&/g, '&amp;').replace(/"/g, '&quot;')
    .replace(/</g, '&lt;').replace(/>/g, '&gt;');
  return `<span class="airspace-filter-help-icon" tabindex="0"`
    + ` role="img" aria-label="${safe}" data-hint="${safe}">?</span>`;
}

function renderList() {
  const ul = listEl();
  if (!ul) return;
  const counts = airspaceGroupCounts();
  ul.innerHTML = AIRSPACE_GROUPS.map((g) => {
    const checked = state.airspaceCategories.has(g.key) ? 'checked' : '';
    const countText = counts[g.key] ? ` <span class="airspace-filter-count">(${counts[g.key]})</span>` : '';
    return (
      `<li class="airspace-filter-row">`
      + `<label>`
      + `<input type="checkbox" data-group="${g.key}" ${checked}>`
      + `<span class="airspace-filter-swatch">${swatchSvg(g.swatch)}</span>`
      + `<span class="airspace-filter-label">${g.label}${countText}</span>`
      + `</label>`
      + helpIcon(g.hint)
      + `</li>`
    );
  }).join('');
}

function applyGroupToggle(key, enabled) {
  if (enabled) state.airspaceCategories.add(key);
  else state.airspaceCategories.delete(key);
  writeAirspaceCategories(state.airspaceCategories);
  reapplyAirspaceFilters();
}

function applyBulk(enabled) {
  for (const g of AIRSPACE_GROUPS) {
    if (enabled) state.airspaceCategories.add(g.key);
    else state.airspaceCategories.delete(g.key);
  }
  writeAirspaceCategories(state.airspaceCategories);
  reapplyAirspaceFilters();
  // Sync the visible checkboxes with the new state.
  const ul = listEl();
  if (ul) {
    for (const cb of ul.querySelectorAll('input[type="checkbox"][data-group]')) {
      cb.checked = state.airspaceCategories.has(cb.dataset.group);
    }
  }
}

function syncSliceToggleCheckbox() {
  const cb = document.getElementById('airspace-slice-toggle');
  if (cb) cb.checked = !!state.airspaceSliceEnabled;
}

function openDialog() {
  const dialog = dialogEl();
  if (!dialog) return;
  renderList();
  syncSliceToggleCheckbox();
  if (typeof dialog.showModal === 'function') dialog.showModal();
  else dialog.setAttribute('open', '');
}

export function initAirspaceFiltersDialog() {
  const dialog = dialogEl();
  if (!dialog) return;
  setAirspaceFiltersOpener(openDialog);

  // Click-outside-closes, same pattern as the other dialogs.
  dialog.addEventListener('click', (e) => {
    const target = e.target;
    if (!(target instanceof Element)) return;
    // The bulk-action buttons live inside the dialog; their clicks must
    // not trigger click-outside.
    if (target.closest('.airspace-filter-action')) {
      const action = target.closest('.airspace-filter-action').dataset.action;
      applyBulk(action === 'all');
      return;
    }
    // Ignore clicks inside the dialog content box. The native <dialog>
    // backdrop is covered by clicks *on the dialog element itself* —
    // `target === dialog` in that case.
    if (target !== dialog) return;
    const r = dialog.getBoundingClientRect();
    const inside = e.clientX >= r.left && e.clientX <= r.right
                && e.clientY >= r.top && e.clientY <= r.bottom;
    if (!inside) dialog.close();
  });

  dialog.addEventListener('change', (e) => {
    const t = e.target;
    if (!(t instanceof HTMLInputElement)) return;
    if (t.type !== 'checkbox') return;
    if (t.id === 'airspace-slice-toggle') {
      state.airspaceSliceEnabled = t.checked;
      writeAirspaceSliceEnabled(t.checked);
      syncSliceEnabled();
      return;
    }
    const key = t.dataset.group;
    if (!key) return;
    applyGroupToggle(key, t.checked);
  });
}
