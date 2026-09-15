# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this project is

StoryForge is a Blazor Server web port of **PlayscriptOfflineTool**, a WinForms desktop tool that lives at
`E:\UnityProject\falseBadge\PlayscriptOfflineTool` inside a separate Unity project repo. StoryForge is its
own standalone repo/app — it does not live inside the Unity project — but at runtime it reads and writes
files inside that Unity project's `Assets/` folder (playscript flow-graph YAML, character card / story
outline JSON, LDtk level data, portrait art, etc.). The WinForms tool and this web port are being developed
in parallel; when porting a feature, the original WinForms source is the reference implementation for
correct behavior — check it before re-deriving logic from scratch, and check `StoryForge.Core` for an
already-ported equivalent before writing a new one (several Core-layer scanners/settings classes have been
ported ahead of the Web-layer feature that will use them).

There is no solution (`.sln`) file — each project is built directly via its own `.csproj`.

## Commands

Dev loop (hot reload, used while actively developing):
```bash
dotnet watch --non-interactive run --project src/StoryForge.Web
```
`--non-interactive` is required: the interactive Y/N/A/V "rude edit, restart?" prompt does not receive
keypresses correctly under Git Bash/MinTTY, so without this flag the process just hangs.

Build to check for compile errors only (does not run the app):
```bash
dotnet build src/StoryForge.Web/StoryForge.Web.csproj
```

Rebuild the Release version used by the one-click desktop launcher (see below):
```bash
publish.bat
```
This must use `dotnet publish`, not `dotnet build` — a plain `build` output directory does not contain a
physical `wwwroot` folder (static web assets there only resolve via an `obj/staticwebassets` manifest
pointing back at the source tree, which only `dotnet run`/`dotnet watch` can use). Any Razor component that
touches `IWebHostEnvironment.WebRootPath` will throw at runtime against a `build`-only output.

No test project exists in this repo.

## Architecture

### Three projects
- **StoryForge.Core** — plain C# class library, no ASP.NET/Blazor dependencies. All file I/O, parsing,
  and domain logic: graph document model, scanners (LDtk, portrait assets, C# factory source, Google Sheet
  index), settings classes, pipeline comparison logic. Should stay reusable by a future non-web frontend.
- **StoryForge.Web** — the Blazor Server app. Thin `*State` classes wrap `StoryForge.Core` types with
  Blazor-friendly scoped-per-circuit lifetimes; Razor components call into those, never into Core directly
  for anything stateful.
- **StoryForge.Launcher** — a `WinExe` (no console window) one-click daily-use launcher, not part of the
  dev loop. See "One-click launcher" below.

### Project-root resolution
`StoryForge.Core.ProjectPaths.ResolveUnityProjectRoot()` is the only way any code finds the Unity project.
Unlike the old WinForms tool (which lived inside the Unity project and could walk up parent directories),
StoryForge has no directory relationship to it, so the root is read from a one-line override file at
`%LocalAppData%\StoryForge\project-root-override.txt`. If that file is missing, every feature that touches
Unity project data fails at that point with a message telling the user to create it.

### Always-mounted tab shell
`Home.razor` renders all four panels (`StoryOutlinePanel`/`PipelineStatusPanel`/`FlowGraphPanel`/
`CharacterCardPanel`) simultaneously inside `.tab-pane` divs; switching tabs only toggles
`style="display:none/block"`. Panels are never conditionally removed with `@if` — that would destroy and
recreate the component, losing state (most importantly `FlowGraphPanel`'s LiteGraph canvas: zoom/pan/
selection). A new panel must follow this same pattern.

