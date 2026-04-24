// Optional anonymous frontend telemetry. Mirrors the backend's design:
// disabled when TELEMETRY_ENABLED=0 or when no PostHog key is baked
// into the build. Same destination + same distinct_id as the backend
// ping, so frontend pageviews and explicit `track()` calls roll up
// against the same Person profile.
//
// Privacy posture:
//   - autocapture: false       (no element-text exfiltration; only
//                               events we explicitly fire end up sent)
//   - capture_pageview: true   (single $pageview per load; counts
//                               sessions without leaking element data)
//   - session recording / surveys: off
//   - persistence: 'memory'    (no cookies, no localStorage; the
//                               install's distinct_id is bootstrapped
//                               from the backend on every load)
//   - $geoip_disable + blank   (PostHog skips IP geolocation on every
//     $ip on every event        event; matches the backend behaviour)

let posthogReady = null;

export async function initTelemetry() {
  let config;
  try {
    const resp = await fetch('/api/telemetry_config', { cache: 'no-store' });
    if (!resp.ok) return;
    config = await resp.json();
  } catch {
    return;
  }
  if (!config.enabled || !config.api_key) return;

  loadPosthogSnippet();

  window.posthog.init(config.api_key, {
    api_host: config.host,
    autocapture: false,
    capture_pageview: true,
    capture_pageleave: false,
    disable_session_recording: true,
    disable_surveys: true,
    disable_external_dependency_loading: true,
    persistence: 'memory',
    bootstrap: { distinctID: config.distinct_id },
    loaded(ph) {
      ph.register({
        $geoip_disable: true,
        $ip: '',
        $lib: 'flightjar-frontend',
      });
    },
  });

  posthogReady = window.posthog;
}

export function track(event, properties) {
  // Safe no-op when telemetry is off / not yet ready / failed to load.
  if (!posthogReady || typeof posthogReady.capture !== 'function') return;
  posthogReady.capture(event, properties || {});
}

// Rotate the install's PostHog distinct_id. POSTs the gated reset
// endpoint, then severs the live tab from the old id by calling
// posthog.reset() and re-identifying as the new id so any further
// events from this tab attach to the new Person. Throws on HTTP
// failure so the caller can surface an error message.
export async function resetTelemetry(authedFetch) {
  const fetchFn = authedFetch || ((url, init) => fetch(url, init));
  const r = await fetchFn('/api/telemetry/reset', { method: 'POST' });
  if (!r.ok) {
    throw new Error(`reset failed: HTTP ${r.status}`);
  }
  const body = await r.json();
  if (window.posthog && typeof window.posthog.reset === 'function') {
    window.posthog.reset();
    if (body.distinct_id && typeof window.posthog.identify === 'function') {
      window.posthog.identify(body.distinct_id);
    }
  }
  return body;
}

// Verbatim PostHog bootstrap snippet from
// https://posthog.com/docs/getting-started/install — creates a queueing
// stub on window.posthog and dynamically loads the real library from
// the project's asset host. Indented for readability; functionally
// identical to the minified one-liner PostHog publishes.
function loadPosthogSnippet() {
  // eslint-disable-next-line
  !function (t, e) {
    var o, n, p, r;
    e.__SV || (window.posthog = e, e._i = [], e.init = function (i, s, a) {
      function g(t, e) {
        var o = e.split('.');
        2 == o.length && (t = t[o[0]], e = o[1]);
        t[e] = function () { t.push([e].concat(Array.prototype.slice.call(arguments, 0))); };
      }
      (p = t.createElement('script')).type = 'text/javascript';
      p.crossOrigin = 'anonymous';
      p.async = !0;
      p.src = s.api_host.replace('.i.posthog.com', '-assets.i.posthog.com') + '/static/array.js';
      (r = t.getElementsByTagName('script')[0]).parentNode.insertBefore(p, r);
      var u = e;
      for (void 0 !== a ? u = e[a] = [] : a = 'posthog', u.people = u.people || [], u.toString = function (t) {
        var e = 'posthog';
        return 'posthog' !== a && (e += '.' + a), t || (e += ' (stub)'), e;
      }, u.people.toString = function () { return u.toString(1) + '.people (stub)'; }, o = 'init capture register register_once unregister getFeatureFlag isFeatureEnabled reloadFeatureFlags identify setPersonProperties group resetGroups reset get_distinct_id alias set_config opt_in_capturing opt_out_capturing has_opted_in_capturing has_opted_out_capturing clear_opt_in_out_capturing debug'.split(' '), n = 0; n < o.length; n++) g(u, o[n]);
      e._i.push([i, s, a]);
    }, e.__SV = 1);
  }(document, window.posthog || []);
}
