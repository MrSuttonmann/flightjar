import { RelayDurableObject } from './relay';

export { RelayDurableObject };

export interface Env {
  RELAY: DurableObjectNamespace;
  RELAY_TOKEN?: string;
}

function unauthorized(reason: string): Response {
  return Response.json({ type: 'auth_fail', reason }, { status: 401 });
}

function checkAuth(request: Request, env: Env): boolean {
  const configured = env.RELAY_TOKEN?.trim();
  if (!configured) return true; // open federation — no token required
  const tokens = configured.split(',').map(t => t.trim()).filter(Boolean);
  if (tokens.length === 0) return true;
  const auth = request.headers.get('Authorization') ?? '';
  if (!auth.startsWith('Bearer ')) return false;
  const supplied = auth.slice(7);
  return tokens.includes(supplied);
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

    if (url.pathname === '/ws') {
      if (request.headers.get('Upgrade') !== 'websocket') {
        return new Response('Expected WebSocket upgrade', { status: 426 });
      }
      if (!checkAuth(request, env)) {
        return unauthorized('invalid token');
      }
      const id = env.RELAY.idFromName('global');
      const stub = env.RELAY.get(id);
      return stub.fetch(request);
    }

    return new Response('Not found', { status: 404 });
  },
};
