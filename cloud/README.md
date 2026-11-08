# @nineday/storyforge-cloud

StoryForge 的雲端資料佈局，以及給 nineday 版 affine-mcp 的 StoryForge 擴充。

一個套件、兩道門：

| 匯入路徑 | 給誰 | 內容 |
|---|---|---|
| `@nineday/storyforge-cloud` | affine-mcp fork | 擴充本體：`{ name, version, register(ctx), cli }`，提供 `sf_*` 工具和 `affine-mcp sf …` 指令 |
| `@nineday/storyforge-cloud/layout` | AFFiNE fork 的網頁編輯器 | 純資料佈局：`createSchema(Y)`、`createFlowGraph(Y)`、`checkRows`、`SF_ASSETS_VERSION`。沒有 MCP、沒有 CLI、沒有 Node 專用程式碼 |
| `@nineday/storyforge-cloud/assets/*` | AFFiNE fork 的 build | 流程圖畫布：`graph-editor.js`、`litegraph.min.js`、`litegraph.css` |

所有會建立 Yjs 值的函式都接收呼叫端的 `Y`，套件本身不帶 yjs。

## 版本

- 套件版本就是擴充版本（`EXTENSION_VERSION` 由 build 從 package.json 產生）。
- `SCHEMA_VERSION`（資料格式）是另一個數字，只在佈局不相容時才動。
- 消費端用精確版本釘住，不用 `^`。

## 開發

```bash
npm run build     # 產生 version.ts、複製畫布檔、tsc
npm test
```

本機同時改 AFFiNE 時，在 AFFiNE repo 用 `yarn link <此資料夾>` 指向這裡；那一行 resolutions 不要 commit。

## 發佈

推 tag `cloud-v<package.json 的版本>`，`.github/workflows/publish-cloud.yml` 會 build、測試、`npm publish`（npm trusted publishing，不需要 token）。
