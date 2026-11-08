// Flow graph in the cloud: db$<ws>$sf.flowgraph.main holds StoryForge's canonical graph (the `graph` half of
// blueprint.json, the same shape Unity's PlayscriptGraphJsonDefinition uses) as Yjs structures, so edits from
// different people merge per field instead of per file. The `baseGraph` half (what Unity last saw) stays local.
//
//   meta        schemaVersion, graphId, extra (JSON of unknown top-level keys)
//   nodes       id -> Y.Map { type, x, y, width, height, data: Y.Map, extra (JSON string) }
//   edges       edgeKey -> plain object (CanonicalEdge)
//   groups      id -> Y.Map { title, color (plain), x, y, width, height, nodes (string[]), extra }
//   stickyNotes id -> plain object (CanonicalStickyNote)
//   memos       node id -> Y.Text (the node's Memo from NodeContentStore; not part of the canonical graph)
//   content     node id -> Y.Map { conflict, change, characters, location: Y.Text } (the node's other
//               NodeContentStore fields — 衝突 / 變化 / 角色 / 地點)
//
// The node schema (ProcessNodeSchema.json, which ports and identity fields each node type has) is stored as a
// JSON string under "nodeSchema" in the rules doc.

import type * as YType from "yjs";

export const FLOWGRAPH_SCHEMA_VERSION = 1;

export function flowgraphDocId(ws: string) { return `db$${ws}$sf.flowgraph.main`; }

type Extra = Record<string, unknown>;
export type CanonicalRect = { x: number; y: number; width: number; height: number } & Extra;
export type CanonicalNode = { id: string; type: string; position: CanonicalRect; data: Record<string, unknown> } & Extra;
export type CanonicalEdge = { fromNode: string; fromPort: string; fromPortId?: string; toNode: string; toPort: string; toPortId?: string } & Extra;
export type CanonicalColor = { r: number; g: number; b: number; a: number } & Extra;
export type CanonicalGroup = { id: string; title: string; color: CanonicalColor; position: CanonicalRect; nodes: string[] } & Extra;
export type CanonicalStickyNote = { id: string; title: string; content: string; position: CanonicalRect } & Extra;
export type CanonicalGraph = {
  schemaVersion: number;
  graphId: string;
  nodes: CanonicalNode[];
  edges: CanonicalEdge[];
  groups: CanonicalGroup[];
  stickyNotes: CanonicalStickyNote[];
} & Extra;

export function edgeKey(e: CanonicalEdge) {
  return `${e.fromNode}.${e.fromPort}.${e.fromPortId ?? ""}->${e.toNode}.${e.toPort}.${e.toPortId ?? ""}`;
}

const NODE_KEYS = new Set(["id", "type", "position", "data"]);
const RECT_KEYS = new Set(["x", "y", "width", "height"]);
const GROUP_KEYS = new Set(["id", "title", "color", "position", "nodes"]);
const GRAPH_KEYS = new Set(["schemaVersion", "graphId", "nodes", "edges", "groups", "stickyNotes"]);

function pickExtra(obj: Record<string, unknown>, known: Set<string>) {
  const out: Extra = {};
  for (const [k, v] of Object.entries(obj)) if (!known.has(k)) out[k] = v;
  return out;
}

const json = (v: unknown) => JSON.stringify(v ?? {});
const parse = (s: unknown): Extra => {
  if (typeof s !== "string" || !s) return {};
  try { return JSON.parse(s) as Extra; } catch { return {}; }
};
const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);

export const NODE_TEXT_FIELDS = ["conflict", "change", "characters", "location"] as const;
export type NodeTextField = (typeof NODE_TEXT_FIELDS)[number];
export type NodeContent = Partial<Record<NodeTextField, string>>;

export type GraphChangeStats = { nodes: number; edges: number; groups: number; stickyNotes: number; memos: number };

