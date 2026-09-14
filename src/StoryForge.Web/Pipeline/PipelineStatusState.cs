using StoryForge.Core;
using StoryForge.Core.CSharpSource;
using StoryForge.Core.GoogleSheet;
using StoryForge.Core.Graph;
using StoryForge.Core.Ldtk;
using StoryForge.Core.Pipeline;
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

// A local .md already exists for the playscript currently at the head of the download queue — the batch
// is paused until the panel calls ResolveSheetScriptConflictAsync with the user's choice.
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

    private readonly GraphEditorState _graphState;
    private readonly AppSettings _appSettings;
    private string? _projectRoot;
    private Dictionary<string, GooglePlayscriptSheetEntry> _sheetEntries = new();

    private readonly Queue<SheetScriptQueueItem> _downloadQueue = new();
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

    public void Refresh()
    {
        try
        {
            _projectRoot = ProjectPaths.ResolveUnityProjectRoot();
            var assetsRoot = Path.Combine(_projectRoot, "Assets");

            var ldtkPath = Path.Combine(assetsRoot, "12.Custom", "Company.ldtk");
            var ldtkNames = File.Exists(ldtkPath)
                ? LdtkPlayscriptScanner.ScanFile(ldtkPath)
                : new HashSet<string>();

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

            var hasLocalFile = NodeScriptTableStore.HasFileAt(_appSettings.ExternalDataFolder, item.PlayscriptName, item.NodeGuid);
            if (!hasLocalFile)
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
            NodeScriptTableStore.HasFileAt(_appSettings.ExternalDataFolder, item.PlayscriptName, item.NodeGuid));
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
}
