import { DurableObject } from 'cloudflare:workers';

interface AircraftEntry {
  data: Record<string, unknown>;
  receivedAt: number; // epoch seconds
}

const STALE_S = 65;
const BROADCAST_INTERVAL_MS = 5_000;
const MAX_INSTANCES = 50;

export class RelayDurableObject extends DurableObject {
  private aggregate = new Map<string, AircraftEntry>();
  private connections = new Set<WebSocket>();

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

    if (this.connections.size >= MAX_INSTANCES) {
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
