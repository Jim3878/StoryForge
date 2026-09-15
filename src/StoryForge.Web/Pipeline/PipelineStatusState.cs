using System.Diagnostics;
using StoryForge.Core;
using StoryForge.Core.Codegen;
using StoryForge.Core.CSharpSource;
using StoryForge.Core.GoogleSheet;
using StoryForge.Core.Graph;
using StoryForge.Core.Ldtk;
using StoryForge.Core.Pipeline;
using StoryForge.Core.Schemas;
using StoryForge.Web.GraphEditor;

namespace StoryForge.Web.Pipeline;

public sealed class PipelineStatusRow
{
    public string Chapter { get; init; } = string.Empty;
    public string PlayscriptName { get; init; } = string.Empty;
    public bool IsOnLdtk { get; init; }
    public bool IsOnGraph { get; init; }

    // "Sheet目錄" — listed in the index sheet at all.
    public bool IsOnSheet { get; init; }

    // "Sheet檔案" — that listing already has its own dedicated GoogleSheet spreadsheet (a real link) AND
    // isn't marked "空" (still literally empty) in whichever empty-check column that chapter's tab has.
    public bool HasSheetFile { get; init; }
    public bool IsOnCSharp { get; init; }

    // "本地劇本" — a NodeScriptTableStore .md file exists on disk for this playscript name, independent of
    // IsOnGraph. A row with this true but IsOnGraph false is a leftover file whose node was deleted.
    public bool HasLocalScriptFile { get; init; }
}

