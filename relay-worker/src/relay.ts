import { DurableObject } from 'cloudflare:workers';

interface AircraftEntry {
  data: Record<string, unknown>;
  receivedAt: number; // epoch seconds
  // WebSockets that have reported this aircraft, keyed by their last
  // contribution timestamp. Lets us tell each recipient how many OTHER
  // peers also see the aircraft, so the UI can render "also seen by N".
  contributors: Map<WebSocket, number>;
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
    // Log only the prefix so the full token isn't recoverable from logs;
    // 8 hex chars is enough to disambiguate a specific instance in support.
    console.log(JSON.stringify({ event: 'register', token_prefix: token.slice(0, 8) }));
    return Response.json({ token });
  }

  private async handleWebSocketUpgrade(request: Request): Promise<Response> {
    const auth = request.headers.get('Authorization') ?? '';
    if (!auth.startsWith('Bearer ')) {
      console.log(JSON.stringify({ event: 'auth_fail', reason: 'missing_token' }));
      return Response.json({ type: 'auth_fail', reason: 'missing token' }, { status: 401 });
    }
    const supplied = auth.slice(7).trim();
    if (!supplied) {
      console.log(JSON.stringify({ event: 'auth_fail', reason: 'missing_token' }));
      return Response.json({ type: 'auth_fail', reason: 'missing token' }, { status: 401 });
    }
    const record = await this.ctx.storage.get<TokenRecord>(TOKEN_PREFIX + supplied);
    if (!record) {
      console.log(JSON.stringify({
        event: 'auth_fail',
        reason: 'unknown_token',
        token_prefix: supplied.slice(0, 8),
      }));
      return Response.json({ type: 'auth_fail', reason: 'unknown token' }, { status: 401 });
    }
    const now = Math.floor(Date.now() / 1000);
    record.lastSeenS = now;
    await this.ctx.storage.put(TOKEN_PREFIX + supplied, record);
    await this.maybeEvictStaleTokens(now);

    if (this.connections.size >= MAX_CONNECTIONS) {
      console.log(JSON.stringify({
        event: 'connection_rejected',
        reason: 'cap_reached',
        connections: this.connections.size,
      }));
      return new Response('Too many connections', { status: 503 });
    }

    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);

    // Use hibernation API so the DO sleeps between messages
    this.ctx.acceptWebSocket(server);
    this.connections.add(server);
    console.log(JSON.stringify({
      event: 'connect',
      token_prefix: supplied.slice(0, 8),
      connections: this.connections.size,
    }));

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
    console.log(JSON.stringify({
      event: 'token_sweep',
      total_tokens: entries.size,
      evicted: toDelete.length,
    }));
  }

  override webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): void {
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
      const key = icao.toLowerCase();
      const existing = this.aggregate.get(key);
      if (existing) {
        existing.data = ac as Record<string, unknown>;
        existing.receivedAt = now;
        existing.contributors.set(ws, now);
      } else {
        this.aggregate.set(key, {
          data: ac as Record<string, unknown>,
          receivedAt: now,
          contributors: new Map([[ws, now]]),
        });
      }
    }
  }

  override webSocketClose(ws: WebSocket): void {
    this.connections.delete(ws);
    this.dropContributor(ws);
    console.log(JSON.stringify({ event: 'disconnect', connections: this.connections.size }));
  }

  override webSocketError(ws: WebSocket, error: unknown): void {
    this.connections.delete(ws);
    this.dropContributor(ws);
    try { ws.close(); } catch { /* already closed */ }
    console.log(JSON.stringify({
      event: 'ws_error',
      connections: this.connections.size,
      message: error instanceof Error ? error.message : String(error),
    }));
  }

  // Remove `ws` from every aircraft's contributor map. Called on clean
  // disconnect so the seen_by_others count drops immediately rather than
  // waiting up to STALE_S for the lazy eviction in broadcast() to catch up.
  private dropContributor(ws: WebSocket): void {
    for (const entry of this.aggregate.values()) {
      entry.contributors.delete(ws);
    }
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

    // Evict stale aggregate entries; for each surviving entry also sweep
    // out per-WS contributor timestamps that have aged past the same
    // cutoff, so seen_by_others counts a contributor only as long as
    // they're actively reporting the aircraft.
    const fresh: AircraftEntry[] = [];
    for (const [icao, entry] of this.aggregate) {
      if (entry.receivedAt < cutoff) {
        this.aggregate.delete(icao);
        continue;
      }
      for (const [ws, ts] of entry.contributors) {
        if (ts < cutoff) entry.contributors.delete(ws);
      }
      fresh.push(entry);
    }

    // Total connected count goes in the envelope; per-aircraft counts go
    // in `seen_by_others` and are computed per-recipient (each WS subtracts
    // itself from every aircraft it's contributing to).
    const peers = Math.max(0, this.connections.size - 1);
    const dead: WebSocket[] = [];

    for (const ws of this.connections) {
      const aircraft = fresh.map(entry => ({
        ...entry.data,
        seen_by_others: entry.contributors.size - (entry.contributors.has(ws) ? 1 : 0),
      }));
      const payload = JSON.stringify({ type: 'aggregate', peers, aircraft });
      try {
        ws.send(payload);
      } catch {
        dead.push(ws);
      }
    }

    for (const ws of dead) {
      this.connections.delete(ws);
      this.dropContributor(ws);
      try { ws.close(); } catch { /* already closed */ }
    }
  }
}
