// Doc-level operations shared by the MCP tools and the sf CLI.

import type * as YType from "yjs";
import type { DocSession, HostContext } from "./host.js";
import {
  createSchema, playscriptDocId, projectDocId, rulesDocId, rowsEqual, normalizeRow,
  type PlayscriptIndexEntry, type Row,
} from "./schema.js";
import { parseRules, type Rules } from "./rules.js";
import { EXTENSION_VERSION } from "./version.js";
import type { SessionPool } from "./session-pool.js";
import { createFlowGraph, flowgraphDocId, type CanonicalGraph, type NodeContent } from "./flowgraph.js";

/** Reference files local StoryForge uploads for AIs that only have MCP (AGENTS.md, the world book, ...). */
export function referencesDocId(ws: string) { return `db$${ws}$sf.references`; }
export type ReferenceUpload = { name: string; text: string; hash: string };

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

/** Progress for a long CLI call: a phase name and, when it is countable, how far along it is. */
export type Progress = (phase: string, done?: number, total?: number) => void;

export function createStore(ctx: HostContext, progress: Progress = () => {}, pool?: SessionPool) {
  const { Y } = ctx;
  const schema = createSchema(Y);
  const flow = createFlowGraph(Y);

  async function withSession<T>(workspaceId: string | undefined, fn: (s: DocSession) => Promise<T>): Promise<T> {
    if (pool) return pool.run(workspaceId, fn);
    progress("連線");
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

  async function pushAll(s: DocSession, items: PushItem[], rulesUpload?: { json: string; hash: string }, references?: ReferenceUpload[]) {
    return (await pushAllEx(s, items, rulesUpload, references)).results;
  }

  /**
   * pushAll plus, when `contentSeed` is given, the one-time seeding of the flow graph's node content
   * (衝突 / 變化 / 角色 / 地點): graphs uploaded before those fields existed have none in the cloud, and a
   * download must not read that as "cleared". Written only while the doc is not marked seeded yet.
   */
  async function pushAllEx(s: DocSession, items: PushItem[], rulesUpload?: { json: string; hash: string }, references?: ReferenceUpload[],
    contentSeed?: Record<string, NodeContent>, graphSeed?: GraphPush) {
    let contentSeeded: boolean | undefined;
    // First upload of the flow graph, in the same call (seed only: never over an existing cloud graph).
    let graph: Awaited<ReturnType<typeof pushGraph>> | undefined;
    if (graphSeed) {
      graph = await pushGraph(s, { ...graphSeed, mode: "seed" });
      if (graph.status === "ok") { contentSeeded = true; contentSeed = undefined; }
    }
    if (contentSeed) {
      const docId = flowgraphDocId(s.workspaceId);
      const doc = await s.load(docId);
      if (!flow.isEmpty(doc)) {
        if (doc.getMap("meta").get("contentSeeded") !== true) {
          progress("上傳節點內容");
          const before = Y.encodeStateVector(doc);
          doc.transact(() => {
            flow.writeContent(doc, contentSeed);
            doc.getMap("meta").set("contentSeeded", true);
          });
          await s.push(docId, doc, before);
        }
        contentSeeded = true;
      }
    }
    return { results: await pushTables(s, items, rulesUpload, references), contentSeeded, graph };
  }

  async function pushTables(s: DocSession, items: PushItem[], rulesUpload?: { json: string; hash: string }, references?: ReferenceUpload[]) {
    progress("檢查雲端");
    const p = await requireWritable(s);
    if (rulesUpload) {
      progress("上傳規則");
      await mutate(s, rulesDocId(s.workspaceId), doc => { schema.writeRules(doc, rulesUpload.json, rulesUpload.hash); });
    }
    if (references?.length) {
      progress("上傳參考資料");
      await writeReferences(s, references);
    }
    type Result = { guid: string; status: "ok" | "unchanged" | "rejected"; message?: string; rows?: Row[]; sv?: string };
    let pushed = 0;
    progress("上傳劇本", 0, items.length);
    const results: Result[] = await mapLimit(items, CONCURRENCY, async (item): Promise<Result> => {
      try { return await pushOne(item); } finally { progress("上傳劇本", ++pushed, items.length); }
    });
    async function pushOne(item: PushItem): Promise<Result> {
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
    }
    // Only touch the index when an entry is new or its name / chapter / orphan flag changed: rewriting the
    // index doc costs a load + push (~1 s) on every upload otherwise.
    const indexed = new Map(p.playscripts.map(e => [e.guid, e]));
    const accepted = items.filter((item, i) => {
      if (results[i].status === "rejected") return false;
      const e = indexed.get(item.guid);
      return !e || e.name !== item.name || e.chapter !== item.chapter
        || (item.orphan !== undefined && (e.orphan === true) !== item.orphan);
    });
    if (accepted.length || !p.exists) {
      progress("更新索引");
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

  type SyncPullOptions = {
    /** Server ms of the previous table download (null = read every table). */
    since: number | null;
    /** Same for the flow graph; it has its own, since a download can leave either part out. */
    graphSince?: number | null;
    tables?: boolean;
    graph?: boolean;
    only?: string[];
  };

  /**
   * Everything local StoryForge's 下載 needs, over one connection: the playscript index (always), the flow
   * graph and the tables — each part only when asked for, and with its `since` (server ms from the previous
   * call) only if changed after it. `serverTime` is what to pass as `since` next time for the parts that
   * were read. Hosts without timestamps get everything.
   */
  async function syncPull(s: DocSession, options: SyncPullOptions) {
    const ws = s.workspaceId;
    const { since, only } = options;
    const graphSince = options.graphSince === undefined ? since : options.graphSince;
    const wantTables = options.tables !== false;
    const wantGraph = options.graph !== false && !only?.length;
    progress("比對變動");
    // One timestamps query covers both parts: from the earlier of their two times.
    const lows = [wantTables ? since : 0, wantGraph ? graphSince : 0].filter((x): x is number => x !== null && x !== undefined);
    const needAll = (wantTables && since === null) || (wantGraph && graphSince === null) || !s.timestamps;
    const changed = s.timestamps ? await s.timestamps(needAll ? 0 : Math.min(...lows)).catch(() => null) : null;
    const serverTime = Math.max(since ?? 0, graphSince ?? 0, ...Object.values(changed ?? {}));
    const changedAfter = (docId: string, t: number | null | undefined) =>
      changed === null || t === null || t === undefined || (docId in changed && changed[docId] > t);

    progress("讀取索引");
    const p = await project(s);
    let graph: Awaited<ReturnType<typeof pullGraph>> | null = null;
    if (wantGraph && (changedAfter(flowgraphDocId(ws), graphSince) || changedAfter(rulesDocId(ws), graphSince)
      || changedAfter(projectDocId(ws), graphSince))) {
      progress("讀取流程圖");
      graph = await pullGraph(s);
    }
    const onlySet = only?.length ? new Set(only) : null;
    const targets = !wantTables && !onlySet ? []
      : p.playscripts.filter(e => (onlySet ? onlySet.has(e.guid) : changedAfter(playscriptDocId(ws, e.guid), since)));
    let read = 0;
    progress("讀取劇本", 0, targets.length);
    const playscripts = await mapLimit(targets, CONCURRENCY, async e => {
      const ps = await readPlayscript(s, e.guid);
      progress("讀取劇本", ++read, targets.length);
      return { ...e, rows: ps.rows.map(r => r.cells), sv: ps.sv };
    });
    return {
      schemaVersion: p.schemaVersion,
      exists: p.exists,
      incremental: changed !== null && since !== null,
      serverTime,
      index: p.playscripts,
      graph,
      playscripts,
    };
  }

  // ----- reference files -----

  type RefEntry = { text: string; hash: string; updatedAt: number };

  async function writeReferences(s: DocSession, refs: ReferenceUpload[]) {
    await mutate(s, referencesDocId(s.workspaceId), doc => doc.transact(() => {
      const files = doc.getMap<RefEntry>("files");
      for (const r of refs) {
        if (files.get(r.name)?.hash === r.hash) continue;
        files.set(r.name, { text: r.text, hash: r.hash, updatedAt: Date.now() });
      }
    }));
  }

  async function readReferences(s: DocSession) {
    const doc = await s.load(referencesDocId(s.workspaceId));
    const out: Record<string, RefEntry> = {};
    doc.getMap<RefEntry>("files").forEach((v, k) => { if (typeof v?.text === "string") out[k] = v; });
    return out;
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
      content: flow.readContent(doc),
      contentSeeded: doc.getMap("meta").get("contentSeeded") === true,
      sv: Buffer.from(Y.encodeStateVector(doc)).toString("base64"),
    };
  }

  type GraphPush = {
    graph: CanonicalGraph;
    memos?: Record<string, string>;
    content?: Record<string, NodeContent>;
    nodeSchema?: string;
    // seed: only into an empty cloud graph. overwrite: replace the cloud graph with this one (two-way).
    mode: "seed" | "overwrite";
  };

  async function pushGraph(s: DocSession, body: GraphPush) {
    progress("檢查雲端");
    await requireWritable(s);
    progress("上傳流程圖");
    const docId = flowgraphDocId(s.workspaceId);
    const doc = await s.load(docId);
    if (body.mode === "seed" && !flow.isEmpty(doc)) {
      return { status: "rejected" as const, message: "雲端已經有流程圖了，以雲端的為主（按「下載」取得）。" };
    }
    const before = Y.encodeStateVector(doc);
    let stats: unknown;
    doc.transact(() => {
      stats = flow.writeGraph(doc, body.graph);
      if (body.memos) flow.writeMemos(doc, body.memos);
      if (body.content) {
        flow.writeContent(doc, body.content);
        doc.getMap("meta").set("contentSeeded", true);
      }
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

  return { flow, pushAllEx, writeReferences, readReferences, syncPull, pullGraph, pushGraph, schema, withSession, mutate, project, requireWritable, rules, resolveGuid, readPlayscript, pullAll, pushAll };
}

export type Store = ReturnType<typeof createStore>;
