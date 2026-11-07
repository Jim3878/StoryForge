// Doc-level operations shared by the MCP tools and the sf CLI.

import type * as YType from "yjs";
import type { DocSession, HostContext } from "./host.js";
import {
  createSchema, playscriptDocId, projectDocId, rulesDocId, rowsEqual, normalizeRow,
  type PlayscriptIndexEntry, type Row,
} from "./schema.js";
import { parseRules, type Rules } from "./rules.js";
import { EXTENSION_VERSION } from "./version.js";
import { createFlowGraph, flowgraphDocId, type CanonicalGraph } from "./flowgraph.js";

function cmpVersion(a: string, b: string) {
  const pa = a.split(/[.-]/).map(x => parseInt(x, 10) || 0);
  const pb = b.split(/[.-]/).map(x => parseInt(x, 10) || 0);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] ?? 0) - (pb[i] ?? 0);
    if (d !== 0) return d;
  }
  return 0;
}

// Runs fn over items with at most `limit` in flight; results keep the input order.
async function mapLimit<T, R>(items: T[], limit: number, fn: (item: T) => Promise<R>): Promise<R[]> {
  const out = new Array<R>(items.length);
  let next = 0;
  const workers = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (next < items.length) {
      const i = next++;
      out[i] = await fn(items[i]);
    }
  });
  await Promise.all(workers);
  return out;
}

const CONCURRENCY = 24;

