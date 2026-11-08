// `affine-mcp sf <command>`: what local StoryForge calls to download / upload playscript tables.
// Input and output are JSON files (UTF-8) so nothing depends on console encoding.
//
//   sf pull  --workspace <id> --out <file>
//   sf push  --workspace <id> --in <file> --out <file>   (body: { playscripts, rules?, references?, contentSeed?, graph? })
//   sf info  --workspace <id>
//   sf sync-pull --workspace <id> [--since <ms>] [--graph-since <ms>] [--no-tables] [--no-graph] [--only guid,guid] --out <file>
//                (index + changed graph/tables, one connection)
//   sf graph-pull --workspace <id> --out <file>
//   sf graph-push --workspace <id> --in <file> --out <file>   (body: { graph, memos?, nodeSchema?, mode: seed|overwrite })

import { readFileSync, writeFileSync } from "fs";
import type { HostContext } from "./host.js";
import { createStore, type Progress } from "./store.js";
import { EXTENSION_VERSION } from "./version.js";

function flag(args: string[], name: string) {
  const i = args.indexOf(`--${name}`);
  return i >= 0 ? args[i + 1] : undefined;
}

// Progress goes to stderr as `@sf-progress {json}` lines (throttled), for the caller to show a progress bar.
// Errors stay plain stderr text, so older callers that only read the last stderr line still work.
function stderrProgress(): Progress {
  let last = 0;
  let lastPhase = "";
  return (phase, done, total) => {
    const now = Date.now();
    const final = total !== undefined && done === total;
    if (phase === lastPhase && !final && now - last < 80) return;
    last = now;
    lastPhase = phase;
    process.stderr.write(`@sf-progress ${JSON.stringify({ phase, done, total })}
`);
  };
}

function writeJson(path: string | undefined, data: unknown) {
  const json = JSON.stringify(data, null, 1);
  if (path) writeFileSync(path, json, "utf8");
  else process.stdout.write(json + "\n");
}

export async function runCli(args: string[], ctx: HostContext) {
  const [command, ...rest] = args;
  const store = createStore(ctx, stderrProgress());
  const workspaceId = flag(rest, "workspace") ?? ctx.defaultWorkspaceId;
  const out = flag(rest, "out");

  switch (command) {
    case "info":
      writeJson(out, await store.withSession(workspaceId, async s => ({
        extensionVersion: EXTENSION_VERSION, hostVersion: ctx.hostVersion, workspaceId: s.workspaceId, project: await store.project(s),
      })));
      return;
    case "pull":
      writeJson(out, await store.withSession(workspaceId, s => store.pullAll(s)));
      return;
    case "push": {
      const input = flag(rest, "in");
      if (!input) throw new Error("sf push 需要 --in <file>");
      const body = JSON.parse(readFileSync(input, "utf8").replace(/^﻿/, ""));
      writeJson(out, await store.withSession(workspaceId, s =>
        store.pushAllEx(s, body.playscripts ?? [], body.rules ?? undefined, body.references ?? undefined, body.contentSeed ?? undefined,
          body.graph ?? undefined)));
      return;
    }
    case "sync-pull": {
      const num = (name: string) => {
        const raw = flag(rest, name);
        const n = raw ? Number(raw) : NaN;
        return Number.isFinite(n) ? n : null;
      };
      const only = flag(rest, "only")?.split(",").filter(Boolean);
      writeJson(out, await store.withSession(workspaceId, s => store.syncPull(s, {
        since: num("since"),
        graphSince: rest.includes("--graph-since") ? num("graph-since") : undefined,
        tables: !rest.includes("--no-tables"),
        graph: !rest.includes("--no-graph"),
        only,
      })));
      return;
    }
    case "graph-pull":
      writeJson(out, await store.withSession(workspaceId, s => store.pullGraph(s)));
      return;
    case "graph-push": {
      const input = flag(rest, "in");
      if (!input) throw new Error("sf graph-push 需要 --in <file>");
      const body = JSON.parse(readFileSync(input, "utf8").replace(/^﻿/, ""));
      writeJson(out, await store.withSession(workspaceId, s => store.pushGraph(s, body)));
      return;
    }
    default:
      throw new Error("用法：affine-mcp sf info|pull|push|sync-pull|graph-pull|graph-push --workspace <id> [--in file] [--out file]");
  }
}