### Flow-graph editor: server model + JS canvas, kept in sync explicitly
`GraphEditorState` (scoped per circuit) holds the authoritative `GraphDocumentModel` server-side, loaded
from either a resumed `%LocalAppData%\StoryForge\session.json` or, on first load, the Unity-exported
`PlayscriptProcessor.yaml` + `ProcessNodeSchema.json`. The canvas itself is LiteGraph.js running in
`wwwroot/js/graph-editor.js` (~1000 lines) — it is never modified in place; instead a set of
`patchXxx()` functions monkey-patch `LGraphCanvas`/`LGraph` prototypes at `init()` time to add custom
behavior LiteGraph doesn't support out of the box (multi-input links, click-and-drag edge rewiring by
grabbing the curve, a custom add-node menu on wire-drop/right-click, disabling the stock node context menu
and properties panel, middle-click-only-pans-never-drags). New canvas behavior should be added as another
`patchXxx()` call from `init()`, not by editing vendored LiteGraph source.

Server ↔ canvas sync is one-directional and explicit, not per-edit round trips:
- Server → client: `GraphEditorState.LoadGraph()` builds one `GraphEditorPayload` DTO (nodes/edges/groups/
  schema/settings) that `graph-editor.js#init()` consumes once.
- Client → server: the 存檔 (save) button calls `graph-editor.js#exportGraph()` to serialize the canvas's
  current node positions/edges/groups back into a `GraphEditorExport`, which `GraphEditorState.ApplyAndSave()`
  diffs against the live model and writes to `session.json`.
- The one exception is node creation: `graph-editor.js` calls back into `GraphEditorState.CreateNode()`
  immediately when a node is created via the add-node menu, so identity/memo field edits on a
  not-yet-saved node have something server-side to write into.
- Node identity/memo field edits go straight into the server-side model (`UpdateNodeField`) and never touch
  the canvas at all — a title built from an edited identity field only updates on the next full reload.

### Node/group title font-size formula
A node whose title is a real identity value (playscriptId/flagId) uses a floor+ceiling clamp:
`Math.min(maxNodeFontPx, Math.max(frozenNodeFontPx, NATURAL_SIZE * scale))`. A node with no identity value
yet (a generic type-label fallback like "AND"/結束劇本, or a brand-new unsaved node) uses plain unclamped
`NATURAL_SIZE * scale` — proportional to zoom like any other canvas content, no floor or ceiling. Group
titles always use the clamped formula regardless. This is driven by `NodeDto.HasIdentityTitle`
(`node._hasIdentityTitle` in JS), which must be set both when a payload node is created in `init()` and
when a node is instantiated client-side from the add-node menu.

### Pipeline Status: 4-source comparison
`PlayscriptPipelineComparer.Compare(ldtkNames, graphNames, sheetNames, csharpNames)` unions playscript names
from up to 4 sources into `PlayscriptPipelineInfo` rows (`IsOnLdtk`/`IsOnGraph`/`IsOnSheet`/`IsOnCSharp`).
`PipelineStatusState.Refresh()` always scans LDtk (`Ldtk/LdtkPlayscriptScanner`) and C# factory source
(`CSharpSource/CSharpFactoryScanner`) live from disk on every refresh (pure local file reads); the Sheet
source only exists once `DownloadSheetAsync()` has fetched `GooglePlayscriptSheetClient`'s xlsx export at
least once — there's no sheet data until that button has been pressed.

### Playscript factory codegen: "參數" column convention
`PlayscriptFactoryCodeGenerator` (`StoryForge.Core/Codegen`) turns each spreadsheet row's `功能` (function)
value into one generated C# call, reading its argument(s) from the `台詞`/`參數` columns. The rule: a
function's value only belongs in `台詞` when it genuinely **is** dialogue/display text being said or shown
(`Chat`'s message, `Button`/`Choice`/`選項`'s choice text, `UpBlock`/`DownBlock`/`LeftBlock`/`RightBlock`'s
spoken line). Any argument that is not dialogue text (a label name, a target playscript name, a duration, a
CG/diff key, ...) goes in `參數`, never `台詞` — this is a hard rule for every new function added going
forward, not just a preference. A function needing more than one such value (e.g. `TryAddLobby`) packs them
into `參數` as `value1;value2` (semicolon-separated — see `SplitParameterValues`) rather than splitting
across columns. `OpenCg`/`Diff` are grandfathered with a `參數`-first/`台詞`-fallback (`GetParameter`) purely
for backward compatibility with sheets authored before `參數` existed — that fallback pattern should not be
copied for new functions.

