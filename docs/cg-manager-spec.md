# CG 管理分頁規格

取代 Unity「CG 匯入整合工具」（`Assets/Editor/EditorWindow/CgImportPipeline/CgImportPipelineWindow.cs`）。
2026-10-12 與使用者逐題確認定案。

## 資料來源

- **CG 清單**：掃 `AppSettings.ExternalDataFolder\Nodes\**\*.md` 劇本表格的「功能」欄 `OpenCg` / `Diff`（參數欄，`參數` 優先、`台詞` 後備，可帶 `;備註`）。
  章節 = .md 所在的 `Nodes\<章節>` 資料夾。C# factory 不掃（Forge 資料是產 C# 前的中間資料）。
  `Diff` 屬於最近一個 `OpenCg`；`CloseCg` 結束目前 CG。
- **美術檔**：在 CG 根目錄（預設 `E:\本地端\遊戲專案管理\臥底治安官\美術\CG圖`）遞迴搜尋，**只依檔名比對，不靠資料夾分類**。
  - 比對容錯：去零寬字元、`-` 視為 `_`、`CG_` 前綴可有可無、不分大小寫。
  - clip / spine：`{CG}.clip` / `{CG}.spine`。
  - png：`{CG}_{差分}.png`。`*.preview.png` 不算來源。
  - clip 或 spine 同名多個 → 異常（重複）。png 只有在「沒有 clip」時才檢查重複。
- **共用設定**
  - `Assets/Editor/CgImportPipeline/CgSpecialCases.json`（Unity 專案內，Forge 與 Unity 共同編輯）：
    只記「哪張 CG 用哪個 prefab 的哪些差分」。每列：CG 名稱（手填）、prefab（下拉）、差分（複選下拉）。
    - 存檔以一列為單位：重新讀檔 → 只換這一列 → 寫回。依 CG 名稱自然排序。Forge 監看此檔自動重載。
    - 主 CG（prefab 主人）擁有「來源全部差分 − 已分給其他列的差分」。全部分走 → 主 CG 只當 prefab 容器，不產 definition（不佔畫廊格子）。
    - 共用 CG 在畫廊是獨立一格：自己的 definition，`prefabAddress` 指向主 CG 的 prefab，`diffDefinitions` 只放分到的差分。
    - 異常：同一差分分給兩張 CG、指定差分在來源不存在、劇本呼叫不屬於該 CG 的差分、主 CG 剩餘差分無劇本使用。
  - `AI劇本\CgTypes.json`：只記被設為**靜態圖**的 CG（預設動態圖）。Unity 不讀，只依 prefab 類型判斷。
  - `%LocalAppData%\StoryForge\cg-manager.json`（本機）：章節篩選（與劇本管線狀態分開）、預覽大小、
    clip 構圖 / spine 動畫清單快取（依檔案大小＋修改時間失效）、每張 CG 上次輸出時的來源修改時間（判斷「來源較新」）。
  - 實作時改動：原訂放 `Assets/06.Definition/PlayscriptOfflineToolData/`，該資料夾不存在，`GalleryDefinition/` 又會被當 protobuf 整包載入，
    所以 `CgSpecialCases.json` 改放 Editor 專用的 `Assets/Editor/CgImportPipeline/`（不會進遊戲包）。

## 狀態

- **來源狀態**（工具列計數，點擊可篩選）：
  - 缺檔：spine / clip / png 都沒有（輸出時放暫代圖並跳錯）
  - 缺 spine：動態圖但沒有 spine
  - 異常：重複、靜態圖卻有 spine、差分歸屬問題
  - 已完成
- **Unity 狀態**（卡片標籤）：未輸出 / 暫代圖 / 差分不全 / 來源較新 / 已同步。
  依 `Assets/06.Definition/GalleryDefinition/{CG}.txt`（protobuf `CgDefinition`）、prefab 存在與否、`Gallery/_Placeholder`、輸出記錄判斷。

## 預覽圖

- 512×288、16:9，比例不符置中補黑邊，放在來源檔同級資料夾，檔名 `{CG}_{差分}.preview.png`。
- clip：每個圖層構圖一張，範圍與正式輸出相同（有裁切框用 `trim`，沒有用整張畫布；舊 Unity 工具也是 trim）。
- spine：每個動畫第 0 幀一張。Spine CLI 先匯出骨架 JSON 取動畫清單與 `bg` slot 範圍（＝遊戲鏡頭框，用來裁切），
  再一次啟動串接所有動畫的 PNG 匯出（每次啟動約 14 秒）。沒有 `bg` slot 時輸出整個骨架範圍。
- 美術 png 直接當預覽時，伺服器端縮成 512×288 再送（記憶體快取）。
- 顯示順序：clip 預覽 → png → spine 預覽 → 暫代圖。有來源但預覽未產生時，往下一順位顯示並標「預覽未產生」。
- 左右箭頭切換「這張 CG 擁有的差分」，底下顯示差分名稱與歸屬。來源缺劇本要的差分 → 該頁暫代圖＋紅字。
- 輸出至 Unity 時，第一個差分的預覽當遊戲內縮圖（`Thumbnail/{CG}_thumbnail.png`），取代 Unity 截圖。
- 暫代圖由 Forge 用 C# 繪製（色相依全部 CG 排序、CG 名稱、差分、劇本備註），卡片預覽、縮圖、1920 正式暫代圖同一套。

