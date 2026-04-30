// Service worker for Flightjar PWA.
// Strategy:
//   - CDN assets (Leaflet, flagcdn): cache-first — they're versioned by URL.
//   - /static/* and /: network-first with cache fallback — keeps the shell
//     up-to-date while still loading offline after a prior visit.
//   - /api/* and /ws: network-only — live data, never cache.

const CACHE = 'flightjar-v1';

const CDN_ORIGINS = ['unpkg.com', 'flagcdn.com'];

self.addEventListener('install', () => {
  self.skipWaiting();
});

self.addEventListener('activate', event => {
  event.waitUntil(
    caches.keys()
      .then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', event => {
  const { request } = event;
  if (request.method !== 'GET') return;

  const url = new URL(request.url);

  // Never intercept live-data endpoints.
  if (url.pathname.startsWith('/api/') || url.pathname === '/ws') return;

  if (CDN_ORIGINS.some(o => url.hostname.includes(o))) {
    // Cache-first for versioned CDN assets.
    event.respondWith(
      caches.match(request).then(cached => {
        if (cached) return cached;
        return fetch(request).then(res => {
          if (res.ok) caches.open(CACHE).then(c => c.put(request, res.clone()));
          return res;
        });
      })
    );
    return;
  }

  // Network-first for app shell (/, /static/*).
  event.respondWith(
    fetch(request)
      .then(res => {
        if (res.ok) caches.open(CACHE).then(c => c.put(request, res.clone()));
        return res;
      })
      .catch(() => caches.match(request))
  );
});
