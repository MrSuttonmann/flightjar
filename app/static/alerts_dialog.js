// Alerts dialog — CRUD UI for the server-side notification channels.
//
// Flow:
//   1. On open: GET /api/notifications/config → local working copy.
//   2. Render sections per channel type. Changes to any field auto-save
//      via POST — no explicit Save button. A field mid-edit posts on
//      blur; checkboxes post on change.
//   3. Test buttons hit /api/notifications/test/{id}, which dispatches
//      a one-off "hey, I'm wired up" message through that channel.
//
// Password-style fields (bot_token, ntfy_token) have a show/hide eye
// toggle so users can paste a fresh token without having to first
// select-all and delete a long masked string.

import { escapeHtml } from './format.js';
import { lucide } from './icons_lib.js';

const X_ICON = lucide('x', { size: 14, strokeWidth: 2 });
const CHECK_ICON = lucide('check', { size: 14, strokeWidth: 2 });
const EYE_ICON = lucide('eye', { size: 14, strokeWidth: 1.8 });
const EYE_OFF_ICON = lucide('eye-off', { size: 14, strokeWidth: 1.8 });

const CONFIG_URL = '/api/notifications/config';
const TEST_URL = (id) => `/api/notifications/test/${encodeURIComponent(id)}`;

const TYPE_LABELS = {
  telegram: 'Telegram chats',
  ntfy: 'ntfy topics',
  webhook: 'Webhooks',
};

// Per-type form field definitions. `secret` flips on password-masked
// rendering for sensitive inputs (bot tokens etc.).
const FIELDS = {
  telegram: [
    { key: 'bot_token', label: 'Bot token', placeholder: '1234567:ABC-…', secret: true },
    { key: 'chat_id', label: 'Chat ID', placeholder: 'e.g. 123456789' },
  ],
  ntfy: [
    { key: 'url', label: 'Topic URL', placeholder: 'https://ntfy.sh/your-topic' },
    { key: 'token', label: 'Token (optional)', placeholder: '', secret: true },
  ],
  webhook: [
    { key: 'url', label: 'Webhook URL', placeholder: 'https://example.com/hook' },
  ],
};

let state = { channels: [] };

// Reset a remove button that was click-armed but not confirmed.
// Module-level so both the timeout callback and the outer-click
// cancellation hook can share it.
function resetRemoveBtn(btn) {
  if (!btn || btn.dataset.confirming !== '1') return;
  btn.dataset.confirming = '0';
  btn.innerHTML = X_ICON;
  btn.title = 'Remove';
  btn.classList.remove('confirming');
  delete btn.dataset.resetTimer;
}

