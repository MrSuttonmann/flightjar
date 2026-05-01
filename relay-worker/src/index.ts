import { RelayDurableObject } from './relay';

export { RelayDurableObject };

export interface Env {
  RELAY: DurableObjectNamespace;
  // Optional per-IP rate limit on /register, configured via the
  // ratelimit binding in wrangler.toml. Falls open when missing
  // (e.g. local dev with no binding configured).
  REGISTER_RATE_LIMIT?: { limit(opts: { key: string }): Promise<{ success: boolean }> };
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname === '/healthz') {
      return new Response('ok', { status: 200 });
    }

    if (url.pathname === '/stats') {
      const id = env.RELAY.idFromName('global');
      const stub = env.RELAY.get(id);
      return stub.fetch(new Request('https://internal/stats'));
    }

    if (url.pathname === '/register') {
      if (request.method !== 'POST') {
        return new Response('Method not allowed', { status: 405 });
      }
      if (env.REGISTER_RATE_LIMIT) {
        const ip = request.headers.get('CF-Connecting-IP') ?? 'unknown';
        const { success } = await env.REGISTER_RATE_LIMIT.limit({ key: ip });
        if (!success) {
          console.log(JSON.stringify({ event: 'register_rate_limited', ip }));
          return new Response('Rate limited', { status: 429 });
        }
      }
      const id = env.RELAY.idFromName('global');
      const stub = env.RELAY.get(id);
      return stub.fetch(new Request('https://internal/register', { method: 'POST' }));
    }

    if (url.pathname === '/ws') {
      if (request.headers.get('Upgrade') !== 'websocket') {
        return new Response('Expected WebSocket upgrade', { status: 426 });
      }
      const id = env.RELAY.idFromName('global');
      const stub = env.RELAY.get(id);
      return stub.fetch(request);
    }

    return new Response('Not found', { status: 404 });
  },
};
