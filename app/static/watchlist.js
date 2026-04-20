// Small persistent watchlist keyed by ICAO24 hex. Entries are stored as
// lowercase hex strings in localStorage under a single JSON-array key.
// Visual state (sidebar highlight, panel star) is driven by consumers
// reading from .has(icao); this module just owns the add/remove/persist
// lifecycle and an optional browser-notification when a watched tail
// reappears.

const STORAGE_KEY = 'flightjar.watchlist';
const NOTIF_PREF_KEY = 'flightjar.watchlist.notify';

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

  function has(icao) {
    return set.has((icao || '').toLowerCase());
  }

  function add(icao) {
    const key = (icao || '').toLowerCase();
    if (!key) return false;
    if (set.has(key)) return false;
    set.add(key);
    saveSet(set);
    return true;
  }

  function remove(icao) {
    const key = (icao || '').toLowerCase();
    if (!set.delete(key)) return false;
    saveSet(set);
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

  return { has, add, remove, toggle, size, noticeAppearances,
           setNotifyEnabled, isNotifyEnabled };
}
