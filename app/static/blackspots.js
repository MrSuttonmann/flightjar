// Terrain blackspots overlay — shades areas where the radio line-of-sight
// to a user-selected target altitude is blocked by terrain or the Earth's
// curvature, with a hover tooltip showing the minimum antenna height
// (AGL from the receiver's ground elevation) that would clear the
// obstruction.
//
// A vertical altitude slider (Leaflet control, right edge of the map)
// drives the target altitude. Slider moves are debounced so a drag
// doesn't spam the backend; each altitude's grid is cached in-memory
// client-side, so toggling back and forth between altitudes is instant.

import {
  ALT_STOPS_M, DEFAULT_STOP_INDEX, bandFor, blockerShade,
  blockerTooltipFor, flLabel, tooltipFor,
} from './blackspots_format.js';
import { state } from './state.js';
import { track } from './telemetry.js';

// ---------- fetch + cache ----------

// Per-altitude cache of the backend payload. Keyed by the integer metres
// value sent to the server so slider round-trips don't ever re-fetch.
const gridCache = new Map();

let activeAltitudeM = ALT_STOPS_M[DEFAULT_STOP_INDEX];
let pendingController = null;
let fetchDebounce = null;
let progressPoll = null;

async function fetchForAltitude(altM) {
  if (gridCache.has(altM)) {
    renderSnapshot(gridCache.get(altM));
    setSliderBusy(false);
    return;
  }
  if (pendingController) pendingController.abort();
  const ctrl = new AbortController();
  pendingController = ctrl;
  setSliderBusy(true);
  startProgressPoll(altM, ctrl.signal);
  try {
    const r = await fetch(`/api/blackspots?target_alt_m=${altM}`, { signal: ctrl.signal });
    if (!r.ok) return;
    const data = await r.json();
    if (data.enabled === false) {
      renderSnapshot(null);
      return;
    }
    if (data.computing) {
      // Backend is still running the initial preload. Retry once after a
      // short delay — typical preload takes a few seconds on a fresh start.
      setTimeout(() => {
        if (activeAltitudeM === altM && state.showBlackspots) fetchForAltitude(altM);
      }, 3000);
      return;
    }
    gridCache.set(altM, data);
    if (activeAltitudeM === altM) renderSnapshot(data);
  } catch (e) {
    if (e.name !== 'AbortError') console.warn('blackspots fetch failed', e);
  } finally {
    if (pendingController === ctrl) {
      pendingController = null;
      setSliderBusy(false);
      stopProgressPoll();
    }
  }
}

// Poll /api/blackspots/progress while a compute is in flight. Updates the
// percent readout under the spinner; stops automatically when the main
// fetch resolves (signal is aborted) or the backend reports active=false.
// First tick fires immediately so a sub-interval compute still produces at
// least one readout instead of flashing past.
function startProgressPoll(altM, abortSignal) {
  stopProgressPoll();
  const tick = async () => {
    if (abortSignal.aborted) {
      stopProgressPoll();
      return;
    }
    try {
      const r = await fetch(`/api/blackspots/progress?target_alt_m=${altM}`, { signal: abortSignal });
      if (!r.ok) return;
      const data = await r.json();
      setSliderProgress(data.active ? data.progress : null);
    } catch (_) { /* AbortError or network blip — either way, let it pass */ }
  };
  tick();
  progressPoll = setInterval(tick, 150);
}

function stopProgressPoll() {
  if (progressPoll) {
    clearInterval(progressPoll);
    progressPoll = null;
  }
  setSliderProgress(null);
}

function scheduleFetch(altM) {
  clearTimeout(fetchDebounce);
  fetchDebounce = setTimeout(() => fetchForAltitude(altM), 250);
}

// ---------- rendering ----------

function cellBounds(cell, gridDeg) {
  const half = gridDeg / 2;
  return [[cell.lat - half, cell.lon - half], [cell.lat + half, cell.lon + half]];
}

// The blocker bin a (lat, lon) sample lands in, expressed as a Leaflet
// LatLngBounds. The backend bins by floor(coord / gridDeg) so the same
// floor operation here recovers the cell's bottom-left corner regardless
// of where inside the bin the tallest sample happened to sit.
function blockerBounds(blocker, gridDeg) {
  const latBase = Math.floor(blocker.lat / gridDeg) * gridDeg;
  const lonBase = Math.floor(blocker.lon / gridDeg) * gridDeg;
  return [[latBase, lonBase], [latBase + gridDeg, lonBase + gridDeg]];
}

