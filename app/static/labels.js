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
  if (state.showLabels) {
    const text = labelText(entry.data);
    if (entry.label) {
      // setTooltipContent re-renders the tooltip DOM, so skip it when
      // the text (callsign or icao fallback) hasn't actually changed —
      // which is every tick for virtually every aircraft.
      if (entry.labelText !== text) {
        entry.marker.setTooltipContent(text);
        entry.labelText = text;
      }
    } else {
      entry.marker.bindTooltip(text, {
        permanent: true, direction: 'right', offset: [10, 0],
        className: 'plane-label',
      });
      entry.label = true;
      entry.labelText = text;
    }
  } else if (entry.label) {
    entry.marker.unbindTooltip();
    entry.label = false;
    entry.labelText = null;
  }
}

export function applyLabelsVisibility() {
  for (const entry of state.aircraft.values()) updateLabelFor(entry);
  state.syncOverlay(state.labelsProxy, state.showLabels);
}
