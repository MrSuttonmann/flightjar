// Disabled-layer signposting for the Leaflet layers control.
//
// Several map layers depend on backend config that the operator may not
// have set up: the OpenAIP basemap + airspaces / obstacles / reporting
// points need an OpenAIP API key, the IFR Low / IFR High overlays need
// a VFRMap chart cycle date, and the terrain blackspots layer needs
// receiver coordinates. Rather than silently hiding those rows, we
// register them in the layers control as visible-but-disabled entries
// — `applyLayerStatus` then walks the live layers-control DOM after
// every Leaflet rebuild and adds:
//
//   - a `disabled` attribute on the row's checkbox/radio (so clicking
//     the label is a no-op rather than enabling a broken layer);
//   - an `overlay-disabled` CSS class that dims the row;
//   - an info button (ⓘ) that toggles a small popover explaining why
//     the layer is disabled and how to enable it.
//
// Leaflet's Control.Layers rebuilds its DOM every time a layer is added
// or removed (same reason openaip.js re-applies its zoom-gate annotation
// on overlayadd/overlayremove), so we re-run on those events plus
// baselayerchange.

import { state } from './state.js';
import { escapeHtml } from './format.js';

// Map of layers-control display label → backend gate key. Every entry
// here corresponds to a row registered by map_setup.js; if the backend
// reports the gate as disabled we paint the row as such.
const LAYER_TO_GATE = {
  'Aeronautical (OpenAIP)': 'openaip',
  'IFR Low (US)': 'vfrmap',
  'IFR High (US)': 'vfrmap',
  'Airspaces': 'openaip',
  'Obstacles': 'openaip',
  'Reporting points': 'openaip',
  'Terrain blackspots': 'blackspots',
};

let cachedStatus = null;
let openPopover = null;
let openPopoverAnchor = null;
// Pinned = opened by click. A pinned popover doesn't close on
// mouseleave — the user has to click elsewhere or press Escape. A
// hovered (un-pinned) popover follows the mouse cursor.
let popoverPinned = false;
let closeTimer = null;

// Walk every label in the layers-control container and yield each
// row paired with its display label. The display text may already
// have been mutated by the openaip.js zoom-gate (e.g. " Airspaces
// (zoom ≥ 5)"), so we strip the trailing zoom suffix before matching.
function* iterRows() {
  const control = state.layersControl;
  if (!control) return;
  const container = control.getContainer();
  const labels = container.querySelectorAll(
    '.leaflet-control-layers-base label, .leaflet-control-layers-overlays label',
  );
  for (const label of labels) {
    const span = label.querySelector('span > span') || label.querySelector('span');
    if (!span) continue;
    let text = span.textContent.trim();
    const zoomSuffix = text.match(/\s*\(zoom ≥ \d+\)$/);
    if (zoomSuffix) text = text.slice(0, zoomSuffix.index).trim();
    yield { label, span, text };
  }
}

function cancelClose() {
  if (closeTimer) { clearTimeout(closeTimer); closeTimer = null; }
}

// Small delay so the user can move the cursor from the info button
// into the popover (or back) without the popover snapping shut.
function scheduleClose(delay = 150) {
  cancelClose();
  closeTimer = setTimeout(() => { closeTimer = null; closePopover(); }, delay);
}

function closePopover() {
  cancelClose();
  if (!openPopover) return;
  openPopover.remove();
  openPopover = null;
  openPopoverAnchor = null;
  popoverPinned = false;
  document.removeEventListener('click', onDocClick, true);
  document.removeEventListener('keydown', onKeyDown, true);
}

function onDocClick(e) {
  if (!openPopover) return;
  if (openPopover.contains(e.target)) return;
  if (e.target.closest('.overlay-info-btn')) return;
  closePopover();
}

function onKeyDown(e) {
  if (e.key === 'Escape') closePopover();
}

function showPopover(anchor, reason, { pinned = false } = {}) {
  cancelClose();
  // Re-show against the same anchor: just upgrade pinned state.
  if (openPopover && openPopoverAnchor === anchor) {
    if (pinned) popoverPinned = true;
    return;
  }
  closePopover();
  popoverPinned = pinned;
  const popover = document.createElement('div');
  popover.className = 'overlay-info-popover';
  popover.setAttribute('role', 'tooltip');
  popover.innerHTML = `<div class="overlay-info-popover-arrow"></div>`
    + `<div class="overlay-info-popover-body">${escapeHtml(reason)}</div>`;
  popover.addEventListener('mouseenter', cancelClose);
  popover.addEventListener('mouseleave', () => {
    if (!popoverPinned) scheduleClose();
  });
  document.body.appendChild(popover);
  positionPopover(popover, anchor);
  openPopover = popover;
  openPopoverAnchor = anchor;
  // Defer listener install by a tick so the click that opened the
  // popover doesn't immediately close it via the doc-click handler.
  setTimeout(() => {
    document.addEventListener('click', onDocClick, true);
    document.addEventListener('keydown', onKeyDown, true);
  }, 0);
}