// Stable string key for a blocker bin, derived the same way the backend
// aggregates obstructions into bins. Used to match blocked cells to the
// blocker their obstruction sample falls into so hover-highlight can
// thicken the right cells in O(1) lookup time.
function blockerBinKey(lat, lon, gridDeg) {
  return `${Math.floor(lat / gridDeg)},${Math.floor(lon / gridDeg)}`;
}

// Style override for a cell currently being highlighted by hovering its
// blocker. Slate stroke at 2.5 px + raised fill opacity makes the
// affected cells pop without recolouring them — the band colour still
// communicates "how much taller would I need".
const HIGHLIGHT_STYLE = {
  color: '#0f172a',
  weight: 2.5,
  opacity: 1.0,
  fillOpacity: 0.7,
};

function renderSnapshot(snapshot) {
  const layer = state.blackspotsLayer;
  if (!layer) return;
  layer.clearLayers();
  // Warn the user when the grid is suspicious: a multi-tile bbox where
  // almost nothing returned elevation data usually means the container
  // can't reach the SRTM bucket, and what renders is just the earth-
  // curvature horizon (= a perfect circle).
  setSliderTileWarning(
    snapshot?.tile_count > 1 && (snapshot?.tiles_with_data ?? 0) <= 1);
  if (!snapshot?.cells?.length || !snapshot.params) return;
  const gridDeg = snapshot.params.grid_deg;

  // Blocker shading first — neutral greyscale patches sit underneath the
  // shadow shading so the coloured blocked-cell rectangles stay the
  // primary visual. Opacity scales with how many shadowed cells trace
  // back to each obstructing bin, so prominent ridges read darker.
  // Rectangles are kept around to wire hover-highlight handlers in a
  // third pass (after the cell→bin lookup is built).
  const blockerEntries = [];  // {rect, key}
  if (snapshot.blockers?.length) {
    for (const blocker of snapshot.blockers) {
      const shade = blockerShade(blocker.blocked_count);
      const rect = L.rectangle(blockerBounds(blocker, gridDeg), {
        renderer: state.airportsCanvas,
        ...shade,
      });
      rect.bindTooltip(blockerTooltipFor(blocker, snapshot.params),
        { direction: 'top', sticky: true });
      rect.addTo(layer);
      blockerEntries.push({
        rect,
        key: blockerBinKey(blocker.lat, blocker.lon, gridDeg),
      });
    }
  }

  // Cells: build the rectangles + index each by the bin its obstruction
  // sample lands in, so hovering the blocker can re-style every cell that
  // traces back to it without scanning the whole list.
  const cellsByBlockerBin = new Map();
  for (const cell of snapshot.cells) {
    // `required_antenna_msl_m` is the absolute MSL height needed; the band
    // colour reflects how much *above the user's current antenna* that is
    // (AGL delta) so two receivers at different elevations still produce
    // comparable colour semantics ("yellow = a bit taller", "red = much
    // taller").
    const delta = cell.required_antenna_msl_m == null
      ? null
      : cell.required_antenna_msl_m - snapshot.params.antenna_msl_m;
    const band = bandFor(delta);
    // Keep the normal style closure-captured so mouseout can restore it
    // without us having to recompute the band → style mapping.
    const normalStyle = {
      color: band.stroke,
      weight: 0.5,
      opacity: 0.55,
      fillColor: band.fill,
      fillOpacity: 0.35,
    };
    const rect = L.rectangle(cellBounds(cell, gridDeg), {
      renderer: state.airportsCanvas,
      ...normalStyle,
    });
    rect.bindTooltip(tooltipFor(cell, snapshot.params),
      { direction: 'top', sticky: true });
    rect.addTo(layer);
    if (cell.obstruction_lat != null && cell.obstruction_lon != null) {
      const key = blockerBinKey(cell.obstruction_lat, cell.obstruction_lon, gridDeg);
      let bucket = cellsByBlockerBin.get(key);
      if (!bucket) { bucket = []; cellsByBlockerBin.set(key, bucket); }
      bucket.push({ rect, normalStyle });
    }
  }

  // Hover-highlight: for each blocker, look up the cells binned to it and
  // wire mouseover / mouseout to thicken / restore. Skipped when the
  // blocker has no associated cells (e.g. a snapshot stitched from old
  // cells that pre-date obstruction tracking).
  for (const { rect: blockerRect, key } of blockerEntries) {
    const linkedCells = cellsByBlockerBin.get(key);
    if (!linkedCells || linkedCells.length === 0) continue;
    blockerRect.on('mouseover', () => {
      for (const { rect } of linkedCells) rect.setStyle(HIGHLIGHT_STYLE);
    });
    blockerRect.on('mouseout', () => {
      for (const { rect, normalStyle } of linkedCells) rect.setStyle(normalStyle);
    });
  }
}

