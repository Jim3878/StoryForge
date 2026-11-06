// sf_* MCP tools: AI reads and writes StoryForge playscript tables directly in the cloud.

import type { HostContext, ToolResult } from "./host.js";
import { createStore } from "./store.js";
import { COLUMNS, COLUMN_KEYS, normalizeRow, playscriptDocId, type ColumnKey, type Row } from "./schema.js";
import { checkRows, type RowIssue } from "./rules.js";

function ok(data: unknown): ToolResult {
  const structured = data !== null && typeof data === "object" && !Array.isArray(data) ? data : { value: data };
  return { content: [{ type: "text", text: JSON.stringify(data) }], structuredContent: structured };
}

function fail(message: string, extra?: Record<string, unknown>): ToolResult {
  const body = { error: message, ...extra };
  return { content: [{ type: "text", text: JSON.stringify(body) }], structuredContent: body, isError: true };
}

const LABEL_TO_KEY = new Map<string, ColumnKey>([
  ...COLUMNS.map(c => [c.label, c.key] as [string, ColumnKey]),
  ...COLUMNS.map(c => [c.key, c.key] as [string, ColumnKey]),
]);

function toRow(input: unknown): Row {
  if (Array.isArray(input)) return normalizeRow(input.map(v => (v ?? "").toString()));
  if (input && typeof input === "object") {
    const row = normalizeRow([]);
    for (const [k, v] of Object.entries(input as Record<string, unknown>)) {
      const key = LABEL_TO_KEY.get(k);
      if (!key) throw new Error(`不認得的欄位「${k}」。欄位只有：${COLUMNS.map(c => c.label).join("、")}`);
      row[COLUMN_KEYS.indexOf(key)] = (v ?? "").toString();
    }
    return row;
  }
  throw new Error("每一列要是物件（例如 {\"名字\":\"遼司\",\"台詞\":\"…\"}）或 7 格的陣列");
}

function rowObject(cells: Row) {
  const o: Record<string, string> = {};
  COLUMNS.forEach((c, i) => { o[c.label] = cells[i]; });
  return o;
}

