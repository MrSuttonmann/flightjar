import { ageOf, compassIcon, escapeHtml, fmt } from './format.js';
import { UNIT_SYSTEMS, getUnitSystem, setUnitSystem, uconv } from './units.js';
import { ALT_STOPS, altColor } from './altitude.js';
import { HIST_LEN, TREND_THRESHOLDS, pushHistory, trendInfo } from './trend.js';

(() => {
  const map = L.map('map', { worldCopyJump: true, zoomControl: false })
    .setView([51.5, -0.1], 6);
  L.control.zoom({ position: 'bottomleft' }).addTo(map);

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
  const overlays = { 'Range rings': rangeRings };
  L.control.layers(baseLayers, overlays, { position: 'topright' }).addTo(map);

  map.on('baselayerchange', (e) => {
    try { localStorage.setItem('flightjar.basemap', e.name); } catch (_) {}
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

  // Build the popup DOM once per aircraft, with placeholder children for
  // every field that changes on snapshot tick. Subsequent ticks call
  // updatePopupContent() to mutate those placeholders in place — this
  // leaves the .ac-photo slot untouched between ticks, so the aircraft
  // photograph doesn't flicker in and out every second the way it did
  // when we rebuilt the whole popup from an HTML string.
  function buildPopupContent(a, now, airports) {
    const root = document.createElement('div');
    root.className = 'ac-popup';
    root.innerHTML =
      `<div class="ac-photo" data-icao="${escapeHtml(a.icao)}"></div>` +
      `<div class="pop-head">` +
        `<b class="pop-callsign"></b> <code class="pop-icao"></code> ` +
        `<span class="pop-emergency"></span>` +
      `</div>` +
      `<div class="pop-reg-line" hidden><span class="ac-info pop-reg"></span></div>` +
      `<div class="pop-route-line" hidden><span class="ac-info pop-route"></span></div>` +
      `<div class="pop-row">Alt: <span class="pop-alt-wrap"><b class="pop-alt"></b>` +
        `<span class="pop-alt-trend"></span></span></div>` +
      `<div class="pop-row">Spd: <span class="pop-spd-wrap"><span class="pop-spd"></span>` +
        `<span class="pop-spd-trend"></span></span> &nbsp; ` +
        `Hdg: <span class="pop-hdg"></span><span class="pop-compass"></span></div>` +
      `<div class="pop-row">VRate: <span class="pop-vrate"></span></div>` +
      `<div class="pop-row">Sqwk: <span class="pop-squawk"></span></div>` +
      `<div class="pop-row"><code class="pop-msgs"></code></div>`;
    updatePopupContent(root, a, now, airports);
    return root;
  }

  function updatePopupContent(root, a, now, airports) {
    const q = (sel) => root.querySelector(sel);

    q('.pop-callsign').textContent = a.callsign || '—';
    q('.pop-icao').textContent = a.icao.toUpperCase();

    const emEl = q('.pop-emergency');
    if (a.emergency) {
      emEl.innerHTML =
        `<span class="emergency-label">EMERGENCY · ${escapeHtml(a.emergency)}</span>`;
    } else {
      emEl.textContent = '';
    }

    const regLine = q('.pop-reg-line');
    if (a.registration || a.type_long) {
      q('.pop-reg').textContent = [a.registration, a.type_long]
        .filter(Boolean).join(' · ');
      regLine.hidden = false;
    } else {
      regLine.hidden = true;
    }

    const routeLine = q('.pop-route-line');
    const routeStr = routeLabel(a, airports);
    if (routeStr) {
      q('.pop-route').innerHTML = routeStr;
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
    q('.pop-alt-wrap').className = 'pop-alt-wrap ' + tAlt.cls;
    q('.pop-alt-trend').innerHTML = tAlt.arrow;
    q('.pop-spd').textContent = uconv('spd', a.speed);
    q('.pop-spd-wrap').className = 'pop-spd-wrap ' + tSpd.cls;
    q('.pop-spd-trend').innerHTML = tSpd.arrow;
    q('.pop-hdg').textContent = fmt(a.track, '°');
    q('.pop-compass').innerHTML = compassIcon(a.track);
    q('.pop-vrate').textContent = uconv('vrt', a.vrate);
    q('.pop-squawk').textContent = a.squawk || '—';
    q('.pop-msgs').textContent = `${a.msg_count} msgs · ${fmt(ageOf(a, now), 's', 1)} ago`;
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
    document.getElementById('labels-toggle').classList.toggle('active', showLabels);
  }

  // Singleton floating tooltip for airport codes. Mounted on <body> so it
  // can paint over the sidebar row's overflow:hidden, and driven by
  // delegated hover + click listeners so it works uniformly across desktop
  // and iPad Safari (where the native `title` attribute is inert).
  const airportTooltip = document.createElement('div');
  airportTooltip.id = 'airport-tooltip';
  airportTooltip.hidden = true;
  document.body.appendChild(airportTooltip);
  let airportTooltipHideTimer = null;

  function hideAirportTooltip() {
    airportTooltip.hidden = true;
    clearTimeout(airportTooltipHideTimer);
    airportTooltipHideTimer = null;
  }

  function showAirportTooltip(el, persistent) {
    const name = el.dataset.title;
    if (!name) return;
    airportTooltip.textContent = name;
    airportTooltip.hidden = false;
    // Measure after unhiding so offsetWidth is meaningful.
    const r = el.getBoundingClientRect();
    const tw = airportTooltip.offsetWidth;
    const th = airportTooltip.offsetHeight;
    const left = Math.max(4, Math.min(window.innerWidth - tw - 4, r.left + r.width / 2 - tw / 2));
    const top = r.top - th - 6 >= 4 ? r.top - th - 6 : r.bottom + 6;
    airportTooltip.style.left = `${left}px`;
    airportTooltip.style.top = `${top}px`;
    clearTimeout(airportTooltipHideTimer);
    airportTooltipHideTimer = persistent ? setTimeout(hideAirportTooltip, 3500) : null;
  }

  // Click anywhere: if it's an airport code, show the tooltip (and swallow
  // the event so the enclosing sidebar row doesn't also select the plane).
  // Clicks elsewhere dismiss any open tooltip.
  document.addEventListener(
    'click',
    (e) => {
      const hit = e.target.closest('.airport-code[data-title]');
      if (hit) {
        e.stopPropagation();
        showAirportTooltip(hit, true);
      } else if (!airportTooltip.hidden) {
        hideAirportTooltip();
      }
    },
    true,
  );

  // Desktop hover: show while the pointer is over a code.
  document.addEventListener('mouseover', (e) => {
    const hit = e.target.closest('.airport-code[data-title]');
    if (hit) showAirportTooltip(hit, false);
  });
  document.addEventListener('mouseout', (e) => {
    if (e.target.closest('.airport-code[data-title]') && airportTooltipHideTimer === null) {
      hideAirportTooltip();
    }
  });

  function applyTrailsVisibility() {
    for (const entry of aircraft.values()) {
      if (showTrails) {
        rebuildTrail(entry, entry.data.trail);
      } else {
        entry.trail.clearLayers();
        entry.trailFp = null;
      }
    }
    document.getElementById('trails-toggle').classList.toggle('active', showTrails);
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
        marker.on('popupopen', () => onSelected(a.icao));
        marker.on('popupclose', () => onDeselected(a.icao));
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
      if (!entry.popupEl) {
        entry.popupEl = buildPopupContent(a, snap.now, snap.airports);
        // autoPan=false stops the map from drifting when the aircraft moves
        // while its popup is open (e.g. after returning from a hidden tab).
        // We pan explicitly on sidebar-triggered selections so the popup is
        // still in view when it first opens.
        entry.marker.bindPopup(entry.popupEl, { autoPan: false, maxWidth: 360 });
      } else {
        updatePopupContent(entry.popupEl, a, snap.now, snap.airports);
      }

      entry.data = a;
      pushHistory(entry, a);
      updateLabelFor(entry);
    }

    // Keep the selected aircraft centred when "Follow" is on. Cheap pan
    // (no animation) so busy skies don't feel jumpy.
    if (followSelected && selectedIcao) {
      const entry = aircraft.get(selectedIcao);
      if (entry) map.panTo(entry.marker.getLatLng(), { animate: false });
    }

    // Drop aircraft no longer reported
    for (const [icao, entry] of aircraft.entries()) {
      if (!seen.has(icao)) {
        if (hoveredFromListIcao === icao) { hoveredFromListIcao = null; setHoverHalo(null); }
        if (hoveredFromMapIcao  === icao) hoveredFromMapIcao  = null;
        map.removeLayer(entry.marker);
        map.removeLayer(entry.trail);
        aircraft.delete(icao);
      }
    }

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
      return `
      <div class="${classes}" data-icao="${icao}">
        <span class="age">${fmt(ageOf(a, snap.now), 's', 1)}</span>
        <div class="row1">
          <span class="cs">${callsign} ${emergencyBadge}</span>
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

  // Side-effects of becoming the selected aircraft (fired by popupopen for
  // markers, or directly for aircraft with no position).
  function onSelected(icao) {
    selectedIcao = icao;
    writeDeepLink(icao);
    const entry = aircraft.get(icao);
    if (entry) {
      const a = entry.data;
      entry.marker.setIcon(planeIcon(a.track, altColor(a.altitude), true, !!a.emergency, a.type_icao));
    }
    document.querySelectorAll('.ac-item').forEach(el => {
      el.classList.toggle('selected', el.dataset.icao === icao);
    });
    fillAircraftPhoto(icao);
  }

  // Cache of adsbdb aircraft records by ICAO24 — keyed lowercase, value is
  // either the info object or `null` for a confirmed miss. The server
  // already caches for 30 days, but this client-side dict spares us a
  // network round-trip when the user re-opens the same popup.
  const aircraftInfoCache = new Map();

  async function fillAircraftPhoto(icao) {
    // Look up the slot the current popup rendered. If the popup closed
    // before the fetch returns, the slot is gone and we quietly drop it.
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
    if (!slot || !info || !info.photo_thumbnail) return;
    // Render the thumbnail with a link out to the full-size original; the
    // photo URL goes through escapeHtml since it's upstream data.
    const thumb = escapeHtml(info.photo_thumbnail);
    const full = info.photo_url ? escapeHtml(info.photo_url) : thumb;
    slot.innerHTML =
      `<a href="${full}" target="_blank" rel="noopener">` +
      `<img src="${thumb}" alt="" loading="lazy"></a>`;
  }

  // Fires when a popup closes (user clicked X, clicked the marker again,
  // clicked map background, or moved selection to another plane).
  function onDeselected(icao) {
    if (selectedIcao !== icao) return;  // already moved to something else
    selectedIcao = null;
    writeDeepLink(null);
    const entry = aircraft.get(icao);
    if (entry) {
      const a = entry.data;
      entry.marker.setIcon(planeIcon(a.track, altColor(a.altitude), false, !!a.emergency, a.type_icao));
    }
    document.querySelectorAll('.ac-item').forEach(el => {
      if (el.dataset.icao === icao) el.classList.remove('selected');
    });
  }

  // Triggered from the sidebar. Opens the popup; popupopen handler then calls
  // onSelected() so marker and list clicks go through the same code path.
  function selectAircraft(icao) {
    const entry = aircraft.get(icao);
    if (entry) {
      map.panTo(entry.marker.getLatLng());
      entry.marker.openPopup();
    } else {
      onSelected(icao);
    }
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
        // Refresh open popups so their units update immediately.
        for (const entry of aircraft.values()) {
          if (entry.popupEl) {
            updatePopupContent(entry.popupEl, entry.data, lastSnap.now, lastSnap.airports);
          }
        }
      }
    });
  });
  renderUnitSwitch();

  document.getElementById('labels-toggle').addEventListener('click', () => {
    showLabels = !showLabels;
    try { localStorage.setItem('flightjar.labels', showLabels ? '1' : '0'); } catch (_) {}
    applyLabelsVisibility();
  });
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

  document.getElementById('trails-toggle').addEventListener('click', () => {
    showTrails = !showTrails;
    try { localStorage.setItem('flightjar.trails', showTrails ? '1' : '0'); } catch (_) {}
    applyTrailsVisibility();
  });
  applyTrailsVisibility();

  function applyFollowState() {
    document.getElementById('follow-toggle').classList.toggle('active', followSelected);
    // Snap the map to the selected aircraft the moment Follow is switched on.
    if (followSelected && selectedIcao) {
      const entry = aircraft.get(selectedIcao);
      if (entry) map.panTo(entry.marker.getLatLng(), { animate: true });
    }
  }
  document.getElementById('follow-toggle').addEventListener('click', () => {
    followSelected = !followSelected;
    try { localStorage.setItem('flightjar.follow', followSelected ? '1' : '0'); } catch (_) {}
    applyFollowState();
  });
  applyFollowState();

  function applyCompactMode() {
    document.body.classList.toggle('compact-mode', compactMode);
    document.getElementById('compact-toggle').classList.toggle('active', compactMode);
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
  document.getElementById('compact-toggle').addEventListener('click', () => setCompact(!compactMode));
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
    document.getElementById('airports-toggle').classList.toggle('active', showAirports);
    if (showAirports) {
      if (!map.hasLayer(airportsLayer)) airportsLayer.addTo(map);
      refreshAirports();
    } else {
      map.removeLayer(airportsLayer);
      airportsLayer.clearLayers();
    }
  }
  function setAirports(value) {
    showAirports = value;
    try { localStorage.setItem('flightjar.airports', showAirports ? '1' : '0'); } catch (_) {}
    applyAirportsToggle();
  }
  document.getElementById('airports-toggle').addEventListener('click', () => setAirports(!showAirports));
  applyAirportsToggle();

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
        '<svg viewBox="0 0 20 20" width="18" height="18" aria-hidden="true">' +
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
      document.getElementById('labels-toggle').click();
    } else if (e.key === 't' || e.key === 'T') {
      document.getElementById('trails-toggle').click();
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
