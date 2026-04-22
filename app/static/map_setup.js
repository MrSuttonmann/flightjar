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
import { state } from './state.js';

function slugify(name) {
  return name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '');
}

function applyBasemapClass(name) {
  for (const cls of [...document.body.classList]) {
    if (cls.startsWith('basemap-')) document.body.classList.remove(cls);
  }
  document.body.classList.add('basemap-' + slugify(name));
}

export function initMap(overlayHandlers) {
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
  };
  const savedBase = localStorage.getItem('flightjar.basemap');
  const defaultBaseName = savedBase && baseLayers[savedBase] ? savedBase : 'OpenStreetMap';
  baseLayers[defaultBaseName].addTo(map);
  applyBasemapClass(defaultBaseName);

  // Range-ring overlay (centred on receiver; rebuilt when receiver is known).
  const rangeRings = L.layerGroup();
  // Airport markers — populated from /api/airports for the current map
  // bbox whenever the Airports layer is toggled on. Uses a canvas
  // renderer so thousands of circleMarkers render smoothly.
  const airportsLayer = L.layerGroup();
  const airportsCanvas = L.canvas({ padding: 0.1 });
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

  const overlays = {
    'Aircraft labels': labelsProxy,
    'Altitude trails': trailsProxy,
    'Airports': airportsProxy,
    'Polar coverage': coverageProxy,
    'Range rings': rangeRings,
  };
  L.control.layers(baseLayers, overlays, { position: 'topright' }).addTo(map);

  map.on('baselayerchange', (e) => {
    try { localStorage.setItem('flightjar.basemap', e.name); } catch (_) {}
    applyBasemapClass(e.name);
  });
  map.on('overlayadd', (e) => {
    if (syncingOverlays) return;
    if (e.layer === labelsProxy) overlayHandlers.setLabels(true);
    else if (e.layer === trailsProxy) overlayHandlers.setTrails(true);
    else if (e.layer === airportsProxy) overlayHandlers.setAirports(true);
    else if (e.layer === coverageProxy) overlayHandlers.setCoverage(true);
  });
  map.on('overlayremove', (e) => {
    if (syncingOverlays) return;
    if (e.layer === labelsProxy) overlayHandlers.setLabels(false);
    else if (e.layer === trailsProxy) overlayHandlers.setTrails(false);
    else if (e.layer === airportsProxy) overlayHandlers.setAirports(false);
    else if (e.layer === coverageProxy) overlayHandlers.setCoverage(false);
  });

  // Publish to the shared state so every other module can reach them
  // by name rather than being threaded the handles as arguments.
  state.map = map;
  state.trailsCanvas = trailsCanvas;
  state.airportsCanvas = airportsCanvas;
  state.airportsLayer = airportsLayer;
  state.labelsProxy = labelsProxy;
  state.trailsProxy = trailsProxy;
  state.airportsProxy = airportsProxy;
  state.coverageProxy = coverageProxy;
  state.syncOverlay = syncOverlay;
  state.buildRangeRings = buildRangeRings;
  state.renderAltLegend = renderAltLegend;
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
