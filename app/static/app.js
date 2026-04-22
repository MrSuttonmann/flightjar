// Flightjar frontend entry point. This file is deliberately thin —
// each concern lives in its own module and this file just wires them
// together in the right order. Initialisation order is load-bearing:
// map_setup must run before any module that reads state.map, detail
// panel + sidebar must be wired before the first WebSocket snapshot
// arrives, and so on.

import { createWatchlist } from './watchlist.js';
import { initAboutDialog, initStatsDialog, initWatchlistDialog } from './dialogs.js';
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
  setTrails,
} from './map_controls.js';
import { initShortcuts } from './shortcuts.js';
import { initSidebar, renderSidebar } from './sidebar.js';
import { connect, startHeartbeat } from './websocket.js';
import {
  getSessionStats,
  readFirstOfDay,
  state,
} from './state.js';

// ---- boot ----

// Map first — everything else reads state.map and the layer handles it
// populates. Overlay add/remove from the layers control dispatches back
// into the setters owned by map_controls.js.
initMap({ setLabels, setTrails, setAirports, setCoverage });

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
