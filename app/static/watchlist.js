// Persistent watchlist keyed by ICAO24 hex. Entries are stored as
// lowercase hex strings in localStorage under a single JSON-array key
// AND mirrored to the server via /api/watchlist so the backend can fire
// Telegram / ntfy / webhook alerts when a watched tail reappears — even
// when no browser tab is open. The browser is authoritative for UI
// state; the server is authoritative for alerts.
//
// Sync policy (union-merge on load):
//   1. Initialise from localStorage (offline-first).
//   2. Pull the server's list in the background. Union remote + local.
//   3. If the union differs from the server copy, push it back.
//   4. Every add/remove POSTs the full list to the server.

const STORAGE_KEY = 'flightjar.watchlist';
const NOTIF_PREF_KEY = 'flightjar.watchlist.notify';
const SYNC_URL = '/api/watchlist';

function loadSet() {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return new Set();
    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) return new Set();
    return new Set(parsed.filter((s) => typeof s === 'string').map((s) => s.toLowerCase()));
  } catch (_) {
    return new Set();
  }
}

function saveSet(set) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify([...set]));
  } catch (_) {
    // localStorage disabled / quota exceeded — drop silently, the user
    // can still watch aircraft for the current session.
  }
}

export function createWatchlist() {
  const set = loadSet();
  // Seen-this-session so the same re-entry doesn't fire N times per
  // snapshot tick. Cleared on page reload, which is fine — a fresh
  // browser session is a fresh notification cycle.
  const seenThisSession = new Set();
  let notifyEnabled = localStorage.getItem(NOTIF_PREF_KEY) === '1';

  // Server sync — best-effort, silent on failure so the UI stays
  // usable when offline. The server listens for snapshot ticks on its
  // own side and fires backend notifications; our job is just to keep
  // it mirrored with what the user has starred.
  async function pushToServer() {
    try {
      await fetch(SYNC_URL, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ icao24s: [...set] }),
      });
    } catch (_) { /* offline — UI + localStorage still work */ }
  }

  // Union-merge on load: entries on either side survive, duplicates
  // collapse. Push back if the union grew the server's copy.
  async function pullAndMergeFromServer() {
    try {
      const r = await fetch(SYNC_URL, { headers: { 'Accept': 'application/json' } });
      if (!r.ok) {
        // Endpoint missing (older server) — push our state so the next
        // reload at least has something to union with.
        if (set.size > 0) pushToServer();
        return;
      }
      const data = await r.json();
      const remote = new Set(
        (data.icao24s || [])
          .filter((s) => typeof s === 'string')
          .map((s) => s.toLowerCase()),
      );
      let changed = false;
      for (const k of remote) {
        if (!set.has(k)) { set.add(k); changed = true; }
      }
      if (changed) saveSet(set);
      // Any local-only entries need mirroring back up to the server.
      if ([...set].some((k) => !remote.has(k))) pushToServer();
    } catch (_) { /* offline — keep local state, retry on next reload */ }
  }

  // Kick initial sync in the background so createWatchlist() stays
  // synchronous for its callers.
  pullAndMergeFromServer();

  function has(icao) {
    return set.has((icao || '').toLowerCase());
  }

  function add(icao) {
    const key = (icao || '').toLowerCase();
    if (!key) return false;
    if (set.has(key)) return false;
    set.add(key);
    saveSet(set);
    pushToServer();
    return true;
  }

  function remove(icao) {
    const key = (icao || '').toLowerCase();
    if (!set.delete(key)) return false;
    saveSet(set);
    pushToServer();
    return true;
  }

  function toggle(icao) {
    if (has(icao)) {
      remove(icao);
      return false;
    }
    add(icao);
    return true;
  }

  function size() {
    return set.size;
  }

  function list() {
    // Snapshot copy so callers can iterate without worrying about
    // concurrent add/remove while they render.
    return [...set];
  }

  // Called once per snapshot with the full list of currently-tracked
  // aircraft objects. Fires a notification the first time a watched
  // aircraft appears within this page session.
  function noticeAppearances(aircraftList) {
    if (!notifyEnabled || typeof Notification === 'undefined') return;
    if (Notification.permission !== 'granted') return;
    for (const a of aircraftList) {
      const key = (a.icao || '').toLowerCase();
      if (!set.has(key) || seenThisSession.has(key)) continue;
      seenThisSession.add(key);
      const title = a.callsign || a.registration || a.icao.toUpperCase();
      const body = [a.registration, a.type_long].filter(Boolean).join(' · ');
      try {
        new Notification(`Watchlist: ${title}`, {
          body: body || 'in range',
          tag: `flightjar-watch-${key}`,
        });
      } catch (_) {
        // Firefox throws in non-secure contexts; ignore.
      }
    }
  }

  async function setNotifyEnabled(on) {
    if (on && typeof Notification !== 'undefined' && Notification.permission === 'default') {
      try {
        await Notification.requestPermission();
      } catch (_) { /* ignore */ }
    }
    notifyEnabled = !!on && typeof Notification !== 'undefined' &&
                    Notification.permission === 'granted';
    try {
      localStorage.setItem(NOTIF_PREF_KEY, notifyEnabled ? '1' : '0');
    } catch (_) { /* ignore */ }
    return notifyEnabled;
  }

  function isNotifyEnabled() { return notifyEnabled; }

  return { has, add, remove, toggle, size, list, noticeAppearances,
           setNotifyEnabled, isNotifyEnabled };
}
