using StoryForge.Core;
using StoryForge.Core.CharacterCard;
using StoryForge.Core.Cloud;
using StoryForge.Core.Codegen;
using StoryForge.Core.Graph;
using StoryForge.Core.Schemas;
using StoryForge.Web.GraphEditor;

namespace StoryForge.Web.Cloud;

public enum CloudItemStatus
{
    Synced,        // local == base == cloud (as of the last download)
    LocalChanged,  // waiting for 上傳
    NewLocal,      // has content, never uploaded
    Conflict,      // both sides changed since base: 用我的 / 用雲端的 / Wait merge
    Waiting,       // Wait merge in progress
    Rejected,      // upload refused: the cloud changed after the last download
    NotOnGraph,    // the cloud has a table for a node this machine's flow graph does not have
}

public sealed class CloudItem
{
    public required string Guid { get; init; }
    public required string Name { get; set; }
    public CloudItemStatus Status { get; set; }
    public string? Message { get; set; }
}

public sealed class MergeCheckResult
{
    public required string Guid { get; init; }
    public string? Failure { get; init; }           // ①–③: back to the three choices
    public ProvenanceReport? Provenance { get; init; } // ④: shown for confirmation
}

// What the progress bar shows while a 下載 / 上傳 runs.
public sealed class CloudProgressInfo
{
    public required string Title { get; init; }
    public DateTime StartedAt { get; } = DateTime.Now;
    public string Phase { get; set; } = "啟動";
    public int? Done { get; set; }
    public int? Total { get; set; }
    public double Percent { get; set; }
    // Where the bar is heading (the end of an uncountable phase like connecting, or the next count of a
    // countable one) and since when: the bar creeps toward it so it never sits still between reports.
    public double CreepTo { get; set; } = 5;
    public DateTime CreepSince { get; set; } = DateTime.Now;

    public double DisplayPercent
    {
        get
        {
            var t = (DateTime.Now - CreepSince).TotalSeconds;
            return Percent + Math.Max(0, CreepTo - Percent) * 0.9 * (1 - Math.Exp(-t / 1.2));
        }
    }
}

// The outcome of the last 下載 / 上傳, shown as a banner until dismissed or the next run.
public sealed record CloudRunResult(bool Success, string Title, string Message, TimeSpan Elapsed, DateTime At);

// 雲端同步 tab. Git-style: 下載 = pull (whole-playscript decisions), 上傳 = push (fast-forward only, per
// playscript). The flow graph merges three-way per field instead; see MergeFlowGraphAsync. The cloud side is reached only through SfCloudClient (affine-mcp's StoryForge extension).
public sealed class CloudSyncState
{
    private readonly AppSettings _settings;
    private readonly GraphEditorState _graph;
    private readonly ReloadCoordinator _reload;
    private readonly Dictionary<string, CloudPlayscript> _lastCloud = new(StringComparer.Ordinal);
    private readonly HashSet<string> _conflicts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _rejected = new(StringComparer.Ordinal);
    private List<CloudPlayscript> _cloudIndex = new();

    public CloudSyncState(AppSettings settings, GraphEditorState graph, ReloadCoordinator reload)
    {
        _settings = settings;
        _graph = graph;
        _reload = reload;
    }

    public bool IsFlowGraphManaged => IsConfigured && Base().IsFlowGraphManaged;

    // Which parts 上傳 / 下載 include (the two checkboxes; both by default, not remembered).
    public bool SyncTables { get; set; } = true;
    public bool SyncGraph { get; set; } = true;

