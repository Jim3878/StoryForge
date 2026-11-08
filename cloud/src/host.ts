// What affine-mcp hands an extension. Kept as a plain interface so this package has no runtime
// dependency on affine-mcp (or on its yjs / zod copies) — the host passes those in.

import type * as YType from "yjs";

export type DocSession = {
  workspaceId: string;
  /** Loads a doc (empty Y.Doc when it does not exist yet). */
  load(docId: string): Promise<YType.Doc>;
  /** Pushes everything in `doc` that is newer than `before` (a state vector from right after load). */
  push(docId: string, doc: YType.Doc, before: Uint8Array): Promise<void>;
  /** docId -> last update time (server ms) for docs changed after `since`; missing on older hosts. */
  timestamps?(since: number): Promise<Record<string, number>>;
  /** Joins a doc's awareness room (local doc id, e.g. `db$sf.flowgraph.main`); missing on older hosts. */
  presence?(docId: string): Promise<PresenceChannel>;
  /** False once the connection dropped (missing on older hosts). */
  alive?(): boolean;
  close(): void;
};

export type PresenceChannel = {
  send(update: Uint8Array): void;
  onUpdate(cb: (update: Uint8Array) => void): void;
  onCollect(cb: () => void): void;
  leave(): void;
};

export type ToolResult = { content: { type: "text"; text: string }[]; structuredContent?: unknown; isError?: boolean };

export type HostContext = {
  Y: typeof YType;
  z: any; // zod v3 instance of the host
  hostVersion: string;
  defaultWorkspaceId?: string;
  openSession(workspaceId?: string): Promise<DocSession>;
  /** The signed-in account (missing on older hosts). */
  currentUser?(): Promise<{ name: string; email: string }>;
  /** Registers a tool through the host's own wrapping (error handling, profile filtering). */
  registerTool(
    name: string,
    options: { title: string; description: string; inputSchema: Record<string, unknown> },
    handler: (args: any) => Promise<ToolResult>,
    flags: { readOnly: boolean },
  ): void;
};

export type Extension = {
  name: string;
  version: string;
  register(ctx: HostContext): void;
  cli?: { command: string; summary: string; run(args: string[], ctx: HostContext): Promise<void> };
};
