// Copies the built extension into the affine-mcp fork's extensions/storyforge/ folder, which the fork
// loads at startup and ships inside its packed tgz. Usage: npm run install-into-mcp [-- <affine-mcp root>]
import { cpSync, existsSync, mkdirSync, readdirSync, rmSync } from "fs";
import path from "path";
import { fileURLToPath } from "url";

const here = path.dirname(fileURLToPath(import.meta.url));
const dist = path.resolve(here, "..", "dist");
const mcpRoot = path.resolve(process.argv[2] || "E:/Dev/affine-mcp-server");
const target = path.join(mcpRoot, "extensions", "storyforge");

if (!existsSync(path.join(mcpRoot, "package.json"))) throw new Error(`affine-mcp not found at ${mcpRoot}`);
if (existsSync(target)) rmSync(target, { recursive: true });
mkdirSync(target, { recursive: true });
for (const f of readdirSync(dist)) if (f.endsWith(".js")) cpSync(path.join(dist, f), path.join(target, f));
console.log(`StoryForge extension copied to ${target}`);
