// WebSocket lifecycle: connect to /ws, reconnect on close, and pipe
// snapshots into the update loop. Plus the tiny heartbeat indicator
// that colours stale / dead in the sidebar header.

import { state } from './state.js';
import { update } from './update_loop.js';

let ws;
let reconnectTimer = null;

function setStatus(stateName, text) {
  const s = document.getElementById('status');
  s.classList.remove('live', 'dead');
  if (stateName) s.classList.add(stateName);
  if (text) document.getElementById('status-text').textContent = text;
}

export function connect() {
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

// Only shown when the feed is stalled — amber after 5s since last
// snapshot, red after 15s.
export function startHeartbeat() {
  const hb = document.getElementById('heartbeat');
  setInterval(() => {
    if (!state.lastSnapAt) { hb.textContent = ''; return; }
    const secs = Math.round((Date.now() - state.lastSnapAt) / 1000);
    if (secs < 5) {
      hb.textContent = '';
      hb.classList.remove('stale', 'dead');
      return;
    }
    hb.textContent = `· ${secs}s ago`;
    hb.classList.toggle('stale', secs < 15);
    hb.classList.toggle('dead', secs >= 15);
  }, 1000);
}
