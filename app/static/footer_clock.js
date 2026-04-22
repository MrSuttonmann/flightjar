// 1 Hz local-time readout in the footer. Ticks independently of the
// WebSocket snapshot interval so it stays smooth during feed hiccups.

export function initFooterClock() {
  const el = document.getElementById('footer-clock');
  function tick() {
    const d = new Date();
    const hh = String(d.getHours()).padStart(2, '0');
    const mm = String(d.getMinutes()).padStart(2, '0');
    const ss = String(d.getSeconds()).padStart(2, '0');
    el.textContent = `${hh}:${mm}:${ss}`;
  }
  tick();
  setInterval(tick, 1000);
}
