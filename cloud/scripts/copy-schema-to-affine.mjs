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
for (const file of ["schema.ts", "rules.ts"]) {
  const text = readFileSync(path.join(src, file), "utf8").replace(/from "\.\/(\w+)\.js"/g, 'from "./$1"');
  writeFileSync(path.join(target, file), header + text);
}
console.log(`schema copied to ${target}`);
