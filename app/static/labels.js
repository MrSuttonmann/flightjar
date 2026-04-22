// Permanent on-map callsign labels. When enabled, every plane gets a
// Leaflet tooltip pinned to the right of its marker; when disabled,
// tooltips are unbound. Toggle state lives in state.showLabels and is
// mirrored into the Leaflet layers control via the labelsProxy.

import { escapeHtml } from './format.js';
import { state } from './state.js';

function labelText(a) {
  return escapeHtml(a.callsign || a.icao.toUpperCase());
}

export function updateLabelFor(entry) {
  const text = labelText(entry.data);
  if (state.showLabels) {
    if (entry.label) {
      entry.marker.setTooltipContent(text);
    } else {
      entry.marker.bindTooltip(text, {
        permanent: true, direction: 'right', offset: [10, 0],
        className: 'plane-label',
      });
      entry.label = true;
    }
  } else if (entry.label) {
    entry.marker.unbindTooltip();
    entry.label = false;
  }
}

export function applyLabelsVisibility() {
  for (const entry of state.aircraft.values()) updateLabelFor(entry);
  state.syncOverlay(state.labelsProxy, state.showLabels);
}