Whenever a `功能` value is added, removed, or has its argument column(s) changed in
`PlayscriptFactoryCodeGenerator` (both `GenerateCode` and its matching check in `ValidateSheetRows`), two
other places must be updated in the same change, not left for later:
1. The legal-value list and per-function rules in `AGENTS.md` at the root of `AppSettings.ExternalDataFolder`
   (default `E:\本地端\遊戲專案管理\臥底治安官\AI劇本\`) — that file, not this one, is what a human or AI
   actually filling in a playscript spreadsheet reads (see the comment atop `NodeScriptTableStore.cs`).
2. The `功能` column's dropdown (Data validation) on the live "劇本檔範本" Google Sheet template itself —
   without this, the sheet still offers/accepts the old value and rejects the new one when someone actually
   types it in. On that sheet the dropdown has been observed saved as two separate data-validation rules
   covering `E2` and `E3:E1000` rather than one rule over the whole column (apparently never consolidated) —
   until that's cleaned up, editing the dropdown means opening the rule from a cell in each of those two
   ranges and applying the same edit to both, or the two ranges will drift out of sync again.

It is easy to change the generator's accepted `功能` values while forgetting that the spreadsheet and
`AGENTS.md` still describe the old ones — both are the actual interface a spreadsheet author sees, this file
isn't.

### Settings persistence: two different homes, do not confuse them
- **Tracked in the Unity project, in git** (`Assets/06.Definition/PlayscriptOfflineToolData/*.json`):
  `CharacterCardSettings`, `StoryOutlineSettings`. Shared, collaboratively-written content — the same
  physical file the old WinForms tool reads/writes if pointed at the same project.
- **Per-machine, in `%LocalAppData%\StoryForge\`**: `AppSettings` (`settings.json`), `ChapterFilterSettings`
  (`pipeline-filter.json`), `PortraitDescriptionSettings` (`portrait-descriptions.json`),
  `PortraitFilterSettings` (`portrait-filter.json`). The old WinForms tool has its own separate
  `%LocalAppData%\PlayscriptOfflineTool\` folder — `LegacyToolMigration.MigrateIfNeeded()` (called once at
  the top of `Program.cs`, gated by a `legacy-migration-done.txt` marker so it only ever runs once) is the
  one-time bridge that copies/merges the old tool's real tuned values into StoryForge's own folder the first
  time this app starts on a machine that has the old tool's data.

### Critical gotcha: sandboxed tool calls cannot reliably touch `%LocalAppData%\StoryForge\`
Windows silently redirects a sandboxed tool's (e.g. an agent's own Bash/PowerShell/Read calls) reads and
writes under `%LocalAppData%` to a container-private overlay, invisible to the app's own real process. A
file written this way *looks* successful when read back by the same sandboxed tool, but the real running
app never sees it. Any migration, settings fix, or data seeding that needs to land in
`%LocalAppData%\StoryForge\` must be done as code that runs inside the real app process (see
`LegacyToolMigration`, which exists specifically because of this), never as an external tool editing that
path directly. Paths on a normal project drive (not under the user profile) are not affected.

### One-click launcher
`StoryForge.Launcher` is a separate, non-dev-loop way to run the app: it resolves (or first-time publishes)
`StoryForge.Web`'s Release build, starts it as its own child process bound to a fixed port, polls until it
responds, opens it in an isolated-profile app-mode Chrome window (`--app=`, no tabs/address bar), and kills
the server when that window closes. It redirects the child processes' stdout/stderr to log files under
`%LocalAppData%\StoryForge\` using `BeginOutputReadLine`/`BeginErrorReadLine` — redirecting without draining
those streams deadlocks the child the moment its startup logging fills the OS pipe buffer, which is a bug
this code already hit once and fixed. A desktop shortcut points at the Launcher's Release exe; `publish.bat`
is what refreshes the Release build both the shortcut and the Launcher's own first-run fallback use.
