// Flightjar frontend entry point. This file is deliberately thin —
// each concern lives in its own module and this file just wires them
// together in the right order. Initialisation order is load-bearing:
// map_setup must run before any module that reads state.map, detail
// panel + sidebar must be wired before the first WebSocket snapshot
// arrives, and so on.

import { createWatchlist } from './watchlist.js';
import { initAboutDialog, initMapKeyDialog, initStatsDialog, initWatchlistDialog } from './dialogs.js';
import { initAlertsDialog } from './alerts_dialog.js';
import { initAirportTooltip } from './tooltip.js';
import {
  initDetailPanel,
  setRenderSidebar,
} from './detail_panel.js';
import { initEggs } from './eggs.js';
import { initFooterClock } from './footer_clock.js';
import { initMap } from './map_setup.js';
import {
  initMapControls,
  setAirports,
  setCoverage,
  setLabels,
  setNavaids,
  setTrails,
} from './map_controls.js';
import { setBlackspots } from './blackspots.js';
import {
  setAirspaces,
  setObstacles,
  setReporting,
} from './openaip.js';
import { initShortcuts } from './shortcuts.js';
import { initSidebar, renderSidebar } from './sidebar.js';
import { connect, startHeartbeat } from './websocket.js';
import {
  getSessionStats,
  readFirstOfDay,
  state,
} from './state.js';

// ---- boot ----

// Fetch deploy-time map config (tile provider API keys, chart-cycle
// dates) before initMap, so tile overlays whose config is missing are
// quietly skipped rather than rendered as broken checkboxes. The
// endpoint is cheap; a failure falls back to {} and the map still boots
// — you just don't get OpenAIP / VFRMap overlays.
async function fetchMapConfig() {
  try {
    const r = await fetch('/api/map_config');
    if (!r.ok) return {};
    return await r.json();
  } catch (_) {
    return {};
  }
}

async function boot() {
  const config = await fetchMapConfig();

  // Map first — everything else reads state.map and the layer handles it
  // populates. Overlay add/remove from the layers control dispatches back
  // into the setters owned by map_controls.js.
  initMap({
    config,
    setLabels, setTrails,
    setAirports, setNavaids,
    setAirspaces, setObstacles, setReporting,
    setCoverage,
    setBlackspots,
  });

  // Shared singletons that aren't Leaflet.
  state.watchlist = createWatchlist();
  state.firstOfDayIcao = readFirstOfDay();

  // DOM-bound pieces (each init function owns its event listeners).
  initAirportTooltip();
  initAlertsDialog();
  initEggs({ getSessionStats });
  initDetailPanel();
  initSidebar();
  initMapControls();
  initAboutDialog();
  initMapKeyDialog();
  initStatsDialog();
  initWatchlistDialog();
  initShortcuts();
  initFooterClock();

  // Late-bind sidebar render into the detail panel so the two can
  // cross-reference without a circular import.
  setRenderSidebar(renderSidebar);

  // Kick the feed.
  startHeartbeat();
  connect();
}

boot();
