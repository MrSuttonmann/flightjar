import { test } from 'node:test';
import assert from 'node:assert/strict';

import { initTelemetry, track } from '../../app/static/telemetry.js';

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