export function createFlowGraph(Y: typeof YType) {
  function setIfChanged(m: YType.Map<unknown>, key: string, value: unknown) {
    if (!same(m.get(key), value)) m.set(key, value);
  }

  function writeNode(m: YType.Map<unknown>, node: CanonicalNode, base: CanonicalNode | undefined) {
    const changedPos = (k: keyof CanonicalRect) => !base || base.position[k] !== node.position[k];
    if (!base || base.type !== node.type) setIfChanged(m, "type", node.type);
    for (const k of ["x", "y", "width", "height"] as const) if (changedPos(k)) setIfChanged(m, k, node.position[k]);
    let data = m.get("data");
    if (!(data instanceof Y.Map)) { data = new Y.Map(); m.set("data", data); }
    const d = data as YType.Map<unknown>;
    const baseData = base?.data ?? {};
    for (const [k, v] of Object.entries(node.data ?? {})) if (!base || !same(baseData[k], v)) setIfChanged(d, k, v);
    for (const k of Object.keys(baseData)) if (!(k in (node.data ?? {}))) d.delete(k);
    const extra = { node: pickExtra(node, NODE_KEYS), position: pickExtra(node.position, RECT_KEYS) };
    const baseExtra = base ? { node: pickExtra(base, NODE_KEYS), position: pickExtra(base.position, RECT_KEYS) } : null;
    if (!baseExtra || !same(baseExtra, extra)) setIfChanged(m, "extra", json(extra));
  }

  function readNode(id: string, m: YType.Map<unknown>): CanonicalNode {
    const extra = parse(m.get("extra")) as { node?: Extra; position?: Extra };
    const data = m.get("data");
    return {
      id,
      type: String(m.get("type") ?? ""),
      position: {
        x: Number(m.get("x") ?? 0), y: Number(m.get("y") ?? 0),
        width: Number(m.get("width") ?? 220), height: Number(m.get("height") ?? 140),
        ...(extra.position ?? {}),
      },
      data: data instanceof Y.Map ? (data.toJSON() as Record<string, unknown>) : {},
      ...(extra.node ?? {}),
    };
  }

  function writeGroup(m: YType.Map<unknown>, g: CanonicalGroup, base: CanonicalGroup | undefined) {
    if (!base || base.title !== g.title) setIfChanged(m, "title", g.title);
    if (!base || !same(base.color, g.color)) setIfChanged(m, "color", g.color);
    for (const k of ["x", "y", "width", "height"] as const)
      if (!base || base.position[k] !== g.position[k]) setIfChanged(m, k, g.position[k]);
    const nodes = [...g.nodes].sort();
    if (!base || !same([...base.nodes].sort(), nodes)) setIfChanged(m, "nodes", nodes);
    const extra = { group: pickExtra(g, GROUP_KEYS), position: pickExtra(g.position, RECT_KEYS) };
    setIfChanged(m, "extra", json(extra));
  }

  function readGroup(id: string, m: YType.Map<unknown>): CanonicalGroup {
    const extra = parse(m.get("extra")) as { group?: Extra; position?: Extra };
    return {
      id,
      title: String(m.get("title") ?? ""),
      color: (m.get("color") as CanonicalColor) ?? { r: 0, g: 0, b: 0, a: 0.3 },
      position: {
        x: Number(m.get("x") ?? 0), y: Number(m.get("y") ?? 0),
        width: Number(m.get("width") ?? 200), height: Number(m.get("height") ?? 100),
        ...(extra.position ?? {}),
      },
      nodes: ((m.get("nodes") as string[]) ?? []).slice(),
      ...(extra.group ?? {}),
    };
  }

  function readGraph(doc: YType.Doc): CanonicalGraph {
    const meta = doc.getMap("meta");
    const nodes: CanonicalNode[] = [];
    doc.getMap<YType.Map<unknown>>("nodes").forEach((m, id) => { if (m instanceof Y.Map) nodes.push(readNode(id, m)); });
    const edges: CanonicalEdge[] = [];
    doc.getMap<CanonicalEdge>("edges").forEach(e => { if (e && typeof e === "object") edges.push({ ...e }); });
    const groups: CanonicalGroup[] = [];
    doc.getMap<YType.Map<unknown>>("groups").forEach((m, id) => { if (m instanceof Y.Map) groups.push(readGroup(id, m)); });
    const stickyNotes: CanonicalStickyNote[] = [];
    doc.getMap<CanonicalStickyNote>("stickyNotes").forEach(n => { if (n && typeof n === "object") stickyNotes.push({ ...n }); });
    // Links to a node that is gone (one person deleted it while another connected it) are dropped.
    const ids = new Set(nodes.map(n => n.id));
    nodes.sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
    return {
      schemaVersion: Number(meta.get("graphSchemaVersion") ?? 1),
      graphId: String(meta.get("graphId") ?? "PlayscriptProcessor"),
      nodes,
      edges: edges.filter(e => ids.has(e.fromNode) && ids.has(e.toNode)),
      groups, // member lists are kept as-is (Unity data can list members that no longer exist)
      stickyNotes,
      ...parse(meta.get("extra")),
    };
  }

  function isEmpty(doc: YType.Doc) {
    return doc.getMap("nodes").size === 0 && doc.getMap("meta").size === 0;
  }

  // Applies the difference between `base` (what the writer started from) and `target` (what it ended with)
  // to the doc, field by field, so concurrent edits by others to other fields/nodes survive. With no base
  // the doc's current content is the base (a plain two-way overwrite).
  function writeGraph(doc: YType.Doc, target: CanonicalGraph, base?: CanonicalGraph): GraphChangeStats {
    const from = base ?? readGraph(doc);
    const stats: GraphChangeStats = { nodes: 0, edges: 0, groups: 0, stickyNotes: 0, memos: 0 };
    const meta = doc.getMap("meta");
    if (meta.get("schemaVersion") !== FLOWGRAPH_SCHEMA_VERSION) meta.set("schemaVersion", FLOWGRAPH_SCHEMA_VERSION);
    setIfChanged(meta as YType.Map<unknown>, "graphSchemaVersion", target.schemaVersion);
    setIfChanged(meta as YType.Map<unknown>, "graphId", target.graphId);
    setIfChanged(meta as YType.Map<unknown>, "extra", json(pickExtra(target, GRAPH_KEYS)));

    const nodesMap = doc.getMap<YType.Map<unknown>>("nodes");
    const baseNodes = new Map(from.nodes.map(n => [n.id, n]));
    const targetNodes = new Map(target.nodes.map(n => [n.id, n]));
    for (const n of target.nodes) {
      const b = baseNodes.get(n.id);
      if (b && same(b, n) && nodesMap.has(n.id)) continue;
      let m = nodesMap.get(n.id);
      const existed = m instanceof Y.Map;
      if (!existed) { m = new Y.Map(); nodesMap.set(n.id, m); }
      writeNode(m as YType.Map<unknown>, n, existed ? b : undefined);
      stats.nodes++;
    }
    for (const id of baseNodes.keys()) if (!targetNodes.has(id) && nodesMap.has(id)) { nodesMap.delete(id); stats.nodes++; }

    const edgesMap = doc.getMap<CanonicalEdge>("edges");
    const baseEdges = new Set(from.edges.map(edgeKey));
    const targetEdges = new Map(target.edges.map(e => [edgeKey(e), e]));
    for (const [k, e] of targetEdges) if (!baseEdges.has(k) || !edgesMap.has(k)) { if (!same(edgesMap.get(k), e)) { edgesMap.set(k, e); stats.edges++; } }
    for (const k of baseEdges) if (!targetEdges.has(k) && edgesMap.has(k)) { edgesMap.delete(k); stats.edges++; }
    // A deleted node takes its links with it, including links someone else just made to it.
    const gone = (id: string) => !nodesMap.has(id);
    edgesMap.forEach((e, k) => { if (gone(e.fromNode) || gone(e.toNode)) edgesMap.delete(k); });

    const groupsMap = doc.getMap<YType.Map<unknown>>("groups");
    const baseGroups = new Map(from.groups.map(g => [g.id, g]));
    const targetGroups = new Map(target.groups.map(g => [g.id, g]));
    for (const g of target.groups) {
      const b = baseGroups.get(g.id);
      if (b && same(b, g) && groupsMap.has(g.id)) continue;
      let m = groupsMap.get(g.id);
      const existed = m instanceof Y.Map;
      if (!existed) { m = new Y.Map(); groupsMap.set(g.id, m); }
      writeGroup(m as YType.Map<unknown>, g, existed ? b : undefined);
      stats.groups++;
    }
    for (const id of baseGroups.keys()) if (!targetGroups.has(id) && groupsMap.has(id)) { groupsMap.delete(id); stats.groups++; }

    const notesMap = doc.getMap<CanonicalStickyNote>("stickyNotes");
    const baseNotes = new Map(from.stickyNotes.map(n => [n.id, n]));
    const targetNotes = new Map(target.stickyNotes.map(n => [n.id, n]));
    for (const n of target.stickyNotes) if (!same(baseNotes.get(n.id), n) || !notesMap.has(n.id)) { if (!same(notesMap.get(n.id), n)) { notesMap.set(n.id, n); stats.stickyNotes++; } }
    for (const id of baseNotes.keys()) if (!targetNotes.has(id) && notesMap.has(id)) { notesMap.delete(id); stats.stickyNotes++; }
    return stats;
  }

  function readMemos(doc: YType.Doc): Record<string, string> {
    const out: Record<string, string> = {};
    doc.getMap<YType.Text>("memos").forEach((t, id) => { const s = t?.toString() ?? ""; if (s) out[id] = s; });
    return out;
  }

  function memoText(doc: YType.Doc, id: string) {
    const memos = doc.getMap<YType.Text>("memos");
    let t = memos.get(id);
    if (!(t instanceof Y.Text)) { t = new Y.Text(); memos.set(id, t); }
    return t;
  }

  function setText(t: YType.Text, value: string) {
    const cur = t.toString();
    if (cur === value) return false;
    let a = 0;
    while (a < cur.length && a < value.length && cur[a] === value[a]) a++;
    let b1 = cur.length, b2 = value.length;
    while (b1 > a && b2 > a && cur[b1 - 1] === value[b2 - 1]) { b1--; b2--; }
    if (b1 > a) t.delete(a, b1 - a);
    if (b2 > a) t.insert(a, value.slice(a, b2));
    return true;
  }

  // Same base/target rule as writeGraph: only memos that changed relative to base are written.
  function writeMemos(doc: YType.Doc, target: Record<string, string>, base?: Record<string, string>) {
    const from = base ?? readMemos(doc);
    let n = 0;
    const ids = new Set([...Object.keys(target), ...Object.keys(from)]);
    for (const id of ids) {
      const v = target[id] ?? "";
      if ((from[id] ?? "") === v) continue;
      if (setText(memoText(doc, id), v)) n++;
    }
    return n;
  }

  function readContent(doc: YType.Doc): Record<string, NodeContent> {
    const out: Record<string, NodeContent> = {};
    doc.getMap<YType.Map<YType.Text>>("content").forEach((m, id) => {
      if (!(m instanceof Y.Map)) return;
      const c: NodeContent = {};
      for (const f of NODE_TEXT_FIELDS) {
        const v = m.get(f)?.toString() ?? "";
        if (v) c[f] = v;
      }
      if (Object.keys(c).length) out[id] = c;
    });
    return out;
  }

  function contentText(doc: YType.Doc, id: string, field: NodeTextField) {
    const all = doc.getMap<YType.Map<YType.Text>>("content");
    let m = all.get(id);
    if (!(m instanceof Y.Map)) { m = new Y.Map<YType.Text>(); all.set(id, m); }
    let t = m.get(field);
    if (!(t instanceof Y.Text)) { t = new Y.Text(); m.set(field, t); }
    return t;
  }

  /** Same base/target rule as writeMemos, per field. */
  function writeContent(doc: YType.Doc, target: Record<string, NodeContent>, base?: Record<string, NodeContent>) {
    const from = base ?? readContent(doc);
    let n = 0;
    for (const id of new Set([...Object.keys(target), ...Object.keys(from)])) {
      for (const f of NODE_TEXT_FIELDS) {
        const v = target[id]?.[f] ?? "";
        if ((from[id]?.[f] ?? "") === v) continue;
        if (setText(contentText(doc, id, f), v)) n++;
      }
    }
    return n;
  }

  return { readGraph, writeGraph, isEmpty, readMemos, memoText, writeMemos, readContent, contentText, writeContent, setText };
}

export type FlowGraph = ReturnType<typeof createFlowGraph>;
