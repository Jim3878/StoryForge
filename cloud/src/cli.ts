// `affine-mcp sf <command>`: what local StoryForge calls to download / upload playscript tables.
// Input and output are JSON files (UTF-8) so nothing depends on console encoding.
//
//   sf pull  --workspace <id> --out <file>
//   sf push  --workspace <id> --in <file> --out <file>
//   sf info  --workspace <id>
//   sf sync-pull --workspace <id> [--since <server ms>] [--only guid,guid] --out <file>   (index + changed graph/tables, one connection)
//   sf graph-pull --workspace <id> --out <file>
//   sf graph-push --workspace <id> --in <file> --out <file>   (body: { graph, memos?, nodeSchema?, mode: seed|overwrite })

import { readFileSync, writeFileSync } from "fs";
import type { HostContext } from "./host.js";
import { createStore } from "./store.js";
import { EXTENSION_VERSION } from "./version.js";

function flag(args: string[], name: string) {
  const i = args.indexOf(`--${name}`);
  return i >= 0 ? args[i + 1] : undefined;
}

function writeJson(path: string | undefined, data: unknown) {
  const json = JSON.stringify(data, null, 1);
  if (path) writeFileSync(path, json, "utf8");
  else process.stdout.write(json + "\n");
}

export async function runCli(args: string[], ctx: HostContext) {
  const [command, ...rest] = args;
  const store = createStore(ctx);
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
      writeJson(out, { results: await store.withSession(workspaceId, s => store.pushAll(s, body.playscripts ?? [], body.rules ?? undefined)) });
      return;
    }
    case "sync-pull": {
      const sinceRaw = flag(rest, "since");
      const since = sinceRaw ? Number(sinceRaw) : null;
      const only = flag(rest, "only")?.split(",").filter(Boolean);
      writeJson(out, await store.withSession(workspaceId, s => store.syncPull(s, Number.isFinite(since) ? since : null, only)));
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
