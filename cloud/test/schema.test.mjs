import test from "node:test";
import assert from "node:assert/strict";
import * as Y from "yjs";
import { createSchema } from "../dist/schema.js";
import { checkRows } from "../dist/rules.js";

const S = createSchema(Y);
const row = (name, text, func = "") => [name, "", "", text, func, "", ""];
const cells = doc => S.readPlayscript(doc).rows.map(r => r.cells);

function sync(a, b) {
  Y.applyUpdate(b, Y.encodeStateAsUpdate(a, Y.encodeStateVector(b)));
  Y.applyUpdate(a, Y.encodeStateAsUpdate(b, Y.encodeStateVector(a)));
}

test("writeRows reaches the target and keeps unchanged row ids", () => {
  const doc = new Y.Doc();
  S.writeRows(doc, [row("遼司", "一"), row("澪音", "二"), row("遼司", "三")]);
  const before = S.readPlayscript(doc).rows;
  const stats = S.writeRows(doc, [row("遼司", "一"), row("澪音", "二改"), row("遼司", "三"), row("灰澤", "四")]);
  const after = S.readPlayscript(doc).rows;
  assert.deepEqual(after.map(r => r.cells), [row("遼司", "一"), row("澪音", "二改"), row("遼司", "三"), row("灰澤", "四")]);
  assert.equal(after[0].rowId, before[0].rowId);
  assert.equal(after[1].rowId, before[1].rowId, "edited row keeps its id");
  assert.equal(after[2].rowId, before[2].rowId);
  assert.deepEqual(stats, { kept: 2, edited: 1, inserted: 1, deleted: 0 });
});

test("writeRows handles deletes, reorders and empty targets", () => {
  const doc = new Y.Doc();
  S.writeRows(doc, [row("a", "1"), row("b", "2"), row("c", "3"), row("d", "4")]);
  S.writeRows(doc, [row("c", "3"), row("a", "1")]);
  assert.deepEqual(cells(doc), [row("c", "3"), row("a", "1")]);
  S.writeRows(doc, []);
  assert.deepEqual(cells(doc), []);
  assert.equal(doc.getMap("rows").size, 0, "deleted rows leave no orphan entries");
});

test("a cell edit merges with someone else's concurrent typing in the same cell", () => {
  const a = new Y.Doc(), b = new Y.Doc();
  S.writeRows(a, [row("遼司", "最便宜的是水")]);
  sync(a, b);
  // b types at the end, a rewrites the start
  const id = S.readPlayscript(b).rows[0].rowId;
  b.getMap("rows").get(id).get("text").insert(6, "，要不要？");
  S.writeRows(a, [row("遼司", "最貴的是水")]);
  sync(a, b);
  assert.equal(cells(a)[0][3], "最貴的是水，要不要？");
  assert.deepEqual(cells(a), cells(b));
});

test("concurrent row inserts from two writers both survive", () => {
  const a = new Y.Doc(), b = new Y.Doc();
  S.writeRows(a, [row("x", "1"), row("x", "2")]);
  sync(a, b);
  S.insertRows(a, 1, [row("A", "a")]);
  S.insertRows(b, 2, [row("B", "b")]);
  sync(a, b);
  assert.equal(cells(a).length, 4);
  assert.deepEqual(cells(a), cells(b));
});

test("moveRows and deleteRows", () => {
  const doc = new Y.Doc();
  const ids = S.insertRows(doc, 0, [row("a", "1"), row("b", "2"), row("c", "3")]);
  S.moveRows(doc, [ids[2]], 0);
  assert.deepEqual(cells(doc).map(r => r[0]), ["c", "a", "b"]);
  S.deleteRows(doc, [ids[0]]);
  assert.deepEqual(cells(doc).map(r => r[0]), ["c", "b"]);
});

test("checkRows flags unknown function, name and per-character face", () => {
  const rules = {
    version: 1,
    functions: [{ name: "OpenCg" }, { name: "Chat", withText: true }],
    dialogNames: ["遼司", "澪音"], chatNames: ["匿名"], faces: ["一般", "生氣"],
    facesByName: { "澪音": ["一般"] },
  };
  const issues = checkRows(rules, [
    ["遼司", "生氣", "", "好", "", "", ""],
    ["澪音", "生氣", "", "好", "opencg", "", ""],
    ["路人", "", "", "好", "Fly", "", ""],
  ]);
  assert.deepEqual(issues.map(i => `${i.row}:${i.column}`), ["2:表情", "3:功能", "3:名字"]);
});