export function createStore(ctx: HostContext) {
  const { Y } = ctx;
  const schema = createSchema(Y);
  const flow = createFlowGraph(Y);

  async function withSession<T>(workspaceId: string | undefined, fn: (s: DocSession) => Promise<T>): Promise<T> {
    const s = await ctx.openSession(workspaceId);
    try { return await fn(s); } finally { s.close(); }
  }

  async function mutate(s: DocSession, docId: string, fn: (doc: YType.Doc) => void | Promise<void>) {
    const doc = await s.load(docId);
    const before = Y.encodeStateVector(doc);
    await fn(doc);
    await s.push(docId, doc, before);
    return doc;
  }

  async function project(s: DocSession) {
    return schema.readProject(await s.load(projectDocId(s.workspaceId)));
  }

  async function requireWritable(s: DocSession) {
    const p = await project(s);
    if (p.minExtensionVersion && cmpVersion(EXTENSION_VERSION, p.minExtensionVersion) < 0) {
      throw new Error(`這個 workspace 需要 StoryForge 擴充 ${p.minExtensionVersion} 以上，目前是 ${EXTENSION_VERSION}。請更新 affine-mcp 安裝檔。`);
    }
    return p;
  }

  async function rules(s: DocSession): Promise<Rules | null> {
    const r = schema.readRules(await s.load(rulesDocId(s.workspaceId)));
    return r ? parseRules(r.json) : null;
  }

  async function resolveGuid(s: DocSession, guidOrName: string) {
    const p = await project(s);
    const hit = p.playscripts.find(x => x.guid === guidOrName)
      ?? p.playscripts.filter(x => x.name === guidOrName)[0];
    if (!hit) throw new Error(`找不到劇本「${guidOrName}」。先用 sf_list_playscripts 看有哪些劇本。`);
    const dupes = p.playscripts.filter(x => x.name === hit.name);
    if (hit.guid !== guidOrName && dupes.length > 1) {
      throw new Error(`有 ${dupes.length} 份劇本都叫「${hit.name}」，請改用 guid：${dupes.map(d => `${d.guid}（${d.chapter}）`).join("、")}`);
    }
    return hit;
  }

  async function readPlayscript(s: DocSession, guid: string) {
    const doc = await s.load(playscriptDocId(s.workspaceId, guid));
    return { ...schema.readPlayscript(doc), sv: Buffer.from(Y.encodeStateVector(doc)).toString("base64") };
  }

  // ----- sf CLI: whole-project pull / push used by local StoryForge -----

  async function pullAll(s: DocSession) {
    const p = await project(s);
    const out = await mapLimit(p.playscripts, CONCURRENCY, async e => {
      const ps = await readPlayscript(s, e.guid);
      return { ...e, rows: ps.rows.map(r => r.cells), sv: ps.sv };
    });
    return { schemaVersion: p.schemaVersion, exists: p.exists, playscripts: out };
  }

  type PushItem = PlayscriptIndexEntry & { rows: Row[]; baseRows: Row[] | null };

  async function pushAll(s: DocSession, items: PushItem[], rulesUpload?: { json: string; hash: string }) {
    await requireWritable(s);
    if (rulesUpload) {
      await mutate(s, rulesDocId(s.workspaceId), doc => { schema.writeRules(doc, rulesUpload.json, rulesUpload.hash); });
    }
    type Result = { guid: string; status: "ok" | "unchanged" | "rejected"; message?: string; rows?: Row[]; sv?: string };
    const results: Result[] = await mapLimit(items, CONCURRENCY, async (item): Promise<Result> => {
      const docId = playscriptDocId(s.workspaceId, item.guid);
      const doc = await s.load(docId);
      const current = schema.readPlayscript(doc).rows.map(r => r.cells);
      const target = item.rows.map(normalizeRow);
      const base = item.baseRows ? item.baseRows.map(normalizeRow) : null;
      const cloudChanged = base ? !rowsEqual(current, base) : current.length > 0;
      const sv = () => Buffer.from(Y.encodeStateVector(doc)).toString("base64");
      if (rowsEqual(current, target)) return { guid: item.guid, status: "unchanged", rows: current, sv: sv() };
      if (cloudChanged) return { guid: item.guid, status: "rejected", message: "雲端在上次下載後被改過，請先下載" };
      const before = Y.encodeStateVector(doc);
      doc.transact(() => {
        schema.initPlayscriptMeta(doc, item.guid, item.name);
        schema.writeRows(doc, target);
      });
      await s.push(docId, doc, before);
      return { guid: item.guid, status: "ok", rows: schema.readPlayscript(doc).rows.map(r => r.cells), sv: sv() };
    });
    const accepted = items.filter((_, i) => results[i].status !== "rejected");
    if (accepted.length) {
      await mutate(s, projectDocId(s.workspaceId), doc => {
        doc.transact(() => {
          schema.ensureProject(doc);
          for (const a of accepted) schema.upsertIndex(doc, { guid: a.guid, name: a.name, chapter: a.chapter, orphan: a.orphan });
        });
      });
    }
    return results;
  }

  // ----- one-connection incremental download (sf sync-pull) -----

  /**
   * Everything local StoryForge's 下載 needs, over one connection: the playscript index (always), the flow
   * graph and the tables — but with `since` (server ms from the previous call) only the docs changed after
   * it are read. `serverTime` is what to pass as `since` next time. Hosts without timestamps get everything.
   */
  async function syncPull(s: DocSession, since: number | null, only?: string[]) {
    const ws = s.workspaceId;
    let changed: Record<string, number> | null = null;
    if (since !== null && s.timestamps) changed = await s.timestamps(since);
    const all = changed === null && s.timestamps ? await s.timestamps(0).catch(() => null) : null;
    const serverTime = Math.max(since ?? 0, ...Object.values(changed ?? all ?? {}));
    const isChanged = (docId: string) => changed === null || docId in changed;

    const p = await project(s);
    let graph: Awaited<ReturnType<typeof pullGraph>> | null = null;
    if (!only?.length && (isChanged(flowgraphDocId(ws)) || isChanged(rulesDocId(ws)))) graph = await pullGraph(s);
    const onlySet = only?.length ? new Set(only) : null;
    const targets = p.playscripts.filter(e => (onlySet ? onlySet.has(e.guid) : isChanged(playscriptDocId(ws, e.guid))));
    const playscripts = await mapLimit(targets, CONCURRENCY, async e => {
      const ps = await readPlayscript(s, e.guid);
      return { ...e, rows: ps.rows.map(r => r.cells), sv: ps.sv };
    });
    return {
      schemaVersion: p.schemaVersion,
      exists: p.exists,
      incremental: changed !== null,
      serverTime,
      index: p.playscripts,
      graph,
      playscripts,
    };
  }

  // ----- flow graph (cloud is the master once seeded) -----

  async function pullGraph(s: DocSession) {
    const doc = await s.load(flowgraphDocId(s.workspaceId));
    const p = await project(s);
    return {
      exists: !flow.isEmpty(doc),
      enabled: p.flowGraphEnabled,
      graph: flow.isEmpty(doc) ? null : flow.readGraph(doc),
      memos: flow.readMemos(doc),
      sv: Buffer.from(Y.encodeStateVector(doc)).toString("base64"),
    };
  }

  type GraphPush = {
    graph: CanonicalGraph;
    memos?: Record<string, string>;
    nodeSchema?: string;
    // seed: only into an empty cloud graph. overwrite: replace the cloud graph with this one (two-way).
    mode: "seed" | "overwrite";
  };

  async function pushGraph(s: DocSession, body: GraphPush) {
    await requireWritable(s);
    const docId = flowgraphDocId(s.workspaceId);
    const doc = await s.load(docId);
    if (body.mode === "seed" && !flow.isEmpty(doc)) {
      return { status: "rejected" as const, message: "雲端已經有流程圖了。要用本機的蓋掉雲端，請改用「用本機流程圖覆蓋雲端」。" };
    }
    const before = Y.encodeStateVector(doc);
    let stats: unknown;
    doc.transact(() => {
      stats = flow.writeGraph(doc, body.graph);
      if (body.memos) flow.writeMemos(doc, body.memos);
    });
    await s.push(docId, doc, before);
    if (body.nodeSchema) {
      await mutate(s, rulesDocId(s.workspaceId), d => { d.getMap("rules").set("nodeSchema", body.nodeSchema); });
    }
    await mutate(s, projectDocId(s.workspaceId), d => d.transact(() => {
      schema.ensureProject(d);
      schema.enableFlowGraph(d);
    }));
    return { status: "ok" as const, stats };
  }

  return { flow, syncPull, pullGraph, pushGraph, schema, withSession, mutate, project, requireWritable, rules, resolveGuid, readPlayscript, pullAll, pushAll };
}

export type Store = ReturnType<typeof createStore>;
