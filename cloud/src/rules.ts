// Rules JSON (uploaded by local StoryForge as db$<ws>$sf.rules) and the light checks every writer runs
// before touching a playscript table. These are only the closed-list checks; the full rule set still lives
// in C# PlayscriptFactoryCodeGenerator.ValidateSheetRows, which runs on download / 匯出成C#.

import { COLUMNS, normalizeRow, type Row } from "./schema.js";

export type FunctionRule = {
  name: string;
  param?: string;          // hint shown for the 參數 column, e.g. "CG 名稱;備註（可省略）"
  withText?: boolean;      // the function sits on the same row as a spoken / displayed line
  legacy?: boolean;        // accepted for old drafts only
};

export type Rules = {
  version: number;
  functions: FunctionRule[];
  aliases?: Record<string, string>; // legacy spelling -> canonical function name
  dialogNames: string[];
  chatNames: string[];
  faces: string[];
  facesByName: Record<string, string[]>;
};

export type RowIssue = { row: number; column: string; value: string; message: string; allowed?: string[] };

const NAME = 0, FACE = 1, FUNC = 4;

export function parseRules(json: string): Rules {
  const r = JSON.parse(json) as Rules;
  r.functions ??= [];
  r.dialogNames ??= [];
  r.chatNames ??= [];
  r.faces ??= [];
  r.facesByName ??= {};
  return r;
}

function short(list: string[]) {
  return list.length > 60 ? [...list.slice(0, 60), `…（共 ${list.length} 個）`] : list;
}

// rowOffset: 1-based display number of rows[0].
export function checkRows(rules: Rules, rows: Row[], rowOffset = 1): RowIssue[] {
  const issues: RowIssue[] = [];
  const funcs = new Map(rules.functions.map(f => [f.name.toLowerCase(), f]));
  const aliases = new Map(Object.entries(rules.aliases ?? {}).map(([k, v]) => [k.toLowerCase(), v]));
  const names = new Set([...rules.dialogNames, ...rules.chatNames]);
  const faces = new Set(rules.faces);

  rows.forEach((raw, i) => {
    const r = normalizeRow(raw);
    const n = i + rowOffset;
    const func = r[FUNC].trim();
    if (func && !funcs.has(func.toLowerCase()) && !aliases.has(func.toLowerCase())) {
      issues.push({ row: n, column: COLUMNS[FUNC].label, value: func, message: "功能欄只能填清單裡的值", allowed: short(rules.functions.filter(f => !f.legacy).map(f => f.name)) });
    }
    const name = r[NAME].trim();
    if (name && names.size > 0 && !names.has(name)) {
      issues.push({ row: n, column: COLUMNS[NAME].label, value: name, message: "名字不在名字清單裡", allowed: short(func.toLowerCase() === "chat" ? rules.chatNames : rules.dialogNames) });
    }
    const face = r[FACE].trim();
    if (face) {
      const own = rules.facesByName[name];
      if (own && own.length > 0) {
        if (!own.includes(face)) issues.push({ row: n, column: COLUMNS[FACE].label, value: face, message: `「${name}」沒有這個表情`, allowed: own });
      } else if (faces.size > 0 && !faces.has(face)) {
        issues.push({ row: n, column: COLUMNS[FACE].label, value: face, message: "表情不在表情清單裡", allowed: short(rules.faces) });
      }
    }
  });
  return issues;
}
