import { ageOf, compassIcon, escapeHtml, flagIcon, fmt } from './format.js';
import { UNIT_SYSTEMS, getUnitSystem, setUnitSystem, uconv } from './units.js';
import { ALT_STOPS, altColor } from './altitude.js';
import { HIST_LEN, TREND_THRESHOLDS, pushHistory, trendInfo } from './trend.js';
import {
  CATEGORY_NAMES,
  LINK_ICON_SVG,
  relativeAge,
  renderAltProfile,
  renderSpdProfile,
  signalLabel,
} from './profile.js';
import { initAirportTooltip } from './tooltip.js';
import { createWatchlist } from './watchlist.js';

(() => {
  const map = L.map('map', { worldCopyJump: true, zoomControl: false })
    .setView([51.5, -0.1], 6);
  L.control.zoom({ position: 'bottomright' }).addTo(map);

  // Canvas renderer for trails — one element handles hundreds of short
  // polyline segments far more cheaply than an SVG path each. Lives in
  // the default overlay pane alongside the airports canvas; no custom
  // pane needed.
  const trailsCanvas = L.canvas({ padding: 0.1 });

  // Altitude colour legend pinned above the map's bottom edge. A horizontal
  // gradient built from the ALT_STOPS palette; tick labels re-render on
  // unit-system change so they always match the rest of the UI.
  const altLegend = L.DomUtil.create('div', 'alt-legend');
  const altLegendBar = L.DomUtil.create('div', 'alt-legend-bar', altLegend);
  const altLegendTicks = L.DomUtil.create('div', 'alt-legend-ticks', altLegend);
  altLegendBar.style.background = 'linear-gradient(to right, ' +
    ALT_STOPS.map(([, [r, g, b]]) => `rgb(${r},${g},${b})`).join(', ') + ')';
  const LEGEND_TICK_FT = [0, 10000, 20000, 30000, 40000];
  function renderAltLegend() {
    altLegendTicks.innerHTML = LEGEND_TICK_FT
      .map(ft => `<span>${uconv('alt', ft)}</span>`)
      .join('');
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

  // Range-ring overlay (centred on receiver; rebuilt when receiver is known).
  const rangeRings = L.layerGroup();
  // Airport markers. Populated from /api/airports for the current map bbox
  // whenever the Airports layer is toggled on; cleared when off. Uses a
  // canvas renderer so thousands of circleMarkers render smoothly.
  const airportsLayer = L.layerGroup();
  const airportsCanvas = L.canvas({ padding: 0.1 });
  function buildRangeRings(rx) {
    rangeRings.clearLayers();
    if (!rx || rx.lat == null || rx.lon == null) return;
    // 50 / 100 / 200 NM. 1 NM = 1852 m. A white halo underneath the coloured
    // ring keeps the circles visible on both light (OSM) and dark (Carto,
    // satellite) base layers.
    for (const nm of [50, 100, 200]) {
      L.circle([rx.lat, rx.lon], {
        radius: nm * 1852,
        color: '#ffffff', weight: 3, opacity: 0.55,
        fill: false, interactive: false,
      }).addTo(rangeRings);
      L.circle([rx.lat, rx.lon], {
        radius: nm * 1852,
        color: '#1d4ed8', weight: 1.25, opacity: 0.85,
        fill: false, dashArray: '4 4', interactive: false,
      }).addTo(rangeRings);
      L.marker([rx.lat, rx.lon], {
        interactive: false,
        icon: L.divIcon({
          className: 'range-label',
          html: `<span>${nm} NM</span>`,
          iconSize: [0, 0],
        }),
      }).addTo(rangeRings).setLatLng([rx.lat + (nm * 1852) / 111000, rx.lon]);
    }
  }
  // Proxy layer-groups feed Leaflet's native layers control. They don't
  // draw anything themselves — adding/removing them from the map just
  // flips the corresponding toggle via the overlayadd/overlayremove
  // events wired up below. A small syncingOverlays flag prevents the
  // setters from re-entering when called in response to an event.
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
  });
  map.on('overlayadd', (e) => {
    if (syncingOverlays) return;
    if (e.layer === labelsProxy) setLabels(true);
    else if (e.layer === trailsProxy) setTrails(true);
    else if (e.layer === airportsProxy) setAirports(true);
    else if (e.layer === coverageProxy) setCoverage(true);
  });
  map.on('overlayremove', (e) => {
    if (syncingOverlays) return;
    if (e.layer === labelsProxy) setLabels(false);
    else if (e.layer === trailsProxy) setTrails(false);
    else if (e.layer === airportsProxy) setAirports(false);
    else if (e.layer === coverageProxy) setCoverage(false);
  });

  const aircraft = new Map(); // icao -> { marker, trail, label, data }
  let selectedIcao = null;
  let showLabels = localStorage.getItem('flightjar.labels') !== '0';
  let showTrails = localStorage.getItem('flightjar.trails') !== '0';
  let followSelected = localStorage.getItem('flightjar.follow') === '1';
  let compactMode = localStorage.getItem('flightjar.compact') === '1';
  let showAirports = localStorage.getItem('flightjar.airports') === '1';
  let firstUpdate = true;
  let lastSnap = null;
  let lastSnapAt = 0;  // Date.now() of the most recent snapshot, for the heartbeat.
  let receiverLayer = null;  // L.LayerGroup containing marker + optional anon circle
  let hoveredFromListIcao = null;  // icao currently hovered in the sidebar
  let hoveredFromMapIcao  = null;  // icao whose marker is currently hovered
  let hoverHalo = null;            // L.circleMarker drawn around the hovered aircraft
  let searchFilter = '';           // lowercased substring filter for the sidebar
  let pendingDeepLinkIcao = null;  // icao from URL hash, applied after first snapshot

  // Sidebar sort state. `dir` is 1 (asc) or -1 (desc).
  // Default direction per key — matches what feels natural for each column.
  const defaultDir = { callsign: 1, altitude: -1, distance: 1, age: 1 };
  let sortKey = 'callsign';
  let sortDir = defaultDir[sortKey];

  // Unit system lives in units.js; initialise from localStorage.
  setUnitSystem(localStorage.getItem('flightjar.units') || 'nautical');

  function sortValue(a, key, now) {
    switch (key) {
      case 'callsign': return (a.callsign || '').trim() || null;
      case 'altitude': return a.altitude;
      case 'age':      return ageOf(a, now);
      case 'distance': return a.distance_km;
    }
    return null;
  }

  // tar1090 per-type silhouettes — loaded asynchronously so a missing
  // tar1090_shapes.js (e.g. first run before the fetch script has run)
  // doesn't break the app. Until it resolves, planeIcon falls back to
  // the generic arrow defined inline below.
  let tar1090Shapes = null;
  let tar1090TypeIcons = null;
  import('./tar1090_shapes.js').then(mod => {
    tar1090Shapes = mod.shapes;
    tar1090TypeIcons = mod.TypeDesignatorIcons;
  }).catch(e => {
    console.warn('tar1090 shapes unavailable — using generic arrow', e);
  });

  function tar1090ShapeFor(typeIcao) {
    if (!tar1090Shapes || !tar1090TypeIcons || !typeIcao) return null;
    const entry = tar1090TypeIcons[typeIcao.toUpperCase()];
    if (!entry) return null;
    const [shapeName, scaleFactor] = entry;
    const shape = tar1090Shapes[shapeName];
    return shape ? { shape, scaleFactor } : null;
  }

  // Render a tar1090-shaped marker. Follows the layout from tar1090's own
  // svgShapeToSVG: stroke width is a small reference (0.5 px in viewBox
  // units) multiplied by shape.strokeScale, so the viewBox-unit scale
  // varies across shapes but the on-screen stroke stays consistent. Main
  // path uses paint-order="stroke" (stroke under fill, so only the
  // outline shows). Accent paths render as fill="none" lines.
  function tar1090Icon(track, color, selected, emergency, shape, scaleFactor) {
    const rot = track == null ? 0 : track;
    const target = 32;  // target pixel size of the longer edge at scaleFactor=1
    const longest = Math.max(shape.w, shape.h);
    const scale = (scaleFactor || 1) * (target / longest);
    const w = Math.round(shape.w * scale);
    const h = Math.round(shape.h * scale);
    let baseStroke = 0.5;
    let stroke = selected ? '#ffffff' : '#000000';
    if (emergency) { stroke = '#ef4444'; baseStroke = 0.8; }
    const ss = shape.strokeScale || 1;
    const strokeWidth = 2 * baseStroke * ss;
    const accentWidth = 0.6 * (shape.accentMult ? shape.accentMult * baseStroke : baseStroke) * ss;

    const paths = Array.isArray(shape.path) ? shape.path : [shape.path];
    let body = paths.map(d =>
      `<path paint-order="stroke" fill="${color}" stroke="${stroke}" ` +
      `stroke-width="${strokeWidth}" d="${d}"/>`
    ).join('');
    if (shape.accent) {
      const accents = Array.isArray(shape.accent) ? shape.accent : [shape.accent];
      body += accents.map(d =>
        `<path fill="none" stroke="${stroke}" stroke-width="${accentWidth}" d="${d}"/>`
      ).join('');
    }
    const innerTransform = shape.transform ? ` transform="${shape.transform}"` : '';
    const svg =
      `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" ` +
      `viewBox="${shape.viewBox}"` +
      (shape.noAspect ? ' preserveAspectRatio="none"' : '') +
      ` style="transform: rotate(${rot}deg); transform-origin: center;">` +
      `<g${innerTransform}>${body}</g></svg>`;
    return L.divIcon({
      html: svg,
      className: 'plane-icon',
      iconSize: [w, h],
      iconAnchor: [w / 2, h / 2],
    });
  }

  // Simple triangular arrow used when tar1090 has no silhouette for this
  // aircraft's ICAO type code (or the bundle hasn't loaded yet).
  const GENERIC_ARROW_PATH = 'M0,-10 L7,8 L0,4 L-7,8 Z';
  const GENERIC_ARROW_SIZE = 26;

  function planeIcon(track, color, selected, emergency, typeIcao) {
    // Prefer tar1090's per-type silhouette when we have one for this ICAO
    // type code; fall back to the generic arrow otherwise.
    const tar = tar1090ShapeFor(typeIcao);
    if (tar) return tar1090Icon(track, color, selected, emergency, tar.shape, tar.scaleFactor);

    const rot = track == null ? 0 : track;
    let stroke = selected ? '#fff' : '#000';
    let sw = selected ? 1.5 : 0.6;
    if (emergency) { stroke = '#ef4444'; sw = 2; }
    const size = GENERIC_ARROW_SIZE;
    const half = size / 2;
    const svg =
      `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="-14 -14 28 28">` +
        `<g transform="rotate(${rot})">` +
          `<path d="${GENERIC_ARROW_PATH}" fill="${color}" stroke="${stroke}" ` +
            `stroke-width="${sw}" stroke-linejoin="round"/>` +
        `</g>` +
      `</svg>`;
    return L.divIcon({
      html: svg,
      className: 'plane-icon',
      iconSize: [size, size],
      iconAnchor: [half, half],
    });
  }

  // Build the panel DOM once per aircraft, with placeholder children for
  // every field that changes on snapshot tick. Subsequent ticks call
  // updatePopupContent() to mutate those placeholders in place — this
  // leaves the .ac-photo slot untouched between ticks, so the aircraft
  // photograph doesn't flicker in and out every second the way it did
  // when we rebuilt everything from an HTML string each tick.
  function buildPopupContent(a, now, airports) {
    const root = document.createElement('div');
    root.className = 'ac-panel';
    root.innerHTML =
      `<div class="ac-photo" data-icao="${escapeHtml(a.icao)}"></div>` +
      `<div class="panel-head">` +
        `<div class="panel-title-row">` +
          `<span class="pop-flag"></span>` +
          `<b class="pop-callsign"></b>` +
          `<code class="pop-icao"></code>` +
          `<span class="pop-emergency"></span>` +
          `<span class="pop-ground" hidden>ON GROUND</span>` +
        `</div>` +
        `<div class="panel-subline pop-reg-line" hidden><span class="pop-reg"></span></div>` +
        `<div class="panel-subline pop-manuf-line" hidden><span class="pop-manuf"></span></div>` +
        `<div class="panel-subline pop-op-line" hidden><span class="pop-op"></span></div>` +
        `<div class="panel-badges">` +
          `<span class="pop-type-badge badge" hidden></span>` +
          `<span class="pop-cat-badge badge" hidden></span>` +
        `</div>` +
      `</div>` +
      `<div class="panel-route pop-route-line" hidden>` +
        `<div class="panel-mini-label">Route</div>` +
        `<div class="route-ticket">` +
          `<div class="route-end">` +
            `<div class="route-code pop-origin-code"></div>` +
            `<div class="route-name pop-origin-name"></div>` +
          `</div>` +
          `<div class="route-arrow">→</div>` +
          `<div class="route-end">` +
            `<div class="route-code pop-dest-code"></div>` +
            `<div class="route-name pop-dest-name"></div>` +
          `</div>` +
        `</div>` +
      `</div>` +
      `<div class="panel-meta">` +
        `<div class="metric"><div class="label">Altitude</div>` +
          `<div class="val pop-alt-wrap"><b class="pop-alt"></b>` +
          `<span class="pop-alt-trend"></span></div></div>` +
        `<div class="metric"><div class="label">Speed</div>` +
          `<div class="val pop-spd-wrap"><span class="pop-spd"></span>` +
          `<span class="pop-spd-trend"></span></div></div>` +
        `<div class="metric"><div class="label">Heading</div>` +
          `<div class="val"><span class="pop-hdg"></span>` +
          `<span class="pop-compass"></span></div></div>` +
        `<div class="metric"><div class="label">V.Rate</div>` +
          `<div class="val pop-vrate"></div></div>` +
        `<div class="metric"><div class="label">Squawk</div>` +
          `<div class="val pop-squawk"></div></div>` +
        `<div class="metric"><div class="label">Distance</div>` +
          `<div class="val pop-dist"></div></div>` +
        `<div class="metric"><div class="label">Latitude</div>` +
          `<div class="val pop-lat"></div></div>` +
        `<div class="metric"><div class="label">Longitude</div>` +
          `<div class="val pop-lon"></div></div>` +
        `<div class="metric"><div class="label">Age</div>` +
          `<div class="val pop-age"></div></div>` +
      `</div>` +
      `<div class="panel-profile">` +
        `<div class="panel-mini-label">Altitude / speed profile (last 5 min)</div>` +
        `<svg class="pop-alt-profile" viewBox="0 0 200 40" ` +
          `preserveAspectRatio="none" aria-hidden="true"></svg>` +
        `<svg class="pop-spd-profile" viewBox="0 0 200 24" ` +
          `preserveAspectRatio="none" aria-hidden="true"></svg>` +
      `</div>` +
      `<div class="panel-links">` +
        `<a class="pop-link-fa" target="_blank" rel="noopener">` +
          `<span>FlightAware</span>${LINK_ICON_SVG}</a>` +
        `<a class="pop-link-fr24" target="_blank" rel="noopener">` +
          `<span>Flightradar24</span>${LINK_ICON_SVG}</a>` +
        `<a class="pop-link-airnav" target="_blank" rel="noopener">` +
          `<span>AirNav Radar</span>${LINK_ICON_SVG}</a>` +
        `<a class="pop-link-ps" target="_blank" rel="noopener">` +
          `<span>Planespotters</span>${LINK_ICON_SVG}</a>` +
      `</div>` +
      `<div class="panel-stats">` +
        `<div class="stat"><span class="stat-label">Messages</span>` +
          `<span class="stat-val pop-msgs"></span></div>` +
        `<div class="stat"><span class="stat-label">Peak signal</span>` +
          `<span class="stat-val pop-signal"></span></div>` +
        `<div class="stat"><span class="stat-label">First seen</span>` +
          `<span class="stat-val pop-first-seen"></span></div>` +
      `</div>`;
    updatePopupContent(root, a, now, airports);
    return root;
  }

  function updatePopupContent(root, a, now, airports) {
    const q = (sel) => root.querySelector(sel);

    // Flag — img markup from flagcdn.com. Only rewrite innerHTML when
    // the ISO actually changes; otherwise the <img> is torn down and
    // re-created every snapshot tick, which makes the flag flash.
    const flagEl = q('.pop-flag');
    const iso = a.country_iso || '';
    if (flagEl.dataset.iso !== iso) {
      flagEl.innerHTML = iso ? flagIcon(iso) : '';
      flagEl.dataset.iso = iso;
    }
    flagEl.title = a.operator_country || a.country_iso || '';

    q('.pop-callsign').textContent = a.callsign || '—';
    q('.pop-icao').textContent = a.icao.toUpperCase();

    const emEl = q('.pop-emergency');
    if (a.emergency) {
      emEl.innerHTML =
        `<span class="emergency-label">EMERGENCY · ${escapeHtml(a.emergency)}</span>`;
    } else {
      emEl.textContent = '';
    }
    q('.pop-ground').hidden = !a.on_ground;

    // Registration line (tar1090-db's registration + aircraft_db's long
    // type name — usually "G-ABCD · Airbus A319-111").
    const regLine = q('.pop-reg-line');
    if (a.registration || a.type_long) {
      q('.pop-reg').textContent = [a.registration, a.type_long]
        .filter(Boolean).join(' · ');
      regLine.hidden = false;
    } else {
      regLine.hidden = true;
    }

    // Manufacturer line — adsbdb's registered manufacturer string, only
    // when it adds information that isn't already in type_long.
    const manufLine = q('.pop-manuf-line');
    const showManuf =
      a.manufacturer &&
      (!a.type_long ||
        !a.type_long.toLowerCase().includes(a.manufacturer.toLowerCase().split(' ')[0]));
    if (showManuf) {
      q('.pop-manuf').textContent = a.manufacturer;
      manufLine.hidden = false;
    } else {
      manufLine.hidden = true;
    }

    // Operator + country (full name; flag lives in the header row).
    const opLine = q('.pop-op-line');
    const opParts = [];
    if (a.operator) opParts.push(a.operator);
    if (a.operator_country) opParts.push(a.operator_country);
    if (opParts.length) {
      q('.pop-op').textContent = opParts.join(' · ');
      opLine.hidden = false;
    } else {
      opLine.hidden = true;
    }

    // Badges — ICAO type code + ADS-B category. Small pill-style chips
    // under the identity lines.
    const typeBadge = q('.pop-type-badge');
    if (a.type_icao) {
      typeBadge.textContent = a.type_icao;
      typeBadge.hidden = false;
    } else {
      typeBadge.hidden = true;
    }
    const catBadge = q('.pop-cat-badge');
    const catName = CATEGORY_NAMES[a.category];
    if (catName) {
      catBadge.textContent = catName;
      catBadge.hidden = false;
    } else {
      catBadge.hidden = true;
    }

    const routeLine = q('.pop-route-line');
    if (a.origin || a.destination) {
      const aports = airports || {};
      const originName = a.origin && aports[a.origin]?.name || '';
      const destName = a.destination && aports[a.destination]?.name || '';
      q('.pop-origin-code').textContent = a.origin || '—';
      q('.pop-dest-code').textContent = a.destination || '—';
      q('.pop-origin-name').textContent = originName;
      q('.pop-dest-name').textContent = destName;
      routeLine.hidden = false;
    } else {
      routeLine.hidden = true;
    }

    // Indicate altitude source: show (geo) when only geometric is known,
    // or tag baro when both are known and they disagree meaningfully.
    let altLabel = uconv('alt', a.altitude);
    if (a.altitude_baro == null && a.altitude_geo != null) {
      altLabel += ' <span class="alt-tag">geo</span>';
    } else if (
      a.altitude_baro != null && a.altitude_geo != null &&
      Math.abs(a.altitude_baro - a.altitude_geo) > 100
    ) {
      altLabel += ` <span class="alt-tag">baro; geo ${uconv('alt', a.altitude_geo)}</span>`;
    }
    const entry = aircraft.get(a.icao);
    const tAlt = trendInfo(entry, 'alt');
    const tSpd = trendInfo(entry, 'spd');

    q('.pop-alt').innerHTML = altLabel;
    q('.pop-alt-wrap').className = 'val pop-alt-wrap ' + tAlt.cls;
    q('.pop-alt-trend').innerHTML = tAlt.arrow;
    q('.pop-spd').textContent = uconv('spd', a.speed);
    q('.pop-spd-wrap').className = 'val pop-spd-wrap ' + tSpd.cls;
    q('.pop-spd-trend').innerHTML = tSpd.arrow;
    q('.pop-hdg').textContent = fmt(a.track, '°');
    q('.pop-compass').innerHTML = compassIcon(a.track);
    q('.pop-vrate').textContent = uconv('vrt', a.vrate);
    q('.pop-squawk').textContent = a.squawk || '—';
    q('.pop-dist').textContent = uconv('dst', a.distance_km);
    q('.pop-lat').textContent = a.lat != null ? a.lat.toFixed(4) + '°' : '—';
    q('.pop-lon').textContent = a.lon != null ? a.lon.toFixed(4) + '°' : '—';
    q('.pop-age').textContent = fmt(ageOf(a, now), 's', 1) + ' ago';

    // Bottom section: altitude + speed profiles, external links, stats.
    renderAltProfile(q('.pop-alt-profile'), a.trail);
    renderSpdProfile(q('.pop-spd-profile'), a.trail);

    const hexUpper = a.icao.toUpperCase();
    const hexLower = a.icao.toLowerCase();
    // Hex-keyed trackers work for any aircraft.
    q('.pop-link-fa').href = `https://flightaware.com/live/modes/${hexLower}/redirect`;
    q('.pop-link-ps').href = `https://www.planespotters.net/hex/${hexUpper}`;
    // FR24 and AirNav Radar deep-link by registration; hide the button
    // until we've got a tail (sparing users a dead-end 404).
    const fr24 = q('.pop-link-fr24');
    const airnav = q('.pop-link-airnav');
    if (a.registration) {
      const regLower = a.registration.toLowerCase();
      fr24.href = `https://www.flightradar24.com/data/aircraft/${regLower}`;
      airnav.href = `https://www.airnavradar.com/data/aircraft/${regLower}`;
      fr24.hidden = false;
      airnav.hidden = false;
    } else {
      fr24.hidden = true;
      airnav.hidden = true;
    }

    q('.pop-msgs').textContent = a.msg_count.toLocaleString();
    q('.pop-signal').textContent = signalLabel(a.signal_peak);
    q('.pop-first-seen').textContent = relativeAge(a.first_seen, now);
  }

  // Route string (e.g. "EGLL → KJFK") from snapshot fields. Returns '' when
  // the server hasn't enriched yet (adsbdb lookup still pending or the
  // feature is disabled). Each airport code that resolves to a known name
  // gets a `data-title` attribute; a delegated click/hover handler shows a
  // body-mounted tooltip so it escapes the sidebar row's overflow:hidden
  // and works on iPad Safari (where the native `title` attribute is inert).
  // OurAirports names and adsbdb codes are upstream data, so both go
  // through escapeHtml before interpolation.
  function routeLabel(a, airports) {
    if (!a.origin && !a.destination) return '';
    const code = (icao) => {
      if (!icao) return '?';
      const info = airports && airports[icao];
      const name = info && info.name ? info.name : null;
      if (!name) return `<span class="airport-code">${escapeHtml(icao)}</span>`;
      return (
        `<span class="airport-code" data-title="${escapeHtml(name)}">` +
        `${escapeHtml(icao)}</span>`
      );
    };
    return `${code(a.origin)} → ${code(a.destination)}`;
  }

  // What text to show as the permanent on-map label. Leaflet's bindTooltip
  // renders content as HTML, so escape — callsign comes from Mode S BDS 2,0.
  function labelText(a) {
    return escapeHtml(a.callsign || a.icao.toUpperCase());
  }

  function updateLabelFor(entry) {
    const text = labelText(entry.data);
    if (showLabels) {
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

  function applyLabelsVisibility() {
    for (const entry of aircraft.values()) updateLabelFor(entry);
    syncOverlay(labelsProxy, showLabels);
  }

  // Singleton floating tooltip for airport codes — lives in tooltip.js
  // since it has no dependencies on anything else here.
  initAirportTooltip();

  function applyTrailsVisibility() {
    for (const entry of aircraft.values()) {
      if (showTrails) {
        rebuildTrail(entry, entry.data.trail);
      } else {
        entry.trail.clearLayers();
        entry.trailFp = null;
      }
    }
    syncOverlay(trailsProxy, showTrails);
  }

  function setHoverHalo(icao) {
    if (hoverHalo) { map.removeLayer(hoverHalo); hoverHalo = null; }
    if (!icao) return;
    const entry = aircraft.get(icao);
    if (!entry) return;
    hoverHalo = L.circleMarker(entry.marker.getLatLng(), {
      radius: 22, color: '#5fa8ff', weight: 2, opacity: 0.9,
      fill: false, interactive: false,
    }).addTo(map);
  }

  function peekListItem(icao, on) {
    hoveredFromMapIcao = on ? icao : null;
    const el = document.querySelector(`.ac-item[data-icao="${icao}"]`);
    if (!el) return;
    el.classList.toggle('peek', on);
    if (on) el.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  }

  // Rebuild the per-aircraft trail as a chain of short segments, each
  // coloured by the altitude at that point. Using many small segments with
  // round lineJoin gives a smooth curved look without a splines library.
  function rebuildTrail(entry, points) {
    if (!points || points.length < 2) {
      if (entry.trailFp != null) { entry.trail.clearLayers(); entry.trailFp = null; }
      return;
    }
    // Skip rebuild if nothing changed (common between snapshot ticks when
    // the aircraft hasn't reported a new position). Comparing length alone
    // breaks once the server's trail deque is full and starts rotating —
    // same length, different contents — so fingerprint the endpoints too.
    const last = points[points.length - 1];
    const first = points[0];
    const fp = `${points.length}:${first[0]},${first[1]}:${last[0]},${last[1]}`;
    if (fp === entry.trailFp) return;
    entry.trailFp = fp;

    entry.trail.clearLayers();
    for (let i = 1; i < points.length; i++) {
      const p0 = points[i - 1];
      const p1 = points[i];
      // Colour by the later point's altitude (falls back to earlier point).
      const alt = p1[2] != null ? p1[2] : p0[2];
      const color = altColor(alt);
      // Fade older segments, but keep a high floor so the whole trail
      // stays legible against busy base tiles.
      const opacity = 0.65 + 0.3 * (i / points.length);
      L.polyline([[p0[0], p0[1]], [p1[0], p1[1]]], {
        renderer: trailsCanvas,
        color,
        weight: 3,
        opacity,
        lineCap: 'round',
        lineJoin: 'round',
        smoothFactor: 0,
        interactive: false,
      }).addTo(entry.trail);
    }
  }

  function renderReceiver(rx) {
    if (!rx || rx.lat == null || rx.lon == null) return;
    if (receiverLayer) return;  // draw once; position doesn't change mid-session
    buildRangeRings(rx);

    const group = L.layerGroup().addTo(map);
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
    receiverLayer = group;
  }

  function update(snap) {
    lastSnap = snap;
    lastSnapAt = Date.now();
    renderReceiver(snap.receiver);
    const seen = new Set();

    for (const a of snap.aircraft) {
      seen.add(a.icao);
      if (a.lat == null || a.lon == null) continue;

      const color = altColor(a.altitude);
      const isSelected = a.icao === selectedIcao;
      const icon = planeIcon(a.track, color, isSelected, !!a.emergency, a.type_icao);

      let entry = aircraft.get(a.icao);
      if (!entry) {
        const marker = L.marker([a.lat, a.lon], { icon }).addTo(map);
        const trail = L.layerGroup().addTo(map);
        marker.on('click', () => selectAircraft(a.icao));
        marker.on('mouseover', () => peekListItem(a.icao, true));
        marker.on('mouseout',  () => peekListItem(a.icao, false));
        entry = {
          marker, trail, label: null, data: a, trailFp: null,
          hist: { alt: [], spd: [], dst: [] },
        };
        aircraft.set(a.icao, entry);
      } else {
        entry.marker.setLatLng([a.lat, a.lon]);
        entry.marker.setIcon(icon);
      }
      if (hoveredFromListIcao === a.icao && hoverHalo) {
        hoverHalo.setLatLng([a.lat, a.lon]);
      }

      if (showTrails) {
        rebuildTrail(entry, a.trail);
      } else if (entry.trailFp != null) {
        entry.trail.clearLayers();
        entry.trailFp = null;
      }
      if (a.icao === selectedIcao && detailPanelContent) {
        updatePopupContent(detailPanelContent, a, snap.now, snap.airports);
      }

      entry.data = a;
      pushHistory(entry, a);
      updateLabelFor(entry);
    }

    // Keep the selected aircraft centred when "Follow" is on. Cheap pan
    // (no animation) so busy skies don't feel jumpy.
    if (followSelected && selectedIcao) {
      const entry = aircraft.get(selectedIcao);
      if (entry) panToFollowed(entry.marker.getLatLng(), { animate: false });
    }

    // Drop aircraft no longer reported
    for (const [icao, entry] of aircraft.entries()) {
      if (!seen.has(icao)) {
        if (hoveredFromListIcao === icao) { hoveredFromListIcao = null; setHoverHalo(null); }
        if (hoveredFromMapIcao  === icao) hoveredFromMapIcao  = null;
        if (selectedIcao === icao) closeDetailPanel();
        map.removeLayer(entry.marker);
        map.removeLayer(entry.trail);
        aircraft.delete(icao);
      }
    }

    watchlist.noticeAppearances(snap.aircraft);
    renderSidebar(snap);

    // Resolve any pending #icao= deep link once its aircraft appears.
    if (pendingDeepLinkIcao) {
      const hit = snap.aircraft.find(a => a.icao === pendingDeepLinkIcao);
      if (hit) {
        selectAircraft(pendingDeepLinkIcao);
        pendingDeepLinkIcao = null;
      }
    }

    // Auto-fit on first populated update
    if (firstUpdate && snap.positioned > 0) {
      const bounds = L.latLngBounds(
        snap.aircraft.filter(a => a.lat != null).map(a => [a.lat, a.lon])
      );
      if (bounds.isValid()) map.fitBounds(bounds.pad(0.2), { maxZoom: 7 });
      firstUpdate = false;
    }
  }

  function renderSortBar() {
    document.querySelectorAll('.sort-chip').forEach(el => {
      const active = el.dataset.key === sortKey;
      el.classList.toggle('active', active);
      el.querySelector('.arrow').textContent = active ? (sortDir === 1 ? '↑' : '↓') : '';
    });
  }

  // Tiny sparkline showing count-over-time in the header. ~1 minute of
  // history at 1Hz snapshot rate.
  const countHistory = [];
  const COUNT_HISTORY_LEN = 60;

  function renderSparkline() {
    const el = document.getElementById('sparkline');
    if (countHistory.length < 2) { el.innerHTML = ''; return; }
    const max = Math.max(1, ...countHistory);
    const w = 60, h = 12;
    const step = w / Math.max(1, COUNT_HISTORY_LEN - 1);
    // Align to the right so the newest sample is always at x = w.
    const offset = w - (countHistory.length - 1) * step;
    const pts = countHistory.map((c, i) => {
      const x = offset + i * step;
      const y = h - (c / max) * (h - 2) - 1;
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    }).join(' ');
    el.innerHTML = `<svg width="${w}" height="${h}" viewBox="0 0 ${w} ${h}"><polyline points="${pts}" fill="none" stroke="currentColor" stroke-width="1" stroke-linejoin="round" stroke-linecap="round" opacity="0.75"/></svg>`;
  }

  // Messages/sec from successive snapshots (server ships cumulative frame
  // counter, we diff here). Needs two samples before anything renders.
  let prevFrames = null;
  let prevFramesAt = null;

  function updateMsgRate(snap) {
    const el = document.getElementById('msg-rate');
    if (snap.frames == null || snap.now == null) { el.textContent = ''; return; }
    if (prevFrames != null && snap.now > prevFramesAt) {
      const rate = (snap.frames - prevFrames) / (snap.now - prevFramesAt);
      el.textContent = `${rate.toFixed(rate < 10 ? 1 : 0)} msg/s`;
    }
    prevFrames = snap.frames;
    prevFramesAt = snap.now;
  }

  function renderSidebar(snap) {
    // Push the current count onto the sparkline buffer before drawing.
    countHistory.push(snap.count);
    if (countHistory.length > COUNT_HISTORY_LEN) countHistory.shift();
    renderSparkline();
    updateMsgRate(snap);
    const status = document.getElementById('status-text');
    status.textContent =
      `${snap.count} aircraft · ${snap.positioned} positioned`;
    const site = snap.site_name || '';
    document.getElementById('site-name').textContent = site;
    document.title = site
      ? `Flightjar — ${site} (${snap.count})`
      : `Flightjar (${snap.count})`;

    // Filter by search text (callsign, ICAO24, or registration substring,
    // case-insensitive).
    const q = searchFilter;
    const filtered = q
      ? snap.aircraft.filter(a =>
          (a.callsign || '').toLowerCase().includes(q) ||
          a.icao.toLowerCase().includes(q) ||
          (a.registration || '').toLowerCase().includes(q))
      : snap.aircraft;

    // Sort a copy so we don't mutate the snapshot used elsewhere.
    const rows = filtered.slice().sort((a, b) => {
      const av = sortValue(a, sortKey, snap.now);
      const bv = sortValue(b, sortKey, snap.now);
      // Null always sinks to the bottom regardless of direction.
      if (av == null && bv == null) return a.icao.localeCompare(b.icao);
      if (av == null) return 1;
      if (bv == null) return -1;
      const cmp = typeof av === 'string' ? av.localeCompare(bv) : av - bv;
      return cmp * sortDir || a.icao.localeCompare(b.icao);
    });

    const list = document.getElementById('ac-list');
    if (rows.length === 0) {
      const msg = q
        ? 'No matches for this search.'
        : snap.count === 0
          ? 'Waiting for aircraft…'
          : 'No aircraft have a callsign or position yet.';
      list.innerHTML = `<div class="ac-empty">${msg}</div>`;
      return;
    }
    list.innerHTML = rows.map(a => {
      const classes = [
        'ac-item',
        a.icao === selectedIcao ? 'selected' : '',
        a.emergency ? 'emergency' : '',
        watchlist.has(a.icao) ? 'watched' : '',
      ].filter(Boolean).join(' ');
      const emergencyBadge = a.emergency
        ? `<span class="emergency-label">${escapeHtml(a.emergency)}</span>`
        : '';
      const subtitle = [a.registration, a.type_icao]
        .filter(Boolean).map(escapeHtml).join(' · ');
      const route = routeLabel(a, snap.airports);
      const entry = aircraft.get(a.icao);
      const tAlt = trendInfo(entry, 'alt');
      const tSpd = trendInfo(entry, 'spd');
      const tDst = trendInfo(entry, 'dst');
      const callsign = a.callsign ? escapeHtml(a.callsign) : '— — — —';
      const icao = escapeHtml(a.icao);
      const flag = flagIcon(a.country_iso);
      const flagTag = flag
        ? `<span class="flag" title="${escapeHtml(a.operator_country || a.country_iso)}">${flag}</span> `
        : '';
      return `
      <div class="${classes}" data-icao="${icao}">
        <span class="age">${fmt(ageOf(a, snap.now), 's', 1)}</span>
        <div class="row1">
          <span class="cs">${flagTag}${callsign} ${emergencyBadge}</span>
          <span class="icao">${subtitle || icao.toUpperCase()}</span>
        </div>
        ${route ? `<div class="route-row">${route}</div>` : ''}
        <div class="meta">
          <div class="metric"><div class="label">Alt</div><div class="val ${tAlt.cls}">${uconv('alt', a.altitude)}${tAlt.arrow}</div></div>
          <div class="metric"><div class="label">Spd</div><div class="val ${tSpd.cls}">${uconv('spd', a.speed)}${tSpd.arrow}</div></div>
          <div class="metric"><div class="label">Hdg</div><div class="val">${fmt(a.track, '°')}${compassIcon(a.track)}</div></div>
          <div class="metric"><div class="label">Dist</div><div class="val ${tDst.cls}">${uconv('dst', a.distance_km)}${tDst.arrow}</div></div>
        </div>
      </div>
    `;
    }).join('');

    list.querySelectorAll('.ac-item').forEach(el => {
      el.addEventListener('click', () => selectAircraft(el.dataset.icao));
      el.addEventListener('mouseenter', () => {
        hoveredFromListIcao = el.dataset.icao;
        setHoverHalo(hoveredFromListIcao);
      });
      el.addEventListener('mouseleave', () => {
        hoveredFromListIcao = null;
        setHoverHalo(null);
      });
    });

    // Preserve map-hover highlight across the snapshot-driven list rebuild.
    if (hoveredFromMapIcao) {
      const el = list.querySelector(`.ac-item[data-icao="${hoveredFromMapIcao}"]`);
      if (el) el.classList.add('peek');
    }
  }

  // ---- detail panel ----

  const detailPanelEl = document.getElementById('detail-panel');
  const detailContentHost = document.getElementById('detail-content');
  const detailWatchBtn = document.getElementById('detail-watch');
  const appEl = document.getElementById('app');
  let detailPanelContent = null;

  const watchlist = createWatchlist();

  function applyWatchStateToPanel() {
    if (!selectedIcao) {
      detailWatchBtn.classList.remove('watched');
      return;
    }
    const on = watchlist.has(selectedIcao);
    detailWatchBtn.classList.toggle('watched', on);
    detailWatchBtn.setAttribute(
      'aria-label', on ? 'Remove from watchlist' : 'Add to watchlist',
    );
    detailWatchBtn.title = on
      ? 'Remove from watchlist'
      : 'Add to watchlist (notify when this aircraft reappears)';
  }

  detailWatchBtn.addEventListener('click', async () => {
    if (!selectedIcao) return;
    const nowWatching = watchlist.toggle(selectedIcao);
    // Best-effort: first time the user stars anything, ask for
    // notification permission so we can actually announce reappearances.
    if (nowWatching) await watchlist.setNotifyEnabled(true);
    applyWatchStateToPanel();
    if (lastSnap) renderSidebar(lastSnap);
  });

  // Cache of adsbdb aircraft records by ICAO24 — the server already caches
  // for 30 days, but this client-side dict spares a network round-trip
  // when the user re-selects an aircraft they've looked at before.
  const aircraftInfoCache = new Map();

  // Pan the map so `latlng` sits in the middle of the portion of the map
  // NOT covered by the detail panel. On mobile the panel overlays the
  // whole screen so this is equivalent to panTo(). On desktop we shift
  // right by half the panel's occluded width, trading map-centre for
  // visible-area-centre.
  function panToFollowed(latlng, opts) {
    if (!detailPanelEl.classList.contains('open')) {
      map.panTo(latlng, opts);
      return;
    }
    const panelRect = detailPanelEl.getBoundingClientRect();
    const mapRect = map.getContainer().getBoundingClientRect();
    // Amount of the map's left edge hidden by the panel. max(0) protects
    // against the mobile overlay case where the panel extends past the
    // map in both directions (still the panel dominates — the whole map
    // is obscured — so we bail early there).
    if (panelRect.right >= mapRect.right && panelRect.left <= mapRect.left) {
      map.panTo(latlng, opts);
      return;
    }
    const obscured = Math.max(0, Math.min(panelRect.right, mapRect.right) - mapRect.left);
    const offsetX = obscured / 2;
    const planePt = map.latLngToContainerPoint(latlng);
    const shifted = map.containerPointToLatLng(L.point(planePt.x - offsetX, planePt.y));
    map.panTo(shifted, opts);
  }

  // Open the detail panel for an aircraft. Rebuilds the inner content DOM
  // from scratch (so the previous aircraft's photo slot is reset) and then
  // lets snapshot-tick updates mutate in place via updatePopupContent.
  function openDetailPanel(icao) {
    const entry = aircraft.get(icao);
    const a = entry?.data;
    if (!a) return;
    selectedIcao = icao;
    writeDeepLink(icao);
    detailContentHost.innerHTML = '';
    detailPanelContent = buildPopupContent(a, lastSnap?.now, lastSnap?.airports);
    detailContentHost.appendChild(detailPanelContent);
    detailPanelEl.classList.add('open');
    appEl.classList.add('panel-open');
    entry.marker.setIcon(
      planeIcon(a.track, altColor(a.altitude), true, !!a.emergency, a.type_icao),
    );
    document.querySelectorAll('.ac-item').forEach(el => {
      el.classList.toggle('selected', el.dataset.icao === icao);
    });
    fillAircraftPhoto(icao);
    applyWatchStateToPanel();
    // Follow the selected aircraft automatically while its panel is open.
    // The user can still toggle Follow off manually to regain free panning.
    followSelected = true;
    applyFollowState();
  }

  function closeDetailPanel() {
    const icao = selectedIcao;
    if (!icao) return;
    selectedIcao = null;
    writeDeepLink(null);
    detailPanelEl.classList.remove('open');
    appEl.classList.remove('panel-open');
    // Let the slide-out finish before clearing the DOM so the photo +
    // fields don't flash blank during the transition.
    const prevContent = detailPanelContent;
    detailPanelContent = null;
    setTimeout(() => {
      if (detailPanelContent === null && detailContentHost.firstChild === prevContent) {
        detailContentHost.innerHTML = '';
      }
    }, 220);
    const entry = aircraft.get(icao);
    if (entry) {
      const a = entry.data;
      entry.marker.setIcon(
        planeIcon(a.track, altColor(a.altitude), false, !!a.emergency, a.type_icao),
      );
    }
    document.querySelectorAll('.ac-item').forEach(el => {
      if (el.dataset.icao === icao) el.classList.remove('selected');
    });
    followSelected = false;
    applyFollowState();
  }

  document.getElementById('detail-close').addEventListener('click', closeDetailPanel);
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && selectedIcao) closeDetailPanel();
  });
  // Clicking empty map background closes the panel. Leaflet only fires the
  // map 'click' event for background clicks — marker clicks don't propagate
  // — so this is a clean "click-away" gesture.
  map.on('click', () => {
    if (selectedIcao) closeDetailPanel();
  });
  // Dragging the map manually disables follow. 'dragstart' is user-only —
  // programmatic panTo() from the follow tick doesn't fire it — so this
  // cleanly distinguishes "user took control" from "we're auto-panning".
  map.on('dragstart', () => {
    if (followSelected) {
      followSelected = false;
      applyFollowState();
    }
  });

  // Fetch the adsbdb aircraft record and drop its photo into the panel's
  // .ac-photo slot. The slot renders a shimmering skeleton by default; on
  // success we swap in an <img>, on miss we add .no-photo to collapse it.
  // A module-scoped cache makes re-selects instant.
  async function fillAircraftPhoto(icao) {
    const slotSelector = `.ac-photo[data-icao="${icao}"]`;
    const findSlot = () => document.querySelector(slotSelector);
    if (!findSlot()) return;

    let info = aircraftInfoCache.get(icao);
    if (info === undefined) {
      try {
        const r = await fetch(`/api/aircraft/${encodeURIComponent(icao)}`);
        if (!r.ok) throw new Error(`HTTP ${r.status}`);
        info = await r.json();
      } catch (e) {
        console.warn('aircraft lookup failed', icao, e);
        info = null;
      }
      aircraftInfoCache.set(icao, info);
    }

    const slot = findSlot();
    if (!slot) return;
    if (!info || !info.photo_thumbnail) {
      slot.classList.add('no-photo');
      slot.innerHTML =
        `<div class="no-photo-inner">` +
          `<svg width="22" height="22" viewBox="0 0 24 24" fill="none" ` +
            `stroke="currentColor" stroke-width="1.5" stroke-linecap="round" ` +
            `stroke-linejoin="round" aria-hidden="true">` +
            `<path d="M3 7h4l1.5-2h7L17 7h4v12H3z"/>` +
            `<circle cx="12" cy="13" r="3"/>` +
            `<line x1="3" y1="3" x2="21" y2="21"/>` +
          `</svg>` +
          `<span>No photo available</span>` +
        `</div>`;
      return;
    }
    const thumb = escapeHtml(info.photo_thumbnail);
    const full = info.photo_url ? escapeHtml(info.photo_url) : thumb;
    slot.innerHTML =
      `<a href="${full}" target="_blank" rel="noopener">` +
      `<img src="${thumb}" alt="" loading="lazy"></a>`;
  }

  // Public entry point for marker clicks + sidebar clicks + deep links.
  // Pans the map to the aircraft (if it has a position yet) and shows
  // the detail panel.
  function selectAircraft(icao) {
    const entry = aircraft.get(icao);
    // Open the panel first so panToFollowed sees it and can shift the
    // target into the visible strip of the map.
    openDetailPanel(icao);
    if (entry) panToFollowed(entry.marker.getLatLng());
  }

  document.querySelectorAll('.sort-chip').forEach(el => {
    el.addEventListener('click', () => {
      const key = el.dataset.key;
      if (key === sortKey) {
        sortDir = -sortDir;
      } else {
        sortKey = key;
        sortDir = defaultDir[key];
      }
      renderSortBar();
      if (lastSnap) renderSidebar(lastSnap);
    });
  });
  renderSortBar();

  function renderUnitSwitch() {
    // Scoped to #unit-switch so the view toggles (which share .unit-btn for
    // styling) aren't dragged into the unit-switch's active-state bookkeeping.
    document.querySelectorAll('#unit-switch .unit-btn').forEach(el => {
      el.classList.toggle('active', el.dataset.unit === getUnitSystem());
    });
  }
  document.querySelectorAll('#unit-switch .unit-btn').forEach(el => {
    el.addEventListener('click', () => {
      setUnitSystem(el.dataset.unit);
      try { localStorage.setItem('flightjar.units', getUnitSystem()); } catch (_) {}
      renderUnitSwitch();
      renderAltLegend();
      if (lastSnap) {
        renderSidebar(lastSnap);
        // Refresh the open detail panel so its units update immediately.
        if (selectedIcao && detailPanelContent) {
          const entry = aircraft.get(selectedIcao);
          if (entry) {
            updatePopupContent(detailPanelContent, entry.data, lastSnap.now, lastSnap.airports);
          }
        }
      }
    });
  });
  renderUnitSwitch();

  function setLabels(value) {
    showLabels = value;
    try { localStorage.setItem('flightjar.labels', showLabels ? '1' : '0'); } catch (_) {}
    applyLabelsVisibility();
  }
  applyLabelsVisibility();

  // ---- About dialog ----
  const aboutDialog = document.getElementById('about-dialog');
  document.getElementById('about-btn').addEventListener('click', () => {
    if (typeof aboutDialog.showModal === 'function') {
      aboutDialog.showModal();
    } else {
      aboutDialog.setAttribute('open', '');  // graceful fallback
    }
  });
  // Clicking the backdrop (i.e. outside the dialog content) also closes it.
  aboutDialog.addEventListener('click', (e) => {
    const r = aboutDialog.getBoundingClientRect();
    const inside = e.clientX >= r.left && e.clientX <= r.right
                && e.clientY >= r.top && e.clientY <= r.bottom;
    if (!inside) aboutDialog.close();
  });

  function setTrails(value) {
    showTrails = value;
    try { localStorage.setItem('flightjar.trails', showTrails ? '1' : '0'); } catch (_) {}
    applyTrailsVisibility();
  }
  applyTrailsVisibility();

  // ---- Follow + Compact map controls ----
  // Both are single-action icon buttons stacked near the layers control
  // in the top-right, rather than text chips in the sidebar header.
  function makeIconControl({ className, title, pathD, onClick }) {
    return L.Control.extend({
      options: { position: 'topright' },
      onAdd() {
        const a = L.DomUtil.create('a', 'leaflet-bar leaflet-control ' + className);
        a.href = '#'; a.title = title;
        a.setAttribute('role', 'button');
        a.setAttribute('aria-label', title);
        a.innerHTML =
          `<svg viewBox="0 0 20 20" width="16" height="16" aria-hidden="true" ` +
            `fill="none" stroke="currentColor" stroke-width="1.8" ` +
            `stroke-linecap="round" stroke-linejoin="round">${pathD}</svg>`;
        L.DomEvent
          .on(a, 'click', (e) => {
            L.DomEvent.preventDefault(e);
            L.DomEvent.stopPropagation(e);
            onClick();
          })
          .on(a, 'dblclick', L.DomEvent.stopPropagation);
        return a;
      },
    });
  }

  function applyFollowState() {
    document.querySelectorAll('.follow-control').forEach(
      el => el.classList.toggle('active', followSelected),
    );
    // Snap the map to the selected aircraft the moment Follow is switched on.
    if (followSelected && selectedIcao) {
      const entry = aircraft.get(selectedIcao);
      if (entry) panToFollowed(entry.marker.getLatLng(), { animate: true });
    }
  }
  function setFollow(value) {
    followSelected = value;
    try { localStorage.setItem('flightjar.follow', followSelected ? '1' : '0'); } catch (_) {}
    applyFollowState();
  }
  // Top-down plane silhouette — unambiguously "track an aircraft" and
  // visually distinct from the Home control's crosshair.
  const FollowControl = makeIconControl({
    className: 'follow-control',
    title: 'Follow selected aircraft',
    pathD:
      `<path d="M10,2 L11.5,9 L18,11 L18,12.5 L11.5,10.5 L11.5,15 ` +
      `L13,17 L13,17.5 L7,17.5 L7,17 L8.5,15 L8.5,10.5 L2,12.5 ` +
      `L2,11 L8.5,9 Z" fill="currentColor" stroke-linejoin="round"/>`,
    onClick: () => setFollow(!followSelected),
  });
  map.addControl(new FollowControl());
  applyFollowState();

  function applyCompactMode() {
    document.body.classList.toggle('compact-mode', compactMode);
    document.querySelectorAll('.compact-control').forEach(
      el => el.classList.toggle('active', compactMode),
    );
    // The map container's size just changed — Leaflet needs a nudge.
    // pan: false so the map stays anchored to whatever the user was
    // looking at instead of drifting to preserve the old geographic centre.
    map.invalidateSize({ pan: false });
  }
  function setCompact(value) {
    compactMode = value;
    try { localStorage.setItem('flightjar.compact', compactMode ? '1' : '0'); } catch (_) {}
    applyCompactMode();
  }
  // Double-chevron glyph suggesting "collapse sidebar into the map".
  // Top-left so it's near the sidebar edge it'll hide / reveal.
  const CompactControl = makeIconControl({
    className: 'compact-control',
    title: 'Hide the sidebar (C)',
    pathD:
      `<polyline points="12 4 6 10 12 16"/>` +
      `<polyline points="17 4 11 10 17 16"/>`,
    onClick: () => setCompact(!compactMode),
  });
  const compactCtl = new CompactControl();
  compactCtl.options.position = 'topleft';
  map.addControl(compactCtl);
  document.getElementById('sidebar-restore').addEventListener('click', () => setCompact(false));
  applyCompactMode();

  // ---- airports overlay ----
  let airportsFetchPending = null;
  function refreshAirports() {
    if (!showAirports) { airportsLayer.clearLayers(); return; }
    const b = map.getBounds();
    const url = `/api/airports?min_lat=${b.getSouth()}&min_lon=${b.getWest()}`
              + `&max_lat=${b.getNorth()}&max_lon=${b.getEast()}&limit=2000`;
    if (airportsFetchPending) airportsFetchPending.abort?.();
    const ctrl = new AbortController();
    airportsFetchPending = ctrl;
    fetch(url, { signal: ctrl.signal })
      .then(r => r.ok ? r.json() : [])
      .then(rows => {
        if (!showAirports) return;
        airportsLayer.clearLayers();
        for (const a of rows) {
          const m = L.circleMarker([a.lat, a.lon], {
            renderer: airportsCanvas,
            radius: a.type === 'large_airport' ? 4 : a.type === 'medium_airport' ? 3 : 2,
            color: '#0e1116', weight: 1,
            fillColor: '#fbbf24', fillOpacity: 0.9,
          });
          m.bindTooltip(`${escapeHtml(a.name)} (${escapeHtml(a.icao)})`, { direction: 'top', sticky: true });
          m.addTo(airportsLayer);
        }
      })
      .catch((e) => { if (e.name !== 'AbortError') console.warn('airports fetch', e); });
  }

  let airportsDebounce = null;
  function scheduleAirportRefresh() {
    clearTimeout(airportsDebounce);
    airportsDebounce = setTimeout(refreshAirports, 250);
  }
  map.on('moveend', () => { if (showAirports) scheduleAirportRefresh(); });

  function applyAirportsToggle() {
    if (showAirports) {
      if (!map.hasLayer(airportsLayer)) airportsLayer.addTo(map);
      refreshAirports();
    } else {
      map.removeLayer(airportsLayer);
      airportsLayer.clearLayers();
    }
    syncOverlay(airportsProxy, showAirports);
  }
  function setAirports(value) {
    showAirports = value;
    try { localStorage.setItem('flightjar.airports', showAirports ? '1' : '0'); } catch (_) {}
    applyAirportsToggle();
  }
  applyAirportsToggle();

  // ---- polar coverage overlay ----
  // Draws the receiver's observed max-range polygon, filled from
  // /api/coverage. The backend keeps max-distance-per-bearing on disk
  // so this renders instantly from a cold cache (the first load for a
  // fresh install is a mostly-empty shape that fills in over time).
  const coverageLayer = L.layerGroup();
  let showCoverage = localStorage.getItem('flightjar.coverage') === '1';
  let coveragePoly = null;
  let coverageFetchInFlight = false;

  // Great-circle destination from (lat,lon) after travelling `distKm` km
  // on initial bearing `bearingDeg`. Used to draw the coverage polygon
  // as a set of end-points rather than shipping lat/lon from the server.
  function destinationPoint(lat, lon, bearingDeg, distKm) {
    const R = 6371;
    const phi1 = lat * Math.PI / 180;
    const lam1 = lon * Math.PI / 180;
    const theta = bearingDeg * Math.PI / 180;
    const d = distKm / R;
    const phi2 = Math.asin(
      Math.sin(phi1) * Math.cos(d) + Math.cos(phi1) * Math.sin(d) * Math.cos(theta),
    );
    const lam2 = lam1 + Math.atan2(
      Math.sin(theta) * Math.sin(d) * Math.cos(phi1),
      Math.cos(d) - Math.sin(phi1) * Math.sin(phi2),
    );
    return [phi2 * 180 / Math.PI, ((lam2 * 180 / Math.PI) + 540) % 360 - 180];
  }

  async function refreshCoverage() {
    if (coverageFetchInFlight) return;
    coverageFetchInFlight = true;
    try {
      const r = await fetch('/api/coverage');
      if (!r.ok) throw new Error(`HTTP ${r.status}`);
      const data = await r.json();
      coverageLayer.clearLayers();
      coveragePoly = null;
      const rx = data.receiver;
      if (!rx || rx.lat == null || rx.lon == null) return;
      if (!data.bearings || data.bearings.length < 3) return;
      // Sort bearings so the polygon winds cleanly.
      const sorted = [...data.bearings].sort((a, b) => a.angle - b.angle);
      const pts = sorted.map(b => destinationPoint(rx.lat, rx.lon, b.angle, b.dist_km));
      coveragePoly = L.polygon(pts, {
        color: '#5fa8ff', weight: 1.5, opacity: 0.75,
        fillColor: '#5fa8ff', fillOpacity: 0.08,
        interactive: false,
      });
      coveragePoly.addTo(coverageLayer);
    } catch (e) {
      console.warn('coverage fetch failed', e);
    } finally {
      coverageFetchInFlight = false;
    }
  }

  function applyCoverageToggle() {
    if (showCoverage) {
      if (!map.hasLayer(coverageLayer)) coverageLayer.addTo(map);
      refreshCoverage();
    } else {
      map.removeLayer(coverageLayer);
      coverageLayer.clearLayers();
      coveragePoly = null;
    }
    syncOverlay(coverageProxy, showCoverage);
  }
  function setCoverage(value) {
    showCoverage = value;
    try { localStorage.setItem('flightjar.coverage', showCoverage ? '1' : '0'); } catch (_) {}
    applyCoverageToggle();
  }
  applyCoverageToggle();
  // Refresh the polygon every 60s while visible so newly-seen sectors fill in.
  setInterval(() => { if (showCoverage) refreshCoverage(); }, 60_000);

  // ---- "Home" map control: re-centre on receiver, preserve zoom ----
  function goHome() {
    const rx = lastSnap?.receiver;
    if (!rx || rx.lat == null || rx.lon == null) return;
    map.panTo([rx.lat, rx.lon]);
  }

  const HomeControl = L.Control.extend({
    options: { position: 'bottomright' },
    onAdd() {
      const a = L.DomUtil.create('a', 'leaflet-bar leaflet-control home-control');
      a.href = '#';
      a.title = 'Re-centre on receiver (H)';
      a.setAttribute('role', 'button');
      a.setAttribute('aria-label', 'Re-centre on receiver');
      // Classic "locate me" crosshair in the accent blue. currentColor
      // lets a :hover rule recolour the icon without duplicating SVG.
      a.innerHTML = (
        '<svg viewBox="0 0 20 20" width="20" height="20" aria-hidden="true">' +
          '<circle cx="10" cy="10" r="6" fill="none" stroke="currentColor" stroke-width="1.6"/>' +
          '<circle cx="10" cy="10" r="2.2" fill="currentColor"/>' +
          '<line x1="10" y1="1" x2="10" y2="4"  stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>' +
          '<line x1="10" y1="16" x2="10" y2="19" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>' +
          '<line x1="1" y1="10" x2="4" y2="10"  stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>' +
          '<line x1="16" y1="10" x2="19" y2="10" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>' +
        '</svg>'
      );
      L.DomEvent
        .on(a, 'click', (e) => { L.DomEvent.preventDefault(e); L.DomEvent.stopPropagation(e); goHome(); })
        .on(a, 'dblclick', L.DomEvent.stopPropagation);
      return a;
    },
  });
  map.addControl(new HomeControl());

  // ---- collapsible filters panel (search + sort) ----
  // Default collapsed on narrow viewports so the list gets more room on
  // a phone. Desktop (>600px) starts expanded.
  const narrowMQ = window.matchMedia('(max-width: 600px)');
  if (narrowMQ.matches) document.body.classList.add('filters-collapsed');
  const filtersToggle = document.getElementById('filters-toggle');
  function setFiltersCollapsed(value) {
    document.body.classList.toggle('filters-collapsed', value);
  }
  filtersToggle.addEventListener('click', () => {
    setFiltersCollapsed(!document.body.classList.contains('filters-collapsed'));
  });

  // ---- sidebar search ----
  const searchInput = document.getElementById('search');
  searchInput.addEventListener('input', () => {
    searchFilter = searchInput.value.trim().toLowerCase();
    if (lastSnap) renderSidebar(lastSnap);
  });

  // ---- URL deep link (#icao=XXXXXX) ----
  function readDeepLink() {
    const m = /[#&]icao=([0-9a-fA-F]{6})/.exec(location.hash);
    return m ? m[1].toLowerCase() : null;
  }
  function writeDeepLink(icao) {
    const target = icao ? `#icao=${icao.toUpperCase()}` : '';
    if (location.hash !== target) history.replaceState(null, '', location.pathname + location.search + target);
  }
  pendingDeepLinkIcao = readDeepLink();
  window.addEventListener('hashchange', () => {
    const icao = readDeepLink();
    if (icao && icao !== selectedIcao) selectAircraft(icao);
  });

  // ---- keyboard shortcuts ----
  document.addEventListener('keydown', (e) => {
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    const tag = (e.target && e.target.tagName) || '';
    const inField = tag === 'INPUT' || tag === 'TEXTAREA' || e.target?.isContentEditable;
    if (e.key === '/' && !inField) {
      e.preventDefault();
      // Expand the filters panel if it's collapsed (mobile default) so the
      // search input is actually visible before we focus it.
      setFiltersCollapsed(false);
      searchInput.focus();
      searchInput.select();
      return;
    }
    if (e.key === 'Escape') {
      if (inField) { searchInput.blur(); return; }
      map.closePopup();
      selectedIcao = null;
      writeDeepLink(null);
      document.querySelectorAll('.ac-item').forEach(el => el.classList.remove('selected'));
      return;
    }
    if (inField) return;
    if (e.key === 'l' || e.key === 'L') {
      setLabels(!showLabels);
    } else if (e.key === 't' || e.key === 'T') {
      setTrails(!showTrails);
    } else if (e.key === 'c' || e.key === 'C') {
      setCompact(!compactMode);
    } else if (e.key === 'a' || e.key === 'A') {
      setAirports(!showAirports);
    } else if (e.key === 'h' || e.key === 'H') {
      goHome();
    } else if (e.key === 'f' || e.key === 'F') {
      const pts = [];
      for (const entry of aircraft.values()) pts.push(entry.marker.getLatLng());
      if (pts.length) map.fitBounds(L.latLngBounds(pts).pad(0.2), { maxZoom: 10 });
    } else if (e.key === 'u' || e.key === 'U') {
      const order = ['metric', 'imperial', 'nautical'];
      const next = order[(order.indexOf(getUnitSystem()) + 1) % order.length];
      document.querySelector(`.unit-btn[data-unit="${next}"]`)?.click();
    }
  });

  // ---- heartbeat ----
  // Only shown when the feed is stalled — amber after 5s since last snapshot,
  // red after 15s. When fresh, the element stays empty (and is hidden via
  // the :empty CSS rule) so the header isn't cluttered during normal ops.
  const hb = document.getElementById('heartbeat');
  setInterval(() => {
    if (!lastSnapAt) { hb.textContent = ''; return; }
    const secs = Math.round((Date.now() - lastSnapAt) / 1000);
    if (secs < 5) {
      hb.textContent = '';
      hb.classList.remove('stale', 'dead');
      return;
    }
    hb.textContent = `· ${secs}s ago`;
    hb.classList.toggle('stale', secs < 15);
    hb.classList.toggle('dead', secs >= 15);
  }, 1000);

  // ---- WebSocket ----
  let ws;
  let reconnectTimer = null;
  function setStatus(state, text) {
    const s = document.getElementById('status');
    s.classList.remove('live', 'dead');
    if (state) s.classList.add(state);
    if (text) document.getElementById('status-text').textContent = text;
  }
  function connect() {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    ws = new WebSocket(`${proto}//${location.host}/ws`);
    ws.onopen = () => setStatus('live', 'Connected');
    ws.onmessage = (ev) => {
      try { update(JSON.parse(ev.data)); }
      catch (e) { console.error('bad snapshot', e); }
    };
    ws.onclose = () => {
      setStatus('dead', 'Disconnected, retrying…');
      clearTimeout(reconnectTimer);
      reconnectTimer = setTimeout(connect, 2000);
    };
    ws.onerror = () => ws.close();
  }
  connect();
})();
