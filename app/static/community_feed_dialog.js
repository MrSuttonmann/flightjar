// Community Feed dialog + header status pill.
//
// The pill lives in the sidebar header (#community-feed-pill) and is
// the only entry point — click to open the dialog. It also doubles as
// the always-visible status indicator: "off" with an accent dot when
// the operator hasn't opted in, "on · N receivers" once they have.
//
// The dialog renders one of two states depending on the server's
// reported config:
//
//   - Onboarding (#cf-onboarding): the operator has never opted in.
//     Explains what's shared / not shared, has a "Turn on" button.
//   - Manage (#cf-manage): the feed is on. Shows live status + a
//     site-name toggle + a "Turn off" button.
//
// First-run auto-open: on a fresh browser (localStorage flag
// `flightjar.cf_onboarding_shown` absent) the dialog opens itself
// ~8s after the first snapshot arrives. The 8s delay gives the map
// time to populate so the user sees the app working before being
// asked to opt into anything; the localStorage flag means we only
// nudge once. Auth-required + locked instances still see the dialog
// (with the locked hint) — they just can't enable until they unlock.

import { authedFetch, getAuthStatus, subscribeAuth } from './auth.js';

const DIALOG_ID = 'community-feed-dialog';
const PILL_ID = 'community-feed-pill';
const AUTOPOPEN_STORAGE_KEY = 'flightjar.cf_onboarding_shown';
const AUTOPOPEN_DELAY_MS = 8000;

let lastConfig = null;        // last server-confirmed config
let lastP2PStatus = null;     // last p2p block from the snapshot
let firstSnapshotSeen = false;
let autoOpenTimer = null;

function dialogEl() { return document.getElementById(DIALOG_ID); }
function pillEl() { return document.getElementById(PILL_ID); }

async function fetchConfig() {
  const auth = getAuthStatus();
  // Skip silent open-time fetch when locked — would pop the unlock
  // prompt the moment the dialog mounted. Onboarding mode still
  // renders fine with lastConfig = null (defaults to off).
  if (auth.required && !auth.unlocked) return null;
  try {
    const r = await fetch('/api/p2p/config', { credentials: 'same-origin' });
    if (!r.ok) return null;
    return await r.json();
  } catch (_) {
    return null;
  }
}

async function postConfig(patch) {
  const r = await authedFetch('/api/p2p/config', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(patch),
  });
  if (!r.ok) throw new Error(`HTTP ${r.status}`);
  return await r.json();
}

function setStatus(elId, text, kind) {
  const el = document.getElementById(elId);
  if (!el) return;
  el.textContent = text;
  el.hidden = !text;
  el.classList.remove('is-error', 'is-ok');
  if (kind) el.classList.add(`is-${kind}`);
}

function isOnState() {
  return !!(lastConfig && lastConfig.enabled && lastConfig.consent_given);
}

function renderDialog() {
  const onboarding = document.getElementById('cf-onboarding');
  const manage = document.getElementById('cf-manage');
  if (!onboarding || !manage) return;

  // A user is "on" only when both flags are set on the server. Anything
  // else (fresh install, locked + never seen config, mid-toggle) renders
  // the onboarding state.
  const isOn = isOnState();
  onboarding.hidden = isOn;
  manage.hidden = !isOn;

  // Lock-hint inside onboarding visible iff the instance is locked.
  const auth = getAuthStatus();
  const locked = auth.required && !auth.unlocked;
  const lockedHint = document.getElementById('cf-locked-hint');
  if (lockedHint) lockedHint.hidden = !locked;
  const enableBtn = document.getElementById('cf-enable-btn');
  if (enableBtn) enableBtn.disabled = locked;

  // Site-name checkboxes: keep both surfaces in sync with the same value.
  const shareValue = !!(lastConfig && lastConfig.share_site_name);
  const shareA = document.getElementById('cf-share-site-name');
  const shareB = document.getElementById('cf-share-site-name-manage');
  if (shareA) shareA.checked = shareValue;
  if (shareB) shareB.checked = shareValue;

  // Live status (manage mode only). Sourced from the snapshot's p2p
  // block via updateP2PStatus(); fall back to "Connecting…" before the
  // first snapshot arrives.
  if (isOn) {
    const dot = document.getElementById('cf-status-dot');
    const text = document.getElementById('cf-status-text');
    const p2p = lastP2PStatus;
    const connected = !!(p2p && p2p.connected);
    const peers = connected ? (p2p.peers | 0) : 0;
    if (dot) {
      dot.classList.toggle('live', connected);
      dot.classList.toggle('dead', !!p2p && !connected);
    }
    if (text) {
      text.textContent = !p2p
        ? 'Connecting…'
        : connected
          ? (peers === 1
              ? 'Connected · 1 other receiver online'
              : `Connected · ${peers} other receivers online`)
          : 'Disconnected — will retry automatically';
    }
  }
}