export function initAlertsDialog() {
  const dialog = document.getElementById('alerts-dialog');
  const sections = document.getElementById('alerts-sections');
  const emptyHint = document.getElementById('alerts-empty-hint');
  const addButtons = dialog.querySelectorAll('.alerts-add');

  // -------- network helpers --------

  async function loadConfig() {
    try {
      const r = await fetch(CONFIG_URL);
      if (!r.ok) return { channels: [] };
      const body = await r.json();
      return { channels: Array.isArray(body.channels) ? body.channels : [] };
    } catch (_) {
      return { channels: [] };
    }
  }

  async function saveConfig() {
    try {
      const r = await fetch(CONFIG_URL, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ channels: state.channels }),
      });
      if (!r.ok) return;
      // Server assigns IDs / normalises fields — adopt its copy so any
      // local placeholder IDs get replaced.
      const body = await r.json();
      if (Array.isArray(body.channels)) {
        state.channels = body.channels;
      }
    } catch (_) { /* offline — user can retry */ }
  }

  async function testChannel(id, btn) {
    const oldLabel = btn.textContent;
    btn.textContent = 'Sending…';
    btn.disabled = true;
    try {
      const r = await fetch(TEST_URL(id), { method: 'POST' });
      btn.textContent = r.ok ? 'Sent ✓' : 'Failed';
    } catch (_) {
      btn.textContent = 'Failed';
    }
    setTimeout(() => {
      btn.textContent = oldLabel;
      btn.disabled = false;
    }, 1800);
  }

  // -------- rendering --------

  function render() {
    const grouped = { telegram: [], ntfy: [], webhook: [] };
    for (const c of state.channels) {
      if (grouped[c.type]) grouped[c.type].push(c);
    }
    sections.innerHTML = '';
    for (const type of ['telegram', 'ntfy', 'webhook']) {
      const entries = grouped[type];
      if (!entries.length) continue;
      const sect = document.createElement('section');
      sect.className = 'alerts-section';
      sect.innerHTML = `<h3>${escapeHtml(TYPE_LABELS[type])}</h3>`;
      for (const c of entries) sect.appendChild(renderEntry(c));
      sections.appendChild(sect);
    }
    emptyHint.hidden = state.channels.length > 0;
  }

  function renderEntry(c) {
    const wrap = document.createElement('div');
    wrap.className = `alerts-entry${c.enabled === false ? ' disabled' : ''}`;
    wrap.dataset.id = c.id;

    const header = document.createElement('div');
    header.className = 'alerts-entry-head';
    header.innerHTML = `
      <input type="text" class="alerts-name" value="${escapeHtml(c.name || '')}"
             placeholder="Name" aria-label="Channel name">
      <button type="button" class="alerts-remove" aria-label="Remove">${X_ICON}</button>
    `;
    wrap.appendChild(header);

    const fields = document.createElement('div');
    fields.className = 'alerts-fields';
    for (const def of FIELDS[c.type] || []) {
      const row = document.createElement('label');
      row.className = 'alerts-field';
      const id = `alerts-${c.id}-${def.key}`;
      const inputType = def.secret ? 'password' : 'text';
      const val = escapeHtml(c[def.key] || '');
      row.innerHTML = `
        <span class="alerts-field-label">${escapeHtml(def.label)}</span>
        <span class="alerts-field-input">
          <input id="${id}" type="${inputType}" value="${val}"
                 spellcheck="false" autocomplete="off"
                 placeholder="${escapeHtml(def.placeholder || '')}"
                 data-key="${escapeHtml(def.key)}">
          ${def.secret ? `<button type="button" class="alerts-show" aria-label="Show"
                                   data-target="${id}">${EYE_ICON}</button>` : ''}
        </span>
      `;
      fields.appendChild(row);
    }
    wrap.appendChild(fields);

    const toggles = document.createElement('div');
    toggles.className = 'alerts-toggles';
    toggles.innerHTML = `
      <label><input type="checkbox" data-flag="enabled"
                    ${c.enabled !== false ? 'checked' : ''}> Enabled</label>
      <label><input type="checkbox" data-flag="watchlist_enabled"
                    ${c.watchlist_enabled !== false ? 'checked' : ''}> Watchlist</label>
      <label><input type="checkbox" data-flag="emergency_enabled"
                    ${c.emergency_enabled !== false ? 'checked' : ''}> Emergency</label>
      <button type="button" class="alerts-test">Test</button>
    `;
    wrap.appendChild(toggles);
    return wrap;
  }

  // -------- event wiring (delegated, since renderEntry makes fresh DOM) --------

  sections.addEventListener('change', async (e) => {
    const row = e.target.closest('.alerts-entry');
    if (!row) return;
    const channel = state.channels.find((c) => c.id === row.dataset.id);
    if (!channel) return;
    const flag = e.target.dataset.flag;
    if (flag) {
      channel[flag] = e.target.checked;
      if (flag === 'enabled') row.classList.toggle('disabled', !e.target.checked);
      await saveConfig();
      return;
    }
    const key = e.target.dataset.key;
    if (key) {
      channel[key] = e.target.value.trim();
      await saveConfig();
    }
  });

  // Blur on free-text inputs also saves (covers the case where the
  // user tabs away without firing a change event, which happens when
  // they don't change the value).
  sections.addEventListener('blur', async (e) => {
    const row = e.target.closest('.alerts-entry');
    if (!row) return;
    const channel = state.channels.find((c) => c.id === row.dataset.id);
    if (!channel) return;
    if (e.target.classList.contains('alerts-name')) {
      channel.name = e.target.value.trim();
      await saveConfig();
    }
  }, true);

  sections.addEventListener('click', async (e) => {
    const row = e.target.closest('.alerts-entry');
    if (!row) return;
    const removeBtn = e.target.classList.contains('alerts-remove')
      ? e.target : e.target.closest('.alerts-remove');
    if (removeBtn) {
      // Two-click confirm: first click switches the button into a
      // "click again to delete" state with a red tint and a check
      // glyph; second click within REMOVE_CONFIRM_MS actually removes.
      // Auto-cancels after the timeout so the button doesn't stay armed.
      const btn = removeBtn;
      if (btn.dataset.confirming === '1') {
        window.clearTimeout(Number(btn.dataset.resetTimer));
        state.channels = state.channels.filter((c) => c.id !== row.dataset.id);
        await saveConfig();
        render();
        return;
      }
      btn.dataset.confirming = '1';
      btn.classList.add('confirming');
      btn.innerHTML = CHECK_ICON;
      btn.title = 'Click again to remove';
      btn.dataset.resetTimer = String(
        window.setTimeout(() => resetRemoveBtn(btn), 3500),
      );
      return;
    }
    if (e.target.classList.contains('alerts-test')) {
      await testChannel(row.dataset.id, e.target);
      return;
    }
    if (e.target.classList.contains('alerts-show')
        || e.target.closest('.alerts-show')) {
      const btn = e.target.classList.contains('alerts-show')
        ? e.target : e.target.closest('.alerts-show');
      const input = document.getElementById(btn.dataset.target);
      if (!input) return;
      if (input.type === 'password') {
        input.type = 'text';
        btn.innerHTML = EYE_OFF_ICON;
      } else {
        input.type = 'password';
        btn.innerHTML = EYE_ICON;
      }
    }
  });

  addButtons.forEach((btn) => {
    btn.addEventListener('click', async () => {
      const type = btn.dataset.type;
      if (!FIELDS[type]) return;
      // Client-side temp id; server will replace it on save.
      const tempId = `tmp-${Math.random().toString(36).slice(2, 10)}`;
      state.channels.push({
        id: tempId,
        type,
        name: `New ${type} channel`,
        enabled: true,
        watchlist_enabled: true,
        emergency_enabled: true,
      });
      await saveConfig();
      render();
      // Focus the name input of the entry we just added. It'll have a
      // server-assigned id now, so just grab the last row.
      const lastRow = sections.querySelector('.alerts-entry:last-child');
      if (lastRow) {
        const nameInput = lastRow.querySelector('.alerts-name');
        if (nameInput) nameInput.focus();
      }
    });
  });

  // -------- dialog open/close --------

  document.getElementById('alerts-btn').addEventListener('click', async () => {
    state = await loadConfig();
    render();
    if (typeof dialog.showModal === 'function') dialog.showModal();
    else dialog.setAttribute('open', '');
  });
  dialog.addEventListener('click', (e) => {
    const r = dialog.getBoundingClientRect();
    const inside = e.clientX >= r.left && e.clientX <= r.right
                && e.clientY >= r.top && e.clientY <= r.bottom;
    if (!inside) dialog.close();
  });
}
