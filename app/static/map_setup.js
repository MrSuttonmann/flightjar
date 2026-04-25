// Leaflet map bootstrap: base layers, overlays, range rings, altitude
// legend, receiver marker. Populates the Leaflet-related fields on
// `state` so every other module can read them by name.
//
// The overlay proxies (labels/trails/airports/coverage) are "dummy"
// L.layerGroups the layers-control attaches checkboxes to; they don't
// draw anything. Flipping them in the control fires overlayadd /
// overlayremove, which we dispatch back into setLabels/setTrails/etc.
// so the Leaflet UI and our own state stay in sync.

import { ALT_STOPS } from './altitude.js';
import { getUnitSystem, uconv } from './units.js';
import { showToast } from './toast.js';
import { state } from './state.js';
import { initLayerStatus } from './map_layer_status.js';

function slugify(name) {
  return name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '');
}

function applyBasemapClass(name) {
  for (const cls of [...document.body.classList]) {
    if (cls.startsWith('basemap-')) document.body.classList.remove(cls);
  }
  document.body.classList.add('basemap-' + slugify(name));
}

export function initMap({ config = {}, ...overlayHandlers } = {}) {
  const map = L.map('map', { worldCopyJump: true, zoomControl: false })
    .setView([51.5, -0.1], 6);
  L.control.zoom({ position: 'bottomright' }).addTo(map);

  // Canvas renderer for trails — one element handles hundreds of short
  // polyline segments far more cheaply than an SVG path each.
  const trailsCanvas = L.canvas({ padding: 0.1 });

  // Altitude colour legend pinned above the map's bottom edge. A horizontal
  // gradient built from the ALT_STOPS palette; tick labels re-render on
  // unit-system change so they always match the rest of the UI.
  const altLegend = L.DomUtil.create('div', 'alt-legend');
  const altLegendBar = L.DomUtil.create('div', 'alt-legend-bar', altLegend);
  const altLegendTicks = L.DomUtil.create('div', 'alt-legend-ticks', altLegend);
  altLegendBar.style.background = 'linear-gradient(to right, ' +
    ALT_STOPS.map(([, [r, g, b]]) => `rgb(${r},${g},${b})`).join(', ') + ')';
  // Tick values in canonical feet (the unit the colour gradient is built
  // on). Imperial/nautical want the obvious 0 / 10k / 20k / 30k / 40k ft
  // labels. Metric picks feet values that convert to round km numbers
  // so the legend reads "3.0 km / 6.0 km" etc.
  const LEGEND_TICKS_FT = {
    nautical: [0, 10000, 20000, 30000, 40000],
    imperial: [0, 10000, 20000, 30000, 40000],
    metric: [0, 9843, 19685, 29528, 39370],
  };
  function renderAltLegend() {
    const ticks = LEGEND_TICKS_FT[getUnitSystem()] || LEGEND_TICKS_FT.nautical;
    altLegendTicks.innerHTML = ticks.map(ft => `<span>${uconv('alt', ft)}</span>`).join('');
  }
  renderAltLegend();
  map.getContainer().appendChild(altLegend);

  const baseLayers = {
    'OpenStreetMap': L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
      maxZoom: 19,
    }),
    'Carto Dark': L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png', {
      attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors &copy; <a href="https://carto.com/attributions">CARTO</a>',
      subdomains: 'abcd', maxZoom: 19,
    }),
    'Satellite': L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}', {
      attribution: 'Tiles &copy; Esri', maxZoom: 19,
    }),
    // OpenTopoMap: CC-BY-SA topographic tiles (contours + hillshade) built
    // from OSM vector data + SRTM relief. Free and keyless; community-run
    // so can be slower than the commercial tiles above. Caps at z17 per
    // their tile server config.
    'Topographic': L.tileLayer('https://{s}.tile.opentopomap.org/{z}/{x}/{y}.png', {
      attribution: 'Map data: &copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors, SRTM · Map style: &copy; <a href="https://opentopomap.org">OpenTopoMap</a> (<a href="https://creativecommons.org/licenses/by-sa/3.0/">CC-BY-SA</a>)',
      subdomains: 'abc', maxZoom: 17,
    }),
  };
  // OpenAIP's aeronautical tiles are a semi-transparent chart layer — on
  // their own there's no ground reference, so the Aeronautical base
  // layer is a composite of OSM underneath + OpenAIP on top. We always
  // register the entry so the user can see it exists; when the OpenAIP
  // gate is closed the layer is a no-op placeholder and applyLayerStatus
  // disables the radio + attaches a why/how info popover.
  if (config.openaip_api_key) {
    baseLayers['Aeronautical (OpenAIP)'] = L.layerGroup([
      L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
        maxZoom: 19,
      }),
      L.tileLayer(
        `https://api.tiles.openaip.net/api/data/openaip/{z}/{x}/{y}.png?apiKey=${encodeURIComponent(config.openaip_api_key)}`,
        {
          attribution:
            '&copy; <a href="https://www.openaip.net/" target="_blank" rel="noopener">OpenAIP</a> (CC BY-NC-SA)',
          maxZoom: 14,
          minZoom: 4,
          opacity: 0.85,
        },
      ),
    ]);
  } else {
    baseLayers['Aeronautical (OpenAIP)'] = L.layerGroup();
  }
  // When a base layer's gate is closed (e.g. OpenAIP without an API key)
  // we register a no-op placeholder so the radio still shows in the
  // layers control as a disabled row — but we must not *activate* it,
  // even if the user previously selected it. Treat such entries as
  // unavailable for the saved-basemap restore.
  const layerStatus = config.layer_status || {};
  const baseGated = {
    'Aeronautical (OpenAIP)': layerStatus.openaip?.enabled === false,
  };
  const savedBase = localStorage.getItem('flightjar.basemap');
  const savedBaseUsable = savedBase && baseLayers[savedBase] && !baseGated[savedBase];
  const defaultBaseName = savedBaseUsable ? savedBase : 'OpenStreetMap';
  baseLayers[defaultBaseName].addTo(map);
  applyBasemapClass(defaultBaseName);

  // Range-ring overlay (centred on receiver; rebuilt when receiver is known).
  const rangeRings = L.layerGroup();
  // Airport markers — populated from /api/airports for the current map
  // bbox whenever the Airports layer is toggled on. Uses a canvas
  // renderer so thousands of circleMarkers render smoothly.
  const airportsLayer = L.layerGroup();
  const airportsCanvas = L.canvas({ padding: 0.1 });
  // Navaids (VOR / DME / NDB) — same bbox-fetch pattern as airports,
  // shares the airports canvas renderer.
  const navaidsLayer = L.layerGroup();
  // OpenAIP overlays — airspaces (polygons), obstacles + reporting points
  // (canvas circleMarkers on the shared airports renderer).
  const airspacesLayer = L.layerGroup();
  const obstaclesLayer = L.layerGroup();
  const reportingLayer = L.layerGroup();
  // Terrain blackspots — precomputed grid of rectangles coloured by
  // required antenna height. Uses the shared airports canvas renderer.
  const blackspotsLayer = L.layerGroup();
  const RANGE_RING_SETS = {
    nautical: { values: [50, 100, 200], metersPerUnit: 1852,    suffix: ' NM' },
    imperial: { values: [50, 100, 200], metersPerUnit: 1609.344, suffix: ' mi' },
    metric:   { values: [100, 200, 400], metersPerUnit: 1000,    suffix: ' km' },
  };

  function buildRangeRings(rx) {
    rangeRings.clearLayers();
    if (!rx || rx.lat == null || rx.lon == null) return;
    const set = RANGE_RING_SETS[getUnitSystem()] || RANGE_RING_SETS.nautical;
    for (const v of set.values) {
      const radius = v * set.metersPerUnit;
      L.circle([rx.lat, rx.lon], {
        radius,
        color: '#ffffff', weight: 3, opacity: 0.55,
        fill: false, interactive: false,
      }).addTo(rangeRings);
      L.circle([rx.lat, rx.lon], {
        radius,
        color: '#1d4ed8', weight: 1.25, opacity: 0.85,
        fill: false, dashArray: '4 4', interactive: false,
      }).addTo(rangeRings);
      L.marker([rx.lat, rx.lon], {
        interactive: false,
        icon: L.divIcon({
          className: 'range-label',
          html: `<span>${v}${set.suffix}</span>`,
          iconSize: [0, 0],
        }),
      }).addTo(rangeRings).setLatLng([rx.lat + radius / 111000, rx.lon]);
    }
  }

  const labelsProxy = L.layerGroup();
  const trailsProxy = L.layerGroup();
  const airportsProxy = L.layerGroup();
  const navaidsProxy = L.layerGroup();
  const airspacesProxy = L.layerGroup();
  const obstaclesProxy = L.layerGroup();
  const reportingProxy = L.layerGroup();
  const blackspotsProxy = L.layerGroup();
  const coverageProxy = L.layerGroup();
  let syncingOverlays = false;
  function syncOverlay(proxy, on) {
    syncingOverlays = true;
    try {
      if (on && !map.hasLayer(proxy)) map.addLayer(proxy);
      else if (!on && map.hasLayer(proxy)) map.removeLayer(proxy);
    } finally {
      syncingOverlays = false;
    }
  }

  // Proxy-overlay registry. Each entry binds a layers-control checkbox to
  // a setter in `overlayHandlers`; adding/removing the proxy dispatches
  // the setter instead of drawing anything directly. Keeping this as a
  // table lets new overlays plug in with one line instead of four
  // if-branches in overlayadd/overlayremove.
  const PROXY_OVERLAYS = [
    { label: 'Aircraft labels', proxy: labelsProxy,   handler: 'setLabels' },
    { label: 'Altitude trails', proxy: trailsProxy,   handler: 'setTrails' },
    { label: 'Airports',        proxy: airportsProxy, handler: 'setAirports' },
    { label: 'Navaids',         proxy: navaidsProxy,  handler: 'setNavaids' },
    { label: 'Airspaces',       proxy: airspacesProxy, handler: 'setAirspaces' },
    { label: 'Obstacles',       proxy: obstaclesProxy, handler: 'setObstacles' },
    { label: 'Reporting points', proxy: reportingProxy, handler: 'setReporting' },
    { label: 'Polar coverage',  proxy: coverageProxy, handler: 'setCoverage' },
    { label: 'Terrain blackspots', proxy: blackspotsProxy, handler: 'setBlackspots' },
  ];
  const proxyToHandler = new Map(PROXY_OVERLAYS.map((o) => [o.proxy, o.handler]));

  // Tile-overlay registry. Unlike the proxy overlays these are real
  // L.tileLayer instances that Leaflet's native add/remove handles
  // directly. We still want their checkbox state to survive reloads, so
  // each entry has a `storageKey` we write on overlayadd/overlayremove.
  // The OpenAIP Aeronautical chart is now a base layer, not an overlay
  // — see the `baseLayers` block above.
  //
  // When the VFRMap chart cycle is missing we still register placeholder
  // layerGroups for IFR Low / IFR High; applyLayerStatus disables their
  // checkboxes and attaches an info popover with VFRMAP_CHART_DATE
  // setup notes, so the user can see the layers exist but understand
  // why they aren't actionable.
  const tileOverlays = [];
  if (config.vfrmap_chart_date) {
    // VFRMap tile URLs embed the 28-day FAA chart cycle date. When the
    // configured date falls off the back of vfrmap.com's retention the
    // server 404s each tile — we watch tileerror and nudge the user if
    // it happens repeatedly.
    const VFRMAP_BASE = `https://vfrmap.com/${encodeURIComponent(config.vfrmap_chart_date)}/tiles`;
    const VFRMAP_ATTRIB =
      'Charts: <a href="https://vfrmap.com/" target="_blank" rel="noopener">VFRMap.com</a> / FAA';
    // One shared counter + one shared toast across both IFR layers —
    // staleness affects both simultaneously so we only want to warn once.
    let ifrTileErrors = 0;
    let ifrToastFired = false;
    function onIfrTileError() {
      ifrTileErrors += 1;
      if (ifrTileErrors >= 5 && !ifrToastFired) {
        ifrToastFired = true;
        showToast(
          'IFR chart date may be stale — update VFRMAP_CHART_DATE to the current FAA cycle.',
          { level: 'warn', duration: 8000 },
        );
      }
    }
    // VFRMap serves tiles in TMS orientation (their own map.js sets
    // `tms:true`); Leaflet's default XYZ scheme would request the
    // mirror-flipped row and silently get 486-byte blank tiles back.
    const ifrLowLayer = L.tileLayer(
      `${VFRMAP_BASE}/ifrlc/{z}/{y}/{x}.jpg`,
      {
        attribution: VFRMAP_ATTRIB,
        tms: true,
        maxZoom: 11,
        minZoom: 5,
        opacity: 0.85,
      },
    );
    ifrLowLayer.on('tileerror', onIfrTileError);
    tileOverlays.push({
      label: 'IFR Low (US)',
      layer: ifrLowLayer,
      storageKey: 'flightjar.ifr_low',
      initiallyOn: state.showIfrLow,
    });
    const ifrHighLayer = L.tileLayer(
      `${VFRMAP_BASE}/ehc/{z}/{y}/{x}.jpg`,
      {
        attribution: VFRMAP_ATTRIB,
        tms: true,
        // EHC tops out at zoom 10 per VFRMap's own config (vs 11 for IFR Low).
        maxZoom: 10,
        minZoom: 5,
        opacity: 0.85,
      },
    );
    ifrHighLayer.on('tileerror', onIfrTileError);
    tileOverlays.push({
      label: 'IFR High (US)',
      layer: ifrHighLayer,
      storageKey: 'flightjar.ifr_high',
      initiallyOn: state.showIfrHigh,
    });
  } else {
    tileOverlays.push({
      label: 'IFR Low (US)',
      layer: L.layerGroup(),
      storageKey: 'flightjar.ifr_low',
      initiallyOn: false,
    });
    tileOverlays.push({
      label: 'IFR High (US)',
      layer: L.layerGroup(),
      storageKey: 'flightjar.ifr_high',
      initiallyOn: false,
    });
  }
  const tileLayerToKey = new Map(tileOverlays.map((o) => [o.layer, o.storageKey]));

  const overlays = {};
  for (const o of PROXY_OVERLAYS) overlays[o.label] = o.proxy;
  for (const o of tileOverlays) overlays[o.label] = o.layer;
  overlays['Range rings'] = rangeRings;
  const layersControl = L.control.layers(baseLayers, overlays, { position: 'topright' }).addTo(map);

  // Restore tile-overlay visibility from localStorage. Done after the
  // layers-control is built so its checkbox state reflects the layer.
  for (const o of tileOverlays) {
    if (o.initiallyOn) o.layer.addTo(map);
  }

  map.on('baselayerchange', (e) => {
    try { localStorage.setItem('flightjar.basemap', e.name); } catch (_) {}
    applyBasemapClass(e.name);
  });
  map.on('overlayadd', (e) => {
    if (syncingOverlays) return;
    const handler = proxyToHandler.get(e.layer);
    if (handler) { overlayHandlers[handler]?.(true); return; }
    const key = tileLayerToKey.get(e.layer);
    if (key) { try { localStorage.setItem(key, '1'); } catch (_) {} }
  });
  map.on('overlayremove', (e) => {
    if (syncingOverlays) return;
    const handler = proxyToHandler.get(e.layer);
    if (handler) { overlayHandlers[handler]?.(false); return; }
    const key = tileLayerToKey.get(e.layer);
    if (key) { try { localStorage.setItem(key, '0'); } catch (_) {} }
  });

  // Publish to the shared state so every other module can reach them
  // by name rather than being threaded the handles as arguments.
  state.map = map;
  state.trailsCanvas = trailsCanvas;
  state.airportsCanvas = airportsCanvas;
  state.airportsLayer = airportsLayer;
  state.navaidsLayer = navaidsLayer;
  state.airspacesLayer = airspacesLayer;
  state.obstaclesLayer = obstaclesLayer;
  state.reportingLayer = reportingLayer;
  state.blackspotsLayer = blackspotsLayer;
  state.labelsProxy = labelsProxy;
  state.trailsProxy = trailsProxy;
  state.airportsProxy = airportsProxy;
  state.navaidsProxy = navaidsProxy;
  state.airspacesProxy = airspacesProxy;
  state.obstaclesProxy = obstaclesProxy;
  state.reportingProxy = reportingProxy;
  state.blackspotsProxy = blackspotsProxy;
  state.coverageProxy = coverageProxy;
  state.layersControl = layersControl;
  state.syncOverlay = syncOverlay;
  state.buildRangeRings = buildRangeRings;
  state.renderAltLegend = renderAltLegend;

  // Paint disabled-layer rows from the backend's reported gate state
  // (missing OPENAIP_API_KEY / VFRMAP_CHART_DATE / receiver coords).
  // `layer_status` may be absent when the endpoint failed — in which
  // case nothing is annotated and the user just sees the working
  // layers, same as before.
  initLayerStatus(config.layer_status);
}

// Draw the receiver marker (+ optional privacy circle) on first snapshot
// that carries coordinates. Idempotent — subsequent calls no-op because
// receiver position doesn't change mid-session.
export function renderReceiver(rx) {
  if (!rx || rx.lat == null || rx.lon == null) return;
  if (state.receiverLayer) return;
  state.buildRangeRings(rx);

  const group = L.layerGroup().addTo(state.map);
  const label = rx.anon_km > 0
    ? `Receiver area (±${rx.anon_km} km)`
    : 'Receiver';
  const icon = L.divIcon({
    className: 'receiver-icon',
    html: '<div class="receiver-dot"></div>',
    iconSize: [14, 14],
    iconAnchor: [7, 7],
  });
  L.marker([rx.lat, rx.lon], { icon, title: label, interactive: false }).addTo(group);
  if (rx.anon_km > 0) {
    L.circle([rx.lat, rx.lon], {
      radius: rx.anon_km * 1000,
      color: '#5fa8ff', weight: 1, opacity: 0.6,
      fillColor: '#5fa8ff', fillOpacity: 0.08, interactive: false,
    }).addTo(group);
  }
  state.receiverLayer = group;
}
