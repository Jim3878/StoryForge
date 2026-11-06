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

// 雲端同步 tab. Git-style: 下載 = pull (whole-playscript decisions), 上傳 = push (fast-forward only, per
// playscript). The cloud side is reached only through SfCloudClient (affine-mcp's StoryForge extension).
public sealed class CloudSyncState
{
    private readonly AppSettings _settings;
    private readonly GraphEditorState _graph;
    private readonly ReloadCoordinator _reload;
    private readonly Dictionary<string, CloudPlayscript> _lastCloud = new(StringComparer.Ordinal);
    private readonly HashSet<string> _conflicts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _rejected = new(StringComparer.Ordinal);

    public CloudSyncState(AppSettings settings, GraphEditorState graph, ReloadCoordinator reload)
    {
        _settings = settings;
        _graph = graph;
        _reload = reload;
    }

    public bool IsFlowGraphManaged => IsConfigured && Base().IsFlowGraphManaged;

    public bool IsBusy { get; private set; }
    public string? StatusMessage { get; private set; }
    public DateTime? LastPullAt { get; private set; }
    public List<CloudItem> Items { get; private set; } = new();
    public int LocalPlayscriptCount { get; private set; }
    public MergeCheckResult? PendingMergeCheck { get; private set; }
    public event Action? Changed;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.CloudWorkspaceId);
    public string WorkspaceId => _settings.CloudWorkspaceId.Trim();
    private string DataFolder => _settings.ExternalDataFolder;

    private SfCloudClient Client() => new(_settings.AffineMcpCommand, WorkspaceId);
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

        foreach (var cloud in _lastCloud.Values.Where(c => !nodeGuids.Contains(c.Guid)))
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
        if (!await BeginAsync("下載中…"))
            return;
        try
        {
            var graphSummary = await DownloadFlowGraphAsync();
            var pull = await Client().PullAsync();
            LastPullAt = DateTime.Now;
            _lastCloud.Clear();
            foreach (var p in pull.Playscripts)
                _lastCloud[p.Guid] = p;

            var nodes = _graph.GetPlayscriptNodes().ToDictionary(n => n.Guid, n => n.PlayscriptName, StringComparer.Ordinal);
            var baseStore = Base();
            int written = 0, conflicts = 0, waitingChanged = 0;
            string? backupDir = null;
            _rejected.Clear();
            _conflicts.Clear();

            foreach (var cloud in pull.Playscripts)
            {
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
            StatusMessage = "下載完成：" + string.Join("；", parts) + "。";
        }
        catch (Exception e)
        {
            StatusMessage = $"下載失敗：{e.Message}";
        }
        finally
        {
            End();
        }
    }

    // ---------- 上傳 (push) ----------

    public async Task UploadAsync()
    {
        if (!await BeginAsync("上傳中…"))
            return;
        try
        {
            var baseStore = Base();
            var items = new List<CloudPushItem>();
            var skippedWaiting = 0;
            foreach (var (guid, name) in _graph.GetPlayscriptNodes())
            {
                if (WaitMergeStore.IsWaiting(DataFolder, guid)) { skippedWaiting++; continue; }
                if (_conflicts.Contains(guid)) continue;
                var local = ReadLocal(name, guid);
                var b = baseStore.Get(guid);
                if (b == null && local.Count == 0) continue;
                if (b != null && CloudRows.AreEqual(local, b.Rows) && b.Name == name) continue;
                items.Add(new CloudPushItem { Guid = guid, Name = name, Chapter = ChapterOf(name), Rows = local, BaseRows = b?.Rows });
            }

            var rules = TryBuildRules(out var rulesWarning);
            if (items.Count == 0 && rules == null)
            {
                StatusMessage = "沒有要上傳的改動。" + (rulesWarning ?? "");
                return;
            }

            var results = await Client().PushAsync(items, rules);
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

            StatusMessage = $"上傳完成：{ok} 份" +
                            (rejected > 0 ? $"；{rejected} 份被擋下（雲端在上次下載後又改過，請先下載）" : "") +
                            (skippedWaiting > 0 ? $"；{skippedWaiting} 份等待合併中，沒有上傳" : "") +
                            (rulesWarning != null ? $"；{rulesWarning}" : "") + "。";
        }
        catch (Exception e)
        {
            StatusMessage = $"上傳失敗：{e.Message}";
        }
        finally
        {
            End();
        }
    }

    // ---------- flow graph ----------

    // Pulls the cloud flow graph (if one was uploaded) into blueprint.json and reloads the canvas, before the
    // tables are downloaded so nodes created in the web editor already exist locally. Null = no cloud graph.
    private async Task<string?> DownloadFlowGraphAsync()
    {
        var pull = await Client().PullGraphAsync();
        if (!pull.Exists || pull.Graph == null)
            return null;
        Base().SetFlowGraphManaged();
        var graph = CanonicalGraphJson.Parse(pull.Graph.ToString(Newtonsoft.Json.Formatting.None));
        var summary = _graph.ApplyCloudGraph(graph, pull.Memos);
        await _reload.RequestReloadAsync();
        var collisions = _graph.SyncRenamedFilesAfterCloudDownload();
        return collisions > 0 ? $"{summary}（{collisions} 個改名的劇本檔撞名，沒有搬，請到流程圖分頁處理）" : summary;
    }

    // First upload of the local flow graph (seed), or — explicitly confirmed — replacing the cloud graph with
    // the local one. Afterwards the local flow graph tab is read-only.
    public async Task UploadFlowGraphAsync(bool overwrite)
    {
        if (!await BeginAsync(overwrite ? "用本機流程圖覆蓋雲端…" : "上傳流程圖…"))
            return;
        try
        {
            if (_graph.HasUnsavedChanges())
            {
                StatusMessage = "流程圖分頁還有沒存檔的修改，請先存檔再上傳。";
                return;
            }
            var upload = _graph.BuildCloudUpload()
                         ?? throw new InvalidOperationException("還沒有藍圖（blueprint.json），請先在流程圖分頁存檔一次。");
            string? nodeSchema = null;
            var schemaPath = Path.Combine(ProjectPaths.ResolveUnityProjectRoot(), "Assets", "06.Definition",
                "PlayscriptOfflineExport", "ProcessNodeSchema.json");
            if (File.Exists(schemaPath))
                nodeSchema = await File.ReadAllTextAsync(schemaPath);
            var refused = await Client().PushGraphAsync(upload.GraphJson, upload.Memos, nodeSchema, overwrite ? "overwrite" : "seed");
            if (refused != null)
            {
                StatusMessage = $"流程圖沒有上傳：{refused}";
                return;
            }
            Base().SetFlowGraphManaged();
            await _reload.RequestReloadAsync();
            StatusMessage = overwrite
                ? "已用本機流程圖覆蓋雲端。本機流程圖分頁現在是唯讀，之後請在網頁版編輯。"
                : "流程圖已上傳。之後請在 AFFiNE 網頁版的「StoryForge 流程圖」編輯，本機流程圖分頁改成唯讀，按「下載」取得最新版。";
        }
        catch (Exception e)
        {
            StatusMessage = $"上傳流程圖失敗：{e.Message}";
        }
        finally
        {
            End();
        }
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
        if (state == null || !await BeginAsync("檢查合併結果…"))
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

            var pull = await Client().PullAsync();
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

    private async Task<bool> BeginAsync(string message)
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
        Notify();
        await Task.Yield();
        return true;
    }

    private void End()
    {
        IsBusy = false;
        RefreshLocal();
    }

    private void Notify() => Changed?.Invoke();
}
