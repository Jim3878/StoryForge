// sf_* MCP tools for the flow graph and the reference files, for an AI that only has MCP (no local
// StoryForge data folder). Writes go straight into the shared flow graph doc while people may be editing it,
// so every write first joins the graph's presence room: a node someone is holding (selected / dragged /
// edited in the web editor) is refused, and the AI shows up there as "<account> 的 AI" with its own
// avatar, cursor and selection while it works.

import type * as YType from "yjs";
import type { DocSession, HostContext, ToolResult } from "./host.js";
import type { Store } from "./store.js";
import { edgeKey, flowgraphDocId, NODE_TEXT_FIELDS, type CanonicalEdge, type CanonicalGraph, type CanonicalNode, type NodeTextField } from "./flowgraph.js";
import { playscriptDocId, projectDocId, rulesDocId } from "./schema.js";
import { AiPresence, FLOWGRAPH_ROOM, type PresenceRoom } from "./presence.js";

type Port = { FieldName: string; PortName?: string; AllowMultiple?: boolean };
type NodeType = {
  TypeName: string; DisplayName?: string; MenuName?: string | null;
  IdentityFields: { FieldName: string; FieldType: string }[]; Inputs: Port[]; Outputs: Port[];
};

const PLAYSCRIPT = "playscript";

// What sf_read_guide puts in front of AGENTS.md: AGENTS.md is written for the local data folder; this maps
// each local file / action it mentions to the cloud tool that replaces it.
const CLOUD_PREFACE = `# 雲端版說明（先讀這段）

你是透過 MCP 連到 StoryForge 雲端，手上**沒有**本機資料夾。下面的 AGENTS.md 是給本機用的，裡面提到的檔案與動作在雲端請改用這些工具：

| AGENTS.md 提到的 | 雲端改用 |
|---|---|
| 世界書（StoryOutline.json） | sf_read_reference name="StoryOutline" |
| AudioNotes.json、LdtkTransports.json、GuidePathPoints.json、CharacterCards.json、PlayscriptNames.json | sf_read_reference（用 path 或 search 只取需要的部分） |
| 劇本 Markdown 表格（Nodes\\<章節>\\<節點>.md） | sf_read_playscript / sf_update_cells / sf_insert_rows / sf_delete_rows / sf_move_rows / sf_write_playscript |
| 節點 JSON 的 Memo、衝突（Conflict）、變化（Change） | sf_read_node / sf_update_node |
| session.json、connections.json（流程圖節點、群組、連線） | sf_find_nodes / sf_read_node / sf_add_node / sf_connect / sf_disconnect / sf_update_node |
| 功能欄合法值、名字與表情清單 | sf_read_rules；寫入前可用 sf_validate 檢查 |

雲端的差別：
- 大家同時在線上編輯。別人正在選取或拖動的節點是鎖住的，寫入會被拒絕並告訴你是誰；請等一下或請使用者跟對方協調，不要換個方式硬改。你寫入時，網頁上會看到「某某 的 AI」的頭像和選取框。
- 參考資料（世界書、傳送點、引導點等）是本機 StoryForge 上傳的副本，雲端是唯讀的。GuidePathPoints 的 memo 在雲端不能改。
- storyforge-format 排版 skill 不適用：雲端表格沒有 Markdown 原始碼，不需要排版。
- 刪除節點請由人在網頁版做；你可以新增節點、改節點內容、接線與拆線。
- 「劇本修改流程」照樣適用：討論模式中只能讀，使用者明確確認後才可以用寫入工具。

---

`;

function ok(data: unknown): ToolResult {
  const structured = data !== null && typeof data === "object" && !Array.isArray(data) ? data : { value: data };
  return { content: [{ type: "text", text: JSON.stringify(data) }], structuredContent: structured };
}

function fail(message: string, extra?: Record<string, unknown>): ToolResult {
  const body = { error: message, ...extra };
  return { content: [{ type: "text", text: JSON.stringify(body) }], structuredContent: body, isError: true };
}

class ToolError extends Error {
  constructor(message: string, readonly extra?: Record<string, unknown>) { super(message); }
}

const identityOf = (n: CanonicalNode) => {
  const v = n.data.playscriptId ?? n.data.flagId;
  return v === undefined || v === null || v === "" ? "" : String(v);
};