export function registerTools(ctx: HostContext) {
  const { z } = ctx;
  const store = createStore(ctx);
  const ws = z.string().optional().describe("AFFiNE workspace id；省略時用預設 workspace");
  const playscript = z.string().describe("劇本的 guid，或劇本名稱（playscriptId）。名稱重複時要用 guid");
  const rowInput = z.union([z.record(z.string()), z.array(z.string())]);

  type Check = { cells: Row; row: number }[];
  const seq = (rows: Row[], offset: number): Check => rows.map((cells, i) => ({ cells, row: offset + i }));

  async function guarded(check: Check, s: any): Promise<RowIssue[]> {
    const rules = await store.rules(s);
    if (!rules) return [];
    return check.flatMap(c => checkRows(rules, [c.cells], c.row));
  }

  ctx.registerTool("sf_list_playscripts", {
    title: "List StoryForge Playscripts",
    description: "列出這個 workspace 裡所有 StoryForge 劇本表格（guid、名稱、章節）。劇本表格不是 AFFiNE 頁面，不能用 read_doc 讀，要用 sf_* 工具。",
    inputSchema: { workspaceId: ws },
  }, async ({ workspaceId }) => store.withSession(workspaceId, async s => {
    const p = await store.project(s);
    if (!p.exists) return fail("這個 workspace 還沒有 StoryForge 專案（沒有 sf.project）。請先從本機 StoryForge 上傳。");
    return ok({ schemaVersion: p.schemaVersion, playscripts: p.playscripts });
  }), { readOnly: true });

  ctx.registerTool("sf_read_playscript", {
    title: "Read StoryForge Playscript",
    description: "讀一份劇本表格。每一列有 rowId（修改時用）和 7 個欄位：名字、表情、配音情緒、台詞、功能、備註、參數。row 是從 1 開始的列號。",
    inputSchema: { workspaceId: ws, playscript },
  }, async ({ workspaceId, playscript }) => store.withSession(workspaceId, async s => {
    const hit = await store.resolveGuid(s, playscript);
    const ps = await store.readPlayscript(s, hit.guid);
    return ok({ ...hit, rows: ps.rows.map((r, i) => ({ row: i + 1, rowId: r.rowId, ...rowObject(r.cells) })) });
  }), { readOnly: true });

  ctx.registerTool("sf_read_rules", {
    title: "Read StoryForge Rules",
    description: "讀劇本表格的規則：功能欄的合法值與參數格式、名字清單、每個角色可用的表情。寫入前先讀；完整寫作規範仍以 AGENTS.md 為準。",
    inputSchema: { workspaceId: ws },
  }, async ({ workspaceId }) => store.withSession(workspaceId, async s => {
    const r = await store.rules(s);
    if (!r) return fail("還沒有上傳規則 JSON。請在本機 StoryForge 按「上傳」。");
    return ok(r);
  }), { readOnly: true });

  ctx.registerTool("sf_validate", {
    title: "Validate StoryForge Rows",
    description: "只檢查、不寫入：檢查一份劇本目前的內容，或檢查你準備寫入的 rows。回傳不合規的列、欄位與合法值。",
    inputSchema: { workspaceId: ws, playscript: playscript.optional(), rows: z.array(rowInput).optional() },
  }, async ({ workspaceId, playscript, rows }) => store.withSession(workspaceId, async s => {
    let target: Row[];
    if (rows) target = rows.map(toRow);
    else if (playscript) target = (await store.readPlayscript(s, (await store.resolveGuid(s, playscript)).guid)).rows.map(r => r.cells);
    else return fail("要給 playscript 或 rows 其中一個");
    const issues = await guarded(seq(target, 1), s);
    return ok({ valid: issues.length === 0, issues });
  }), { readOnly: true });

  async function write(workspaceId: string | undefined, name: string, fn: (doc: any, s: any, current: { rowId: string; cells: Row }[]) => Promise<{ check: Check; result: unknown } | ToolResult>) {
    return store.withSession(workspaceId, async s => {
      await store.requireWritable(s);
      const hit = await store.resolveGuid(s, name);
      const docId = playscriptDocId(s.workspaceId, hit.guid);
      const doc = await s.load(docId);
      const before = ctx.Y.encodeStateVector(doc);
      const current = store.schema.readPlayscript(doc).rows;
      const out = await fn(doc, s, current);
      if ("content" in (out as any)) return out as ToolResult;
      const o = out as { check: Check; result: unknown };
      const issues = await guarded(o.check, s);
      if (issues.length) return fail("有不合規的內容，什麼都沒有寫入。請照 allowed 修正後再試。", { issues });
      await s.push(docId, doc, before);
      return ok({ guid: hit.guid, name: hit.name, ...(o.result as object) });
    });
  }

  ctx.registerTool("sf_update_cells", {
    title: "Update StoryForge Cells",
    description: "改劇本表格的格子。只改有變的字元，跟同時在線的人不會互相覆蓋。column 用中文欄名（名字、表情、配音情緒、台詞、功能、備註、參數）。",
    inputSchema: {
      workspaceId: ws, playscript,
      edits: z.array(z.object({ rowId: z.string(), column: z.string(), value: z.string() })).min(1),
    },
  }, async ({ workspaceId, playscript, edits }) => write(workspaceId, playscript, async (doc, _s, current) => {
    const byId = new Map(current.map((r, i) => [r.rowId, { i, cells: [...r.cells] as Row }]));
    for (const e of edits) {
      const key = LABEL_TO_KEY.get(e.column);
      if (!key) return fail(`不認得的欄位「${e.column}」`);
      const r = byId.get(e.rowId);
      if (!r) return fail(`找不到 rowId ${e.rowId}`);
      r.cells[COLUMN_KEYS.indexOf(key)] = e.value;
    }
    const touched = [...new Set(edits.map((e: any) => e.rowId))].map(id => byId.get(id as string)!);
    let changed = 0;
    doc.transact(() => {
      for (const e of edits) if (store.schema.updateCell(doc, e.rowId, LABEL_TO_KEY.get(e.column)!, e.value)) changed++;
    });
    return { check: touched.map(t => ({ cells: t.cells, row: t.i + 1 })), result: { changedCells: changed } };
  }), { readOnly: false });

  ctx.registerTool("sf_insert_rows", {
    title: "Insert StoryForge Rows",
    description: "在劇本表格插入新的列。用 afterRowId 指定插在哪一列後面（省略就加在最後；給空字串就插在最前面）。每列是 {\"名字\":…,\"台詞\":…} 這種物件。",
    inputSchema: { workspaceId: ws, playscript, afterRowId: z.string().optional(), rows: z.array(rowInput).min(1) },
  }, async ({ workspaceId, playscript, afterRowId, rows }) => write(workspaceId, playscript, async (doc, _s, current) => {
    const newRows = rows.map(toRow);
    let index = current.length;
    if (afterRowId === "") index = 0;
    else if (afterRowId) {
      const i = current.findIndex(r => r.rowId === afterRowId);
      if (i < 0) return fail(`找不到 rowId ${afterRowId}`);
      index = i + 1;
    }
    let ids: string[] = [];
    doc.transact(() => { ids = store.schema.insertRows(doc, index, newRows); });
    return { check: seq(newRows, index + 1), result: { insertedRowIds: ids, at: index + 1 } };
  }), { readOnly: false });

  ctx.registerTool("sf_delete_rows", {
    title: "Delete StoryForge Rows",
    description: "刪除劇本表格的列（用 rowId）。",
    inputSchema: { workspaceId: ws, playscript, rowIds: z.array(z.string()).min(1) },
  }, async ({ workspaceId, playscript, rowIds }) => write(workspaceId, playscript, async (doc, _s, current) => {
    const known = new Set(current.map(r => r.rowId));
    const missing = rowIds.filter((id: string) => !known.has(id));
    if (missing.length) return fail(`找不到 rowId：${missing.join("、")}`);
    doc.transact(() => store.schema.deleteRows(doc, rowIds));
    return { check: [], result: { deleted: rowIds.length } };
  }), { readOnly: false });

  ctx.registerTool("sf_move_rows", {
    title: "Move StoryForge Rows",
    description: "移動劇本表格的列：把 rowIds（保持原本順序）移到 afterRowId 後面；afterRowId 給空字串就移到最前面。",
    inputSchema: { workspaceId: ws, playscript, rowIds: z.array(z.string()).min(1), afterRowId: z.string() },
  }, async ({ workspaceId, playscript, rowIds, afterRowId }) => write(workspaceId, playscript, async (doc, _s, current) => {
    let to = 0;
    if (afterRowId) {
      const i = current.findIndex(r => r.rowId === afterRowId);
      if (i < 0) return fail(`找不到 rowId ${afterRowId}`);
      if (rowIds.includes(afterRowId)) return fail("afterRowId 不能是要移動的列");
      to = i + 1;
    }
    doc.transact(() => store.schema.moveRows(doc, rowIds, to));
    return { check: [], result: { moved: rowIds.length } };
  }), { readOnly: false });

  ctx.registerTool("sf_write_playscript", {
    title: "Write StoryForge Playscript",
    description: "整份改寫一份劇本表格。會自動算出最小差異（沒變的列和字元都不動），適合大幅改寫；小改動用 sf_update_cells。",
    inputSchema: { workspaceId: ws, playscript, rows: z.array(rowInput) },
  }, async ({ workspaceId, playscript, rows }) => write(workspaceId, playscript, async doc => {
    const target = rows.map(toRow);
    let stats: unknown;
    doc.transact(() => { stats = store.schema.writeRows(doc, target); });
    return { check: seq(target, 1), result: { stats } };
  }), { readOnly: false });
}
