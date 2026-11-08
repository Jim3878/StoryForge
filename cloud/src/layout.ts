// The `@nineday/storyforge-cloud/layout` door: only the cloud data layout — no MCP tools, no CLI, nothing that
// needs Node — for the AFFiNE fork's web editors. Everything that builds Yjs values takes the host's own `Y`
// (createSchema(Y), createFlowGraph(Y)), so a consumer never ends up with two copies of yjs.
export * from "./schema.js";
export * from "./rules.js";
export * from "./flowgraph.js";
export { SF_ASSET_FILES, SF_ASSETS_VERSION } from "./assets-version.js";
