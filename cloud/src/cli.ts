// `affine-mcp sf <command>`: what local StoryForge calls to download / upload playscript tables.
// Input and output are JSON files (UTF-8) so nothing depends on console encoding.
//
//   sf pull  --workspace <id> --out <file>
//   sf push  --workspace <id> --in <file> --out <file>
//   sf info  --workspace <id>

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
    default:
      throw new Error("用法：affine-mcp sf info|pull|push --workspace <id> [--in file] [--out file]");
  }
}