function renderPill() {
  const pill = pillEl();
  if (!pill) return;
  const isOn = isOnState();
  const p2p = lastP2PStatus;
  const connected = isOn && !!(p2p && p2p.connected);
  const disconnected = isOn && !!p2p && !connected;
  pill.classList.toggle('is-off', !isOn);
  pill.classList.toggle('is-on', isOn && (connected || !p2p));
  pill.classList.toggle('is-disconnected', disconnected);

  const peers = connected ? (p2p.peers | 0) : 0;
  const label = pill.querySelector('.community-feed-pill-label');
  if (label) {
    if (!isOn) {
      label.textContent = 'Community Feed: off';
    } else if (!p2p) {
      label.textContent = 'Community Feed · connecting…';
    } else if (connected) {
      label.textContent = peers === 1
        ? 'Community Feed · 1 receiver'
        : `Community Feed · ${peers} receivers`;
    } else {
      label.textContent = 'Community Feed · reconnecting…';
    }
  }
  pill.title = isOn
    ? 'Community Feed — sharing with the network. Click to manage.'
    : 'Community Feed — share aircraft this receiver detects with the network. Click to enable.';
}

function openDialog() {
  const dialog = dialogEl();
  if (!dialog) return;
  renderDialog();
  if (typeof dialog.showModal === 'function') dialog.showModal();
  else dialog.setAttribute('open', '');
}

// Schedule the first-run auto-open. Fires once `firstSnapshotSeen` is
// true and a single setTimeout has elapsed. Marks the localStorage
// flag immediately on fire so a slow user who lets it open, closes
// it, and refreshes the page doesn't get nudged twice.
function maybeScheduleAutoOpen() {
  if (autoOpenTimer != null) return;
  if (!firstSnapshotSeen) return;
  let alreadyShown = false;
  try { alreadyShown = localStorage.getItem(AUTOPOPEN_STORAGE_KEY) === '1'; }
  catch (_) { /* storage disabled — treat as not shown */ }
  if (alreadyShown) return;
  // Already opted in? Skip the nudge entirely.
  if (isOnState()) return;

  autoOpenTimer = setTimeout(() => {
    autoOpenTimer = null;
    try { localStorage.setItem(AUTOPOPEN_STORAGE_KEY, '1'); }
    catch (_) { /* storage disabled */ }
    // Don't reopen if the user has manually opened it in the meantime,
    // or if they've opted in via some other path (very unlikely, but
    // belt-and-braces).
    if (dialogEl()?.open) return;
    if (isOnState()) return;
    openDialog();
  }, AUTOPOPEN_DELAY_MS);
}

// Called by sidebar.js on every snapshot tick — feeds the pill and the
// dialog's live status, and triggers the first-run auto-open.
export function updateP2PStatus(p2p) {
  lastP2PStatus = p2p || null;
  if (!firstSnapshotSeen) {
    firstSnapshotSeen = true;
    maybeScheduleAutoOpen();
  }
  renderPill();
  if (dialogEl()?.open) renderDialog();
}

