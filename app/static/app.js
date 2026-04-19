(() => {
  const map = L.map('map', { worldCopyJump: true }).setView([51.5, -0.1], 6);

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
  const defaultBaseName = savedBase && baseLayers[savedBase] ? savedBase : 'Carto Dark';
  baseLayers[defaultBaseName].addTo(map);

  // Range-ring overlay (centred on receiver; rebuilt when receiver is known).
  const rangeRings = L.layerGroup();
  function buildRangeRings(rx) {
    rangeRings.clearLayers();
    if (!rx || rx.lat == null || rx.lon == null) return;
    // 50 / 100 / 200 NM. 1 NM = 1852 m.
    for (const nm of [50, 100, 200]) {
      L.circle([rx.lat, rx.lon], {
        radius: nm * 1852,
        color: '#5fa8ff', weight: 1, opacity: 0.35,
        fill: false, dashArray: '3 4', interactive: false,
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

  // Unit system. Feed speeds/alts are knots and feet; distances are km.
  // Conversions below keep each system internally consistent.
  const UNIT_SYSTEMS = {
    metric:   { alt: { mul: 0.3048,   suf: ' m',    digits: 0 },
                spd: { mul: 1.852,    suf: ' km/h', digits: 0 },
                vrt: { mul: 0.00508,  suf: ' m/s',  digits: 1 },
                dst: { mul: 1,        suf: ' km',   digits: 0 } },
    imperial: { alt: { mul: 1,        suf: ' ft',   digits: 0 },
                spd: { mul: 1.15078,  suf: ' mph',  digits: 0 },
                vrt: { mul: 1,        suf: ' fpm',  digits: 0 },
                dst: { mul: 0.621371, suf: ' mi',   digits: 0 } },
    nautical: { alt: { mul: 1,        suf: ' ft',   digits: 0 },
                spd: { mul: 1,        suf: ' kt',   digits: 0 },
                vrt: { mul: 1,        suf: ' fpm',  digits: 0 },
                dst: { mul: 0.539957, suf: ' nm',   digits: 0 } },
  };
  let unitSystem = localStorage.getItem('flightjar.units');
  if (!UNIT_SYSTEMS[unitSystem]) unitSystem = 'nautical';

  function uconv(kind, value) {
    if (value == null || isNaN(value)) return '—';
    // Metric altitude: show km once we cross 1 km, otherwise metres.
    if (unitSystem === 'metric' && kind === 'alt') {
      const m = Number(value) * 0.3048;
      return m >= 1000 ? (m / 1000).toFixed(1) + ' km' : m.toFixed(0) + ' m';
    }
    const u = UNIT_SYSTEMS[unitSystem][kind];
    return (Number(value) * u.mul).toFixed(u.digits) + u.suf;
  }

  function sortValue(a, key, now) {
    switch (key) {
      case 'callsign': return (a.callsign || '').trim() || null;
      case 'altitude': return a.altitude;
      case 'age':      return ageOf(a, now);
      case 'distance': return a.distance_km;
    }
    return null;
  }

  // Altitude → colour ramp. Stops picked from ColorBrewer's Spectral (reversed)
  // for a smooth, perceptually-even transition: warm red at low altitudes,
  // through yellow/green at mid, to cool blue/violet at high. Linear
  // interpolation in RGB between adjacent stops.
  const ALT_STOPS = [
    [    0, [213,  62,  79]],  // #d53e4f  low
    [ 4000, [244, 109,  67]],  // #f46d43
    [ 8000, [253, 174,  97]],  // #fdae61
    [13000, [254, 224, 139]],  // #fee08b
    [18000, [230, 245, 152]],  // #e6f598
    [23000, [171, 221, 164]],  // #abdda4
    [28000, [102, 194, 165]],  // #66c2a5
    [33000, [ 50, 136, 189]],  // #3288bd
    [40000, [ 94,  79, 162]],  // #5e4fa2  high
  ];

  function altColor(alt) {
    if (alt == null) return '#8a94a3';
    if (alt <= ALT_STOPS[0][0]) return _rgb(ALT_STOPS[0][1]);
    const last = ALT_STOPS[ALT_STOPS.length - 1];
    if (alt >= last[0]) return _rgb(last[1]);
    for (let i = 1; i < ALT_STOPS.length; i++) {
      const [a1, c1] = ALT_STOPS[i];
      if (alt <= a1) {
        const [a0, c0] = ALT_STOPS[i - 1];
        const t = (alt - a0) / (a1 - a0);
        return _rgb([
          Math.round(c0[0] + t * (c1[0] - c0[0])),
          Math.round(c0[1] + t * (c1[1] - c0[1])),
          Math.round(c0[2] + t * (c1[2] - c0[2])),
        ]);
      }
    }
    return _rgb(last[1]);
  }
  function _rgb([r, g, b]) { return `rgb(${r},${g},${b})`; }

  // Top-down silhouettes. All designed around viewBox -14 -14 28 28 so a single
  // planeIcon() can swap paths. Rotated to match the aircraft's ground track.
  const PLANE_SHAPES = {
    generic: {
      size: 26,
      paths: ['M0,-10 L7,8 L0,4 L-7,8 Z'],
    },
    jet: {
      size: 28,
      paths: ['M0,-12 L2,-9 L2,-2 L11,2 L11,4 L2,3 L2,8 L4,11 L4,12 L-4,12 L-4,11 L-2,8 L-2,3 L-11,4 L-11,2 L-2,-2 L-2,-9 Z'],
    },
    widebody: {
      size: 32,
      paths: ['M0,-13 L3,-10 L3,-2 L14,3 L14,5 L3,4 L3,10 L6,13 L6,14 L-6,14 L-6,13 L-3,10 L-3,4 L-14,5 L-14,3 L-3,-2 L-3,-10 Z'],
    },
    turboprop: {
      size: 26,
      paths: ['M0,-11 L2,-8 L2,-1 L11,-1 L11,1 L2,2 L2,7 L4,10 L4,11 L-4,11 L-4,10 L-2,7 L-2,2 L-11,1 L-11,-1 L-2,-1 L-2,-8 Z'],
    },
    light: {
      size: 22,
      paths: ['M0,-9 L1,-6 L1,-1 L9,-1 L9,1 L1,2 L1,6 L3,8 L3,9 L-3,9 L-3,8 L-1,6 L-1,2 L-9,1 L-9,-1 L-1,-1 L-1,-6 Z'],
    },
    heli: {
      size: 28,
      // Rotor disc + small teardrop fuselage.
      paths: [
        'M0,-2 L1,1 L1,6 L-1,6 L-1,1 Z',           // fuselage
      ],
      disc: { r: 12, dash: '2 3' },
    },
  };

  // Maps ICAO type codes to a silhouette family. Just the common ones;
  // everything else falls through to category heuristics.
  const TYPE_SHAPES = {
    // Wide-body
    B772: 'widebody', B773: 'widebody', B77L: 'widebody', B77W: 'widebody',
    B744: 'widebody', B748: 'widebody', B788: 'widebody', B789: 'widebody',
    B78X: 'widebody', A332: 'widebody', A333: 'widebody', A338: 'widebody',
    A339: 'widebody', A342: 'widebody', A343: 'widebody', A345: 'widebody',
    A346: 'widebody', A359: 'widebody', A35K: 'widebody', A388: 'widebody',
    // Narrow-body jets
    B712: 'jet', B736: 'jet', B737: 'jet', B738: 'jet', B739: 'jet',
    B73G: 'jet', B38M: 'jet', B39M: 'jet', B3XM: 'jet',
    A319: 'jet', A320: 'jet', A321: 'jet', A318: 'jet',
    A19N: 'jet', A20N: 'jet', A21N: 'jet',
    E170: 'jet', E175: 'jet', E190: 'jet', E195: 'jet', E290: 'jet', E295: 'jet',
    CRJ2: 'jet', CRJ7: 'jet', CRJ9: 'jet', CRJX: 'jet',
    // Turboprop
    DH8A: 'turboprop', DH8B: 'turboprop', DH8C: 'turboprop', DH8D: 'turboprop',
    AT42: 'turboprop', AT43: 'turboprop', AT44: 'turboprop', AT45: 'turboprop',
    AT46: 'turboprop', AT72: 'turboprop', AT75: 'turboprop', AT76: 'turboprop',
    BE20: 'turboprop', B350: 'turboprop', BE30: 'turboprop', BE9L: 'turboprop',
    PC12: 'turboprop', TBM9: 'turboprop', TBM8: 'turboprop',
    // Light single / twin piston
    C152: 'light', C172: 'light', C162: 'light', C182: 'light', C206: 'light',
    C210: 'light', PA28: 'light', P28A: 'light', P28R: 'light', PA32: 'light',
    PA34: 'light', PA44: 'light', PA46: 'light', SR20: 'light', SR22: 'light',
    DA40: 'light', DA42: 'light', DA62: 'light', DR40: 'light', AA5: 'light',
  };

  function silhouette(a) {
    const t = a.type_icao;
    if (t && TYPE_SHAPES[t]) return TYPE_SHAPES[t];
    // Helicopter type codes typically start with H in the ICAO designator list.
    if (t && t[0] === 'H') return 'heli';
    switch (a.category) {
      case 1: return 'light';     // light < 15500 lb
      case 2: return 'jet';       // small
      case 3: return 'jet';       // large
      case 5: return 'widebody';  // heavy
      case 7: return 'heli';      // rotorcraft
    }
    return 'generic';
  }

  function planeIcon(track, color, selected, emergency, shapeName) {
    const shape = PLANE_SHAPES[shapeName] || PLANE_SHAPES.generic;
    const rot = track == null ? 0 : track;
    let stroke = selected ? '#fff' : '#000';
    let sw = selected ? 1.5 : 0.6;
    if (emergency) { stroke = '#ef4444'; sw = 2; }
    const size = shape.size;
    const half = size / 2;
    const disc = shape.disc
      ? `<circle cx="0" cy="0" r="${shape.disc.r}" fill="none" stroke="${stroke}" stroke-width="${sw}" stroke-dasharray="${shape.disc.dash}" opacity="0.7"/>`
      : '';
    const paths = shape.paths.map(d =>
      `<path d="${d}" fill="${color}" stroke="${stroke}" stroke-width="${sw}" stroke-linejoin="round"/>`
    ).join('');
    const svg = `
      <svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="-14 -14 28 28">
        <g transform="rotate(${rot})">${disc}${paths}</g>
      </svg>`;
    return L.divIcon({
      html: svg,
      className: 'plane-icon',
      iconSize: [size, size],
      iconAnchor: [half, half],
    });
  }

  function fmt(n, suffix = '', digits = 0) {
    if (n == null || isNaN(n)) return '—';
    return Number(n).toFixed(digits) + suffix;
  }

  // Per-aircraft rolling history used to derive trend arrows. The buffer
  // holds the last HIST_LEN sampled values; trend() compares ends with a
  // dead-zone so tiny fluctuations don't flicker between up / down / steady.
  const HIST_LEN = 10;
  const TREND_THRESHOLDS = { alt: 50, spd: 3, dst: 0.5 };  // ft, kt, km

  // Small triangle rotated to the compass heading. 0° points up (north).
  function compassIcon(deg) {
    if (deg == null || isNaN(deg)) return '';
    return `<svg class="compass" viewBox="-6 -6 12 12" width="12" height="12" style="transform: rotate(${Number(deg)}deg)"><path d="M0,-5 L3,4 L0,2 L-3,4 Z" fill="currentColor"/></svg>`;
  }

  function pushHistory(entry, a) {
    const h = entry.hist;
    for (const [key, value] of [['alt', a.altitude], ['spd', a.speed], ['dst', a.distance_km]]) {
      const buf = h[key];
      buf.push(value);
      if (buf.length > HIST_LEN) buf.shift();
    }
  }

  function trendInfo(entry, key) {
    const buf = entry?.hist?.[key];
    if (!buf || buf.length < 3) return { dir: '', arrow: '', cls: '' };
    // Find oldest and newest non-null values; ignore gaps.
    let oldVal = null, newVal = null;
    for (let i = 0; i < buf.length; i++) {
      if (buf[i] != null) { oldVal = buf[i]; break; }
    }
    for (let i = buf.length - 1; i >= 0; i--) {
      if (buf[i] != null) { newVal = buf[i]; break; }
    }
    if (oldVal == null || newVal == null) return { dir: '', arrow: '', cls: '' };
    const delta = newVal - oldVal;
    const th = TREND_THRESHOLDS[key] ?? 0;
    let dir = 'flat', glyph = '—', cls = '';
    if (delta > th)  { dir = 'up'; glyph = '↑'; cls = 'trend-up'; }
    else if (delta < -th) { dir = 'down'; glyph = '↓'; cls = 'trend-down'; }
    return { dir, arrow: `<span class="trend">${glyph}</span>`, cls };
  }

  function ageOf(a, now) {
    if (now == null || a.last_seen == null) return null;
    return Math.max(0, now - a.last_seen);
  }

  function popupHtml(a, now) {
    const emergency = a.emergency
      ? `<span class="emergency-label">EMERGENCY · ${a.emergency}</span><br>`
      : '';
    const regLine = a.registration || a.type_long
      ? `<span class="ac-info">${[a.registration, a.type_long].filter(Boolean).join(' · ')}</span><br>`
      : '';
    // Indicate altitude source: show (geo) if only geometric, or add a small
    // (baro/geo) suffix when both are known and they disagree meaningfully.
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
    return `
      <b>${a.callsign || '—'}</b> <code>${a.icao.toUpperCase()}</code> ${emergency}<br>
      ${regLine}
      Alt: <span class="${tAlt.cls}"><b>${altLabel}</b>${tAlt.arrow}</span><br>
      Spd: <span class="${tSpd.cls}">${uconv('spd', a.speed)}${tSpd.arrow}</span> &nbsp; Hdg: ${fmt(a.track, '°')}${compassIcon(a.track)}<br>
      VRate: ${uconv('vrt', a.vrate)}<br>
      Sqwk: ${a.squawk || '—'}<br>
      <code>${a.msg_count} msgs · ${fmt(ageOf(a, now), 's', 1)} ago</code>`;
  }

  // What text to show as the permanent on-map label.
  // Future-friendly: easy to extend to configurable datapoints.
  function labelText(a) {
    return a.callsign || a.icao.toUpperCase();
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

  function applyTrailsVisibility() {
    for (const entry of aircraft.values()) {
      if (showTrails) {
        rebuildTrail(entry, entry.data.trail);
      } else {
        entry.trail.clearLayers();
        entry.trailLen = 0;
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
      if (entry.trailLen !== 0) { entry.trail.clearLayers(); entry.trailLen = 0; }
      return;
    }
    // Skip rebuild if nothing changed (common between snapshot ticks when
    // the aircraft hasn't reported a new position).
    if (points.length === entry.trailLen) return;

    entry.trail.clearLayers();
    for (let i = 1; i < points.length; i++) {
      const p0 = points[i - 1];
      const p1 = points[i];
      // Colour by the later point's altitude (falls back to earlier point).
      const alt = p1[2] != null ? p1[2] : p0[2];
      const color = altColor(alt);
      // Increase opacity towards the end of the trail.
      const opacity = 0.25 + 0.55 * (i / points.length);
      L.polyline([[p0[0], p0[1]], [p1[0], p1[1]]], {
        color,
        weight: 2.5,
        opacity,
        lineCap: 'round',
        lineJoin: 'round',
        smoothFactor: 0,
        interactive: false,
      }).addTo(entry.trail);
    }
    entry.trailLen = points.length;
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
      const shape = silhouette(a);
      const icon = planeIcon(a.track, color, isSelected, !!a.emergency, shape);

      let entry = aircraft.get(a.icao);
      if (!entry) {
        const marker = L.marker([a.lat, a.lon], { icon }).addTo(map);
        const trail = L.layerGroup().addTo(map);
        marker.on('popupopen', () => onSelected(a.icao));
        marker.on('popupclose', () => onDeselected(a.icao));
        marker.on('mouseover', () => peekListItem(a.icao, true));
        marker.on('mouseout',  () => peekListItem(a.icao, false));
        entry = {
          marker, trail, label: null, data: a, trailLen: 0,
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
      } else if (entry.trailLen !== 0) {
        entry.trail.clearLayers();
        entry.trailLen = 0;
      }
      entry.marker.setPopupContent(popupHtml(a, snap.now));
      if (!entry.marker.getPopup()) {
        // autoPan=false stops the map from drifting when the aircraft moves
        // while its popup is open (e.g. after returning from a hidden tab).
        // We pan explicitly on sidebar-triggered selections so the popup is
        // still in view when it first opens.
        entry.marker.bindPopup(popupHtml(a, snap.now), { autoPan: false });
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

  function renderSidebar(snap) {
    // Push the current count onto the sparkline buffer before drawing.
    countHistory.push(snap.count);
    if (countHistory.length > COUNT_HISTORY_LEN) countHistory.shift();
    renderSparkline();
    const status = document.getElementById('status-text');
    status.textContent =
      `${snap.count} aircraft · ${snap.positioned} positioned`;
    const site = snap.site_name || '';
    document.getElementById('site-name').textContent = site;
    document.title = site
      ? `Flightjar — ${site} (${snap.count})`
      : `Flightjar (${snap.count})`;

    // Filter by search text (callsign or icao substring, case-insensitive).
    const q = searchFilter;
    const filtered = q
      ? snap.aircraft.filter(a =>
          (a.callsign || '').toLowerCase().includes(q) ||
          a.icao.toLowerCase().includes(q))
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
        ? `<span class="emergency-label">${a.emergency}</span>`
        : '';
      const subtitle = [a.registration, a.type_icao].filter(Boolean).join(' · ');
      const entry = aircraft.get(a.icao);
      const tAlt = trendInfo(entry, 'alt');
      const tSpd = trendInfo(entry, 'spd');
      const tDst = trendInfo(entry, 'dst');
      return `
      <div class="${classes}" data-icao="${a.icao}">
        <span class="age">${fmt(ageOf(a, snap.now), 's', 1)}</span>
        <div class="row1">
          <span class="cs">${a.callsign || '— — — —'} ${emergencyBadge}</span>
          <span class="icao">${subtitle || a.icao.toUpperCase()}</span>
        </div>
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
      entry.marker.setIcon(planeIcon(a.track, altColor(a.altitude), true, !!a.emergency, silhouette(a)));
    }
    document.querySelectorAll('.ac-item').forEach(el => {
      el.classList.toggle('selected', el.dataset.icao === icao);
    });
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
      entry.marker.setIcon(planeIcon(a.track, altColor(a.altitude), false, !!a.emergency, silhouette(a)));
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
    document.querySelectorAll('.unit-btn').forEach(el => {
      el.classList.toggle('active', el.dataset.unit === unitSystem);
    });
  }
  document.querySelectorAll('.unit-btn').forEach(el => {
    el.addEventListener('click', () => {
      unitSystem = el.dataset.unit;
      try { localStorage.setItem('flightjar.units', unitSystem); } catch (_) {}
      renderUnitSwitch();
      if (lastSnap) {
        renderSidebar(lastSnap);
        // Refresh open popups so their units update immediately.
        for (const entry of aircraft.values()) {
          entry.marker.setPopupContent(popupHtml(entry.data, lastSnap?.now));
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
    } else if (e.key === 'f' || e.key === 'F') {
      const pts = [];
      for (const entry of aircraft.values()) pts.push(entry.marker.getLatLng());
      if (pts.length) map.fitBounds(L.latLngBounds(pts).pad(0.2), { maxZoom: 10 });
    } else if (e.key === 'u' || e.key === 'U') {
      const order = ['metric', 'imperial', 'nautical'];
      const next = order[(order.indexOf(unitSystem) + 1) % order.length];
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
