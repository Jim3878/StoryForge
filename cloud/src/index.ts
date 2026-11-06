import type { Extension } from "./host.js";
import { registerTools } from "./mcp.js";
import { runCli } from "./cli.js";
import { EXTENSION_VERSION } from "./version.js";

const extension: Extension = {
  name: "storyforge",
  version: EXTENSION_VERSION,
  register: registerTools,
  cli: { command: "sf", summary: "StoryForge playscript tables: info | pull | push", run: runCli },
};

export default extension;
