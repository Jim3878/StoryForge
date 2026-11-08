// AI presence: an MCP client shows up in the web editors like a person does (avatar, cursor, selection,
// node locks), and reads who else is there before it writes. Speaks the y-protocols awareness wire format
// directly (no y-protocols dependency): an update is
//   varUint(count) then per client: varUint(clientID) varUint(clock) varString(JSON state | "null").

import type { DocSession, HostContext, PresenceChannel } from "./host.js";
import type { SessionPool } from "./session-pool.js";

// ---------- wire format ----------

function writeVarUint(out: number[], n: number) {
  while (n > 0x7f) {
    out.push(0x80 | (n & 0x7f));
    n = Math.floor(n / 128);
  }
  out.push(n);
}

function writeVarString(out: number[], s: string) {
  const bytes = new TextEncoder().encode(s);
  writeVarUint(out, bytes.length);
  for (const b of bytes) out.push(b);
}

export function encodeAwareness(entries: { clientId: number; clock: number; state: unknown }[]): Uint8Array {
  const out: number[] = [];
  writeVarUint(out, entries.length);
  for (const e of entries) {
    writeVarUint(out, e.clientId);
    writeVarUint(out, e.clock);
    writeVarString(out, e.state === null ? "null" : JSON.stringify(e.state));
  }
  return new Uint8Array(out);
}

export function decodeAwareness(bin: Uint8Array): { clientId: number; clock: number; state: any }[] {
  let pos = 0;
  const readVarUint = () => {
    let n = 0, mult = 1;
    for (;;) {
      if (pos >= bin.length) throw new Error("truncated awareness update");
      const b = bin[pos++];
      n += (b & 0x7f) * mult;
      if (b < 0x80) return n;
      mult *= 128;
    }
  };
  const readVarString = () => {
    const len = readVarUint();
    const s = new TextDecoder().decode(bin.subarray(pos, pos + len));
    pos += len;
    return s;
  };
  const out: { clientId: number; clock: number; state: any }[] = [];
  const count = readVarUint();
  for (let i = 0; i < count; i++) {
    const clientId = readVarUint();
    const clock = readVarUint();
    const raw = readVarString();
    let state: any = null;
    try { state = JSON.parse(raw); } catch { state = null; }
    out.push({ clientId, clock, state });
  }
  return out;
}

// ---------- one room ----------

// Same palette as the web editors (modules/storyforge/docs.ts PRESENCE_COLORS).
// The AI takes from the end, so it rarely lands on a person's usual colour.
const COLORS = ["#5c940d", "#9c36b5", "#1098ad", "#e03131", "#5b5ce0", "#b8860b", "#0ca5a5", "#d6336c", "#2fa05a", "#8e44ad", "#2a9df4", "#f08a24"];

export type Peer = { clientId: number; user: { name: string; color: string; ai?: boolean }; state: Record<string, any> };

export class PresenceRoom {
  readonly clientId = Math.floor(Math.random() * 0xffffffff) >>> 0;
  private clock = 0;
  private state: Record<string, any> | null = null;
  private readonly peers = new Map<number, { clock: number; state: any; at: number }>();
  private renew: ReturnType<typeof setInterval> | null = null;

  private constructor(private readonly channel: PresenceChannel) {
    channel.onUpdate(bin => {
      let entries;
      try { entries = decodeAwareness(bin); } catch { return; }
      for (const e of entries) {
        if (e.clientId === this.clientId) continue;
        const cur = this.peers.get(e.clientId);
        if (cur && cur.clock > e.clock) continue;
        if (e.state === null) this.peers.delete(e.clientId);
        else this.peers.set(e.clientId, { clock: e.clock, state: e.state, at: Date.now() });
      }
    });
    channel.onCollect(() => this.broadcast());
    // Web clients drop a state not renewed for 30 s.
    this.renew = setInterval(() => this.broadcast(), 10_000);
    (this.renew as any).unref?.();
  }