    public bool IsBusy { get; private set; }
    public string? StatusMessage { get; private set; }
    public DateTime? LastPullAt { get; private set; }
    public List<CloudItem> Items { get; private set; } = new();
    public int LocalPlayscriptCount { get; private set; }
    public MergeCheckResult? PendingMergeCheck { get; private set; }
    public CloudProgressInfo? Progress { get; private set; }
    public CloudRunResult? LastResult { get; private set; }
    public event Action? Changed;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.CloudWorkspaceId);
    public string WorkspaceId => _settings.CloudWorkspaceId.Trim();
    private string DataFolder => _settings.ExternalDataFolder;

    private SfCloudClient Client() => new(_settings.AffineMcpCommand, WorkspaceId)
    {
        Progress = p => SetPhase(p.Phase, p.Done, p.Total),
    };
    private CloudBaseStore Base() => new(WorkspaceId);

    public IReadOnlyList<WaitMergeState> Waiting => WaitMergeStore.All(DataFolder);

    // ---------- local view (no network) ----------

    public void RefreshLocal()
    {
        if (!IsConfigured)
        {
            Items = new();
            Notify();
            return;
        }

        var baseStore = Base();
        var items = new List<CloudItem>();
        var nodeGuids = new HashSet<string>(StringComparer.Ordinal);
        var nodes = _graph.GetPlayscriptNodes();
        LocalPlayscriptCount = nodes.Count;
        foreach (var (guid, name) in nodes)
        {
            nodeGuids.Add(guid);
            var item = new CloudItem { Guid = guid, Name = name };
            var local = ReadLocal(name, guid);
            var b = baseStore.Get(guid);
            if (WaitMergeStore.IsWaiting(DataFolder, guid)) item.Status = CloudItemStatus.Waiting;
            else if (_conflicts.Contains(guid)) item.Status = CloudItemStatus.Conflict;
            else if (_rejected.TryGetValue(guid, out var msg)) { item.Status = CloudItemStatus.Rejected; item.Message = msg; }
            else if (b == null) item.Status = local.Count > 0 ? CloudItemStatus.NewLocal : CloudItemStatus.Synced;
            else item.Status = CloudRows.AreEqual(local, b.Rows) ? CloudItemStatus.Synced : CloudItemStatus.LocalChanged;
            if (b == null && local.Count == 0 && !_lastCloud.ContainsKey(guid))
                continue; // empty and never synced: nothing to show
            items.Add(item);
        }

        foreach (var cloud in _cloudIndex.Where(c => !c.Orphan && !nodeGuids.Contains(c.Guid)))
            items.Add(new CloudItem { Guid = cloud.Guid, Name = cloud.Name, Status = CloudItemStatus.NotOnGraph, Message = "本機流程圖沒有這個節點，先略過" });

        Items = items
            .OrderBy(i => i.Status switch
            {
                CloudItemStatus.Conflict => 0, CloudItemStatus.Waiting => 1, CloudItemStatus.Rejected => 2,
                CloudItemStatus.LocalChanged => 3, CloudItemStatus.NewLocal => 4, CloudItemStatus.NotOnGraph => 5, _ => 6,
            })
            .ThenBy(i => i.Name, StringComparer.Ordinal)
            .ToList();
        Notify();
    }

    // ---------- 下載 (pull) ----------

    public async Task DownloadAsync()
    {
        bool tables = SyncTables, graph = SyncGraph;
        if (!tables && !graph)
            return;
        if (!await BeginAsync("下載中…", "下載"))
            return;
        try
        {
            // One call, and after the first download only what changed since the previous one (server time),
            // kept separately for the tables and the flow graph.
            var baseStore = Base();
            var pull = await Client().SyncPullAsync(baseStore.LastPullServerTime, baseStore.LastGraphPullServerTime, tables, graph);
            SetPhase("套用流程圖");
            string? graphSummary = null;
            if (graph)
            {
                // A part that was not merged keeps its old server time, so the next download reads it again.
                if (FlowGraphWaitStore.IsWaiting(DataFolder))
                    graphSummary = "流程圖等待合併中，這次沒有下載流程圖";
                else if (pull.Graph is { Exists: true, Graph: not null } && _graph.HasUnsavedChanges())
                    graphSummary = "流程圖分頁還有沒存檔的修改，流程圖這次沒有下載（存檔後再按下載）";
                else
                {
                    if (pull.Graph is { Exists: true, Graph: not null })
                        graphSummary = (await MergeFlowGraphAsync(pull.Graph)).Summary;
                    else if (pull.Graph is { Exists: false } && !baseStore.IsFlowGraphManaged)
                        graphSummary = "雲端還沒有流程圖（按「上傳」會把本機的流程圖一起傳上去）";
                    baseStore.LastGraphPullServerTime = pull.ServerTime;
                }
            }
            LastPullAt = DateTime.Now;
            _cloudIndex = pull.Index;
            if (!tables)
            {
                StatusMessage = null;
                Finish(true, "下載完成（只有流程圖）", (graphSummary ?? "流程圖沒有變動") + "。");
                return;
            }
            if (!pull.Incremental)
            {
                _lastCloud.Clear();
                _conflicts.Clear();
                _rejected.Clear();
            }
            foreach (var p in pull.Playscripts)
            {
                _lastCloud[p.Guid] = p;
                _conflicts.Remove(p.Guid);
                _rejected.Remove(p.Guid);
            }

            var nodes = _graph.GetPlayscriptNodes().ToDictionary(n => n.Guid, n => n.PlayscriptName, StringComparer.Ordinal);
            int written = 0, conflicts = 0, waitingChanged = 0;
            string? backupDir = null;

            var applied = 0;
            foreach (var cloud in pull.Playscripts)
            {
                if (applied++ % 20 == 0)
                {
                    SetPhase("寫入本機", applied - 1, pull.Playscripts.Count);
                    await Task.Yield(); // let the progress bar render between batches
                }
                if (!nodes.TryGetValue(cloud.Guid, out var name))
                    continue;
                if (WaitMergeStore.IsWaiting(DataFolder, cloud.Guid))
                {
                    if (!CloudRows.AreEqual(cloud.Rows, WaitMergeStore.ReadTheirs(DataFolder, cloud.Guid)))
                        waitingChanged++;
                    continue; // handled by 繼續合併
                }

                var path = NodeScriptTableStore.ResolvePath(DataFolder, name, cloud.Guid);
                var local = ReadLocal(name, cloud.Guid);
                var b = baseStore.Get(cloud.Guid);
                var cloudRows = CloudRows.Normalize(cloud.Rows);

                if (CloudRows.AreEqual(local, cloudRows))
                {
                    baseStore.Set(cloud.Guid, name, cloudRows, cloud.Sv);
                    continue;
                }

                var localChanged = b == null ? local.Count > 0 : !CloudRows.AreEqual(local, b.Rows);
                var cloudChanged = b == null || !CloudRows.AreEqual(cloudRows, b.Rows);
                if (!cloudChanged)
                    continue; // only local changed: keep it, waiting for 上傳
                if (localChanged)
                {
                    _conflicts.Add(cloud.Guid);
                    conflicts++;
                    continue;
                }

                backupDir ??= NewBackupFolder();
                Backup(path, backupDir, name);
                NodeScriptTableStore.WriteContentAt(path, name, cloudRows);
                baseStore.Set(cloud.Guid, name, cloudRows, cloud.Sv);
                written++;
            }

            var parts = new List<string>();
            if (graphSummary != null) parts.Add(graphSummary);
            parts.Add($"劇本更新 {written} 份");
            if (conflicts > 0) parts.Add($"{conflicts} 份兩邊都改過，請逐份選擇");
            if (waitingChanged > 0) parts.Add($"{waitingChanged} 份等待合併中的劇本雲端又改了（按「繼續合併」時會處理）");
            if (backupDir != null) parts.Add($"覆寫前的檔案備份在 {backupDir}");
            baseStore.LastPullServerTime = pull.ServerTime;
            var scope = pull.Incremental ? $"（只讀了上次之後有變動的 {pull.Playscripts.Count} 份）" : "";
            if (!graph) scope += "（沒有下載流程圖）";
            StatusMessage = null;
            Finish(true, "下載完成" + scope, string.Join("；", parts) + "。");
        }
        catch (Exception e)
        {
            StatusMessage = null;
            Finish(false, "下載失敗", e.Message);
        }
        finally
        {
            End();
        }
    }

    // ---------- 上傳 (push) ----------

    public async Task UploadAsync()
    {
        bool tables = SyncTables, graph = SyncGraph;
        if (!tables && !graph)
            return;
        if (!await BeginAsync("上傳中…", "上傳"))
            return;
        try
        {
            var baseStore = Base();
            var items = new List<CloudPushItem>();
            var skippedWaiting = 0;
            IReadOnlyList<(string Guid, string PlayscriptName)> nodes = tables ? _graph.GetPlayscriptNodes() : Array.Empty<(string, string)>();
            var scanned = 0;
            foreach (var (guid, name) in nodes)
            {
                if (scanned++ % 50 == 0)
                {
                    SetPhase("比對本機", scanned - 1, nodes.Count);
                    await Task.Yield();
                }
                if (WaitMergeStore.IsWaiting(DataFolder, guid)) { skippedWaiting++; continue; }
                if (_conflicts.Contains(guid)) continue;
                var local = ReadLocal(name, guid);
                var b = baseStore.Get(guid);
                if (b == null && local.Count == 0) continue;
                if (b != null && CloudRows.AreEqual(local, b.Rows) && b.Name == name) continue;
                items.Add(new CloudPushItem { Guid = guid, Name = name, Chapter = ChapterOf(name), Rows = local, BaseRows = b?.Rows });
            }

            var rules = TryBuildRules(out var rulesWarning);
            if (rules != null && rules.Value.Hash == baseStore.LastRulesHash)
                rules = null; // already in the cloud
            var sentHashes = baseStore.ReferenceHashes;
            var references = BuildReferences().Where(r => !sentHashes.TryGetValue(r.Name, out var h) || h != r.Hash).ToList();
            // The flow graph: the first time (the cloud has none yet) the whole local graph goes up as the seed.
            // After that only the local edits do — what differs from the base — after merging in whatever the
            // cloud changed meanwhile; a place both sides changed stops the graph's upload in a wait merge.
            CloudGraphSeed? graphSeed = null;
            CloudGraphMerge? graphMerge = null;
            FlowGraphSnapshot? graphMine = null;
            var graphFailed = false;
            string? graphNote = null;
            if (graph && baseStore.IsFlowGraphManaged)
            {
                if (FlowGraphWaitStore.IsWaiting(DataFolder))
                    graphNote = "流程圖等待合併中，這次沒有上傳流程圖";
                else if (_graph.HasUnsavedChanges())
                    graphNote = "流程圖分頁還有沒存檔的修改，流程圖這次沒有上傳（存檔後再按上傳）";
                else
                {
                    var schema = _graph.Schema;
                    var baseSnap = ReadGraphBase();
                    var mine = _graph.BuildLocalSnapshot(baseSnap);
                    if (baseSnap == null || mine == null)
                        graphNote = "流程圖還沒有和雲端對過，這次沒有上傳流程圖（請先按一次「下載」）";
                    else if (!FlowGraphMerge.AreEqual(mine, baseSnap, schema))
                    {
                        SetPhase("檢查雲端");
                        var graphPull = await Client().SyncPullAsync(null, baseStore.LastGraphPullServerTime, tables: false, graph: true);
                        var conflict = false;
                        if (graphPull.Graph is { Exists: true, Graph: not null })
                        {
                            var merged = await MergeFlowGraphAsync(graphPull.Graph);
                            conflict = merged.Conflict;
                            if (conflict)
                            {
                                graphNote = merged.Summary + "，流程圖沒有上傳";
                                graphFailed = true;
                            }
                            baseSnap = ReadGraphBase();
                            mine = _graph.BuildLocalSnapshot(baseSnap);
                        }
                        baseStore.LastGraphPullServerTime = graphPull.ServerTime;
                        if (!conflict && mine != null && baseSnap != null && !FlowGraphMerge.AreEqual(mine, baseSnap, schema))
                        {
                            graphMerge = new CloudGraphMerge(mine.ToJObject(schema), baseSnap.ToJObject(schema), ReadNodeSchema());
                            graphMine = mine;
                        }
                    }
                }
            }
            if (graph && !baseStore.IsFlowGraphManaged)
            {
                if (_graph.HasUnsavedChanges())
                    graphNote = "流程圖分頁還有沒存檔的修改，流程圖這次沒有上傳（存檔後再按上傳）";
                else if (_graph.BuildCloudUpload() is { } up)
                    graphSeed = new CloudGraphSeed(up.GraphJson, up.Memos, up.Content, ReadNodeSchema());
                else
                    graphNote = "還沒有藍圖（blueprint.json），流程圖沒有上傳；請先在流程圖分頁存檔一次";
            }
            var contentSeed = graph && graphSeed == null && baseStore.IsFlowGraphManaged && !baseStore.NodeContentSeeded
                ? _graph.BuildCloudNodeContent() : null;
            if (items.Count == 0 && rules == null && references.Count == 0 && contentSeed == null && graphSeed == null && graphMerge == null)
            {
                StatusMessage = null;
                Finish(graphNote == null, graphNote == null ? "上傳完成" : "沒有上傳", "沒有要上傳的改動。" + (graphNote != null ? graphNote + "。" : "") + (rulesWarning ?? ""));
                return;
            }

            var outcome = await Client().PushAsync(items, rules, references, contentSeed, graphSeed, graphMerge);
            if (outcome.Graph is { } gr)
            {
                baseStore.SetFlowGraphManaged();
                if (gr.Status == "ok")
                {
                    baseStore.NodeContentSeeded = true;
                    if (_graph.BuildLocalSnapshot(null) is { } seeded)
                        baseStore.FlowGraphBaseJson = seeded.ToJson(_graph.Schema);
                    graphNote = "流程圖已上傳，之後本機和 AFFiNE 網頁版都能編輯，按「上傳」／「下載」互相同步";
                }
                else
                {
                    // Rejected = the cloud already has a flow graph (someone else uploaded theirs first).
                    graphNote = "雲端已經有流程圖，這次沒有上傳流程圖；請按「下載」取得雲端的版本（本機的流程圖會先備份再換成雲端的）";
                }
            }
            if (graphMerge != null)
            {
                if (outcome.GraphMerge is { Status: "ok" })
                {
                    baseStore.FlowGraphBaseJson = graphMine!.ToJson(_graph.Schema);
                    graphNote = "流程圖的本機修改已上傳";
                }
                else
                {
                    graphFailed = true;
                    graphNote = outcome.GraphMerge == null
                        ? "流程圖沒有上傳：affine-mcp 裡的 StoryForge 擴充太舊（需要 @nineday/storyforge-cloud 1.2.0 以上）"
                        : $"流程圖沒有上傳：{outcome.GraphMerge.Message}";
                }
            }
            var results = outcome.Results;
            if (rules != null)
                baseStore.LastRulesHash = rules.Value.Hash;
            if (references.Count > 0)
            {
                foreach (var r in references) sentHashes[r.Name] = r.Hash;
                baseStore.ReferenceHashes = sentHashes;
            }
            if (outcome.ContentSeeded == true)
                baseStore.NodeContentSeeded = true;
            var names = items.ToDictionary(i => i.Guid, i => i.Name, StringComparer.Ordinal);
            int ok = 0, rejected = 0;
            _rejected.Clear();
            foreach (var r in results)
            {
                if (r.Status is "ok" or "unchanged")
                {
                    baseStore.Set(r.Guid, names[r.Guid], r.Rows ?? new(), r.Sv);
                    if (r.Status == "ok") ok++;
                }
                else
                {
                    _rejected[r.Guid] = r.Message ?? "雲端在上次下載後被改過，請先下載";
                    rejected++;
                }
            }

            StatusMessage = null;
            Finish(rejected == 0 && !graphFailed,
                rejected > 0 ? "上傳完成，但有劇本被擋下" : graphFailed ? "上傳完成，但流程圖沒有上傳" : "上傳完成",
                (graphNote != null ? graphNote + "；" : "") +
                (tables ? $"劇本上傳 {ok} 份" : "沒有上傳劇本表格") +
                (rejected > 0 ? $"；{rejected} 份被擋下（雲端在上次下載後又改過，請先下載）" : "") +
                (skippedWaiting > 0 ? $"；{skippedWaiting} 份等待合併中，沒有上傳" : "") +
                (references.Count > 0 ? $"；AI 參考資料更新 {references.Count} 份（{string.Join("、", references.Select(r => r.Name))}）" : "") +
                (contentSeed != null && outcome.ContentSeeded == true ? "；節點的衝突／變化等內容已送上雲端" : "") +
                (rulesWarning != null ? $"；{rulesWarning}" : "") + "。");
        }
        catch (Exception e)
        {
            StatusMessage = null;
            Finish(false, "上傳失敗", e.Message);
        }
        finally
        {
            End();
        }
    }

    // ---------- flow graph ----------

    public FlowGraphWaitState? GraphWait => IsConfigured ? FlowGraphWaitStore.State(DataFolder) : null;
    public string GraphWaitFolder => FlowGraphWaitStore.Folder(DataFolder);

    private FlowGraphSnapshot? ReadGraphBase() =>
        Base().FlowGraphBaseJson is { } json ? FlowGraphSnapshot.Parse(json, _graph.Schema) : null;

    private FlowGraphSnapshot CloudSnapshot(CloudGraphPull pull, FlowGraphSnapshot contentFallback)
    {
        var snapshot = FlowGraphSnapshot.From(pull.Graph!, pull.Memos, pull.ContentSeeded ? pull.Content : null, _graph.Schema);
        // A graph uploaded before node content was synced has none: that is "never sent", not "cleared".
        if (!pull.ContentSeeded)
            snapshot.Content = contentFallback.Clone().Content;
        return snapshot;
    }

    // Merges the cloud flow graph into the local one (three-way, on the base both last agreed on) and reloads
    // the canvas; the base becomes the cloud's version, so whatever the local graph still differs by is what
    // the next 上傳 sends. A place both sides changed starts the flow graph's wait merge instead (nothing
    // local is touched, the base stays).
    private async Task<(string Summary, bool Conflict)> MergeFlowGraphAsync(CloudGraphPull pull)
    {
        var schema = _graph.Schema ?? throw new InvalidOperationException("流程圖尚未載入，請先切換到流程圖分頁。");
        var baseStore = Base();
        var baseSnap = ReadGraphBase();
        var mine = _graph.BuildLocalSnapshot(baseSnap);
        baseStore.SetFlowGraphManaged();
        if (pull.ContentSeeded)
            baseStore.NodeContentSeeded = true;

        if (mine == null)
        {
            // No blueprint yet (never saved here): the cloud graph simply becomes the local one.
            var cloudOnly = FlowGraphSnapshot.From(pull.Graph!, pull.Memos, pull.ContentSeeded ? pull.Content : null, schema);
            var first = await ApplyGraphAsync(cloudOnly, pull.ContentSeeded);
            baseStore.FlowGraphBaseJson = (_graph.BuildLocalSnapshot(cloudOnly) ?? cloudOnly).ToJson(schema);
            return (first, false);
        }

        // Never synced here: the local graph counts as unchanged, so the cloud's version wins everywhere.
        baseSnap ??= mine;
        var theirs = CloudSnapshot(pull, baseSnap);
        var outcome = FlowGraphMerge.Merge(baseSnap, mine, theirs, schema);
        if (outcome.HasConflicts)
        {
            FlowGraphWaitStore.Start(DataFolder, baseSnap, mine, theirs, outcome, schema);
            await _reload.RequestReloadAsync(); // the flow graph tab turns read-only
            return ($"流程圖有 {outcome.ConflictCount} 處本機和雲端都改過，已進入等待合併（合併用的檔案在 {GraphWaitFolder}）", true);
        }

        var summary = FlowGraphMerge.AreEqual(outcome.Merged, mine, schema) ? "流程圖沒有變動" : await ApplyGraphAsync(outcome.Merged, true);
        baseStore.FlowGraphBaseJson = theirs.ToJson(schema);
        if (!FlowGraphMerge.AreEqual(outcome.Merged, theirs, schema))
            summary += "（本機還有修改沒上傳）";
        return (summary, false);
    }

    private async Task<string> ApplyGraphAsync(FlowGraphSnapshot snapshot, bool withContent, Action? beforeReload = null)
    {
        var summary = _graph.ApplyCloudGraph(CanonicalGraphJson.Clone(snapshot.Graph), snapshot.Memos, withContent ? snapshot.Content : null);
        beforeReload?.Invoke();
        await _reload.RequestReloadAsync();
        var collisions = _graph.SyncRenamedFilesAfterCloudDownload();
        return collisions > 0 ? $"{summary}（{collisions} 個改名的劇本檔撞名，沒有搬，請到流程圖分頁處理）" : summary;
    }

    // 繼續合併 (flow graph): the working file must load and pass the graph checks; a cloud that changed again
    // during the wait is merged in on top (and only stops the merge if it hits the same places again).
    private bool _graphUnchangedWarned;

    public async Task ContinueGraphMergeAsync()
    {
        if (!FlowGraphWaitStore.IsWaiting(DataFolder) || !await BeginAsync("檢查流程圖合併結果…", "檢查流程圖合併結果"))
            return;
        const string notYet = "還不能完成流程圖合併";
        try
        {
            var schema = _graph.Schema ?? throw new InvalidOperationException("流程圖尚未載入，請先切換到流程圖分頁。");
            FlowGraphSnapshot working;
            try
            {
                working = FlowGraphWaitStore.ReadWorking(DataFolder, schema);
            }
            catch (Exception e)
            {
                Finish(false, notYet, $"merged.json 讀不進來：{e.Message}");
                return;
            }
            var validation = CanonicalGraphJson.Validate(working.Graph, schema);
            if (!validation.IsValid)
            {
                Finish(false, notYet, "merged.json 的流程圖檢查沒過：" + string.Join("；", validation.Errors.Take(5)) +
                                      (validation.Errors.Count > 5 ? $"…（共 {validation.Errors.Count} 個）" : ""));
                return;
            }

            var baseSnap = FlowGraphWaitStore.ReadBase(DataFolder, schema);
            var mine = FlowGraphWaitStore.ReadMine(DataFolder, schema);
            var theirs = FlowGraphWaitStore.ReadTheirs(DataFolder, schema);
            var untouched = FlowGraphMerge.AreEqual(working, FlowGraphMerge.Merge(baseSnap, mine, theirs, schema).Merged, schema);
            if (untouched && !_graphUnchangedWarned)
            {
                _graphUnchangedWarned = true;
                Finish(false, notYet, "merged.json 沒有改動，看起來還沒合併。如果確定衝突的地方全部用本機的，再按一次「繼續合併」。");
                return;
            }

            var pull = await Client().SyncPullAsync(null, null, tables: false, graph: true);
            if (pull.Graph is not { Exists: true, Graph: not null })
                throw new InvalidOperationException("雲端讀不到流程圖。");
            var cloud = CloudSnapshot(pull.Graph, theirs);
            if (!FlowGraphMerge.AreEqual(cloud, theirs, schema))
            {
                // What the cloud changed since `theirs`, on top of the merge result (which already has theirs).
                var again = FlowGraphMerge.Merge(theirs, working, cloud, schema);
                if (again.HasConflicts)
                {
                    FlowGraphWaitStore.Restack(DataFolder, theirs, working, cloud, again, schema);
                    _graphUnchangedWarned = false;
                    Finish(false, notYet, $"雲端在等待期間又被改了，而且有 {again.ConflictCount} 處和合併結果改到同一個地方。" +
                                          "已把目前的合併結果當成新的 mine.json、最新的雲端版本當成新的 theirs.json，conflicts.md 和 merged.json 也更新了，請再合併一次。");
                    return;
                }
                working = again.Merged;
                theirs = cloud;
            }

            var summary = await ApplyGraphAsync(working, true, () => FlowGraphWaitStore.Finish(DataFolder));
            var baseStore = Base();
            baseStore.FlowGraphBaseJson = theirs.ToJson(schema);
            baseStore.LastGraphPullServerTime = pull.ServerTime;
            _graphUnchangedWarned = false;
            StatusMessage = null;
            Finish(true, "流程圖合併完成", $"{summary}。已解除唯讀，按「上傳」送出合併結果。");
        }
        catch (Exception e)
        {
            StatusMessage = null;
            Finish(false, "流程圖合併失敗", e.Message);
        }
        finally
        {
            End();
        }
    }

    // Drops the wait: the local graph is still as it was, and the next 下載 / 上傳 will find the same conflicts.
    public async Task CancelGraphWaitAsync()
    {
        if (IsBusy || !FlowGraphWaitStore.IsWaiting(DataFolder))
            return;
        FlowGraphWaitStore.Finish(DataFolder);
        _graphUnchangedWarned = false;
        StatusMessage = "已取消流程圖的等待合併，本機流程圖維持原樣並解除唯讀。下次下載或上傳時，同樣的衝突會再出現。";
        await _reload.RequestReloadAsync();
        Notify();
    }

    // Files an AI that only has MCP needs (AGENTS.md tells it to read them), uploaded read-only for
    // sf_read_guide / sf_read_reference. Missing files are skipped.
    private static readonly (string Name, string File)[] ReferenceFiles =
    {
        ("AGENTS.md", "AGENTS.md"), ("StoryOutline", "StoryOutline.json"), ("CharacterCards", "CharacterCards.json"),
        ("AudioNotes", "AudioNotes.json"), ("LdtkTransports", "LdtkTransports.json"), ("GuidePathPoints", "GuidePathPoints.json"),
        ("PlayscriptNames", "PlayscriptNames.json"),
    };

    private List<CloudReference> BuildReferences()
    {
        var list = new List<CloudReference>();
        foreach (var (name, file) in ReferenceFiles)
        {
            var path = Path.Combine(DataFolder, file);
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path).TrimStart('\uFEFF');
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            list.Add(new CloudReference(name, text, hash));
        }
        return list;
    }

    private static string? ReadNodeSchema()
    {
        var path = Path.Combine(ProjectPaths.ResolveUnityProjectRoot(), "Assets", "06.Definition",
            "PlayscriptOfflineExport", "ProcessNodeSchema.json");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private (string Json, string Hash)? TryBuildRules(out string? warning)
    {
        warning = null;
        try
        {
            var root = ProjectPaths.ResolveUnityProjectRoot();
            var doc = EnumLabelMapSource.RefreshOrLoadExisting(root, out var w);
            if (doc == null)
            {
                warning = "找不到 Enum 對照表，規則沒有上傳";
                return null;
            }
            warning = w;
            return CloudRulesExport.Build(EnumLabelMaps.FromDocument(doc), CharacterCardSettings.LoadOrDefault(DataFolder));
        }
        catch (Exception e)
        {
            warning = $"規則沒有上傳：{e.Message}";
            return null;
        }
    }

    // ---------- conflicts: 用我的 / 用雲端的 / Wait merge ----------

    public void UseMine(string guid)
    {
        if (!TryConflict(guid, out var name, out var cloud)) return;
        var backupDir = NewBackupFolder();
        WriteBackupRows(backupDir, name + "（雲端）", name, cloud.Rows);
        Base().Set(guid, name, cloud.Rows, cloud.Sv);
        _conflicts.Remove(guid);
        StatusMessage = $"「{name}」用本機的版本；雲端版本備份在 {backupDir}。按「上傳」送出。";
        RefreshLocal();
    }

    public void UseTheirs(string guid)
    {
        if (!TryConflict(guid, out var name, out var cloud)) return;
        var path = NodeScriptTableStore.ResolvePath(DataFolder, name, guid);
        var backupDir = NewBackupFolder();
        Backup(path, backupDir, name);
        NodeScriptTableStore.WriteContentAt(path, name, CloudRows.Normalize(cloud.Rows));
        Base().Set(guid, name, cloud.Rows, cloud.Sv);
        _conflicts.Remove(guid);
        StatusMessage = $"「{name}」用雲端的版本；本機版本備份在 {backupDir}。";
        RefreshLocal();
    }

    public void StartWaitMerge(string guid)
    {
        if (!TryConflict(guid, out var name, out var cloud)) return;
        var path = NodeScriptTableStore.ResolvePath(DataFolder, name, guid);
        var baseRows = Base().Get(guid)?.Rows ?? new List<string[]>();
        WaitMergeStore.Start(DataFolder, guid, name, path, baseRows, ReadLocal(name, guid), cloud.Rows);
        _conflicts.Remove(guid);
        StatusMessage = $"「{name}」進入等待合併。合併用的檔案在 {WaitMergeStore.FolderFor(DataFolder, guid)}，" +
                        $"請叫 Claude 照裡面的 README.md 把結果寫進 {path}，再按「繼續合併」。";
        RefreshLocal();
    }

    public void CancelWait(string guid)
    {
        var state = Waiting.FirstOrDefault(w => w.Guid == guid);
        if (state == null) return;
        NodeScriptTableStore.WriteContentAt(state.WorkingPath, state.PlayscriptName, WaitMergeStore.ReadMine(DataFolder, guid));
        var theirs = WaitMergeStore.ReadTheirs(DataFolder, guid);
        WaitMergeStore.Finish(DataFolder, guid);
        if (!_lastCloud.ContainsKey(guid))
            _lastCloud[guid] = new CloudPlayscript { Guid = guid, Name = state.PlayscriptName, Rows = theirs };
        _conflicts.Add(guid);
        PendingMergeCheck = null;
        StatusMessage = $"已取消「{state.PlayscriptName}」的等待合併，工作檔還原成等待前的內容。請重新選擇。";
        RefreshLocal();
    }

    // 繼續合併: ① working file changed, ② cloud changed during the wait, ③ C# validation, ④ provenance.
    public async Task ContinueMergeAsync(string guid)
    {
        var state = Waiting.FirstOrDefault(w => w.Guid == guid);
        if (state == null || !await BeginAsync("檢查合併結果…", "檢查合併結果"))
            return;
        try
        {
            var name = state.PlayscriptName;
            var working = CloudRows.Normalize(NodeScriptTableStore.ReadRowsFromFile(state.WorkingPath, keepBlankRows: true));
            var mine = WaitMergeStore.ReadMine(DataFolder, guid);
            var theirs = WaitMergeStore.ReadTheirs(DataFolder, guid);

            if (CloudRows.AreEqual(working, mine) && !CloudRows.AreEqual(mine, theirs))
            {
                PendingMergeCheck = new MergeCheckResult { Guid = guid, Failure = "① 工作檔沒有改動，看起來還沒合併。" };
                return;
            }

            var pull = await Client().SyncPullAsync(null, null, tables: true, graph: false, only: new[] { guid });
            var cloud = pull.Playscripts.FirstOrDefault(p => p.Guid == guid);
            var cloudRows = CloudRows.Normalize(cloud?.Rows);
            if (cloud != null) _lastCloud[guid] = cloud;
            if (!CloudRows.AreEqual(cloudRows, theirs))
            {
                WaitMergeStore.Restack(DataFolder, guid, name, working, cloudRows);
                PendingMergeCheck = new MergeCheckResult
                {
                    Guid = guid,
                    Failure = "② 雲端在等待期間又被改了。已把最新的雲端版本當成新的 theirs.md、目前的合併結果當成新的 mine.md，請再合併一次。",
                };
                return;
            }

            var error = PlayscriptRowValidator.Create(ProjectPaths.ResolveUnityProjectRoot()).Validate(name, working);
            if (error != null)
            {
                PendingMergeCheck = new MergeCheckResult { Guid = guid, Failure = $"③ 規則檢查沒過：{error}" };
                return;
            }

            PendingMergeCheck = new MergeCheckResult
            {
                Guid = guid,
                Provenance = MergeProvenance.Build(WaitMergeStore.ReadBase(DataFolder, guid), mine, theirs, working),
            };
        }
        catch (Exception e)
        {
            PendingMergeCheck = new MergeCheckResult { Guid = guid, Failure = $"檢查失敗：{e.Message}" };
        }
        finally
        {
            End();
        }
    }

    // After ④ is confirmed: the merge result becomes the local version on top of the cloud's (base = cloud).
    public void ConfirmMerge(string guid)
    {
        var state = Waiting.FirstOrDefault(w => w.Guid == guid);
        if (state == null || PendingMergeCheck?.Guid != guid || PendingMergeCheck.Provenance == null) return;
        var theirs = WaitMergeStore.ReadTheirs(DataFolder, guid);
        Base().Set(guid, state.PlayscriptName, theirs, _lastCloud.TryGetValue(guid, out var c) ? c.Sv : null);
        WaitMergeStore.Finish(DataFolder, guid);
        PendingMergeCheck = null;
        StatusMessage = $"「{state.PlayscriptName}」合併完成，已解除唯讀。按「上傳」送出合併結果。";
        RefreshLocal();
    }

    public void DismissMergeCheck()
    {
        PendingMergeCheck = null;
        Notify();
    }

    // ---------- helpers ----------

    private bool TryConflict(string guid, out string name, out CloudPlayscript cloud)
    {
        name = _graph.GetPlayscriptNodes().FirstOrDefault(n => n.Guid == guid).PlayscriptName ?? string.Empty;
        if (!_conflicts.Contains(guid) || !_lastCloud.TryGetValue(guid, out cloud!) || name.Length == 0)
        {
            cloud = null!;
            StatusMessage = "這份劇本已經不是衝突狀態，請重新下載。";
            Notify();
            return false;
        }
        return true;
    }

    private List<string[]> ReadLocal(string name, string guid) =>
        CloudRows.Normalize(NodeScriptTableStore.ReadRowsFromFile(NodeScriptTableStore.ResolvePath(DataFolder, name, guid), keepBlankRows: true));

    private static string ChapterOf(string name)
    {
        var i = name.IndexOf('/');
        return i > 0 ? name[..i] : "_其他";
    }

    private string NewBackupFolder()
    {
        var dir = Path.Combine(DataFolder, "備份", "雲端同步", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Backup(string path, string backupDir, string name)
    {
        if (!File.Exists(path)) return;
        var target = Path.Combine(backupDir, SafeName(name) + ".md");
        File.Copy(path, target, overwrite: true);
    }

    private static void WriteBackupRows(string backupDir, string fileLabel, string name, IReadOnlyList<string[]> rows) =>
        NodeScriptTableStore.WriteContentAt(Path.Combine(backupDir, SafeName(fileLabel) + ".md"), name, CloudRows.Normalize(rows));

    private static string SafeName(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // Phase -> share of the bar it covers, in the order the phases happen.
    private static readonly Dictionary<string, (double From, double To)> PhaseRanges = new()
    {
        ["啟動"] = (0, 5), ["比對本機"] = (0, 10), ["連線"] = (10, 25), ["比對變動"] = (25, 28), ["檢查雲端"] = (25, 30),
        ["讀取索引"] = (28, 32), ["讀取流程圖"] = (32, 40), ["上傳規則"] = (30, 35), ["上傳流程圖"] = (30, 85),
        ["讀取劇本"] = (40, 85), ["上傳節點內容"] = (22, 28), ["上傳參考資料"] = (33, 38), ["上傳劇本"] = (38, 95), ["更新索引"] = (95, 99), ["套用流程圖"] = (85, 90),
        ["寫入本機"] = (90, 100),
    };

    private DateTime _lastProgressNotify;

    private void SetPhase(string phase, int? done = null, int? total = null)
    {
        var p = Progress;
        if (p == null) return;
        bool phaseChanged;
        lock (p)
        {
            var (from, to) = PhaseRanges.TryGetValue(phase, out var r) ? r : (p.Percent, p.Percent);
            var pct = total is > 0 && done is { } d ? from + (to - from) * Math.Clamp(d / (double)total.Value, 0, 1) : from;
            phaseChanged = p.Phase != phase;
            var shown = p.DisplayPercent;
            p.Phase = phase;
            p.Done = done;
            p.Total = total;
            // Never moves backwards, including from where the creep already got to.
            p.Percent = Math.Max(Math.Max(p.Percent, pct), Math.Min(shown, total is > 0 ? to : from));
            p.CreepTo = total is > 0 && done is { } dn && dn < total
                ? from + (to - from) * (dn + 1) / (double)total.Value
                : to;
            p.CreepSince = DateTime.Now;
        }
        // Progress lines can come many times a second; the bar only needs ~10 redraws per second.
        var now = DateTime.UtcNow;
        if (!phaseChanged && done != total && (now - _lastProgressNotify).TotalMilliseconds < 100) return;
        _lastProgressNotify = now;
        Notify();
    }

    private void Finish(bool success, string title, string message)
    {
        var elapsed = Progress != null ? DateTime.Now - Progress.StartedAt : TimeSpan.Zero;
        LastResult = new CloudRunResult(success, title, message, elapsed, DateTime.Now);
    }

    public void DismissResult()
    {
        LastResult = null;
        Notify();
    }

    private async Task<bool> BeginAsync(string message, string title)
    {
        if (IsBusy) return false;
        if (!IsConfigured)
        {
            StatusMessage = "還沒設定雲端 workspace id（設定 → 雲端同步）。";
            Notify();
            return false;
        }
        IsBusy = true;
        StatusMessage = message;
        LastResult = null;
        Progress = new CloudProgressInfo { Title = title };
        Notify();
        await Task.Yield();
        return true;
    }

    private void End()
    {
        IsBusy = false;
        Progress = null;
        RefreshLocal();
    }

    private void Notify() => Changed?.Invoke();
}