function wireOnboarding() {
  const enableBtn = document.getElementById('cf-enable-btn');
  const shareEl = document.getElementById('cf-share-site-name');
  if (!enableBtn || !shareEl) return;

  enableBtn.addEventListener('click', async () => {
    setStatus('cf-onboarding-status', 'Turning on…');
    enableBtn.disabled = true;
    try {
      lastConfig = await postConfig({
        enabled: true,
        consent_given: true,
        share_site_name: shareEl.checked,
      });
      setStatus('cf-onboarding-status', '', null);
      renderPill();
      renderDialog();
    } catch (err) {
      setStatus('cf-onboarding-status', `Couldn't turn on: ${err.message || err}`, 'error');
      enableBtn.disabled = false;
    }
  });
}

function wireManage() {
  const shareEl = document.getElementById('cf-share-site-name-manage');
  const disableBtn = document.getElementById('cf-disable-btn');
  if (!shareEl || !disableBtn) return;

  shareEl.addEventListener('change', async () => {
    setStatus('cf-manage-status', 'Saving…');
    try {
      lastConfig = await postConfig({ share_site_name: shareEl.checked });
      setStatus('cf-manage-status', 'Saved.', 'ok');
      renderDialog();
    } catch (err) {
      setStatus('cf-manage-status', `Save failed: ${err.message || err}`, 'error');
    }
  });

  disableBtn.addEventListener('click', async () => {
    setStatus('cf-manage-status', 'Turning off…');
    disableBtn.disabled = true;
    try {
      // Leave consent_given true — they've already opted in once. Just
      // flip enabled off. Re-enabling later won't need a fresh consent
      // moment; the manage view becomes a one-toggle thing.
      lastConfig = await postConfig({ enabled: false });
      setStatus('cf-manage-status', '', null);
      renderPill();
      renderDialog();
    } catch (err) {
      setStatus('cf-manage-status', `Couldn't turn off: ${err.message || err}`, 'error');
    } finally {
      disableBtn.disabled = false;
    }
  });
}

export function initCommunityFeedDialog() {
  const dialog = dialogEl();
  const pill = pillEl();
  if (!dialog || !pill) return;

  wireOnboarding();
  wireManage();
  renderPill();

  pill.addEventListener('click', async () => {
    // Mark the auto-open as "handled" the moment the user clicks the
    // pill themselves — no point auto-opening 8s later if they've
    // already engaged with it.
    if (autoOpenTimer != null) {
      clearTimeout(autoOpenTimer);
      autoOpenTimer = null;
    }
    try { localStorage.setItem(AUTOPOPEN_STORAGE_KEY, '1'); }
    catch (_) { /* storage disabled */ }
    // Pull fresh config every open so other tabs / restart-time
    // migrations are reflected.
    const cfg = await fetchConfig();
    if (cfg) lastConfig = cfg;
    openDialog();
  });

  dialog.addEventListener('close', () => {
    setStatus('cf-onboarding-status', '', null);
    setStatus('cf-manage-status', '', null);
  });
  dialog.addEventListener('click', (e) => {
    const r = dialog.getBoundingClientRect();
    const inside = e.clientX >= r.left && e.clientX <= r.right
                && e.clientY >= r.top && e.clientY <= r.bottom;
    if (!inside) dialog.close();
  });

  // Re-render when auth state changes — unlocking can reveal config we
  // skipped on the silent open-time fetch.
  subscribeAuth(async () => {
    const cfg = await fetchConfig();
    if (cfg) {
      lastConfig = cfg;
      renderPill();
    }
    if (dialog.open) renderDialog();
  });

  // Eagerly fetch once on init so the pill reflects state even before
  // the first snapshot arrives. Skips silently when locked.
  fetchConfig().then((cfg) => {
    if (cfg) {
      lastConfig = cfg;
      renderPill();
    }
  });
}