function positionPopover(popover, anchor) {
  const rect = anchor.getBoundingClientRect();
  // The layers control sits at the top-right; anchor the popover to the
  // left of the icon so it grows back into the map area. Vertically
  // align the arrow with the icon's centre.
  const popRect = popover.getBoundingClientRect();
  const margin = 8;
  let left = rect.left - popRect.width - margin;
  if (left < margin) left = rect.right + margin;
  let top = rect.top + rect.height / 2 - popRect.height / 2;
  if (top < margin) top = margin;
  const maxTop = window.innerHeight - popRect.height - margin;
  if (top > maxTop) top = maxTop;
  popover.style.left = `${left}px`;
  popover.style.top = `${top}px`;
}

function buildInfoButton(reason) {
  const btn = document.createElement('button');
  btn.type = 'button';
  btn.className = 'overlay-info-btn';
  btn.dataset.fjLayerInfo = '1';
  btn.setAttribute('aria-label', 'Why is this layer disabled?');
  btn.title = 'Why is this layer disabled?';
  // lucide: info — small circle-i, no fill, currentColor stroke.
  btn.innerHTML =
    '<svg viewBox="0 0 24 24" width="13" height="13" aria-hidden="true" '
    + 'fill="none" stroke="currentColor" stroke-width="2" '
    + 'stroke-linecap="round" stroke-linejoin="round">'
    + '<circle cx="12" cy="12" r="10"/>'
    + '<line x1="12" y1="16" x2="12" y2="12"/>'
    + '<line x1="12" y1="8" x2="12.01" y2="8"/></svg>';
  // Clicking inside the row's <label> would otherwise toggle the
  // checkbox; swallow the event so the icon only opens the popover.
  const swallow = (e) => { e.preventDefault(); e.stopPropagation(); };
  // Hover: open un-pinned, follows the cursor. Click: pin it open
  // (toggle off on a second click). Both supported so mouse users
  // get a quick peek and touch/keyboard users still have a way in.
  btn.addEventListener('mouseenter', () => showPopover(btn, reason));
  btn.addEventListener('mouseleave', () => {
    if (!popoverPinned) scheduleClose();
  });
  btn.addEventListener('focus', () => showPopover(btn, reason));
  btn.addEventListener('blur', () => {
    if (!popoverPinned) scheduleClose();
  });
  btn.addEventListener('click', (e) => {
    swallow(e);
    if (openPopover && openPopoverAnchor === btn && popoverPinned) {
      closePopover();
      return;
    }
    showPopover(btn, reason, { pinned: true });
  });
  btn.addEventListener('mousedown', (e) => e.stopPropagation());
  btn.addEventListener('dblclick', swallow);
  return btn;
}

// Apply the disabled markup to one row. Idempotent — guards on
// `data-fj-layer-info` so we don't add duplicate buttons when called
// repeatedly across Leaflet DOM rebuilds.
function applyToRow(row, reason) {
  row.label.classList.add('overlay-disabled');
  const input = row.label.querySelector('input');
  if (input) input.disabled = true;
  if (!row.label.querySelector('[data-fj-layer-info]')) {
    row.label.appendChild(buildInfoButton(reason));
  }
}

// Public entry. Cache the status object so re-renders triggered by
// overlayadd / overlayremove don't need the original config to be
// threaded through.
export function applyLayerStatus(layerStatus) {
  if (layerStatus) cachedStatus = layerStatus;
  if (!cachedStatus) return;
  for (const row of iterRows()) {
    const gate = LAYER_TO_GATE[row.text];
    if (!gate) continue;
    const status = cachedStatus[gate];
    if (!status || status.enabled !== false) continue;
    applyToRow(row, status.reason || 'This layer is disabled.');
  }
}

// Leaflet's L.Control.Layers._checkDisabledLayers (leaflet-src.js
// line 5430) unconditionally rewrites every input's `disabled`
// property based on the layer's minZoom/maxZoom vs the current map
// zoom. It's called on zoomend, after _update, and after _addItem —
// which means *every* layer add anywhere on the map (aircraft
// markers, trails, the receiver) triggers it and clobbers our
// disabled state. We monkey-patch the prototype so our apply runs
// immediately after Leaflet's, and the by-config disabled state
// survives. Idempotent — guards against being applied twice if the
// module is hot-reloaded.
function patchLeafletDisabledCheck() {
  if (!window.L?.Control?.Layers?.prototype) return;
  const proto = window.L.Control.Layers.prototype;
  if (proto._fjPatched) return;
  proto._fjPatched = true;
  const orig = proto._checkDisabledLayers;
  proto._checkDisabledLayers = function () {
    orig.apply(this, arguments);
    applyLayerStatus();
  };
}

// Hook into Leaflet DOM rebuilds so disabled rows survive overlay
// toggles. Call once after initMap.
export function initLayerStatus(layerStatus) {
  patchLeafletDisabledCheck();
  applyLayerStatus(layerStatus);
  if (!state.map) return;
  const reapply = () => applyLayerStatus();
  state.map.on('overlayadd overlayremove baselayerchange layeradd layerremove', reapply);
  // Closing the popover on map move/zoom keeps it from drifting away
  // from its anchor (which is in viewport coords).
  state.map.on('movestart zoomstart', closePopover);
}
