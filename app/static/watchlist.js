// Persistent watchlist keyed by ICAO24 hex. Entries are stored as
// lowercase hex strings in localStorage under a single JSON-array key
// AND mirrored to the server via /api/watchlist so the backend can fire
// Telegram / ntfy / webhook alerts when a watched tail reappears — even
// when no browser tab is open.
//
// Sync policy:
//   - On boot: union-merge localStorage with the server's list. Skip
//     silently when the server's locked (no auto-prompt on page load).
//   - Mutations (add/remove): push the *tentative* new set to the
//     server first; only commit to localStorage on 2xx. If the server
//     returns 401 the user gets the unlock dialog; if they cancel,
//     the mutation is aborted entirely and local state is unchanged.
//     This is the security boundary — a tampered localStorage cannot
//     forge a watchlist entry the server didn't accept.
//   - Network / 5xx errors still commit locally (offline-friendly):
//     the next reload's union-merge reconciles. Auth failure does not.

import { authedFetch } from './auth.js';

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

  // Push a tentative set to the server. Returns one of:
  //   'committed'      — server accepted it, commit locally
  //   'auth-rejected'  — 401 with no successful unlock; abort mutation
  //   'transient'      — network/5xx; commit locally, reconcile later
  // Plain fetch is fine for the unauthed 'feature disabled' path
  // because the server returns 200 on every call; authedFetch handles
  // the 401-prompt-and-retry cycle when auth is enabled.
  async function pushTentative(targetSet) {
    try {
      const r = await authedFetch(SYNC_URL, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ icao24s: [...targetSet] }),
      });
      if (r.ok) return 'committed';
      if (r.status === 401) return 'auth-rejected';
      return 'transient';
    } catch (_) {
      return 'transient';
    }
  }

  // Initial union-merge with the server's list. Plain fetch (no
  // unlock prompt) — when the instance is locked we want the page
  // to load cleanly; the user gets the prompt when they take an
  // action that needs auth.
  async function pullAndMergeFromServer() {
    try {
      const r = await fetch(SYNC_URL, {
        headers: { 'Accept': 'application/json' },
        credentials: 'same-origin',
      });
      if (r.status === 401) return;          // locked — skip silently
      if (!r.ok) {
        // Endpoint missing (older server) — push our state via
        // authedFetch so the next reload at least has something
        // to union with.
        if (set.size > 0) pushTentative(set);
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
      // Any local-only entries need mirroring back up. authedFetch
      // would prompt if locked — the operator just unlocked moments
      // ago by visiting this page, so it's fine to ask.
      if ([...set].some((k) => !remote.has(k))) pushTentative(set);
    } catch (_) { /* offline — keep local state, retry on next reload */ }
  }

  // Kick initial sync in the background so createWatchlist() stays
  // synchronous for its callers.
  pullAndMergeFromServer();

  function has(icao) {
    return set.has((icao || '').toLowerCase());
  }

  async function add(icao) {
    const key = (icao || '').toLowerCase();
    if (!key) return false;
    if (set.has(key)) return false;
    // Tentative copy — only mutate the real set after the server
    // confirms (or fails non-auth-related-ly).
    const next = new Set(set);
    next.add(key);
    const result = await pushTentative(next);
    if (result === 'auth-rejected') return false;
    set.add(key);
    saveSet(set);
    return true;
  }

  async function remove(icao) {
    const key = (icao || '').toLowerCase();
    if (!set.has(key)) return false;
    const next = new Set(set);
    next.delete(key);
    const result = await pushTentative(next);
    if (result === 'auth-rejected') return false;
    set.delete(key);
    saveSet(set);
    return true;
  }

  async function toggle(icao) {
    if (has(icao)) {
      const removed = await remove(icao);
      return !removed; // false when removal landed (no longer watching)
                       // true when removal was rejected (still watching)
    }
    const added = await add(icao);
    return added;
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
