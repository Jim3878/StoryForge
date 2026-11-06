// StoryForge cloud data layout (schemaVersion 1), shared by the affine-mcp extension, the sf CLI and
// (as a copied file) the AFFiNE fork's playscript table editor. Everything that builds Yjs values goes
// through the `Y` instance the host passes in, so the extension never loads a second copy of yjs next to
// the one affine-mcp itself uses.
//
// Docs (all inside one AFFiNE workspace, synced through AFFiNE's normal doc channel):
//   db$<ws>$sf.project              meta (schemaVersion, minExtensionVersion, modules) + playscripts index
//   db$<ws>$sf.rules                rules JSON uploaded by local StoryForge
//   db$<ws>$sf.playscript.<guid>    one playscript table: meta, order (rowIds), rows (rowId -> col -> Y.Text)

import type * as YType from "yjs";

export const SCHEMA_VERSION = 1;
export const MIN_EXTENSION_VERSION = "1.0.0";

// Column order matches NodeScriptTableStore.Headers / the "劇本檔範本" sheet exactly.
export const COLUMNS = [
  { key: "name", label: "名字" },
  { key: "face", label: "表情" },
  { key: "voice", label: "配音情緒" },
  { key: "text", label: "台詞" },
  { key: "func", label: "功能" },
  { key: "note", label: "備註" },
  { key: "param", label: "參數" },
] as const;
export type ColumnKey = (typeof COLUMNS)[number]["key"];
export const COLUMN_KEYS: ColumnKey[] = COLUMNS.map(c => c.key);

export type Row = string[]; // always COLUMNS.length cells, in COLUMNS order

export function projectDocId(ws: string) { return `db$${ws}$sf.project`; }
export function rulesDocId(ws: string) { return `db$${ws}$sf.rules`; }
export function playscriptDocId(ws: string, guid: string) { return `db$${ws}$sf.playscript.${guid}`; }

export type PlayscriptIndexEntry = {
  guid: string;
  name: string;
  chapter: string;
  supplementDocId?: string;
  orphan?: boolean;
};

export type RowWithId = { rowId: string; cells: Row };

export function normalizeRow(cells: readonly (string | undefined | null)[]): Row {
  const row: Row = [];
  for (let i = 0; i < COLUMNS.length; i++) row.push((cells[i] ?? "").toString());
  return row;
}

function randomId(): string {
  const chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
  let s = "";
  for (let i = 0; i < 12; i++) s += chars[Math.floor(Math.random() * chars.length)];
  return s;
}

export class SchemaVersionError extends Error {
  constructor(public found: number) {
    super(`這份資料的 schemaVersion 是 ${found}，比這個版本支援的 ${SCHEMA_VERSION} 新。請先更新 StoryForge 擴充，再寫入。`);
    this.name = "SchemaVersionError";
  }
}