// One entry in the "下載Sheet劇本" batch's final report — covers all three reasons a playscript didn't end
// up with fresh content: no SpreadsheetLink to download from, a download/parse failure, or the user
// choosing to skip it at a conflict prompt.
public sealed class SheetScriptFailure
{
    public string PlayscriptName { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

// A local .md already exists WITH REAL CONTENT (NodeScriptTableStore.HasContentAt) for the playscript
// currently at the head of the download queue — a blank template 建立劇本檔 already wrote ahead of time
// doesn't count, so it never blocks the download. The batch is paused until the panel calls
// ResolveSheetScriptConflictAsync with the user's choice.
public sealed class SheetScriptConflict
{
    public string PlayscriptName { get; init; } = string.Empty;

    // How many other not-yet-processed queue items are already known (from a local, no-network check) to
    // also be conflicts — shown so "覆蓋/略過接下來的 N 個" reflects what it will actually affect.
    public int RemainingConflictCount { get; init; }
}

public enum SheetScriptConflictChoice
{
    OverwriteOne,
    SkipOne,
    OverwriteRemaining,
    SkipRemaining,
}

// A local file already exists for the playscript currently at the head of 建立劇本檔's conflict queue,
// owned by a different node's guid — the batch is paused until the panel calls ResolveScriptFileConflict
// with the user's choice (reusing SheetScriptConflictChoice's same four verbs).
public sealed class ScriptFileConflict
{
    public string PlayscriptName { get; init; } = string.Empty;
    public int RemainingConflictCount { get; init; }
}

// A playscript currently at the head of "匯出成C#"'s queue already has a generated .cs file on disk —
// unlike a .md 劇本檔 (hand/AI-edited content), a generated .cs is pure output, so the default assumption is
// "regenerating and overwriting is normal", but the batch still pauses for an explicit choice (reusing
// SheetScriptConflictChoice's same four verbs) rather than silently clobbering it.
public sealed class CSharpExportConflict
{
    public string PlayscriptName { get; init; } = string.Empty;
    public int RemainingConflictCount { get; init; }
}

// Scoped per circuit. LDtk/C# are plain local-file scans, re-run on every Refresh(). The Sheet source
// needs its own local xlsx (Assets/11.Other/Sheet/Playscript/PlaylistIndex.xlsx) — there's no such file
// until DownloadSheetAsync has been run at least once, mirroring the original tool's own 下載Sheet flow.
public sealed class PipelineStatusState
{
    private enum ConflictAutoMode { None, OverwriteRemaining, SkipRemaining }

    private sealed class SheetScriptQueueItem
    {
        public string PlayscriptName = string.Empty;
        public string? SpreadsheetLink;
        public string? NodeGuid;
    }

    // "上傳補完到Sheet"'s own queue item — chapter/sheet already resolved once up front (via
    // GooglePlayscriptSheetClient.BuildUploadRows) rather than per-item, since it needs the whole batch's
    // worth of tab names to resolve correctly (see ResolveSheetName's ambiguous-match case).
    private sealed class UploadQueueItem
    {
        public string PlayscriptName = string.Empty;
        public string ChapterKey = string.Empty;
        public string SheetName = string.Empty;
    }

    // "匯出成C#"'s own queue item — the guid resolves which local .md this playscript's rows actually live
    // at (same resolution NodeScriptTableStore.ReadRows needs elsewhere in this class). Rows is read once
    // up front (StartCSharpExportAsync, to pre-filter out names with no content at all) and carried here
    // rather than re-read when the item is actually drained, so content isn't read from disk twice per item.
    private sealed class CSharpExportQueueItem
    {
        public string PlayscriptName = string.Empty;
        public string? NodeGuid;
        public List<string[]> Rows = new();
    }

    private readonly GraphEditorState _graphState;
    private readonly AppSettings _appSettings;
    private string? _projectRoot;
    private Dictionary<string, GooglePlayscriptSheetEntry> _sheetEntries = new();
    private string? _ldtkPath;
    private Dictionary<string, LdtkPlayscriptLocation> _ldtkLocations = new();

    private readonly Queue<SheetScriptQueueItem> _downloadQueue = new();
    private readonly Queue<UploadQueueItem> _uploadQueue = new();
    private ConflictAutoMode _conflictAutoMode = ConflictAutoMode.None;
    private SheetScriptQueueItem? _pendingConflictItem;
    private List<string[]>? _pendingConflictRows;

    // Held across a conflict pause so ResolveSheetScriptConflictAsync can hand the same list back into
    // DrainSheetScriptQueueAsync instead of losing whatever had already accumulated before the pause.
    private List<SheetScriptFailure>? _currentFailures;

    // 建立劇本檔's own conflict queue — a selected name whose plain-path file belongs to a different
    // (necessarily no-longer-live, see GraphEditorState.PlanNodeContentFilesSync) node's guid, needing the
    // user's explicit overwrite/skip choice. Reuses ConflictAutoMode/SheetScriptConflictChoice rather than
    // declaring parallel types for what is the same four-way verb (one/remaining × overwrite/skip).
    private readonly Queue<GraphEditorState.ScriptFileSyncItem> _scriptFileConflictQueue = new();
    private ConflictAutoMode _scriptFileConflictAutoMode = ConflictAutoMode.None;
    private int _scriptFileSyncedCount;

    // "匯出成C#"'s own queue/conflict-pause state — mirrors the SheetScript* download queue's shape (a queue
    // drained item-by-item, pausing on a pre-existing output file the same way a pre-existing local .md
    // pauses a download), but keeps its own auto-mode/pending-item fields since it's a logically separate
    // pause point from either of the two above.
    private readonly Queue<CSharpExportQueueItem> _cSharpExportQueue = new();
    private ConflictAutoMode _cSharpExportConflictAutoMode = ConflictAutoMode.None;
    private CSharpExportQueueItem? _pendingCSharpExportItem;
    private GeneratedPlayscriptOutput? _pendingCSharpExportOutput;
    private List<SheetScriptFailure>? _currentCSharpExportFailures;

    // Resolved once at the top of StartCSharpExportAsync (Enum對照表 load requires disk IO, and the output
    // root is fixed per project) rather than re-resolved on every queue item drained.
    private EnumLabelMaps? _cSharpExportEnumLabelMaps;
    private string? _cSharpExportOutputRoot;

    public PipelineStatusState(GraphEditorState graphState, AppSettings appSettings)
    {
        _graphState = graphState;
        _appSettings = appSettings;
    }

    public List<PipelineStatusRow> Rows { get; private set; } = new();
    public string StatusMessage { get; private set; } = string.Empty;

    // Non-null while the batch is paused waiting for the user to resolve an overwrite conflict.
    public SheetScriptConflict? PendingConflict { get; private set; }

    // Non-null while 建立劇本檔 is paused waiting for the user to resolve a plain-path file collision.
    public ScriptFileConflict? PendingScriptFileConflict { get; private set; }

    public bool IsSheetScriptDownloadRunning { get; private set; }

    // Total items queued when the batch started — fixed for the whole run, used with
    // SheetScriptProcessedCount to render an actual progress bar rather than just a remaining-count string.
    public int SheetScriptQueueTotal { get; private set; }

    // Incremented once per item as it finishes (written, skipped, or failed) — NOT incremented for an item
    // sitting at a conflict prompt until the user actually resolves it, so the bar doesn't jump ahead of a
    // decision that hasn't been made yet.
    public int SheetScriptProcessedCount { get; private set; }

    // The playscript actively being downloaded/written right now (or sitting at a conflict prompt) — shown
    // next to the progress bar so a slow single download still visibly says something is happening instead
    // of looking identical to a hung/crashed circuit.
    public string? SheetScriptCurrentName { get; private set; }

    // Not-yet-dequeued items only — doesn't count the one currently paused at a conflict prompt (already
    // dequeued by the time PendingConflict is set), so the panel can show "還剩 N 筆待處理".
    public int SheetScriptQueueRemaining => _downloadQueue.Count;

    // Set once a batch finishes; cleared by DismissSheetScriptResults. Empty list still means "ran but
    // nothing to report" — the panel only needs to pop the results dialog when this is non-null.
    public List<SheetScriptFailure>? SheetScriptResults { get; private set; }

    // Invoked after every queue item finishes processing (including pauses) so the panel can re-render
    // progress mid-batch instead of only once the whole thing settles — set by the panel itself.
    public Action? OnSheetScriptProgress { get; set; }

    // "上傳補完到Sheet" — mirrors the SheetScript* progress properties above, but has no conflict-pause
    // state: unlike a download, an upload always overwrites (that was a deliberate choice — local .md
    // content is authoritative), so there's never a decision to pause and wait on mid-batch.
    public bool IsUploadRunning { get; private set; }
    public int UploadQueueTotal { get; private set; }
    public int UploadProcessedCount { get; private set; }
    public string? UploadCurrentName { get; private set; }
    public List<SheetScriptFailure>? UploadResults { get; private set; }
    public Action? OnUploadProgress { get; set; }

    // "匯出成C#" — same progress-property shape as the two batches above. Generation itself is pure local
    // computation (no network), so a batch typically finishes almost instantly, but the panel still shows
    // per-item progress for UI consistency with the other two buttons.
    public bool IsCSharpExportRunning { get; private set; }
    public int CSharpExportQueueTotal { get; private set; }
    public int CSharpExportProcessedCount { get; private set; }
    public string? CSharpExportCurrentName { get; private set; }
    public List<SheetScriptFailure>? CSharpExportResults { get; private set; }
    public Action? OnCSharpExportProgress { get; set; }

    // Non-null while "匯出成C#" is paused waiting for the user to resolve an existing-.cs-file conflict.
    public CSharpExportConflict? PendingCSharpExportConflict { get; private set; }

    public void Refresh()
    {
        try
        {
            _projectRoot = ProjectPaths.ResolveUnityProjectRoot();
            var assetsRoot = Path.Combine(_projectRoot, "Assets");

            _ldtkPath = Path.Combine(assetsRoot, "12.Custom", "Company.ldtk");
            _ldtkLocations = File.Exists(_ldtkPath)
                ? LdtkPlayscriptScanner.ScanFileEntries(_ldtkPath)
                : new();
            var ldtkPath = _ldtkPath;
            var ldtkNames = _ldtkLocations.Keys.ToHashSet(StringComparer.Ordinal);

            var factoryFolder = Path.Combine(assetsRoot, "12.Custom", "PlayscriptFactory");
            var cSharpNames = CSharpFactoryScanner.ScanFolder(factoryFolder);

            var sheetPath = Path.Combine(assetsRoot, GooglePlayscriptSheetClient.GooglePlayscriptFolder,
                GooglePlayscriptSheetClient.GooglePlayscriptFileName);
            var hasSheetFile = File.Exists(sheetPath);
            _sheetEntries = hasSheetFile ? GooglePlayscriptSheetClient.LoadEntries(sheetPath) : new();

            var graphNames = _graphState.GetPlayscriptIdentities().ToList();

            var infos = PlayscriptPipelineComparer.Compare(
                ldtkNames, graphNames, _sheetEntries.Keys, cSharpNames);

            Rows = infos
                .Select(info => new PipelineStatusRow
                {
                    Chapter = info.Chapter,
                    PlayscriptName = info.PlayscriptName,
                    IsOnLdtk = info.IsOnLdtk,
                    IsOnGraph = info.IsOnGraph,
                    IsOnSheet = info.IsOnSheet,
                    HasSheetFile = _sheetEntries.TryGetValue(info.PlayscriptName, out var entry) &&
                                   entry.SpreadsheetLink != null && !entry.IsMarkedEmpty,
                    IsOnCSharp = info.IsOnCSharp,
                    HasLocalScriptFile = HasLocalScriptFile(info.PlayscriptName),
                })
                .ToList();

            var missing = new List<string>();
            if (!File.Exists(ldtkPath)) missing.Add("找不到LDtk檔案");
            if (!hasSheetFile) missing.Add("Sheet尚未下載");
            var suffix = missing.Count > 0 ? $"；{string.Join("，", missing)}" : string.Empty;

            StatusMessage = $"共 {Rows.Count} 個劇本名稱（LDtk={ldtkNames.Count}, 流程圖={graphNames.Count}, " +
                            $"Sheet={_sheetEntries.Count}, C#={cSharpNames.Count}）{suffix}";
        }
        catch (Exception e)
        {
            Rows = new();
            StatusMessage = $"讀取失敗：{e.Message}";
        }
    }

    // Only remaining trigger for NodeContentStore/NodeScriptTableStore's per-node .json/.md files (flow-graph
    // load/save no longer does this) — scoped to whatever rows the caller currently has checked via the
    // panel's row-selection checkboxes, so a row the user hasn't checked never gets files written for it.
    // A selected name whose plain-path file belongs to a different (no-longer-live) node's guid is queued
    // into PendingScriptFileConflict for the user to explicitly overwrite or skip, rather than silently
    // suffixed or silently skipped — see GraphEditorState.PlanNodeContentFilesSync's own comment.
    public void BuildNodeContentFiles(IReadOnlyCollection<string> selectedPlayscriptNames)
    {
        var plan = _graphState.PlanNodeContentFilesSync(selectedPlayscriptNames);
        _scriptFileSyncedCount = _graphState.SyncNodeContentFiles(plan.Ready);

        _scriptFileConflictQueue.Clear();
        foreach (var item in plan.Conflicts)
            _scriptFileConflictQueue.Enqueue(item);
        _scriptFileConflictAutoMode = ConflictAutoMode.None;
        PendingScriptFileConflict = null;

        DrainScriptFileConflictQueue();
    }

    public void ResolveScriptFileConflict(SheetScriptConflictChoice choice)
    {
        if (_scriptFileConflictQueue.Count == 0)
            return;

        var item = _scriptFileConflictQueue.Dequeue();
        switch (choice)
        {
            case SheetScriptConflictChoice.OverwriteOne:
                _graphState.OverwriteNodeContentFile(item);
                _scriptFileSyncedCount++;
                break;
            case SheetScriptConflictChoice.OverwriteRemaining:
                _scriptFileConflictAutoMode = ConflictAutoMode.OverwriteRemaining;
                _graphState.OverwriteNodeContentFile(item);
                _scriptFileSyncedCount++;
                break;
            case SheetScriptConflictChoice.SkipRemaining:
                _scriptFileConflictAutoMode = ConflictAutoMode.SkipRemaining;
                break;
            case SheetScriptConflictChoice.SkipOne:
                break;
        }

        PendingScriptFileConflict = null;
        DrainScriptFileConflictQueue();
    }

    private void DrainScriptFileConflictQueue()
    {
        while (_scriptFileConflictQueue.Count > 0)
        {
            var item = _scriptFileConflictQueue.Peek();

            if (_scriptFileConflictAutoMode == ConflictAutoMode.OverwriteRemaining)
            {
                _scriptFileConflictQueue.Dequeue();
                _graphState.OverwriteNodeContentFile(item);
                _scriptFileSyncedCount++;
                continue;
            }

            if (_scriptFileConflictAutoMode == ConflictAutoMode.SkipRemaining)
            {
                _scriptFileConflictQueue.Dequeue();
                continue;
            }

            PendingScriptFileConflict = new ScriptFileConflict
            {
                PlayscriptName = item.PlayscriptName,
                RemainingConflictCount = _scriptFileConflictQueue.Count - 1,
            };
            SetBuildStatusMessage();
            return;
        }

        PendingScriptFileConflict = null;
        SetBuildStatusMessage();
        Refresh();
    }

    private void SetBuildStatusMessage()
    {
        StatusMessage = _scriptFileSyncedCount > 0
            ? $"已建立/同步 {_scriptFileSyncedCount} 個劇本節點的檔案"
            : "勾選的列內沒有可建立檔案的劇本節點";
    }

    // "本地劇本" — resolves the exact (possibly guid-suffixed, for a leftover file predating this fix)
    // path for whichever live node currently owns this playscript name, so a real file never reads as
    // missing just because it isn't sitting at the plain name. A name with no live node at all (the row is
    // a leftover-file-only case) falls back to the plain-path-only check, matching its own row semantics.
    private bool HasLocalScriptFile(string playscriptName)
    {
        var guid = _graphState.FindNodeGuidForPlayscript(playscriptName);
        return guid != null
            ? NodeScriptTableStore.HasFileAt(_appSettings.ExternalDataFolder, playscriptName, guid)
            : NodeScriptTableStore.HasFile(_appSettings.ExternalDataFolder, playscriptName);
    }

    // Entry point for the "下載Sheet劇本" button — every checked row is queued regardless of whether it has
    // a SpreadsheetLink; one with none is resolved as an immediate failure once its turn comes up, rather
    // than being silently excluded, so the final report explains every checked row's outcome.
    public async Task StartSheetScriptDownloadAsync(IReadOnlyCollection<string> selectedPlayscriptNames)
    {
        if (IsSheetScriptDownloadRunning)
            return;

        _downloadQueue.Clear();
        foreach (var name in selectedPlayscriptNames)
        {
            _sheetEntries.TryGetValue(name, out var entry);
            _downloadQueue.Enqueue(new SheetScriptQueueItem
            {
                PlayscriptName = name,
                SpreadsheetLink = entry is { SpreadsheetLink: not null, IsMarkedEmpty: false } ? entry.SpreadsheetLink : null,
                NodeGuid = _graphState.FindNodeGuidForPlayscript(name),
            });
        }

        _conflictAutoMode = ConflictAutoMode.None;
        PendingConflict = null;
        SheetScriptResults = null;
        IsSheetScriptDownloadRunning = true;
        SheetScriptQueueTotal = _downloadQueue.Count;
        SheetScriptProcessedCount = 0;
        SheetScriptCurrentName = null;
        var failures = new List<SheetScriptFailure>();

        await DrainSheetScriptQueueAsync(failures);
    }

    public async Task ResolveSheetScriptConflictAsync(SheetScriptConflictChoice choice)
    {
        if (_pendingConflictItem == null || _pendingConflictRows == null || SheetScriptResults != null)
            return;

        var item = _pendingConflictItem;
        var rows = _pendingConflictRows;
        _pendingConflictItem = null;
        _pendingConflictRows = null;
        PendingConflict = null;

        var failures = _currentFailures ?? new List<SheetScriptFailure>();

        switch (choice)
        {
            case SheetScriptConflictChoice.OverwriteOne:
                NodeScriptTableStore.WriteContent(_appSettings.ExternalDataFolder, item.PlayscriptName, item.NodeGuid, rows);
                break;
            case SheetScriptConflictChoice.SkipOne:
                failures.Add(new SheetScriptFailure { PlayscriptName = item.PlayscriptName, Reason = "使用者選擇略過（本機已有劇本檔）" });
                break;
            case SheetScriptConflictChoice.OverwriteRemaining:
                _conflictAutoMode = ConflictAutoMode.OverwriteRemaining;
                NodeScriptTableStore.WriteContent(_appSettings.ExternalDataFolder, item.PlayscriptName, item.NodeGuid, rows);
                break;
            case SheetScriptConflictChoice.SkipRemaining:
                _conflictAutoMode = ConflictAutoMode.SkipRemaining;
                failures.Add(new SheetScriptFailure { PlayscriptName = item.PlayscriptName, Reason = "使用者選擇略過（本機已有劇本檔）" });
                break;
        }

        SheetScriptProcessedCount++;
        await DrainSheetScriptQueueAsync(failures);
    }

    public void DismissSheetScriptResults()
    {
        SheetScriptResults = null;
    }

    private async Task DrainSheetScriptQueueAsync(List<SheetScriptFailure> failures)
    {
        _currentFailures = failures;

        while (_downloadQueue.Count > 0)
        {
            var item = _downloadQueue.Dequeue();
            SheetScriptCurrentName = item.PlayscriptName;
            OnSheetScriptProgress?.Invoke();

            if (item.SpreadsheetLink == null)
            {
                failures.Add(new SheetScriptFailure { PlayscriptName = item.PlayscriptName, Reason = "沒有專屬 Sheet 連結（尚未建立或被標記為空劇本）" });
                SheetScriptProcessedCount++;
                OnSheetScriptProgress?.Invoke();
                continue;
            }

            List<string[]> rows;
            try
            {
                rows = await GooglePlayscriptSheetClient.DownloadScriptTableAsync(item.SpreadsheetLink);
            }
            catch (Exception e)
            {
                failures.Add(new SheetScriptFailure { PlayscriptName = item.PlayscriptName, Reason = e.Message });
                SheetScriptProcessedCount++;
                OnSheetScriptProgress?.Invoke();
                continue;
            }

            var hasLocalContent = NodeScriptTableStore.HasContentAt(_appSettings.ExternalDataFolder, item.PlayscriptName, item.NodeGuid);
            if (!hasLocalContent)
            {
                NodeScriptTableStore.WriteContent(_appSettings.ExternalDataFolder, item.PlayscriptName, item.NodeGuid, rows);
                SheetScriptProcessedCount++;
                OnSheetScriptProgress?.Invoke();
                continue;
            }

            if (_conflictAutoMode == ConflictAutoMode.OverwriteRemaining)
            {
                NodeScriptTableStore.WriteContent(_appSettings.ExternalDataFolder, item.PlayscriptName, item.NodeGuid, rows);
                SheetScriptProcessedCount++;
                OnSheetScriptProgress?.Invoke();
                continue;
            }

            if (_conflictAutoMode == ConflictAutoMode.SkipRemaining)
            {
                failures.Add(new SheetScriptFailure { PlayscriptName = item.PlayscriptName, Reason = "使用者選擇略過（本機已有劇本檔）" });
                SheetScriptProcessedCount++;
                OnSheetScriptProgress?.Invoke();
                continue;
            }

            // Paused here — SheetScriptProcessedCount deliberately not bumped yet; ResolveSheetScriptConflictAsync
            // does that once the user actually decides, so the progress bar doesn't advance past a choice
            // that hasn't been made.
            _pendingConflictItem = item;
            _pendingConflictRows = rows;
            PendingConflict = new SheetScriptConflict
            {
                PlayscriptName = item.PlayscriptName,
                RemainingConflictCount = CountRemainingConflicts(),
            };
            OnSheetScriptProgress?.Invoke();
            return;
        }

        IsSheetScriptDownloadRunning = false;
        SheetScriptCurrentName = null;
        SheetScriptResults = failures;
        Refresh();
        OnSheetScriptProgress?.Invoke();
    }

    // Pure local-file check (no network) over whatever's still queued, used only to show an accurate "N"
    // on the conflict prompt's "接下來的 N 個" buttons — doesn't affect which items actually turn out to be
    // conflicts once really processed (a queued item with a link can still fail to download).
    private int CountRemainingConflicts()
    {
        return _downloadQueue.Count(item =>
            item.SpreadsheetLink != null &&
            NodeScriptTableStore.HasContentAt(_appSettings.ExternalDataFolder, item.PlayscriptName, item.NodeGuid));
    }

    public async Task DownloadSheetAsync()
    {
        if (_projectRoot == null)
        {
            StatusMessage = "請先按重新整理一次。";
            return;
        }

        try
        {
            StatusMessage = "正在下載 Google Sheet...";
            var assetsRoot = Path.Combine(_projectRoot, "Assets");
            await GooglePlayscriptSheetClient.DownloadAsync(assetsRoot);
            Refresh();
        }
        catch (Exception e)
        {
            StatusMessage = $"Sheet 下載失敗：{e.Message}";
        }
    }

    // Entry point for "上傳補完到Sheet", called once the panel's own confirm dialog has already been
    // accepted — every checked name is processed regardless of its current IsOnGraph/IsOnSheet/HasSheetFile
    // status (a deliberate choice: whatever's checked gets whatever step is actually still needed for it).
    // A name BuildUploadRows can't resolve a chapter for (doesn't match "章節/劇本" naming) is reported as an
    // immediate failure rather than silently dropped, same philosophy as the download queue's own entry point.
    public async Task StartUploadAsync(IReadOnlyCollection<string> selectedPlayscriptNames)
    {
        if (IsUploadRunning || selectedPlayscriptNames.Count == 0)
            return;

        var sheetTabNames = GetLocalSheetTabNames();
        var uploadRows = GooglePlayscriptSheetClient.BuildUploadRows(sheetTabNames, selectedPlayscriptNames);

        _uploadQueue.Clear();
        foreach (var row in uploadRows)
        {
            _uploadQueue.Enqueue(new UploadQueueItem
            {
                PlayscriptName = row.PlayscriptName,
                ChapterKey = row.ChapterKey,
                SheetName = row.SheetName,
            });
        }

        var failures = new List<SheetScriptFailure>();
        var resolvedNames = uploadRows.Select(r => r.PlayscriptName).ToHashSet(StringComparer.Ordinal);
        foreach (var name in selectedPlayscriptNames)
        {
            if (!resolvedNames.Contains(PlayscriptNaming.Normalize(name)))
                failures.Add(new SheetScriptFailure
                {
                    PlayscriptName = name,
                    Reason = "劇本名稱不符合「章節/劇本名」格式，無法判斷所屬章節",
                });
        }

        IsUploadRunning = true;
        UploadResults = null;
        UploadQueueTotal = _uploadQueue.Count;
        UploadProcessedCount = 0;
        UploadCurrentName = null;

        await DrainUploadQueueAsync(failures);
    }

    private List<string> GetLocalSheetTabNames()
    {
        if (_projectRoot == null)
            return new List<string>();

        var sheetPath = Path.Combine(_projectRoot, "Assets", GooglePlayscriptSheetClient.GooglePlayscriptFolder,
            GooglePlayscriptSheetClient.GooglePlayscriptFileName);
        return File.Exists(sheetPath) ? GooglePlayscriptSheetClient.GetSheetTabNames(sheetPath) : new List<string>();
    }

    private async Task DrainUploadQueueAsync(List<SheetScriptFailure> failures)
    {
        while (_uploadQueue.Count > 0)
        {
            var item = _uploadQueue.Dequeue();
            UploadCurrentName = item.PlayscriptName;
            OnUploadProgress?.Invoke();

            var guid = _graphState.FindNodeGuidForPlayscript(item.PlayscriptName);
            var hasLocalFile = guid != null
                ? NodeScriptTableStore.HasFileAt(_appSettings.ExternalDataFolder, item.PlayscriptName, guid)
                : NodeScriptTableStore.HasFile(_appSettings.ExternalDataFolder, item.PlayscriptName);
            var rows = hasLocalFile
                ? NodeScriptTableStore.ReadRows(_appSettings.ExternalDataFolder, item.PlayscriptName, guid)
                : null;

            try
            {
                var response = await GooglePlayscriptSheetClient.UploadPlayscriptCompleteAsync(
                    item.ChapterKey, item.SheetName, item.PlayscriptName, rows);

                if (response.Success == false)
                {
                    failures.Add(new SheetScriptFailure
                    {
                        PlayscriptName = item.PlayscriptName,
                        Reason = response.Message ?? "Apps Script 回傳失敗，但未附加錯誤訊息",
                    });
                }
                else if (response.SpreadsheetId != null)
                {
                    // Known good — either just created or already existed; either way this playscript now
                    // definitely has a real dedicated spreadsheet, so the grid should reflect that without
                    // waiting for a manual "讀取Sheet資料" re-download.
                    var link = response.Link ?? $"https://docs.google.com/spreadsheets/d/{response.SpreadsheetId}/edit";
                    _sheetEntries[item.PlayscriptName] = new GooglePlayscriptSheetEntry
                    {
                        PlayscriptName = item.PlayscriptName,
                        SpreadsheetLink = link,
                    };
                }
                else if (!_sheetEntries.ContainsKey(item.PlayscriptName))
                {
                    // The Apps Script response-loss quirk (see ParseUploadCompleteResponse) or a row that
                    // only needed an index entry — either way, at least record that it's now on the index.
                    _sheetEntries[item.PlayscriptName] = new GooglePlayscriptSheetEntry { PlayscriptName = item.PlayscriptName };
                }
            }
            catch (Exception e)
            {
                failures.Add(new SheetScriptFailure { PlayscriptName = item.PlayscriptName, Reason = e.Message });
            }

            UploadProcessedCount++;
            OnUploadProgress?.Invoke();
        }

        IsUploadRunning = false;
        UploadCurrentName = null;
        UploadResults = failures;
        Refresh();
        OnUploadProgress?.Invoke();
    }

    public void DismissUploadResults()
    {
        UploadResults = null;
    }

    // Entry point for "匯出成C#" — every checked name is queued regardless of whether it currently has local
    // .md content (a name with none is resolved as an immediate per-row failure once its turn comes up, same
    // philosophy as the other two batch entry points). The Enum對照表 is a whole-batch precondition checked
    // once up front, not per row — missing it aborts before a single row is even queued, matching the
    // original WinForms tool's own up-front check rather than repeating the same "missing" reason on every row.
    // Returns the checked names that were dropped from the batch before it even started, because they have
    // no real local .md content at all — 匯出成C# reads that content as its input, so queuing one of these
    // just to report the same "尚未下載" reason on every future export attempt (and leaving it checked
    // forever, since a row hidden by the current chapter filter/search has no checkbox left to uncheck it
    // with) serves no purpose. The caller removes these from its own selection so they stop reappearing.
    // "下載Sheet劇本"/"上傳補完到Sheet" don't get this treatment — an empty/missing local file is the NORMAL
    // starting point for those, not a dead end.
    public async Task<IReadOnlyList<string>> StartCSharpExportAsync(IReadOnlyCollection<string> selectedPlayscriptNames)
    {
        if (IsCSharpExportRunning || selectedPlayscriptNames.Count == 0)
            return Array.Empty<string>();

        if (_projectRoot == null)
        {
            StatusMessage = "請先按重新整理一次。";
            return Array.Empty<string>();
        }

        var assetsRoot = Path.Combine(_projectRoot, "Assets");
        var enumMapPath = Path.Combine(assetsRoot, "06.Definition", "PlayscriptOfflineExport", "PlayscriptEnumLabelMaps.json");
        if (!File.Exists(enumMapPath))
        {
            StatusMessage = "找不到 Enum 對照表，請先在 Unity 的「精簡劇本管理器」按「匯出Enum對照表」。";
            return Array.Empty<string>();
        }

        var enumDocument = DocumentLoader.LoadEnumLabelMapDocument(enumMapPath);
        _cSharpExportEnumLabelMaps = EnumLabelMaps.FromDocument(enumDocument);
        _cSharpExportOutputRoot = Path.Combine(assetsRoot, "12.Custom", "PlayscriptFactory");

        var excludedForNoContent = new List<string>();
        _cSharpExportQueue.Clear();
        foreach (var name in selectedPlayscriptNames)
        {
            var guid = _graphState.FindNodeGuidForPlayscript(name);
            var rows = NodeScriptTableStore.ReadRows(_appSettings.ExternalDataFolder, name, guid);
            if (rows.Count == 0)
            {
                excludedForNoContent.Add(name);
                continue;
            }

            _cSharpExportQueue.Enqueue(new CSharpExportQueueItem { PlayscriptName = name, NodeGuid = guid, Rows = rows });
        }

        if (_cSharpExportQueue.Count == 0)
            return excludedForNoContent;

        _cSharpExportConflictAutoMode = ConflictAutoMode.None;
        PendingCSharpExportConflict = null;
        CSharpExportResults = null;
        IsCSharpExportRunning = true;
        CSharpExportQueueTotal = _cSharpExportQueue.Count;
        CSharpExportProcessedCount = 0;
        CSharpExportCurrentName = null;
        var failures = new List<SheetScriptFailure>();

        await DrainCSharpExportQueueAsync(failures);
        return excludedForNoContent;
    }

    public async Task ResolveCSharpExportConflictAsync(SheetScriptConflictChoice choice)
    {
        if (_pendingCSharpExportItem == null || _pendingCSharpExportOutput == null || CSharpExportResults != null)
            return;

        var item = _pendingCSharpExportItem;
        var output = _pendingCSharpExportOutput;
        _pendingCSharpExportItem = null;
        _pendingCSharpExportOutput = null;
        PendingCSharpExportConflict = null;

        var failures = _currentCSharpExportFailures ?? new List<SheetScriptFailure>();

        switch (choice)
        {
            case SheetScriptConflictChoice.OverwriteOne:
                PlayscriptFactoryCodeGenerator.WriteGeneratedOutput(output);
                break;
            case SheetScriptConflictChoice.SkipOne:
                failures.Add(new SheetScriptFailure { PlayscriptName = item.PlayscriptName, Reason = "使用者選擇略過（本機已有產生過的C#檔案）" });
                break;
            case SheetScriptConflictChoice.OverwriteRemaining:
                _cSharpExportConflictAutoMode = ConflictAutoMode.OverwriteRemaining;
                PlayscriptFactoryCodeGenerator.WriteGeneratedOutput(output);
                break;
            case SheetScriptConflictChoice.SkipRemaining:
                _cSharpExportConflictAutoMode = ConflictAutoMode.SkipRemaining;
                failures.Add(new SheetScriptFailure { PlayscriptName = item.PlayscriptName, Reason = "使用者選擇略過（本機已有產生過的C#檔案）" });
                break;
        }

        CSharpExportProcessedCount++;
        await DrainCSharpExportQueueAsync(failures);
    }

    public void DismissCSharpExportResults()
    {
        CSharpExportResults = null;
    }

    // Lets the user inspect the existing .cs file the conflict prompt is asking about before deciding
    // whether to overwrite it — opens in whatever program Windows has associated with .cs files, the same
    // way GraphEditorState.OpenScriptTable opens a .md 劇本檔. Doesn't touch PendingCSharpExportConflict, so
    // the modal stays open (clicking this is just "let me look first", not a decision).
    public void OpenPendingCSharpExportFile()
    {
        var path = _pendingCSharpExportOutput?.OutputPath;
        if (path != null)
            OpenFileWithDefaultProgram(path, "開啟C#檔案失敗");
    }

    // "跳到劇情節點"'s sibling right-click actions — "開啟本地劇本檔"/"開啟C#檔" — resolve straight from a
    // playscript name (the grid row has no live node at all for some rows, e.g. a leftover file with no
    // current graph node) rather than requiring a guid up front, same reasoning as HasLocalScriptFile's own
    // guid-or-plain-path fallback. Both are no-ops if the row's own IsOnCSharp/HasLocalScriptFile check
    // already said there's nothing to open — the panel gates the menu item's enabled state on that same
    // flag, but these re-check File.Exists anyway rather than trusting a possibly-stale row snapshot.
    public void OpenLocalScriptFile(string playscriptName)
    {
        var guid = _graphState.FindNodeGuidForPlayscript(playscriptName);
        var path = NodeScriptTableStore.ResolvePath(_appSettings.ExternalDataFolder, playscriptName, guid);
        OpenFileWithDefaultProgram(path, "開啟本地劇本檔失敗");
    }

    public void OpenCSharpFile(string playscriptName)
    {
        if (_projectRoot == null)
            return;

        var outputRoot = Path.Combine(_projectRoot, "Assets", "12.Custom", "PlayscriptFactory");
        string path;
        try
        {
            path = PlayscriptFactoryCodeGenerator.GetOutputPath(playscriptName, outputRoot);
        }
        catch (Exception)
        {
            // A playscript name PlayscriptNameParser can't make sense of has no meaningful output path to
            // open — same as it has none to write to during 匯出成C#, just silently nothing to do here.
            return;
        }

        OpenFileWithDefaultProgram(path, "開啟C#檔案失敗");
    }

    // "跳到劇情節點"'s LDtk counterpart — launches LDtk pointed at this playscript's first matching Entity
    // (LdtkPlayscriptScanner.ScanFileEntries keeps only the first occurrence per name, in file order).
    // LDtk's own single-instance lock (see its ElectronMain "second-instance" handling) means this is safe
    // to call even when LDtk is already open on this exact project — it forwards the args to that instance
    // and brings its window to front instead of opening a second one. LdtkWorkingDirectory being set is what
    // signals "this is the dev-mode Electron launch, which needs the '.' arg AND that working directory to
    // resolve it against" — a real packaged LDtk.exe needs neither, so AppSettings leaving it blank skips
    // both rather than needing a separate on/off setting for the same thing.
    public void LocateInLdtk(string playscriptName)
    {
        if (_ldtkPath == null || !_ldtkLocations.TryGetValue(playscriptName, out var location))
            return;

        if (string.IsNullOrWhiteSpace(_appSettings.LdtkExecutablePath) || !File.Exists(_appSettings.LdtkExecutablePath))
        {
            StatusMessage = "找不到LDtk執行檔，請先在「設定」裡填入LDtk執行檔路徑。";
            return;
        }

        var startInfo = new ProcessStartInfo(_appSettings.LdtkExecutablePath) { UseShellExecute = false };
        var hasWorkingDirectory = !string.IsNullOrWhiteSpace(_appSettings.LdtkWorkingDirectory);
        if (hasWorkingDirectory)
        {
            startInfo.WorkingDirectory = _appSettings.LdtkWorkingDirectory;
            startInfo.ArgumentList.Add(".");
        }

        startInfo.ArgumentList.Add(_ldtkPath);
        startInfo.ArgumentList.Add($"--goto-level={location.LevelIid}");
        startInfo.ArgumentList.Add($"--goto-entity={location.EntityIid}");

        try
        {
            Process.Start(startInfo);
            StatusMessage = $"已定位到LDtk：{location.LevelIdentifier} 裡的 {location.EntityIdentifier}";
        }
        catch (Exception e)
        {
            StatusMessage = $"定位到LDtk失敗：{e.Message}";
        }
    }

    private void OpenFileWithDefaultProgram(string path, string failureStatusPrefix)
    {
        if (!File.Exists(path))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            StatusMessage = $"{failureStatusPrefix}：{e.Message}";
        }
    }

    private async Task DrainCSharpExportQueueAsync(List<SheetScriptFailure> failures)
    {
        _currentCSharpExportFailures = failures;
        var enumLabelMaps = _cSharpExportEnumLabelMaps!;
        var outputRoot = _cSharpExportOutputRoot!;

        while (_cSharpExportQueue.Count > 0)
        {
            var item = _cSharpExportQueue.Dequeue();
            CSharpExportCurrentName = item.PlayscriptName;
            OnCSharpExportProgress?.Invoke();

            // Pure local computation — no network/disk-IO-bound await in this loop body, so a Task.Yield is
            // inserted purely so Blazor Server actually gets a chance to flush the progress bar to the
            // browser between items instead of the whole batch appearing to jump straight to its final state.
            await Task.Yield();

            GeneratedPlayscriptOutput output;
            try
            {
                output = PlayscriptFactoryCodeGenerator.Generate(item.PlayscriptName, item.Rows, enumLabelMaps, outputRoot);
            }
            catch (Exception e)
            {
                failures.Add(new SheetScriptFailure { PlayscriptName = item.PlayscriptName, Reason = e.Message });
                CSharpExportProcessedCount++;
                OnCSharpExportProgress?.Invoke();
                continue;
            }

            if (!File.Exists(output.OutputPath))
            {
                PlayscriptFactoryCodeGenerator.WriteGeneratedOutput(output);
                CSharpExportProcessedCount++;
                OnCSharpExportProgress?.Invoke();
                continue;
            }

            // 劇本代號重複 — PlayscriptNameParser.Parse's ClassName drops a filename's last "_"-segment, so
            // two playscripts sharing everything up to that segment (e.g. "ReN100_s10_黑警雙人組閒話" and
            // "ReN100_s10_慎次閒話") collide onto the identical class name/output path. The existing file at
            // this path belongs to whichever of them was exported most recently, not necessarily this one —
            // a normal overwrite-confirm here would silently destroy that OTHER playscript's only C# output,
            // which is a data-loss class this batch treats as a hard failure rather than a choice, unlike an
            // ordinary "I'm regenerating my own file" conflict just below. TryReadPlayscriptName returning
            // null (not one of this generator's own outputs, or an unrecognized format) falls through to the
            // normal conflict flow instead — there's no way to tell it's a collision in that case.
            var existingPlayscriptName = CSharpFactoryScanner.TryReadPlayscriptName(output.OutputPath);
            if (existingPlayscriptName != null &&
                !string.Equals(existingPlayscriptName, item.PlayscriptName, StringComparison.Ordinal))
            {
                failures.Add(new SheetScriptFailure
                {
                    PlayscriptName = item.PlayscriptName,
                    Reason = $"劇本代號重複，跟「{existingPlayscriptName}」算出同一個C#類別名稱，兩者無法各自擁有一份C#檔案",
                });
                CSharpExportProcessedCount++;
                OnCSharpExportProgress?.Invoke();
                continue;
            }

            if (_cSharpExportConflictAutoMode == ConflictAutoMode.OverwriteRemaining)
            {
                PlayscriptFactoryCodeGenerator.WriteGeneratedOutput(output);
                CSharpExportProcessedCount++;
                OnCSharpExportProgress?.Invoke();
                continue;
            }

            if (_cSharpExportConflictAutoMode == ConflictAutoMode.SkipRemaining)
            {
                failures.Add(new SheetScriptFailure { PlayscriptName = item.PlayscriptName, Reason = "使用者選擇略過（本機已有產生過的C#檔案）" });
                CSharpExportProcessedCount++;
                OnCSharpExportProgress?.Invoke();
                continue;
            }

            // Paused here — CSharpExportProcessedCount deliberately not bumped yet; ResolveCSharpExportConflictAsync
            // does that once the user actually decides, so the progress bar doesn't advance past a choice
            // that hasn't been made.
            _pendingCSharpExportItem = item;
            _pendingCSharpExportOutput = output;
            PendingCSharpExportConflict = new CSharpExportConflict
            {
                PlayscriptName = item.PlayscriptName,
                RemainingConflictCount = CountRemainingCSharpExportConflicts(outputRoot),
            };
            OnCSharpExportProgress?.Invoke();
            return;
        }

        IsCSharpExportRunning = false;
        CSharpExportCurrentName = null;
        CSharpExportResults = failures;
        Refresh();
        OnCSharpExportProgress?.Invoke();
    }

    // Pure local-file check over whatever's still queued, used only to show an accurate "N" on the conflict
    // prompt's "接下來的 N 個" buttons — a name whose output path can't even be resolved (malformed
    // playscript name) just doesn't count as a conflict here; it will surface as its own row failure once
    // actually processed.
    private int CountRemainingCSharpExportConflicts(string outputRoot)
    {
        return _cSharpExportQueue.Count(item =>
        {
            try
            {
                return File.Exists(PlayscriptFactoryCodeGenerator.GetOutputPath(item.PlayscriptName, outputRoot));
            }
            catch (Exception)
            {
                return false;
            }
        });
    }
}
