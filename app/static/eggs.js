// Playful UI easter eggs — all purely discoverable, none of them
// interfere with normal operation.
//
// - Konami code (↑↑↓↓←→←→BA) briefly hue-shifts the altitude legend
//   and trails, then fires a toast.
// - Clicking the Flightjar logo seven times inside a 5-second window
//   reveals a small floating "session stats" card with counters that
//   the app tracks anyway.
// - Typing the literal phrase "barrel roll" into the search input
//   spins the map once, then clears the input.

import { lucide } from './icons_lib.js';
import { showToast } from './toast.js';

const EGG_CLOSE_ICON = lucide('x', { size: 14, strokeWidth: 2 });

const KONAMI = [
  'ArrowUp', 'ArrowUp', 'ArrowDown', 'ArrowDown',
  'ArrowLeft', 'ArrowRight', 'ArrowLeft', 'ArrowRight',
  'b', 'a',
];
const KONAMI_CLASS = 'egg-party';
const KONAMI_DURATION_MS = 4500;

const LOGO_CLICK_TARGET = 7;
const LOGO_CLICK_WINDOW_MS = 5000;

const BARREL_ROLL_PHRASE = 'barrel roll';
const BARREL_ROLL_CLASS = 'egg-barrel-roll';

// -------- Konami --------

function initKonami() {
  const buf = [];
  document.addEventListener('keydown', (e) => {
    // Skip when focused in an input / textarea so the user typing the
    // letters 'b' / 'a' into the search box doesn't trip the code.
    const t = e.target;
    if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) {
      return;
    }
    buf.push(e.key);
    if (buf.length > KONAMI.length) buf.shift();
    if (buf.length !== KONAMI.length) return;
    // Arrow keys are case-exact; the letters we match case-insensitively.
    const ok = KONAMI.every((want, i) => {
      const got = buf[i];
      return want.length === 1 ? got.toLowerCase() === want : got === want;
    });
    if (!ok) return;
    buf.length = 0;
    document.body.classList.add(KONAMI_CLASS);
    showToast('Cheat code activated', { level: 'egg' });
    setTimeout(() => document.body.classList.remove(KONAMI_CLASS), KONAMI_DURATION_MS);
  });
}

// -------- Logo click × 7 --------

function initLogoClicks(getSessionStats) {
  const logo = document.querySelector('#header h1');
  if (!logo) return;
  logo.style.cursor = 'pointer';
  logo.title = 'Flightjar';
  let count = 0;
  let firstClickAt = 0;
  logo.addEventListener('click', () => {
    const now = Date.now();
    if (now - firstClickAt > LOGO_CLICK_WINDOW_MS) {
      count = 0;
      firstClickAt = now;
    }
    count += 1;
    if (count < LOGO_CLICK_TARGET) return;
    count = 0;
    showSessionStatsCard(getSessionStats());
  });
}

function showSessionStatsCard(stats) {
  const existing = document.getElementById('egg-session-stats');
  if (existing) existing.remove();

  const card = document.createElement('div');
  card.id = 'egg-session-stats';
  card.className = 'egg-session-card';
  const rows = [
    ['Planes seen', `${stats.planesSeen.toLocaleString()}`],
    ['Messages', `${stats.messages.toLocaleString()}`],
    ['Max range', stats.maxRangeKm ? `${Math.round(stats.maxRangeKm)} km` : '—'],
    ['Longest trail', stats.longestTrailKm ? `${Math.round(stats.longestTrailKm)} km` : '—'],
  ];
  card.innerHTML = `
    <div class="egg-session-head">
      <span>Session stats</span>
      <button type="button" class="egg-session-close" aria-label="Close">${EGG_CLOSE_ICON}</button>
    </div>
    <dl class="egg-session-body">
      ${rows.map(([k, v]) => `<div><dt>${k}</dt><dd>${v}</dd></div>`).join('')}
    </dl>
  `;
  document.body.appendChild(card);

  const dismiss = () => card.remove();
  card.querySelector('.egg-session-close').addEventListener('click', dismiss);
  const outside = (e) => {
    if (!card.contains(e.target)) {
      dismiss();
      document.removeEventListener('click', outside, true);
      document.removeEventListener('keydown', onKey);
    }
  };
  const onKey = (e) => {
    if (e.key === 'Escape') {
      dismiss();
      document.removeEventListener('click', outside, true);
      document.removeEventListener('keydown', onKey);
    }
  };
  // Defer so the click that opened the card doesn't immediately close it.
  setTimeout(() => document.addEventListener('click', outside, true), 0);
  document.addEventListener('keydown', onKey);
}

// -------- Barrel roll --------

function initBarrelRoll() {
  const search = document.getElementById('search');
  const map = document.getElementById('map');
  if (!search || !map) return;
  search.addEventListener('input', () => {
    if (search.value.trim().toLowerCase() !== BARREL_ROLL_PHRASE) return;
    if (map.classList.contains(BARREL_ROLL_CLASS)) return;
    map.classList.add(BARREL_ROLL_CLASS);
    search.value = '';
    search.dispatchEvent(new Event('input', { bubbles: true }));
    setTimeout(() => map.classList.remove(BARREL_ROLL_CLASS), 1100);
  });
}

// -------- Christmas Eve snow --------

function initSeasonalSnow() {
  const d = new Date();
  if (d.getMonth() !== 11 || d.getDate() !== 24) return;
  document.body.classList.add('christmas-eve');
  // Spawn a small number of snowflakes as absolute-positioned spans
  // in the map container. CSS drives the animation; we just need the
  // elements to animate. 18 flakes gives a sparse-but-noticeable look
  // without tanking frame rates on slower machines.
  const map = document.getElementById('map');
  if (!map) return;
  const layer = document.createElement('div');
  layer.className = 'egg-snow';
  layer.setAttribute('aria-hidden', 'true');
  for (let i = 0; i < 18; i++) {
    const flake = document.createElement('span');
    flake.className = 'egg-snowflake';
    flake.style.left = `${Math.random() * 100}%`;
    flake.style.animationDelay = `${-Math.random() * 10}s`;
    flake.style.animationDuration = `${8 + Math.random() * 6}s`;
    flake.textContent = '❄';
    layer.appendChild(flake);
  }
  map.appendChild(layer);
}

// -------- public init --------

export function initEggs(opts = {}) {
  initKonami();
  initLogoClicks(opts.getSessionStats || (() => ({
    planesSeen: 0, messages: 0, maxRangeKm: 0, longestTrailKm: 0,
  })));
  initBarrelRoll();
  initSeasonalSnow();
}