// ---------- flow graph ----------
import { createFlowGraph } from "../dist/flowgraph.js";
import { readFileSync, existsSync } from "fs";
const F = createFlowGraph(Y);
const BLUEPRINT = "E:/DevTool/_sf-cloud-test/data/blueprint.json";
const sortGraph = g => JSON.parse(JSON.stringify({ ...g,
  nodes: [...g.nodes].sort((a, b) => a.id.localeCompare(b.id)),
  edges: [...g.edges].map(e => JSON.stringify(e)).sort(),
  groups: [...g.groups].map(x => ({ ...x, nodes: [...x.nodes].sort() })).sort((a, b) => a.id.localeCompare(b.id)),
  stickyNotes: [...g.stickyNotes].sort((a, b) => a.id.localeCompare(b.id)) }));

test("flow graph round-trips the real blueprint graph losslessly", { skip: !existsSync(BLUEPRINT) }, () => {
  const graph = JSON.parse(readFileSync(BLUEPRINT, "utf8")).graph;
  const doc = new Y.Doc();
  F.writeGraph(doc, graph);
  const back = F.readGraph(doc);
  assert.deepEqual(sortGraph(back), sortGraph(graph));
  // Through an encoded update too (what actually goes over the wire).
  const doc2 = new Y.Doc();
  Y.applyUpdate(doc2, Y.encodeStateAsUpdate(doc));
  assert.deepEqual(sortGraph(F.readGraph(doc2)), sortGraph(graph));
});

test("two people's saves from the same base both survive", () => {
  const base = {
    schemaVersion: 1, graphId: "PlayscriptProcessor", stickyNotes: [], groups: [],
    nodes: [
      { id: "a", type: "playscript", position: { x: 0, y: 0, width: 220, height: 140 }, data: { playscriptId: "A1/1" } },
      { id: "b", type: "and", position: { x: 300, y: 0, width: 100, height: 60 }, data: {} },
    ],
    edges: [],
  };
  const a = new Y.Doc(), b = new Y.Doc();
  F.writeGraph(a, base);
  Y.applyUpdate(b, Y.encodeStateAsUpdate(a));
  // A moves node a; B renames node a and adds a link.
  const editA = structuredClone(base); editA.nodes[0].position.x = 50;
  const editB = structuredClone(base); editB.nodes[0].data.playscriptId = "A1/1_改名";
  editB.edges.push({ fromNode: "a", fromPort: "isFinish", toNode: "b", toPort: "condition" });
  F.writeGraph(a, editA, base);
  F.writeGraph(b, editB, base);
  Y.applyUpdate(a, Y.encodeStateAsUpdate(b)); Y.applyUpdate(b, Y.encodeStateAsUpdate(a));
  const g = F.readGraph(a);
  assert.equal(g.nodes[0].position.x, 50);
  assert.equal(g.nodes[0].data.playscriptId, "A1/1_改名");
  assert.equal(g.edges.length, 1);
  assert.deepEqual(sortGraph(g), sortGraph(F.readGraph(b)));
});

test("a link to a node someone else deleted is dropped", () => {
  const base = { schemaVersion: 1, graphId: "g", stickyNotes: [], groups: [], edges: [],
    nodes: [{ id: "a", type: "and", position: { x: 0, y: 0, width: 1, height: 1 }, data: {} },
            { id: "b", type: "and", position: { x: 0, y: 0, width: 1, height: 1 }, data: {} }] };
  const a = new Y.Doc(), b = new Y.Doc();
  F.writeGraph(a, base); Y.applyUpdate(b, Y.encodeStateAsUpdate(a));
  F.writeGraph(a, { ...base, nodes: [base.nodes[0]] }, base);
  F.writeGraph(b, { ...base, edges: [{ fromNode: "a", fromPort: "o", toNode: "b", toPort: "i" }] }, base);
  Y.applyUpdate(a, Y.encodeStateAsUpdate(b));
  const g = F.readGraph(a);
  assert.equal(g.nodes.length, 1);
  assert.equal(g.edges.length, 0);
});