export function registerGraphTools(ctx: HostContext, store: Store, presence: AiPresence) {
  const { z, Y } = ctx;
  const ws = z.string().optional().describe("AFFiNE workspace id；省略時用預設 workspace");
  const nodeRef = z.string().describe("節點 id（guid），或劇本節點的 playscriptId（例如 A10/ReL10_澪音匯合）");
  const contentShape = {
    memo: z.string().optional().describe("節點 Memo"),
    conflict: z.string().optional().describe("衝突"),
    change: z.string().optional().describe("變化"),
    characters: z.string().optional().describe("角色"),
    location: z.string().optional().describe("地點"),
  };

  // ---------- reading ----------

  type GraphView = {
    s: DocSession; doc: YType.Doc; graph: CanonicalGraph; types: Map<string, NodeType>;
    byId: Map<string, CanonicalNode>;
  };

  async function view(s: DocSession): Promise<GraphView> {
    const [doc, rulesDoc] = await Promise.all([s.load(flowgraphDocId(s.workspaceId)), s.load(rulesDocId(s.workspaceId))]);
    if (store.flow.isEmpty(doc)) throw new ToolError("這個 workspace 還沒有雲端流程圖。請先在本機 StoryForge 的雲端同步分頁按「上傳流程圖」。");
    const graph = store.flow.readGraph(doc);
    let types = new Map<string, NodeType>();
    try {
      const raw = store.schema.readNodeSchema(rulesDoc);
      if (raw) types = new Map((JSON.parse(raw).NodeTypes as NodeType[]).map(t => [t.TypeName, t]));
    } catch { /* no schema: type checks are skipped */ }
    return { s, doc, graph, types, byId: new Map(graph.nodes.map(n => [n.id, n])) };
  }

  const typeLabel = (v: GraphView, type: string) => v.types.get(type)?.DisplayName || type;
  const titleOf = (v: GraphView, n: CanonicalNode) => identityOf(n) || typeLabel(v, n.type);
  const groupsOf = (v: GraphView, id: string) => v.graph.groups.filter(g => g.nodes.includes(id)).map(g => g.title);

  function resolve(v: GraphView, ref: string): CanonicalNode {
    const direct = v.byId.get(ref);
    if (direct) return direct;
    const hits = v.graph.nodes.filter(n => identityOf(n) === ref);
    const scripts = hits.filter(n => n.type === PLAYSCRIPT);
    if (scripts.length === 1) return scripts[0];
    if (hits.length === 1) return hits[0];
    if (hits.length === 0) throw new ToolError(`找不到節點「${ref}」。用 sf_find_nodes 搜尋。`);
    throw new ToolError(`有 ${hits.length} 個節點都叫「${ref}」，請改用 id`, {
      candidates: hits.map(n => ({ id: n.id, type: n.type, typeLabel: typeLabel(v, n.type), groups: groupsOf(v, n.id) })),
    });
  }

  function summary(v: GraphView, n: CanonicalNode) {
    return { id: n.id, type: n.type, typeLabel: typeLabel(v, n.type), title: titleOf(v, n), groups: groupsOf(v, n.id) };
  }

  function nodeDetail(v: GraphView, n: CanonicalNode) {
    const memos = store.flow.readMemos(v.doc);
    const content = store.flow.readContent(v.doc)[n.id] ?? {};
    const t = v.types.get(n.type);
    const link = (e: CanonicalEdge, otherId: string) => {
      const o = v.byId.get(otherId);
      return o ? { id: o.id, title: titleOf(v, o), type: o.type } : { id: otherId };
    };
    return {
      ...summary(v, n),
      fields: n.data,
      position: { x: n.position.x, y: n.position.y },
      memo: memos[n.id] ?? "",
      ...Object.fromEntries(NODE_TEXT_FIELDS.map(f => [f, content[f] ?? ""])),
      inputs: v.graph.edges.filter(e => e.toNode === n.id).map(e => ({ port: e.toPort, from: link(e, e.fromNode), fromPort: e.fromPort })),
      outputs: v.graph.edges.filter(e => e.fromNode === n.id).map(e => ({ port: e.fromPort, to: link(e, e.toNode), toPort: e.toPort })),
      ports: t ? { inputs: t.Inputs.map(p => p.FieldName), outputs: t.Outputs.map(p => p.FieldName) } : undefined,
    };
  }

  // ---------- presence / locks ----------

  async function graphRoom(s: DocSession) {
    return presence.room(s.workspaceId, FLOWGRAPH_ROOM).catch(() => null);
  }

  /** Who (other than this AI) is holding any of these nodes right now. */
  function holders(room: PresenceRoom | null, ids: string[]) {
    if (!room) return [];
    const out: { node: string; by: string }[] = [];
    for (const p of room.others()) {
      const locked = p.state.flowLocked && typeof p.state.flowLocked === "object" ? p.state.flowLocked : {};
      for (const id of ids) if (id in locked) out.push({ node: id, by: p.user.name });
    }
    return out;
  }

  function refuseIfHeld(v: GraphView, room: PresenceRoom | null, ids: string[]) {
    const held = holders(room, ids);
    if (!held.length) return;
    throw new ToolError(
      held.map(h => `${h.by} 正在動「${titleOf(v, v.byId.get(h.node) ?? ({ data: {}, type: "" } as any)) || h.node}」`).join("；") +
        "，這些節點現在鎖住了，什麼都沒有寫入。請等對方放開（取消選取）再試，或請使用者協調。",
      { lockedBy: held });
  }

  /** Shows the AI working on `id` (selected, locked, cursor on it) until `release`. */
  function hold(room: PresenceRoom | null, n: { id: string; position: { x: number; y: number; width: number; height: number } }) {
    room?.set({
      flowSelected: n.id,
      flowLocked: { [n.id]: Date.now() },
      flowCursor: { x: n.position.x + n.position.width / 2, y: n.position.y + n.position.height / 2 },
    });
  }
  // After the write the lock goes at once (people must not wait for an AI); the selection and cursor stay
  // until the presence connection idles out, so everyone sees what it just touched.
  const release = (room: PresenceRoom | null) => room?.set({ flowLocked: {} });

  async function writeGraph<T>(workspaceId: string | undefined, fn: (v: GraphView, room: PresenceRoom | null) => Promise<T> | T) {
    return store.withSession(workspaceId, async s => {
      await store.requireWritable(s);
      const [v, room] = await Promise.all([view(s), graphRoom(s)]);
      const before = Y.encodeStateVector(v.doc);
      try {
        const result = await fn(v, room);
        await s.push(flowgraphDocId(s.workspaceId), v.doc, before);
        return result;
      } finally {
        release(room);
      }
    });
  }

  const run = (fn: () => Promise<unknown>) => fn().then(r => (r && typeof r === "object" && "content" in (r as any) ? (r as ToolResult) : ok(r)))
    .catch(e => (e instanceof ToolError ? fail(e.message, e.extra) : fail(e instanceof Error ? e.message : String(e))));

  // ---------- node helpers for writes ----------

  function nodeMap(v: GraphView, id: string) {
    const m = v.doc.getMap<YType.Map<unknown>>("nodes").get(id);
    if (!(m instanceof Y.Map)) throw new ToolError(`節點 ${id} 不在雲端流程圖裡（可能剛被刪掉）`);
    return m;
  }

  function writeContent(v: GraphView, id: string, c: Partial<Record<"memo" | NodeTextField, string | undefined>>) {
    let changed = 0;
    const given = ["memo", ...NODE_TEXT_FIELDS].filter(f => (c as any)[f] !== undefined);
    const n = v.byId.get(id);
    // Local StoryForge keeps Memo / 衝突 / 變化 / 角色 / 地點 only for 劇本 nodes.
    if (given.length && n && n.type !== PLAYSCRIPT)
      throw new ToolError(`只有劇本節點有 Memo、衝突、變化、角色、地點；${typeLabel(v, n.type)}節點沒有`);
    // Before local StoryForge has seeded them, the cloud's 衝突/變化/... are not the real ones yet (the first
    // 上傳 would overwrite them).
    if (NODE_TEXT_FIELDS.some(f => c[f] !== undefined) && v.doc.getMap("meta").get("contentSeeded") !== true)
      throw new ToolError("雲端還沒有節點的衝突／變化／角色／地點（本機還沒上傳過）。請使用者先在本機 StoryForge 按「上傳」，Memo 可以先改。");
    if (c.memo !== undefined && store.flow.setText(store.flow.memoText(v.doc, id), c.memo)) changed++;
    for (const f of NODE_TEXT_FIELDS) {
      const value = c[f];
      if (value !== undefined && store.flow.setText(store.flow.contentText(v.doc, id, f), value)) changed++;
    }
    return changed;
  }

  function playscriptNames(v: GraphView, except?: string) {
    return new Set(v.graph.nodes.filter(n => n.type === PLAYSCRIPT && n.id !== except).map(identityOf).filter(Boolean));
  }

  // A 劇本 node owns a table: index entry + table meta, the same as the web editor's syncTables.
  async function syncTable(s: DocSession, guid: string, name: string) {
    const chapter = name.includes("/") ? name.slice(0, name.indexOf("/")) : "_其他";
    await store.mutate(s, playscriptDocId(s.workspaceId, guid), t => t.transact(() => store.schema.initPlayscriptMeta(t, guid, name)));
    await store.mutate(s, projectDocId(s.workspaceId), d => d.transact(() => {
      store.schema.ensureProject(d);
      store.schema.upsertIndex(d, { guid, name, chapter, orphan: false });
    }));
  }

  function checkIdentity(v: GraphView, type: string, data: Record<string, unknown>, selfId?: string) {
    const id = typeof data.playscriptId === "string" ? data.playscriptId : undefined;
    if (id === undefined) return;
    if (type === PLAYSCRIPT) {
      if (!id) throw new ToolError("劇本節點要有 playscriptId（例如 A10/ReL20_某某）");
      if (playscriptNames(v, selfId).has(id)) throw new ToolError(`已經有劇本節點叫「${id}」，playscriptId 不能重複`);
    } else if (id && !playscriptNames(v).has(id)) {
      // 劇本條件 / 開啟劇本 point at an existing 劇本 node.
      throw new ToolError(`沒有叫「${id}」的劇本節點；${typeLabel(v, type)}的 playscriptId 要填已存在的劇本`);
    }
  }

  function coerceFields(t: NodeType | undefined, fields: Record<string, unknown>) {
    const data: Record<string, unknown> = {};
    for (const [k, raw] of Object.entries(fields)) {
      const f = t?.IdentityFields.find(x => x.FieldName === k);
      if (t && !f) throw new ToolError(`${t.DisplayName || t.TypeName} 沒有欄位「${k}」。可用欄位：${t.IdentityFields.map(x => x.FieldName).join("、") || "（無）"}`);
      data[k] = f?.FieldType === "System.Int32" ? Number.parseInt(String(raw), 10) || 0 : String(raw ?? "");
    }
    return data;
  }

  function findGroup(v: GraphView, ref: string) {
    const g = v.graph.groups.find(x => x.id === ref) ?? v.graph.groups.filter(x => x.title === ref)[0];
    if (!g) throw new ToolError(`找不到群組「${ref}」。群組有：${v.graph.groups.map(x => x.title).join("、")}`);
    return g;
  }

  // A free spot: to the right of `near` (then stepping down), or right of the group's members.
  function place(v: GraphView, near: CanonicalNode | null, groupId: string | null) {
    const W = 220, H = 140, GAP = 60;
    const overlaps = (x: number, y: number) => v.graph.nodes.some(n =>
      x < n.position.x + n.position.width + 20 && x + W + 20 > n.position.x &&
      y < n.position.y + n.position.height + 20 && y + H + 20 > n.position.y);
    let x: number, y: number;
    if (near) {
      x = near.position.x + near.position.width + GAP * 2;
      y = near.position.y;
    } else {
      const g = groupId ? v.graph.groups.find(x2 => x2.id === groupId) : undefined;
      const members = g ? g.nodes.map(id => v.byId.get(id)).filter((n): n is CanonicalNode => !!n) : [];
      if (members.length) {
        x = Math.max(...members.map(n => n.position.x + n.position.width)) + GAP * 2;
        y = Math.min(...members.map(n => n.position.y));
      } else if (g) {
        x = g.position.x + 20; y = g.position.y + 60;
      } else {
        throw new ToolError("要給 near（放在哪個節點旁邊）、group 或 position 其中一個");
      }
    }
    for (let i = 0; i < 200 && overlaps(x, y); i++) y += H + GAP;
    return { x: Math.round(x), y: Math.round(y), width: W, height: H };
  }

  function addToGroup(v: GraphView, groupId: string, node: { id: string; position: { x: number; y: number; width: number; height: number } }) {
    const m = v.doc.getMap<YType.Map<unknown>>("groups").get(groupId);
    if (!(m instanceof Y.Map)) return;
    const nodes = ((m.get("nodes") as string[]) ?? []).filter(id => id !== node.id);
    m.set("nodes", [...nodes, node.id].sort());
    // Grow the stored bounds to cover the node (the web editor recomputes them from members anyway).
    const pad = 20, bar = 40;
    const gx = Number(m.get("x") ?? 0), gy = Number(m.get("y") ?? 0);
    const gw = Number(m.get("width") ?? 0), gh = Number(m.get("height") ?? 0);
    const x1 = Math.min(gx, node.position.x - pad), y1 = Math.min(gy, node.position.y - bar);
    const x2 = Math.max(gx + gw, node.position.x + node.position.width + pad), y2 = Math.max(gy + gh, node.position.y + node.position.height + pad);
    if (x1 !== gx) m.set("x", x1);
    if (y1 !== gy) m.set("y", y1);
    if (x2 - x1 !== gw) m.set("width", x2 - x1);
    if (y2 - y1 !== gh) m.set("height", y2 - y1);
  }

  function removeFromGroups(v: GraphView, id: string) {
    v.doc.getMap<YType.Map<unknown>>("groups").forEach(m => {
      if (!(m instanceof Y.Map)) return;
      const nodes = (m.get("nodes") as string[]) ?? [];
      if (nodes.includes(id)) m.set("nodes", nodes.filter(x => x !== id));
    });
  }

  function checkPort(v: GraphView, n: CanonicalNode, port: string, dir: "in" | "out") {
    const t = v.types.get(n.type);
    if (!t) return;
    const ports = dir === "out" ? t.Outputs : t.Inputs;
    if (!ports.some(p => p.FieldName === port))
      throw new ToolError(`${titleOf(v, n)}（${typeLabel(v, n.type)}）沒有${dir === "out" ? "輸出" : "輸入"}埠「${port}」。可用：${ports.map(p => p.FieldName).join("、") || "（無）"}`);
  }

  // ---------- tools ----------

  ctx.registerTool("sf_read_guide", {
    title: "Read StoryForge AI Guide",
    description: "寫劇本前必讀：StoryForge 的 AI 工作規範（AGENTS.md）加上雲端版對照說明，以及有哪些參考資料可以用 sf_read_reference 讀。",
    inputSchema: { workspaceId: ws },
  }, args => run(() => store.withSession(args.workspaceId, async s => {
    const refs = await store.readReferences(s);
    const agents = refs["AGENTS.md"];
    const references = Object.entries(refs).filter(([k]) => k !== "AGENTS.md")
      .map(([name, r]) => ({ name, chars: r.text.length, updatedAt: new Date(r.updatedAt).toISOString() }));
    if (!agents) return fail("雲端還沒有 AGENTS.md。請在本機 StoryForge 的雲端同步分頁按「上傳」。", { references });
    return ok({ guide: CLOUD_PREFACE + agents.text, references });
  })), { readOnly: true });

  ctx.registerTool("sf_read_reference", {
    title: "Read StoryForge Reference",
    description: "讀本機 StoryForge 上傳的參考資料（唯讀）：StoryOutline（世界書）、CharacterCards、AudioNotes、LdtkTransports、GuidePathPoints、PlayscriptNames。檔案大時用 path 只取一部分（例如 [\"Playscripts\",\"A10/ReL10_澪音匯合\"]），或用 search 找出含某段文字的位置。",
    inputSchema: {
      workspaceId: ws,
      name: z.string().describe("參考資料名稱，例如 StoryOutline、LdtkTransports"),
      path: z.array(z.string()).optional().describe("JSON 裡的路徑（key 或陣列索引），只回傳那一部分"),
      search: z.string().optional().describe("只回傳 key 或值包含這段文字的位置（最多 50 筆）"),
    },
  }, args => run(() => store.withSession(args.workspaceId, async s => {
    const refs = await store.readReferences(s);
    const key = Object.keys(refs).find(k => k === args.name || k.replace(/\.json$/i, "") === args.name.replace(/\.json$/i, ""));
    if (!key) throw new ToolError(`沒有參考資料「${args.name}」。有：${Object.keys(refs).filter(k => k !== "AGENTS.md").join("、")}`);
    const text = refs[key].text;
    let value: unknown;
    try { value = JSON.parse(text); } catch { return ok({ name: key, text }); }
    let at: unknown = value;
    for (const p of args.path ?? []) {
      if (at === null || typeof at !== "object") throw new ToolError(`路徑 ${JSON.stringify(args.path)} 走到「${p}」時已經不是物件`);
      const next = Array.isArray(at) ? at[Number(p)] : (at as Record<string, unknown>)[p];
      if (next === undefined) {
        const keys = Array.isArray(at) ? `0..${at.length - 1}` : Object.keys(at).slice(0, 80).join("、");
        throw new ToolError(`找不到「${p}」。這一層有：${keys}`);
      }
      at = next;
    }
    if (args.search) {
      const needle = args.search;
      const hits: { path: (string | number)[]; value: unknown }[] = [];
      const walk = (x: unknown, path: (string | number)[]) => {
        if (hits.length >= 50) return;
        if (x !== null && typeof x === "object") {
          for (const [k, c] of Object.entries(x)) {
            if (k.includes(needle)) hits.push({ path: [...path, k], value: c });
            else walk(c, [...path, Array.isArray(x) ? Number(k) : k]);
            if (hits.length >= 50) return;
          }
        } else if (String(x).includes(needle)) hits.push({ path, value: x });
      };
      walk(at, args.path ?? []);
      return ok({ name: key, search: needle, hits });
    }
    const json = JSON.stringify(at);
    if (json.length > 60_000) {
      const keys = at && typeof at === "object" ? (Array.isArray(at) ? `陣列，${at.length} 項` : Object.keys(at)) : undefined;
      return ok({ name: key, tooLarge: true, chars: json.length, keys, hint: "內容太大，請用 path 指定一部分，或用 search 搜尋" });
    }
    return ok({ name: key, path: args.path ?? [], value: at });
  })), { readOnly: true });

  ctx.registerTool("sf_find_nodes", {
    title: "Find StoryForge Flow Graph Nodes",
    description: "搜尋雲端流程圖的節點：用 query 比對節點名稱（playscriptId / flagId），可再用 type、group 篩選；searchMemo=true 時也搜 Memo。回傳 id、類型、名稱、所在群組。",
    inputSchema: {
      workspaceId: ws,
      query: z.string().optional(), type: z.string().optional().describe("節點類型，例如 playscript、playscript-flag、bool-flag"),
      group: z.string().optional().describe("群組名稱（例如 A10）"), searchMemo: z.boolean().optional(),
      limit: z.number().int().min(1).max(500).optional(),
    },
  }, args => run(() => store.withSession(args.workspaceId, async s => {
    const v = await view(s);
    const memos = args.searchMemo ? store.flow.readMemos(v.doc) : {};
    const group = args.group ? findGroup(v, args.group) : null;
    const q = args.query ?? "";
    const hits = v.graph.nodes.filter(n =>
      (!args.type || n.type === args.type) &&
      (!group || group.nodes.includes(n.id)) &&
      (!q || titleOf(v, n).includes(q) || (args.searchMemo && (memos[n.id] ?? "").includes(q))));
    const limit = args.limit ?? 100;
    return ok({
      total: hits.length,
      nodes: hits.slice(0, limit).map(n => summary(v, n)),
      groups: group ? undefined : v.graph.groups.map(g => g.title),
      nodeTypes: [...v.types.values()].map(t => ({ type: t.TypeName, label: t.DisplayName, fields: t.IdentityFields.map(f => f.FieldName), inputs: t.Inputs.map(p => p.FieldName), outputs: t.Outputs.map(p => p.FieldName) })),
    });
  })), { readOnly: true });

  ctx.registerTool("sf_read_node", {
    title: "Read StoryForge Flow Graph Node",
    description: "讀一個流程圖節點：類型、欄位、Memo、衝突、變化、角色、地點、所在群組，以及所有入線（誰接進來）和出線（接到誰）。現在有人正在動它時會回傳 lockedBy。",
    inputSchema: { workspaceId: ws, node: nodeRef },
  }, args => run(() => store.withSession(args.workspaceId, async s => {
    const [v, room] = await Promise.all([view(s), graphRoom(s)]);
    const n = resolve(v, args.node);
    const held = holders(room, [n.id]);
    return ok({ ...nodeDetail(v, n), lockedBy: held.length ? held[0].by : undefined });
  })), { readOnly: true });

  ctx.registerTool("sf_update_node", {
    title: "Update StoryForge Flow Graph Node",
    description: "改一個流程圖節點：fields（例如 playscriptId、flagId）、Memo、衝突、變化、角色、地點、position（{x,y}）、group（移到另一個群組）。只給要改的。劇本節點改 playscriptId 時，劇本表格會跟著改名。有人正在動這個節點時會被拒絕。",
    inputSchema: {
      workspaceId: ws, node: nodeRef,
      fields: z.record(z.union([z.string(), z.number()])).optional(),
      ...contentShape,
      position: z.object({ x: z.number(), y: z.number() }).optional(),
      group: z.string().optional().describe("移到這個群組（群組名稱或 id）"),
    },
  }, args => run(async () => {
    const renamed: { to?: string } = {};
    const result = await writeGraph(args.workspaceId, async (v, room) => {
      const n = resolve(v, args.node);
      refuseIfHeld(v, room, [n.id]);
      hold(room, n);
      const m = nodeMap(v, n.id);
      const changes: string[] = [];
      if (args.fields) {
        const data = coerceFields(v.types.get(n.type), args.fields);
        checkIdentity(v, n.type, { ...n.data, ...data }, n.id);
        const d = m.get("data") as YType.Map<unknown>;
        for (const [k, val] of Object.entries(data)) {
          if (d.get(k) === val) continue;
          d.set(k, val);
          changes.push(k);
          if (k === "playscriptId" && n.type === PLAYSCRIPT) renamed.to = String(val);
        }
      }
      if (args.position) {
        if (m.get("x") !== args.position.x) m.set("x", Math.round(args.position.x));
        if (m.get("y") !== args.position.y) m.set("y", Math.round(args.position.y));
        changes.push("position");
      }
      if (args.group) {
        const g = findGroup(v, args.group);
        removeFromGroups(v, n.id);
        addToGroup(v, g.id, { id: n.id, position: { ...n.position, ...(args.position ?? {}) } });
        changes.push("group");
      }
      const contentChanged = writeContent(v, n.id, args);
      if (contentChanged) changes.push(`${contentChanged} 個文字欄位`);
      return { id: n.id, title: titleOf(v, n), changed: changes };
    });
    if (renamed.to) await store.withSession(args.workspaceId, s => syncTable(s, result.id, renamed.to!));
    return { ...result, tableRenamed: renamed.to };
  }), { readOnly: false });

  ctx.registerTool("sf_add_node", {
    title: "Add StoryForge Flow Graph Node",
    description: "在雲端流程圖新增一個節點。type 是節點類型（劇本節點是 playscript），fields 填識別欄位（劇本節點的 playscriptId 不能重複；劇本條件 / 開啟劇本的 playscriptId 要填已存在的劇本）。用 near 指定放在哪個節點旁邊（會自動避開重疊並加入同一個群組），或給 group / position。劇本節點會同時建立空的劇本表格。新增後記得用 sf_connect 接上前後的線。",
    inputSchema: {
      workspaceId: ws,
      type: z.string(), fields: z.record(z.union([z.string(), z.number()])).optional(),
      near: z.string().optional().describe("放在這個節點右邊（節點 id 或 playscriptId）"),
      group: z.string().optional().describe("加入這個群組（名稱或 id）；有 near 時預設跟 near 同一個群組"),
      position: z.object({ x: z.number(), y: z.number() }).optional(),
      ...contentShape,
    },
  }, args => run(async () => {
    const id = crypto.randomUUID();
    const table: { name?: string } = {};
    const result = await writeGraph(args.workspaceId, async (v, room) => {
      const t = v.types.get(args.type);
      if (v.types.size && !t) throw new ToolError(`沒有節點類型「${args.type}」。可用：${[...v.types.values()].map(x => `${x.TypeName}（${x.DisplayName}）`).join("、")}`);
      const data: Record<string, unknown> = {};
      for (const f of t?.IdentityFields ?? []) data[f.FieldName] = f.FieldType === "System.Int32" ? 0 : "";
      Object.assign(data, coerceFields(t, args.fields ?? {}));
      checkIdentity(v, args.type, data);
      const near = args.near ? resolve(v, args.near) : null;
      const groupId = args.group ? findGroup(v, args.group).id
        : near ? (v.graph.groups.find(g => g.nodes.includes(near.id))?.id ?? null) : null;
      const position = args.position
        ? { x: Math.round(args.position.x), y: Math.round(args.position.y), width: 220, height: 140 }
        : place(v, near, groupId);
      const node = { id, type: args.type, position, data };
      hold(room, node);
      const m = new Y.Map<unknown>();
      v.doc.getMap<YType.Map<unknown>>("nodes").set(id, m);
      m.set("type", args.type);
      for (const k of ["x", "y", "width", "height"] as const) m.set(k, position[k]);
      const d = new Y.Map<unknown>();
      m.set("data", d);
      for (const [k, val] of Object.entries(data)) d.set(k, val);
      m.set("extra", JSON.stringify({ node: {}, position: {} }));
      if (groupId) addToGroup(v, groupId, node);
      v.byId.set(id, node as CanonicalNode);
      writeContent(v, id, args);
      if (args.type === PLAYSCRIPT) table.name = String(data.playscriptId);
      const g = groupId ? v.graph.groups.find(x => x.id === groupId)?.title : undefined;
      return { id, type: args.type, title: identityOf(node as CanonicalNode) || typeLabel(v, args.type), group: g, position: { x: position.x, y: position.y } };
    });
    if (table.name) await store.withSession(args.workspaceId, s => syncTable(s, id, table.name!));
    return { ...result, tableCreated: !!table.name, next: "用 sf_connect 把它接到前後的節點" };
  }), { readOnly: false });

  const edgeShape = {
    workspaceId: ws,
    from: z.string().describe("線的起點節點（id 或 playscriptId）"),
    fromPort: z.string().describe("起點的輸出埠，例如 isFinish、link、output、isOn"),
    to: z.string().describe("線的終點節點"),
    toPort: z.string().describe("終點的輸入埠，例如 condition、finishCondition、input、unlockCondition、link"),
  };

  ctx.registerTool("sf_connect", {
    title: "Connect StoryForge Flow Graph Nodes",
    description: "在雲端流程圖加一條線：from 節點的 fromPort（輸出）接到 to 節點的 toPort（輸入）。埠名稱可用 sf_read_node 的 ports 查。兩端節點有人正在動時會被拒絕。",
    inputSchema: edgeShape,
  }, args => run(() => writeGraph(args.workspaceId, (v, room) => {
    const a = resolve(v, args.from), b = resolve(v, args.to);
    if (a.id === b.id) throw new ToolError("不能接到自己");
    checkPort(v, a, args.fromPort, "out");
    checkPort(v, b, args.toPort, "in");
    refuseIfHeld(v, room, [a.id, b.id]);
    hold(room, b);
    const edge: CanonicalEdge = { fromNode: a.id, fromPort: args.fromPort, toNode: b.id, toPort: args.toPort };
    const edges = v.doc.getMap<CanonicalEdge>("edges");
    const key = edgeKey(edge);
    if (edges.has(key)) return { connected: false, message: "這條線已經存在" };
    const inPort = v.types.get(b.type)?.Inputs.find(p => p.FieldName === args.toPort);
    if (inPort && inPort.AllowMultiple === false && v.graph.edges.some(e => e.toNode === b.id && e.toPort === args.toPort))
      throw new ToolError(`${titleOf(v, b)} 的 ${args.toPort} 只能接一條線，已經有了；要換請先 sf_disconnect`);
    edges.set(key, edge);
    return { connected: true, from: titleOf(v, a), fromPort: args.fromPort, to: titleOf(v, b), toPort: args.toPort };
  })), { readOnly: false });

  ctx.registerTool("sf_disconnect", {
    title: "Disconnect StoryForge Flow Graph Nodes",
    description: "拆掉雲端流程圖的一條線（參數跟 sf_connect 一樣）。",
    inputSchema: edgeShape,
  }, args => run(() => writeGraph(args.workspaceId, (v, room) => {
    const a = resolve(v, args.from), b = resolve(v, args.to);
    refuseIfHeld(v, room, [a.id, b.id]);
    hold(room, b);
    const edges = v.doc.getMap<CanonicalEdge>("edges");
    let removed = 0;
    edges.forEach((e, k) => {
      if (e.fromNode === a.id && e.fromPort === args.fromPort && e.toNode === b.id && e.toPort === args.toPort) { edges.delete(k); removed++; }
    });
    if (!removed) throw new ToolError("沒有這條線。用 sf_read_node 看節點現有的入線與出線");
    return { disconnected: removed, from: titleOf(v, a), to: titleOf(v, b) };
  })), { readOnly: false });
}
