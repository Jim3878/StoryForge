// Copies the shared data layout (schema.ts, rules.ts) into the AFFiNE fork's StoryForge module, so the
// web editor and the MCP extension read and write exactly the same structure. Run after changing either.
// Usage: node scripts/copy-schema-to-affine.mjs [<AFFiNE repo root>]
import { readFileSync, writeFileSync, mkdirSync } from "fs";
import path from "path";
import { fileURLToPath } from "url";

const here = path.dirname(fileURLToPath(import.meta.url));
const src = path.resolve(here, "..", "src");
const affine = path.resolve(process.argv[2] || "E:/Dev/AFFiNE");
const target = path.join(affine, "packages/frontend/core/src/modules/storyforge/shared");
mkdirSync(target, { recursive: true });

const header = "// GENERATED: copied from StoryForge cloud/src by scripts/copy-schema-to-affine.mjs. Edit it there.\n/* eslint-disable */\n";
for (const file of ["schema.ts", "rules.ts", "flowgraph.ts"]) {
  const text = readFileSync(path.join(src, file), "utf8").replace(/from "\.\/(\w+)\.js"/g, 'from "./$1"');
  writeFileSync(path.join(target, file), header + text);
}
// The flow graph canvas: StoryForge's own graph-editor.js on top of the vendored LiteGraph, served as
// static files (core/public is copied into the web build) and loaded as plain scripts by the page.
const webRoot = path.resolve(here, "..", "..", "src/StoryForge.Web/wwwroot");
const assets = path.join(affine, "packages/frontend/core/public/sf-assets");
mkdirSync(assets, { recursive: true });
for (const [from, to] of [["lib/litegraph/litegraph.min.js", "litegraph.min.js"], ["lib/litegraph/litegraph.css", "litegraph.css"], ["js/graph-editor.js", "graph-editor.js"]]) {
  writeFileSync(path.join(assets, to), readFileSync(path.join(webRoot, from)));
}
console.log(`schema copied to ${target}; canvas scripts copied to ${assets}`);
