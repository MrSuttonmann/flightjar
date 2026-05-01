import { DurableObject } from 'cloudflare:workers';

interface AircraftEntry {
  data: Record<string, unknown>;
  receivedAt: number; // epoch seconds
}

interface TokenRecord {
  // Last time this token authenticated, in epoch seconds. Used by the
  // periodic eviction sweep so dead deployments don't accumulate forever.
  lastSeenS: number;
}

const STALE_S = 65;
const BROADCAST_INTERVAL_MS = 5_000;
// Per-DO connection backstop. Registration gives us per-instance identity
// so abuse can be handled by revocation rather than a tight cap; this
// just stops a single misbehaving caller from saturating one DO's memory.
const MAX_CONNECTIONS = 5_000;
// Tokens unused for this long get evicted. Operators that come back online
// after a longer pause will just register again on next start.
const TOKEN_TTL_S = 90 * 24 * 60 * 60;
const TOKEN_PREFIX = 'token:';
const EVICTION_THROTTLE_S = 24 * 60 * 60;

export class RelayDurableObject extends DurableObject {
  private aggregate = new Map<string, AircraftEntry>();
  private connections = new Set<WebSocket>();
  // In-memory throttle for the eviction sweep. Resets to 0 if the DO is
  // evicted from memory, which at worst causes one extra sweep on the
  // next request — cheap and self-healing.
  private lastEvictionS = 0;

  constructor(ctx: DurableObjectState, env: never) {
    super(ctx, env);
    // Restore hibernated WebSockets on DO restart
    for (const ws of this.ctx.getWebSockets()) {
      this.connections.add(ws);
    }
  }

  override async fetch(request: Request): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname === '/stats') {
      return Response.json({
        connections: this.connections.size,
        aircraft: this.aggregate.size,
      });
    }

    if (url.pathname === '/register') {
      return this.handleRegister();
    }

    return this.handleWebSocketUpgrade(request);
  }

  private async handleRegister(): Promise<Response> {
    const bytes = new Uint8Array(32);
    crypto.getRandomValues(bytes);
    const token = Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
    const now = Math.floor(Date.now() / 1000);
    await this.ctx.storage.put<TokenRecord>(TOKEN_PREFIX + token, { lastSeenS: now });
    await this.maybeEvictStaleTokens(now);
    return Response.json({ token });
  }

  private async handleWebSocketUpgrade(request: Request): Promise<Response> {
    const auth = request.headers.get('Authorization') ?? '';
    if (!auth.startsWith('Bearer ')) {
      return Response.json({ type: 'auth_fail', reason: 'missing token' }, { status: 401 });
    }
    const supplied = auth.slice(7).trim();
    if (!supplied) {
      return Response.json({ type: 'auth_fail', reason: 'missing token' }, { status: 401 });
    }
    const record = await this.ctx.storage.get<TokenRecord>(TOKEN_PREFIX + supplied);
    if (!record) {
      return Response.json({ type: 'auth_fail', reason: 'unknown token' }, { status: 401 });
    }
    const now = Math.floor(Date.now() / 1000);
    record.lastSeenS = now;
    await this.ctx.storage.put(TOKEN_PREFIX + supplied, record);
    await this.maybeEvictStaleTokens(now);

    if (this.connections.size >= MAX_CONNECTIONS) {
      return new Response('Too many connections', { status: 503 });
    }

    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);

    // Use hibernation API so the DO sleeps between messages
    this.ctx.acceptWebSocket(server);
    this.connections.add(server);

    // Schedule the first broadcast alarm if not already set
    const current = await this.ctx.storage.getAlarm();
    if (current === null) {
      await this.ctx.storage.setAlarm(Date.now() + BROADCAST_INTERVAL_MS);
    }

    return new Response(null, { status: 101, webSocket: client });
  }

  private async maybeEvictStaleTokens(nowS: number): Promise<void> {
    if (nowS - this.lastEvictionS < EVICTION_THROTTLE_S) return;
    this.lastEvictionS = nowS;
    const cutoff = nowS - TOKEN_TTL_S;
    const entries = await this.ctx.storage.list<TokenRecord>({ prefix: TOKEN_PREFIX });
    const toDelete: string[] = [];
    for (const [key, record] of entries) {
      if (record.lastSeenS < cutoff) toDelete.push(key);
    }
    if (toDelete.length > 0) {
      await this.ctx.storage.delete(toDelete);
    }
  }

  override webSocketMessage(_ws: WebSocket, message: string | ArrayBuffer): void {
    if (typeof message !== 'string') return;

    let parsed: Record<string, unknown>;
    try {
      parsed = JSON.parse(message) as Record<string, unknown>;
    } catch {
      return;
    }

    if (parsed['type'] !== 'snapshot') return;

    const aircraft = parsed['aircraft'];
    if (!Array.isArray(aircraft)) return;

    const now = Date.now() / 1000;
    for (const ac of aircraft) {
      if (typeof ac !== 'object' || ac === null) continue;
      const icao = (ac as Record<string, unknown>)['icao'];
      if (typeof icao !== 'string' || !icao) continue;
      this.aggregate.set(icao.toLowerCase(), { data: ac as Record<string, unknown>, receivedAt: now });
    }
  }

  override webSocketClose(ws: WebSocket): void {
    this.connections.delete(ws);
  }

  override webSocketError(ws: WebSocket): void {
    this.connections.delete(ws);
    try { ws.close(); } catch { /* already closed */ }
  }

  override async alarm(): Promise<void> {
    this.broadcast();
    // Re-schedule unless there are no connections (DO will hibernate)
    if (this.connections.size > 0) {
      await this.ctx.storage.setAlarm(Date.now() + BROADCAST_INTERVAL_MS);
    }
  }

  private broadcast(): void {
    if (this.connections.size === 0) return;

    const now = Date.now() / 1000;
    const cutoff = now - STALE_S;

    // Evict stale and collect fresh
    const fresh: Record<string, unknown>[] = [];
    for (const [icao, entry] of this.aggregate) {
      if (entry.receivedAt < cutoff) {
        this.aggregate.delete(icao);
      } else {
        fresh.push(entry.data);
      }
    }

    if (fresh.length === 0 && this.connections.size === 0) return;

    const payload = JSON.stringify({ type: 'aggregate', aircraft: fresh });
    const dead: WebSocket[] = [];

    for (const ws of this.connections) {
      try {
        ws.send(payload);
      } catch {
        dead.push(ws);
      }
    }

    for (const ws of dead) {
      this.connections.delete(ws);
      try { ws.close(); } catch { /* already closed */ }
    }
  }
}