// ---------- slider control ----------

let sliderControl = null;
let sliderInput = null;
let sliderLabel = null;
let sliderStatus = null;
let sliderProgress = null;
let sliderWarning = null;

function makeSliderControl() {
  const AltControl = L.Control.extend({
    options: { position: 'topright' },
    onAdd() {
      const container = L.DomUtil.create('div', 'leaflet-bar blackspots-slider');
      container.title = 'Target altitude for the blackspot LOS calculation';
      container.innerHTML = `
        <div class="blackspots-slider-title">Altitude</div>
        <div class="blackspots-slider-label"></div>
        <input type="range" min="0" max="${ALT_STOPS_M.length - 1}" value="${DEFAULT_STOP_INDEX}" step="1" orient="vertical" aria-label="Target altitude">
        <div class="blackspots-slider-status" role="status" aria-label="Computing blackspots">
          <div class="blackspots-slider-spinner"></div>
          <div class="blackspots-slider-progress"></div>
        </div>
        <div class="blackspots-slider-warning" role="alert" hidden
             title="Almost no terrain data loaded — the grid below is earth-curvature only, not real terrain. Check the container's outbound access to elevation-tiles-prod.s3.amazonaws.com.">
          ⚠ no terrain
        </div>
      `;
      sliderLabel = container.querySelector('.blackspots-slider-label');
      sliderInput = container.querySelector('input[type="range"]');
      sliderStatus = container.querySelector('.blackspots-slider-status');
      sliderProgress = container.querySelector('.blackspots-slider-progress');
      sliderWarning = container.querySelector('.blackspots-slider-warning');

      // Swallow map pans / zooms while the user is interacting with the
      // slider — otherwise a drag on the handle would also drag the map.
      L.DomEvent.disableClickPropagation(container);
      L.DomEvent.disableScrollPropagation(container);

      sliderInput.addEventListener('input', () => {
        const idx = Number(sliderInput.value);
        const altM = ALT_STOPS_M[idx];
        activeAltitudeM = altM;
        updateLabel(altM);
        // Instant-path for cached altitudes so the displayed grid updates
        // as the user drags; uncached altitudes debounce through the
        // backend on drag-end.
        if (gridCache.has(altM)) {
          clearTimeout(fetchDebounce);
          renderSnapshot(gridCache.get(altM));
        } else {
          scheduleFetch(altM);
        }
      });
      // `change` fires once on commit (release / arrow-key step), unlike
      // `input` which fires continuously. Telemetry only cares about the
      // committed value so we don't emit an event per pixel of drag.
      sliderInput.addEventListener('change', () => {
        const idx = Number(sliderInput.value);
        const altM = ALT_STOPS_M[idx];
        track('blackspots_altitude_changed', { alt_m: altM });
      });

      updateLabel(activeAltitudeM);
      return container;
    },
  });
  return new AltControl();
}

function updateLabel(altM) {
  if (sliderLabel) sliderLabel.textContent = flLabel(altM);
}

function setSliderBusy(busy) {
  if (sliderStatus) sliderStatus.classList.toggle('busy', busy);
}

function setSliderProgress(frac) {
  if (!sliderProgress) return;
  if (frac == null || frac <= 0) {
    sliderProgress.textContent = '';
    return;
  }
  sliderProgress.textContent = `${Math.round(frac * 100)}%`;
}

function setSliderTileWarning(show) {
  if (!sliderWarning) return;
  sliderWarning.hidden = !show;
}

// ---------- toggle wiring ----------

function applyToggle() {
  const layer = state.blackspotsLayer;
  const proxy = state.blackspotsProxy;
  if (!layer || !proxy) return;
  if (state.showBlackspots) {
    if (!state.map.hasLayer(layer)) layer.addTo(state.map);
    if (!sliderControl) sliderControl = makeSliderControl();
    state.map.addControl(sliderControl);
    fetchForAltitude(activeAltitudeM);
  } else {
    state.map.removeLayer(layer);
    if (sliderControl) state.map.removeControl(sliderControl);
    // Keep gridCache — toggling off and back on shouldn't wipe the
    // already-fetched altitudes.
  }
  state.syncOverlay(proxy, state.showBlackspots);
}

export function setBlackspots(value) {
  state.showBlackspots = value;
  try { localStorage.setItem('flightjar.blackspots', value ? '1' : '0'); } catch (_) { /* no-op */ }
  applyToggle();
}

export function initBlackspotsOverlay() {
  applyToggle();
}