export function createSchema(Y: typeof YType) {
  // ---------- project ----------
  function readProject(doc: YType.Doc) {
    const meta = doc.getMap("meta");
    const playscripts = doc.getMap<YType.Map<unknown>>("playscripts");
    const list: PlayscriptIndexEntry[] = [];
    playscripts.forEach((m, guid) => {
      if (!(m instanceof Y.Map)) return;
      list.push({
        guid,
        name: String(m.get("name") ?? ""),
        chapter: String(m.get("chapter") ?? ""),
        supplementDocId: (m.get("supplementDocId") as string | undefined) || undefined,
        orphan: m.get("orphan") === true,
      });
    });
    list.sort((a, b) => (a.chapter + "/" + a.name).localeCompare(b.chapter + "/" + b.name));
    const modules = (meta.get("modules") ?? {}) as { flowGraph?: { enabled?: boolean } };
    return {
      exists: meta.size > 0,
      schemaVersion: Number(meta.get("schemaVersion") ?? 0),
      minExtensionVersion: String(meta.get("minExtensionVersion") ?? ""),
      flowGraphEnabled: modules.flowGraph?.enabled === true,
      playscripts: list,
    };
  }

  function ensureProject(doc: YType.Doc) {
    const meta = doc.getMap("meta");
    const found = Number(meta.get("schemaVersion") ?? 0);
    if (found > SCHEMA_VERSION) throw new SchemaVersionError(found);
    if (meta.size === 0) {
      meta.set("schemaVersion", SCHEMA_VERSION);
      meta.set("minExtensionVersion", MIN_EXTENSION_VERSION);
      meta.set("modules", {
        flowGraph: { enabled: false, instances: "single" },
        playscript: { enabled: true, columns: COLUMNS.map(c => c.label) },
        outline: { enabled: false },
      });
    }
  }

  // The flow graph module is on once a graph was uploaded: from then on the cloud graph is the master and
  // local StoryForge's flow graph tab is read-only.
  function enableFlowGraph(doc: YType.Doc) {
    const meta = doc.getMap("meta");
    const modules = { ...((meta.get("modules") ?? {}) as Record<string, any>) };
    if (modules.flowGraph?.enabled === true) return;
    modules.flowGraph = { ...(modules.flowGraph ?? {}), enabled: true, instances: "single" };
    meta.set("modules", modules);
  }

  function readNodeSchema(doc: YType.Doc): string | null {
    const v = doc.getMap("rules").get("nodeSchema");
    return typeof v === "string" ? v : null;
  }

  function upsertIndex(doc: YType.Doc, entry: PlayscriptIndexEntry) {
    const playscripts = doc.getMap<YType.Map<unknown>>("playscripts");
    let m = playscripts.get(entry.guid);
    if (!(m instanceof Y.Map)) {
      m = new Y.Map<unknown>();
      playscripts.set(entry.guid, m);
    }
    if (m.get("name") !== entry.name) m.set("name", entry.name);
    if (m.get("chapter") !== entry.chapter) m.set("chapter", entry.chapter);
    if (entry.supplementDocId && m.get("supplementDocId") !== entry.supplementDocId) m.set("supplementDocId", entry.supplementDocId);
    if (entry.orphan !== undefined && (m.get("orphan") === true) !== entry.orphan) m.set("orphan", entry.orphan);
  }

  // ---------- rules ----------
  function readRules(doc: YType.Doc): { json: string; hash: string; uploadedAt: string } | null {
    const m = doc.getMap("rules");
    const json = m.get("json");
    if (typeof json !== "string") return null;
    return { json, hash: String(m.get("hash") ?? ""), uploadedAt: String(m.get("uploadedAt") ?? "") };
  }

  function writeRules(doc: YType.Doc, json: string, hash: string) {
    const m = doc.getMap("rules");
    if (m.get("hash") === hash && m.get("json") === json) return false;
    doc.transact(() => {
      m.set("json", json);
      m.set("hash", hash);
      m.set("uploadedAt", new Date().toISOString());
    });
    return true;
  }

  // ---------- playscript ----------
  function checkVersion(doc: YType.Doc) {
    const found = Number(doc.getMap("meta").get("schemaVersion") ?? 0);
    if (found > SCHEMA_VERSION) throw new SchemaVersionError(found);
  }

  function readPlayscript(doc: YType.Doc) {
    const meta = doc.getMap("meta");
    const order = doc.getArray<string>("order");
    const rowsMap = doc.getMap<YType.Map<YType.Text>>("rows");
    const rows: RowWithId[] = [];
    for (const rowId of order.toArray()) {
      const m = rowsMap.get(rowId);
      if (!(m instanceof Y.Map)) continue;
      rows.push({ rowId, cells: COLUMN_KEYS.map(k => m.get(k)?.toString() ?? "") });
    }
    return {
      exists: meta.size > 0 || order.length > 0,
      schemaVersion: Number(meta.get("schemaVersion") ?? 0),
      guid: String(meta.get("guid") ?? ""),
      name: String(meta.get("name") ?? ""),
      rows,
    };
  }

  function initPlayscriptMeta(doc: YType.Doc, guid: string, name: string) {
    checkVersion(doc);
    const meta = doc.getMap("meta");
    if (meta.get("schemaVersion") !== SCHEMA_VERSION) meta.set("schemaVersion", SCHEMA_VERSION);
    if (meta.get("guid") !== guid) meta.set("guid", guid);
    if (meta.get("name") !== name) meta.set("name", name);
  }

  function newRowMap(cells: Row) {
    const m = new Y.Map<YType.Text>();
    COLUMN_KEYS.forEach((k, i) => m.set(k, new Y.Text(cells[i] ?? "")));
    return m;
  }

  function ensureCell(m: YType.Map<YType.Text>, key: ColumnKey) {
    let t = m.get(key);
    if (!(t instanceof Y.Text)) {
      t = new Y.Text();
      m.set(key, t);
    }
    return t;
  }

  // Minimal edit of one Y.Text: keep the common prefix/suffix, replace only the middle, so a person typing
  // in the same cell at the same time keeps the characters outside the changed span.
  function setText(t: YType.Text, value: string) {
    const current = t.toString();
    if (current === value) return false;
    let start = 0;
    while (start < current.length && start < value.length && current[start] === value[start]) start++;
    let endCur = current.length;
    let endNew = value.length;
    while (endCur > start && endNew > start && current[endCur - 1] === value[endNew - 1]) { endCur--; endNew--; }
    if (endCur > start) t.delete(start, endCur - start);
    if (endNew > start) t.insert(start, value.slice(start, endNew));
    return true;
  }

  function rowMapOf(doc: YType.Doc, rowId: string) {
    const m = doc.getMap<YType.Map<YType.Text>>("rows").get(rowId);
    if (!(m instanceof Y.Map)) throw new Error(`找不到列 ${rowId}`);
    return m;
  }

  function updateCell(doc: YType.Doc, rowId: string, key: ColumnKey, value: string) {
    return setText(ensureCell(rowMapOf(doc, rowId), key), value);
  }

  function insertRows(doc: YType.Doc, index: number, rows: Row[]): string[] {
    const order = doc.getArray<string>("order");
    const rowsMap = doc.getMap<YType.Map<YType.Text>>("rows");
    const ids: string[] = [];
    const at = Math.max(0, Math.min(index, order.length));
    for (const r of rows) {
      const id = randomId();
      rowsMap.set(id, newRowMap(normalizeRow(r)));
      ids.push(id);
    }
    if (ids.length) order.insert(at, ids);
    return ids;
  }

  function deleteRows(doc: YType.Doc, rowIds: string[]) {
    const order = doc.getArray<string>("order");
    const rowsMap = doc.getMap("rows");
    const set = new Set(rowIds);
    for (let i = order.length - 1; i >= 0; i--) {
      if (set.has(order.get(i))) order.delete(i, 1);
    }
    for (const id of rowIds) rowsMap.delete(id);
  }

  function moveRows(doc: YType.Doc, rowIds: string[], toIndex: number) {
    const order = doc.getArray<string>("order");
    const set = new Set(rowIds);
    const current = order.toArray();
    const moving = current.filter(id => set.has(id));
    if (moving.length !== rowIds.length) throw new Error("有些 rowId 不在這份劇本裡");
    const before = current.slice(0, Math.max(0, Math.min(toIndex, current.length))).filter(id => !set.has(id)).length;
    for (let i = order.length - 1; i >= 0; i--) if (set.has(order.get(i))) order.delete(i, 1);
    order.insert(before, moving);
  }

  // Two-way diff from the doc's current rows to `target`, applied as the smallest set of CRDT edits:
  // identical rows (LCS on whole-row content) are kept untouched; inside each gap, rows are paired up
  // positionally and edited cell by cell, leftovers are deleted or inserted.
  function writeRows(doc: YType.Doc, target: Row[]) {
    const order = doc.getArray<string>("order");
    const rowsMap = doc.getMap<YType.Map<YType.Text>>("rows");
    const current = readPlayscript(doc).rows;
    const tgt = target.map(normalizeRow);
    const key = (r: Row) => r.join("\u0001");
    const a = current.map(r => key(r.cells));
    const b = tgt.map(key);
    const n = a.length, m = b.length;
    const dp: number[][] = Array.from({ length: n + 1 }, () => new Array<number>(m + 1).fill(0));
    for (let i = n - 1; i >= 0; i--)
      for (let j = m - 1; j >= 0; j--)
        dp[i][j] = a[i] === b[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
    const pairs: [number, number][] = [];
    for (let i = 0, j = 0; i < n && j < m;) {
      if (a[i] === b[j]) { pairs.push([i, j]); i++; j++; }
      else if (dp[i + 1][j] >= dp[i][j + 1]) i++;
      else j++;
    }
    pairs.push([n, m]);

    const stats = { kept: 0, edited: 0, inserted: 0, deleted: 0 };
    const finalIds: string[] = [];
    const toDelete: string[] = [];
    let pi = 0, pj = 0;
    for (const [ei, ej] of pairs) {
      const gapA = current.slice(pi, ei);
      const gapB = tgt.slice(pj, ej);
      const k = Math.min(gapA.length, gapB.length);
      for (let x = 0; x < k; x++) {
        const id = gapA[x].rowId;
        const rm = rowMapOf(doc, id);
        COLUMN_KEYS.forEach((col, c) => { setText(ensureCell(rm, col), gapB[x][c]); });
        finalIds.push(id);
        stats.edited++;
      }
      for (let x = k; x < gapA.length; x++) { toDelete.push(gapA[x].rowId); stats.deleted++; }
      for (let x = k; x < gapB.length; x++) {
        const id = randomId();
        rowsMap.set(id, newRowMap(gapB[x]));
        finalIds.push(id);
        stats.inserted++;
      }
      if (ei < n) { finalIds.push(current[ei].rowId); stats.kept++; }
      pi = ei + 1; pj = ej + 1;
    }

    // Rewrite order only where it differs (keeps concurrent inserts by others when nothing moved here).
    const existing = order.toArray();
    const visible = existing.filter(id => rowsMap.has(id) && !toDelete.includes(id));
    if (visible.join(",") !== finalIds.join(",") || existing.length !== visible.length) {
      // Common prefix/suffix of the order array, replace the middle.
      let s = 0;
      while (s < existing.length && s < finalIds.length && existing[s] === finalIds[s]) s++;
      let e1 = existing.length, e2 = finalIds.length;
      while (e1 > s && e2 > s && existing[e1 - 1] === finalIds[e2 - 1]) { e1--; e2--; }
      if (e1 > s) order.delete(s, e1 - s);
      if (e2 > s) order.insert(s, finalIds.slice(s, e2));
    }
    for (const id of toDelete) rowsMap.delete(id);
    return stats;
  }

  return {
    readProject, ensureProject, upsertIndex, enableFlowGraph, readNodeSchema,
    readRules, writeRules,
    readPlayscript, initPlayscriptMeta, updateCell, insertRows, deleteRows, moveRows, writeRows, setText,
  };
}

export type Schema = ReturnType<typeof createSchema>;

export function rowsEqual(a: Row[], b: Row[]) {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) {
    const x = normalizeRow(a[i]), y = normalizeRow(b[i]);
    for (let c = 0; c < x.length; c++) if (x[c] !== y[c]) return false;
  }
  return true;
}