## 輸出

- **輸出至 Unity** 順序：spine（靜態圖時忽略並記異常）> clip > png > 暫代圖並跳錯。
  - clip 每次都重新匯出 1920 PNG，存一份在 clip 同級資料夾 `{CG}_{差分}.png`（先刪該 CG 舊檔）。
  - 輸出 Log 列：缺檔 / 缺 spine / 異常 / 已完成（附原因）。
- **輸出 PNG**：只做 clip → clip 同級 1920 PNG，不碰 Unity。沒 clip 灰掉/略過。
- **覆蓋詢問**：開始前一次問完。覆蓋 / 略過 / 取消，加「套用到剩下 N 張」。類型改變時註明。暫代圖直接覆蓋。
- **Unity 執行**：Forge 準備好檔案（clip 匯出的 PNG / 暫代圖 / 縮圖放暫存資料夾），寫 `Library/StoryForgeCgImport/pending.json`。
  PNG 複製進 Gallery、spine 的 Spine CLI 匯出（沿用 `SpineCgImportService` 的匯出設定與 PMA）由 Unity 端 `CgImportQueue` 做。
  - Unity 開著（獨佔開啟 `Temp/UnityLockfile` 失敗）→ Unity 端 hook 在 refresh/切回時執行。
  - Unity 沒開 → `Unity.exe -batchmode -projectPath … -executeMethod CgImportQueue.ProcessPendingBatch -quit -logFile …`。
  - 兩者都呼叫 `CgImportQueue.ProcessPending()`，回寫進度檔與結果檔。Forge 等結果檔才標「已完成」。
  - 狀態列階段：準備檔案 i/N → 等待 Unity（請切回 Unity）/ Unity 啟動中（秒數＋log 末行）→ Unity 匯入 i/N → 完成。

## Csp 工具

- 新增 `clip_layer_comp_exporter.exe --batch job.json`（`clip_batch_export.py`）：一個 clip 只載入一次，一次輸出多構圖、多尺寸；
  `render_all` 一次輸出全部構圖（路徑裡的 `{comp}` 換成構圖名）。exe 是 `--windowed`，沒有可用 stdout，
  所以進度寫到 job 指定的 `progress_file`（JSON lines，長步驟每秒心跳），結果寫 `result_file`。
- 只能用 `build_clip_export_gui.ps1` 打包（見 Csp `AGENTS.md`）。

## 介面

- 工具列：章節多選｜缺檔/缺 spine/異常/已完成 計數（可點篩選）｜預覽大小滑桿｜重新掃描｜更新預覽圖｜輸出 PNG｜輸出至 Unity｜CG 共用表｜取消。
  批次按鈕只作用於目前篩選出的 CG。
- 卡片：預覽＋差分切換、CG 名稱、`clip`/`png`/`spine` 標籤、Unity 狀態標籤、類型下拉、更新預覽、輸出至 Unity、劇本名稱（點擊跳流程圖節點）。
- 右鍵：複製 CG 名稱｜開啟 clip 檔｜開啟 spine 檔（無則灰）｜開啟 clip 資料夾｜開啟 spine 資料夾｜輸出 PNG｜開啟劇本檔（.md）｜跳到流程圖節點（多劇本時二級選單）。
- 底部 Log 可收合、可複製。
- 工作在背景佇列一次跑一個，不鎖畫面；狀態列顯示階段/秒數/心跳，處理中的卡片蓋遮罩、排隊中標示；取消 = 當前這張結束後停止（已交給 Unity 的不能取消）。
- 掃描時機：切到 CG 分頁自動掃一次＋手動重新掃描；兩份 JSON 檔案監看。

## Unity 端

- 新增 `CgImportQueue`（待匯入清單 → 產 prefab/definition → 進度檔/結果檔），供 hook 與 batchmode 共用。
- `CgImportQueue` 沿用 `SpriteCgImportService` / `SpineCgImportService`；縮圖用 Forge 提供的檔案（不在 Unity 截圖），
  共用 prefab 的 definition 與主 CG 的差分過濾也由它寫。
- 新增 CG 共用表視窗（`素材/CG/CG 共用表`，`CgShareTableWindow`，編輯 `CgSpecialCases.json`）。
- 遊戲端修正：`GallerySystem.IsCurrentCg` 原本比對 prefab 內 `definition.id`，共用 prefab 的 CG 會每幀重建；
  改為 `GalleryHandleCmpt.instantiatedId`。`VideoDashboardWindowBinderSub` 同步改用 `handle.id`。
- 舊 CG 匯入整合工具：本次標題加「（已由 StoryForge 取代）」；Forge 實際輸出過一章後再刪除。

## 設定頁新增

CG 根目錄、Csp EXE 路徑、Spine CLI 路徑、Unity.exe 路徑（預設依 `ProjectVersion.txt` 推 Hub 路徑）。
