// One kept-open connection per workspace for a long-lived host (the MCP server), so each tool call does not
// pay for connecting and joining the workspace again (~2 s). The CLI keeps opening one session per call.

import type { DocSession, HostContext } from "./host.js";

const CONNECTION_ERROR = /disconnect|not connected|socket|timed? ?out|ECONN|EPIPE|transport/i;

export class SessionPool {
  private entries = new Map<string, { session: Promise<DocSession>; timer?: ReturnType<typeof setTimeout> }>();

  constructor(private readonly ctx: HostContext, private readonly idleMs = 60_000) {}

  private key(workspaceId?: string) {
    return workspaceId || this.ctx.defaultWorkspaceId || "";
  }

  get(workspaceId?: string): Promise<DocSession> {
    const key = this.key(workspaceId);
    let entry = this.entries.get(key);
    if (entry) {
      // A host that can tell: a dropped socket gets a fresh session instead of failing the call.
      const session = entry.session;
      entry.session = session.then(s => (s.alive && !s.alive() ? (s.close(), this.ctx.openSession(workspaceId)) : s));
    } else {
      entry = { session: this.ctx.openSession(workspaceId) };
      this.entries.set(key, entry);
    }
    const mine = entry;
    entry.session.catch(() => { if (this.entries.get(key) === mine) this.entries.delete(key); });
    this.touch(key);
    return entry.session;
  }

  touch(workspaceId?: string) {
    const key = this.key(workspaceId);
    const entry = this.entries.get(key);
    if (!entry) return;
    if (entry.timer) clearTimeout(entry.timer);
    entry.timer = setTimeout(() => this.drop(key), this.idleMs);
    (entry.timer as any).unref?.();
  }

  drop(workspaceId?: string) {
    const key = this.key(workspaceId);
    const entry = this.entries.get(key);
    if (!entry) return;
    this.entries.delete(key);
    if (entry.timer) clearTimeout(entry.timer);
    entry.session.then(s => s.close(), () => {});
  }

  /** Runs fn on the pooled session; a connection failure is retried once on a fresh one. */
  async run<T>(workspaceId: string | undefined, fn: (s: DocSession) => Promise<T>): Promise<T> {
    for (let attempt = 0; ; attempt++) {
      const s = await this.get(workspaceId);
      try {
        return await fn(s);
      } catch (error) {
        if (attempt === 0 && CONNECTION_ERROR.test(error instanceof Error ? error.message : String(error))) {
          this.drop(workspaceId);
          continue;
        }
        throw error;
      } finally {
        this.touch(workspaceId);
      }
    }
  }
}
