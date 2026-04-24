import { test } from 'node:test';
import assert from 'node:assert/strict';

import { initTelemetry, resetTelemetry, track } from '../../app/static/telemetry.js';

// Minimal globals so the module can `fetch()` and reference `window`
// without a real browser. Each test sets up exactly what it needs.
async function withFakeEnv(setup, run) {
  const origFetch = globalThis.fetch;
  const origWindow = globalThis.window;
  const origDocument = globalThis.document;
  try {
    setup();
    return await run();
  } finally {
    globalThis.fetch = origFetch;
    globalThis.window = origWindow;
    globalThis.document = origDocument;
  }
}

test('track is a safe no-op before init', () => {
  // The boot sequence calls track(...) for things like dialog opens long
  // before initTelemetry() finishes; track() must never throw and must
  // never touch a non-existent posthog object.
  assert.doesNotThrow(() => track('x'));
  assert.doesNotThrow(() => track('y', { foo: 1 }));
});

test('initTelemetry bails when /api/telemetry_config returns enabled:false', async () => {
  await withFakeEnv(
    () => {
      globalThis.fetch = async () => ({
        ok: true,
        json: async () => ({ enabled: false }),
      });
      globalThis.window = {};
      globalThis.document = { /* never used on the disabled path */ };
    },
    async () => {
      await initTelemetry();
      // Must not have created window.posthog — disabled path skips the
      // snippet entirely and never makes a CDN request.
      assert.equal(globalThis.window.posthog, undefined);
    },
  );
});

test('initTelemetry bails on a network failure', async () => {
  await withFakeEnv(
    () => {
      globalThis.fetch = async () => { throw new Error('network'); };
      globalThis.window = {};
      globalThis.document = {};
    },
    async () => {
      // Must swallow — a flaky telemetry endpoint can't be allowed to
      // throw out of the boot sequence (it's called unawaited but a
      // sync throw would still escape).
      await initTelemetry();
      assert.equal(globalThis.window.posthog, undefined);
    },
  );
});

test('resetTelemetry POSTs and severs the live tab from the old id', async () => {
  let posted = null;
  let reset = false;
  let identifiedAs = null;
  await withFakeEnv(
    () => {
      globalThis.fetch = async (url, init) => {
        posted = { url, method: init?.method };
        return {
          ok: true,
          json: async () => ({ ok: true, distinct_id: 'new-id', telemetry_enabled: true }),
        };
      };
      globalThis.window = {
        posthog: {
          reset: () => { reset = true; },
          identify: (id) => { identifiedAs = id; },
        },
      };
      globalThis.document = {};
    },
    async () => {
      const body = await resetTelemetry();
      assert.equal(body.distinct_id, 'new-id');
      assert.deepEqual(posted, { url: '/api/telemetry/reset', method: 'POST' });
      assert.equal(reset, true);
      assert.equal(identifiedAs, 'new-id');
    },
  );
});

test('resetTelemetry throws on non-OK response so the UI can surface it', async () => {
  await withFakeEnv(
    () => {
      globalThis.fetch = async () => ({ ok: false, status: 401, json: async () => ({}) });
      globalThis.window = {};
      globalThis.document = {};
    },
    async () => {
      await assert.rejects(() => resetTelemetry(), /401/);
    },
  );
});

test('resetTelemetry no-ops PostHog calls when posthog never loaded', async () => {
  await withFakeEnv(
    () => {
      globalThis.fetch = async () => ({
        ok: true,
        json: async () => ({ ok: true, distinct_id: 'fresh', telemetry_enabled: false }),
      });
      // window.posthog deliberately absent — telemetry was disabled this
      // session, but the user may still want to rotate the persisted id.
      globalThis.window = {};
      globalThis.document = {};
    },
    async () => {
      const body = await resetTelemetry();
      assert.equal(body.telemetry_enabled, false);
      assert.equal(body.distinct_id, 'fresh');
    },
  );
});

test('initTelemetry bails when api_key missing despite enabled:true', async () => {
  await withFakeEnv(
    () => {
      globalThis.fetch = async () => ({
        ok: true,
        json: async () => ({ enabled: true, api_key: '', host: 'https://eu.i.posthog.com' }),
      });
      globalThis.window = {};
      globalThis.document = {};
    },
    async () => {
      await initTelemetry();
      assert.equal(globalThis.window.posthog, undefined);
    },
  );
});
