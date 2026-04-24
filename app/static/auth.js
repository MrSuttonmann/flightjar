// Optional shared-secret unlock layer. The server's the security
// boundary — every gated endpoint enforces auth itself. This module
// is just the user-facing UX:
//
//   - On boot, GET /api/auth/status to find out if a password is
//     configured and whether the current cookie is still valid.
//   - authedFetch wraps fetch so that any 401 from a gated endpoint
//     auto-pops the unlock dialog and (on success) retries the
//     original request once.
//   - The dialog is a real <form> with hidden username + autocomplete=
//     current-password, so password managers reliably detect it.
//
// Nothing in this module grants access; flipping `state.unlocked` in
// the JS console only changes the UI hint. The cookie that actually
// authenticates is HttpOnly and unreadable from JS.

const STATUS_URL = '/api/auth/status';
const LOGIN_URL = '/api/auth/login';
const LOGOUT_URL = '/api/auth/logout';

const state = { required: false, unlocked: false, ready: false };
const listeners = new Set();
let unlockInFlight = null;

function notify() {
  const snap = { ...state };
  for (const fn of listeners) {
    try { fn(snap); } catch (e) { console.warn('auth listener threw', e); }
  }
}

export function getAuthStatus() {
  return { ...state };
}

export function subscribeAuth(fn) {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

export async function refreshAuthStatus() {
  try {
    const r = await fetch(STATUS_URL, { credentials: 'same-origin' });
    if (!r.ok) return;
    const body = await r.json();
    state.required = !!body.required;
    state.unlocked = !!body.unlocked;
    state.ready = true;
    notify();
  } catch (_) {
    // Network blip — leave previous state in place.
  }
}

export async function logout() {
  try {
    await fetch(LOGOUT_URL, {
      method: 'POST',
      credentials: 'same-origin',
    });
  } catch (_) { /* ignore — best effort */ }
  state.unlocked = false;
  notify();
}

// Show the unlock dialog. Resolves to true when the user successfully
// entered the password, false when they dismissed without unlocking.
// Concurrent callers share a single dialog instance — useful when
// several gated requests fire in parallel and all 401 at once.
export function promptUnlock() {
  if (unlockInFlight) return unlockInFlight;
  const dialog = document.getElementById('auth-dialog');
  if (!dialog) {
    // No dialog wired (e.g. test fixture without the UI) — caller
    // should treat this as "user can't unlock right now".
    return Promise.resolve(false);
  }
  const form = document.getElementById('auth-form');
  const pwInput = document.getElementById('auth-password');
  const errEl = document.getElementById('auth-error');

  unlockInFlight = new Promise((resolve) => {
    pwInput.value = '';
    errEl.hidden = true;
    errEl.textContent = '';

    let succeeded = false;

    async function onSubmit(e) {
      e.preventDefault();
      errEl.hidden = true;
      const candidate = pwInput.value;
      try {
        const r = await fetch(LOGIN_URL, {
          method: 'POST',
          credentials: 'same-origin',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ password: candidate }),
        });
        if (r.ok) {
          state.unlocked = true;
          state.required = true;
          notify();
          succeeded = true;
          dialog.close();
          return;
        }
        if (r.status === 429) {
          errEl.textContent = 'Too many attempts. Wait a minute and try again.';
        } else if (r.status === 404) {
          // Server says auth isn't configured — refresh status and bail.
          state.required = false;
          state.unlocked = false;
          notify();
          succeeded = true; // treat as "unlocked" so the retry proceeds
          dialog.close();
          return;
        } else {
          errEl.textContent = 'Incorrect password.';
        }
        errEl.hidden = false;
        pwInput.select();
      } catch (_) {
        errEl.textContent = 'Could not reach server.';
        errEl.hidden = false;
      }
    }

    function cleanup() {
      form.removeEventListener('submit', onSubmit);
      dialog.removeEventListener('close', cleanup);
      unlockInFlight = null;
      resolve(succeeded);
    }

    form.addEventListener('submit', onSubmit);
    dialog.addEventListener('close', cleanup);
    if (typeof dialog.showModal === 'function') dialog.showModal();
    else dialog.setAttribute('open', '');
    // Defer focus to the next tick so showModal's own focus management
    // doesn't fight us.
    queueMicrotask(() => pwInput.focus());
  });
  return unlockInFlight;
}

// fetch shim that handles 401 by prompting the user to unlock and
// retrying once on success. Same signature / return shape as fetch.
export async function authedFetch(input, init) {
  const opts = { credentials: 'same-origin', ...init };
  let resp = await fetch(input, opts);
  if (resp.status !== 401) return resp;
  // Server is the source of truth — a 401 means we're locked,
  // regardless of what state.unlocked says client-side.
  state.unlocked = false;
  if (!state.required) {
    state.required = true;
  }
  notify();
  const unlocked = await promptUnlock();
  if (!unlocked) return resp; // user cancelled — surface the original 401
  return await fetch(input, opts);
}

// Pre-flight gate for management UIs (Watchlist / Alerts dialogs).
// Resolves to true when the caller may proceed — auth not required,
// already unlocked, OR successfully unlocked just now via the prompt.
// Resolves to false when the user dismissed the prompt without
// unlocking, in which case the caller should bail out of opening
// whatever it was about to open.
export async function ensureUnlocked() {
  if (!state.required) return true;
  if (state.unlocked) return true;
  return await promptUnlock();
}

export async function initAuth() {
  await refreshAuthStatus();
  wireDialogClose();
  wireLockButton();
}

function wireDialogClose() {
  const dialog = document.getElementById('auth-dialog');
  if (!dialog) return;
  // Click-outside-closes, matching the other dialogs.
  dialog.addEventListener('click', (e) => {
    if (e.target !== dialog) return;
    const r = dialog.getBoundingClientRect();
    const inside = e.clientX >= r.left && e.clientX <= r.right
                && e.clientY >= r.top && e.clientY <= r.bottom;
    if (!inside) dialog.close();
  });
}

function wireLockButton() {
  const btn = document.getElementById('auth-lock-btn');
  if (!btn) return;
  function applyVisibility(snap) {
    btn.hidden = !snap.required;
    btn.setAttribute('aria-pressed', snap.unlocked ? 'true' : 'false');
    btn.title = snap.unlocked
      ? 'Unlocked — click to lock again'
      : 'Locked — click to unlock';
    btn.classList.toggle('locked', !snap.unlocked);
  }
  applyVisibility(state);
  subscribeAuth(applyVisibility);
  btn.addEventListener('click', async () => {
    if (state.unlocked) {
      await logout();
    } else {
      await promptUnlock();
    }
  });
}
