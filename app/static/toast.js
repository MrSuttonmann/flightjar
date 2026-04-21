// Minimal floating-message helper. No external deps, no existing toast
// pattern in the codebase — so keep it small. One persistent host div
// at top-right collects queued toasts; each auto-dismisses after
// `duration` ms with a short fade. Every easter egg that needs to say
// something goes through this so the visual language stays consistent.

const LEVELS = new Set(['info', 'success', 'egg', 'warn']);
const LEVEL_ICONS = { info: 'ℹ', success: '✓', warn: '!', egg: '✦' };
const DEFAULT_DURATION_MS = 4000;
const HOST_ID = 'toast-host';

let hostEl = null;

function ensureHost() {
  if (hostEl) return hostEl;
  hostEl = document.getElementById(HOST_ID);
  if (!hostEl) {
    hostEl = document.createElement('div');
    hostEl.id = HOST_ID;
    hostEl.setAttribute('role', 'status');
    hostEl.setAttribute('aria-live', 'polite');
    document.body.appendChild(hostEl);
  }
  return hostEl;
}

export function showToast(message, opts = {}) {
  if (!message) return;
  const level = LEVELS.has(opts.level) ? opts.level : 'info';
  const duration = Number.isFinite(opts.duration) ? opts.duration : DEFAULT_DURATION_MS;
  const host = ensureHost();
  const el = document.createElement('div');
  el.className = `toast toast-${level}`;
  // CSS uses this to drive the auto-dismiss progress bar's duration
  // via an animation; kept in sync with the setTimeout below.
  el.style.setProperty('--toast-duration', `${duration}ms`);
  const icon = document.createElement('span');
  icon.className = 'toast-icon';
  icon.setAttribute('aria-hidden', 'true');
  icon.textContent = LEVEL_ICONS[level] || LEVEL_ICONS.info;
  const body = document.createElement('span');
  body.className = 'toast-body';
  body.textContent = message;
  const progress = document.createElement('span');
  progress.className = 'toast-progress';
  progress.setAttribute('aria-hidden', 'true');
  el.append(icon, body, progress);
  host.appendChild(el);
  // Force a reflow so the fade-in transition triggers cleanly rather
  // than snapping to the visible state.
  // eslint-disable-next-line no-unused-expressions
  el.offsetHeight;
  el.classList.add('visible');

  const dismiss = () => {
    el.classList.remove('visible');
    el.classList.add('leaving');
    setTimeout(() => el.remove(), 300);
  };
  const timer = setTimeout(dismiss, duration);
  // Click to dismiss early.
  el.addEventListener('click', () => {
    clearTimeout(timer);
    dismiss();
  });
}