  /** Joins the room and waits briefly for everyone's current state. Null on hosts without presence. */
  static async join(s: DocSession, docId: string, settleMs = 500): Promise<PresenceRoom | null> {
    if (!s.presence) return null;
    const room = new PresenceRoom(await s.presence(docId));
    await new Promise(r => setTimeout(r, settleMs));
    return room;
  }

  others(): Peer[] {
    const out: Peer[] = [];
    const stale = Date.now() - 35_000;
    for (const [clientId, p] of this.peers) {
      if (p.at < stale || !p.state?.user) continue;
      out.push({ clientId, user: p.state.user, state: p.state });
    }
    return out;
  }

  /** A colour none of the people here has. */
  freeColor() {
    const used = new Set(this.others().map(p => p.user.color));
    return COLORS.find(c => !used.has(c)) ?? COLORS[this.clientId % COLORS.length];
  }

  set(fields: Record<string, any>) {
    this.state = { ...(this.state ?? {}), ...fields };
    this.clock++;
    this.broadcast();
  }

  private broadcast() {
    if (!this.state) return;
    this.channel.send(encodeAwareness([{ clientId: this.clientId, clock: this.clock, state: this.state }]));
  }

  leave() {
    if (this.renew) clearInterval(this.renew);
    this.renew = null;
    if (this.state) {
      this.clock++;
      this.channel.send(encodeAwareness([{ clientId: this.clientId, clock: this.clock, state: null }]));
    }
    this.state = null;
    this.channel.leave();
  }
}

// ---------- the AI's presence, kept for a while after each write ----------

/**
 * The AI's presence rooms, on the pooled connection, so the avatar / selection stays visible for a while
 * after a write instead of vanishing when the tool call ends. A room is left after `lingerMs` unused.
 */
export class AiPresence {
  private rooms = new Map<string, { room: Promise<PresenceRoom | null>; session: DocSession | null; timer?: ReturnType<typeof setTimeout> }>();
  private userName: Promise<string> | null = null;

  constructor(private readonly ctx: HostContext, private readonly pool: SessionPool, private readonly lingerMs = 15_000) {}

  /** "<account> 的 AI" */
  name() {
    this.userName ??= (this.ctx.currentUser?.() ?? Promise.resolve({ name: "", email: "" }))
      .then(u => `${u.name || "某人"} 的 AI`)
      .catch(() => "AI");
    return this.userName;
  }

  async room(workspaceId: string, docId: string): Promise<PresenceRoom | null> {
    const key = `${workspaceId}
${docId}`;
    const session = await this.pool.get(workspaceId);
    let entry = this.rooms.get(key);
    if (entry && entry.session !== session) {
      // The pooled connection was replaced: the old room went with it.
      this.leave(key);
      entry = undefined;
    }
    if (!entry) {
      const e: { room: Promise<PresenceRoom | null>; session: DocSession | null; timer?: ReturnType<typeof setTimeout> } = {
        room: PresenceRoom.join(session, docId).catch(() => null), session,
      };
      entry = e;
      this.rooms.set(key, entry);
      const r = await entry.room;
      if (r) r.set({ user: { name: await this.name(), color: r.freeColor(), ai: true } });
    }
    if (entry.timer) clearTimeout(entry.timer);
    entry.timer = setTimeout(() => this.leave(key), this.lingerMs);
    (entry.timer as any).unref?.();
    return entry.room;
  }

  private leave(key: string) {
    const entry = this.rooms.get(key);
    if (!entry) return;
    this.rooms.delete(key);
    if (entry.timer) clearTimeout(entry.timer);
    entry.room.then(r => { try { r?.leave(); } catch { /* connection already gone */ } });
  }
}

export const FLOWGRAPH_ROOM = "db$sf.flowgraph.main";
export const playscriptRoom = (guid: string) => `db$sf.playscript.${guid}`;
