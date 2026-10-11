using System.Diagnostics;
using Microsoft.VisualBasic.FileIO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StoryForge.Core;
using StoryForge.Core.Graph;
using StoryForge.Core.Pipeline;
using StoryForge.Core.Schemas;

namespace StoryForge.Web.GraphEditor;

// Scoped per user circuit (registered AddScoped in Program.cs) — one browser tab, one loaded graph.
// 流程圖的讀寫：StoryForge 自己的存檔是「藍圖」（blueprint.json，內含目前的流程圖 graph 與上次和 Unity 同步時的
// baseGraph），格式和 Unity 的 PlayscriptProcessor.json 完全相同。存檔只寫藍圖；「從Unity匯入」用 Unity 的版本覆蓋藍圖；
// 「匯出至Unity」以 baseGraph 為基準三方合併後寫入 Unity 的檔案。這個類別只讀寫檔案，不執行任何 git 操作。
public sealed class GraphEditorState
{
    // Only a 劇本 node represents a story beat — 劇本條件/公用開關/公用變數/AND/OR/... logic nodes are
    // condition sources, never narrative content, so they never get a NodeContentStore file. Sourced from
    // NodeContentStore itself so Core (GraphDocumentModel's deletion cleanup) and this class agree.
    private const string PlayscriptProcessNodeTypeName = NodeContentStore.PlayscriptProcessNodeTypeName;

    private const string BlueprintFileName = "blueprint.json";
    private const string BackupFolderName = "備份";

    private readonly AppSettings _appSettings;
    private string? _blueprintPath;
    private string? _unityGraphPath;
    private GraphDocumentModel? _model;

    // 目前畫布上每個群組顯示的範圍（載入時依成員重新計算，存檔時更新）。畫布回傳的範圍和它相同 = 使用者沒有動到
    // 這個群組，存檔時保留檔案裡原本的範圍，避免 Unity 的舊範圍被改寫而產生差異。
    private readonly Dictionary<string, (float X, float Y, float Width, float Height)> _canvasGroupBounds = new();

    // 上次從 Unity 匯入或匯出至 Unity 時的 Unity 版本
    private CanonicalGraphDocument? _baseGraph;

    // 匯出確認期間的狀態：確認時 Unity 的檔案必須和準備時相同，否則要重新準備
    private string? _pendingExportUnityText;

    // 匯入／匯出後會重新載入整個流程圖（LoadGraph 會覆寫狀態列），這段訊息接在載入結果前面
    private string? _statusPrefix;

    public GraphEditorState(AppSettings appSettings)
    {
        _appSettings = appSettings;
    }

    public string? StatusMessage { get; private set; }

    // 最近一次存檔（存檔、只存模型、匯入、匯出）的結果，給存檔完成的彈窗與提示音使用
    public SaveReport? LastSaveReport { get; private set; }

    // 已存檔的藍圖和上次同步的 Unity 版本不同（不含畫布上還沒存檔的修改）
    public bool HasUnexportedChanges { get; private set; }

    // Unity 的檔案在上次匯入／匯出後被改過（載入、存檔、匯入、匯出時檢查）
    public bool UnityHasNewVersion { get; private set; }

    // 第一次 LoadGraph 結束（成功或失敗都算）時完成。劇本管線狀態的第一次重新整理會等它：
    // 一來它需要流程圖的劇本名稱，二來兩邊同時讀大檔會互搶 CPU／GC，讓流程圖開很久。
    private readonly TaskCompletionSource _firstLoadCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task FirstLoadCompleted => _firstLoadCompleted.Task;

    public GraphEditorPayload LoadGraph()
    {
        var prefix = _statusPrefix;
        _statusPrefix = null;
        try
        {
            DiagnosticsLog.Write(
                $"LoadGraph: _appSettings.NodeFrozenTextSize={_appSettings.NodeFrozenTextSize}, " +
                $"NodeMaxTextSize={_appSettings.NodeMaxTextSize}, instance={_appSettings.GetHashCode()}");

            var projectRoot = ProjectPaths.ResolveUnityProjectRoot();
            var assetsRoot = Path.Combine(projectRoot, "Assets");
            var schemaJsonPath = Path.Combine(assetsRoot, "06.Definition", "PlayscriptOfflineExport",
                "ProcessNodeSchema.json");
            if (!File.Exists(schemaJsonPath))
                throw new FileNotFoundException("找不到節點 Schema，請先在 Unity 按「編輯器/劇本/匯出劇本節點Schema」。", schemaJsonPath);

            var schemaDocument = DocumentLoader.LoadNodeSchemaDocument(schemaJsonPath);

            Directory.CreateDirectory(_appSettings.ExternalDataFolder);
            NodeFileIndex.Rebuild(_appSettings.ExternalDataFolder);
            _blueprintPath = Path.Combine(_appSettings.ExternalDataFolder, BlueprintFileName);
            _unityGraphPath = Path.Combine(assetsRoot, "06.Definition", "PlayscriptGraphJsonDefinition",
                "PlayscriptProcessor.json");

            if (File.Exists(_blueprintPath))
            {
                var blueprint = CanonicalGraphJson.ParseBlueprint(File.ReadAllText(_blueprintPath));
                _baseGraph = blueprint.BaseGraph;
                _model = GraphDocumentModel.LoadCanonical(blueprint.Graph, blueprint.BaseGraph, schemaDocument,
                    _appSettings);
                StatusMessage = $"已載入藍圖：{_model.Nodes.Count} 節點、{_model.Edges.Count} 連線、{_model.Groups.Count} 群組";
            }
            else
            {
                // 還沒有藍圖（第一次使用）：直接從 Unity 的版本開始，第一次存檔時才會建立藍圖
                var unityGraph = ReadUnityGraph();
                _baseGraph = unityGraph;
                _model = GraphDocumentModel.LoadCanonical(unityGraph, unityGraph, schemaDocument, _appSettings);
                StatusMessage = $"尚無藍圖，已從 Unity 載入：{_model.Nodes.Count} 節點、{_model.Edges.Count} 連線、{_model.Groups.Count} 群組";
            }

            RefreshSyncState();
            StatusMessage = prefix + StatusMessage + DescribeSyncState() + DescribeContentFileState();
            return ToPayload();
        }
        catch (Exception e)
        {
            StatusMessage = prefix + $"載入失敗：{e.Message}";
            return new GraphEditorPayload();
        }
        finally
        {
            _firstLoadCompleted.TrySetResult();
        }
    }

    // 存檔：把畫布的內容套用到模型，只寫入藍圖，不碰 Unity 的檔案
    public void ApplyAndSave(GraphEditorExport export)
    {
        if (_model == null)
        {
            LastSaveReport = new SaveReport(SaveOutcome.Failure, "存檔失敗", new[] { "流程圖尚未載入，請先切換到流程圖分頁或按「讀取」。" });
            return;
        }

        try
        {
            ApplyExportDiff(export);

            var renameCollisions = SyncRenamedScriptFiles();
            PersistBlueprint();

            StatusMessage = $"已存檔：{_model.Nodes.Count} 節點、{_model.Edges.Count} 連線、{_model.Groups.Count} 群組" +
                            DescribeSyncState();
            AppendRenameCollisionWarning(renameCollisions);
            LastSaveReport = BuildSaveReport("已存檔", renameCollisions);
        }
        catch (Exception e)
        {
            StatusMessage = $"存檔失敗：{e.Message}";
            LastSaveReport = new SaveReport(SaveOutcome.Failure, "存檔失敗", new[] { e.Message });
        }
    }

    // 從Unity匯入的確認內容：藍圖裡會被覆蓋掉、還沒匯出的修改
    public ImportFromUnityPreview PrepareImportFromUnity()
    {
        if (_model == null || _baseGraph == null)
            return new ImportFromUnityPreview { Error = "流程圖尚未載入。" };

        try
        {
            var unityGraph = ReadUnityGraph();
            var current = _model.BuildCanonicalDocument();
            return new ImportFromUnityPreview
            {
                LostChanges = CanonicalGraphJson.DescribeChanges(_baseGraph, current, _model.Schema).ToList(),
                AlreadyUpToDate = CanonicalGraphJson.Equivalent(unityGraph, current, _model.Schema),
            };
        }
        catch (Exception e)
        {
            return new ImportFromUnityPreview { Error = e.Message };
        }
    }

    // 用 Unity 的版本覆蓋藍圖。覆蓋前若藍圖和 Unity 不同，先把藍圖備份起來。呼叫端之後要重新載入流程圖。
    public void ImportFromUnity()
    {
        if (_model == null || _blueprintPath == null)
        {
            LastSaveReport = new SaveReport(SaveOutcome.Failure, "從Unity匯入失敗", new[] { "流程圖尚未載入。" });
            return;
        }

        try
        {
            var unityGraph = ReadUnityGraph();
            var details = new List<string>();
            if (File.Exists(_blueprintPath) &&
                !CanonicalGraphJson.Equivalent(_model.BuildCanonicalDocument(), unityGraph, _model.Schema))
            {
                var backupPath = Backup(_blueprintPath, "blueprint");
                details.Add($"原本的藍圖已備份：{backupPath}");
            }

            WriteBlueprint(unityGraph, unityGraph);
            details.Insert(0, $"{unityGraph.Nodes.Count} 節點、{unityGraph.Edges.Count} 連線、{unityGraph.Groups.Count} 群組");
            _statusPrefix = "已從Unity匯入；";
            LastSaveReport = new SaveReport(SaveOutcome.Success, "已從Unity匯入", details);
        }
        catch (Exception e)
        {
            StatusMessage = $"從Unity匯入失敗：{e.Message}";
            LastSaveReport = new SaveReport(SaveOutcome.Failure, "從Unity匯入失敗", new[] { e.Message });
        }
    }

    // 匯出至Unity 第一步：先存檔（套用畫布並寫入藍圖），再和 Unity 目前的檔案三方合併，回傳要給使用者確認的內容
    public ExportToUnityPlan PrepareExportToUnity(GraphEditorExport export)
    {
        _pendingExportUnityText = null;
        if (_model == null || _baseGraph == null || _unityGraphPath == null)
            return new ExportToUnityPlan { Error = "流程圖尚未載入。" };

        try
        {
            ApplyExportDiff(export);
            SyncRenamedScriptFiles();
            PersistBlueprint();

            var unityText = File.ReadAllText(_unityGraphPath);
            var unityGraph = CanonicalGraphJson.Parse(unityText);
            var local = _model.BuildCanonicalDocument();
            var schema = _model.Schema;
            var plan = new ExportToUnityPlan
            {
                RemoteChanges = CanonicalGraphJson.DescribeChanges(_baseGraph, unityGraph, schema).ToList(),
                LocalChanges = CanonicalGraphJson.DescribeChanges(_baseGraph, local, schema).ToList(),
            };

            var merge = CanonicalGraphJson.Merge(_baseGraph, local, unityGraph, schema);
            plan.AutoResolvedCount = merge.AutoResolved.Select(c => c.EntityKey).Distinct().Count();
            plan.Conflicts = merge.Conflicts
                .GroupBy(c => c.EntityKey)
                .Select(g => DescribeConflict(g.Key, g.ToList(), _baseGraph, local, unityGraph))
                .ToList();

            if (merge.Graph != null)
            {
                plan.NothingToExport = CanonicalGraphJson.Equivalent(merge.Graph, unityGraph, schema);
                plan.ValidationErrors = CanonicalGraphJson.Validate(merge.Graph, schema).Errors;
            }

            _pendingExportUnityText = unityText;
            return plan;
        }
        catch (Exception e)
        {
            return new ExportToUnityPlan { Error = e.Message };
        }
    }

    // 匯出至Unity 第二步：照使用者對每個衝突的選擇合併，備份 Unity 原檔後寫入。成功後藍圖的 graph 與 baseGraph
    // 都變成寫入的版本（包含 Unity 那邊的修改），呼叫端要重新載入流程圖。
    public void ExportToUnity(IReadOnlyDictionary<string, GraphMergeSide> resolutions)
    {
        const string failTitle = "匯出至Unity失敗";
        if (_model == null || _baseGraph == null || _unityGraphPath == null || _pendingExportUnityText == null)
        {
            LastSaveReport = new SaveReport(SaveOutcome.Failure, failTitle, new[] { "請重新按「匯出至Unity」。" });
            return;
        }

        try
        {
            var unityText = File.ReadAllText(_unityGraphPath);
            if (!string.Equals(unityText, _pendingExportUnityText, StringComparison.Ordinal))
            {
                LastSaveReport = new SaveReport(SaveOutcome.Failure, failTitle,
                    new[] { "確認期間 Unity 的流程圖檔案又被修改了，這次沒有寫入。請重新按「匯出至Unity」。" });
                return;
            }

            var schema = _model.Schema;
            var unityGraph = CanonicalGraphJson.Parse(unityText);
            var merge = CanonicalGraphJson.Merge(_baseGraph, _model.BuildCanonicalDocument(), unityGraph, schema,
                resolutions);
            if (!merge.Succeeded)
            {
                LastSaveReport = new SaveReport(SaveOutcome.Failure, failTitle,
                    new[] { $"還有 {merge.Conflicts.Select(c => c.EntityKey).Distinct().Count()} 個衝突沒有選擇。" });
                return;
            }

            var validation = CanonicalGraphJson.Validate(merge.Graph!, schema);
            if (!validation.IsValid)
            {
                LastSaveReport = new SaveReport(SaveOutcome.Failure, failTitle,
                    new[] { "合併結果無法通過檢查，這次沒有寫入：" }.Concat(validation.Errors.Take(20)).ToList());
                return;
            }

            var details = new List<string>
            {
                $"{merge.Graph!.Nodes.Count} 節點、{merge.Graph.Edges.Count} 連線、{merge.Graph.Groups.Count} 群組",
                $"寫入：{_unityGraphPath}",
            };

            var mergedText = CanonicalGraphJson.Serialize(merge.Graph, schema);
            if (string.Equals(mergedText, unityText, StringComparison.Ordinal))
            {
                details.Add("Unity 的檔案已經是這個版本，沒有變更。");
            }
            else
            {
                details.Add($"Unity 原檔已備份：{Backup(_unityGraphPath, "PlayscriptProcessor")}");
                CanonicalGraphJson.WriteAtomic(_unityGraphPath, mergedText);
            }

            WriteBlueprint(merge.Graph, merge.Graph);
            _pendingExportUnityText = null;
            _statusPrefix = "已匯出至Unity；";
            LastSaveReport = new SaveReport(SaveOutcome.Success, "已匯出至Unity", details);
        }
        catch (Exception e)
        {
            StatusMessage = $"匯出至Unity失敗：{e.Message}";
            LastSaveReport = new SaveReport(SaveOutcome.Failure, failTitle, new[] { e.Message });
        }
    }

    public void CancelExportToUnity() => _pendingExportUnityText = null;

    private CanonicalGraphDocument ReadUnityGraph()
    {
        if (_unityGraphPath == null || !File.Exists(_unityGraphPath))
            throw new FileNotFoundException("找不到 Unity 的流程圖 JSON。", _unityGraphPath);

        return CanonicalGraphJson.Parse(File.ReadAllText(_unityGraphPath));
    }

    // 把檔案複製到 AI劇本\備份\，檔名加上時間；回傳備份路徑
    private string Backup(string sourcePath, string name)
    {
        var folder = Path.Combine(_appSettings.ExternalDataFolder, BackupFolderName);
        Directory.CreateDirectory(folder);
        var backupPath = Path.Combine(folder, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.Copy(sourcePath, backupPath, true);
        return backupPath;
    }

    // 載入、存檔時更新「藍圖有未匯出的修改」與「Unity 有新版本」。
    // 每次存檔都會跑：基準版只在換掉 _baseGraph 時重新正規化，Unity 版只在檔案的修改時間／大小變了才重讀，
    // 目前版本直接用 MarkClean 剛算好的 JSON，避免一次存檔做好幾次整張圖的序列化。
    private void RefreshSyncState()
    {
        if (_model?.LastCleanJson is not { } savedJson || _baseGraph == null)
            return;

        var baseJson = GetBaseGraphJson();
        HasUnexportedChanges = !string.Equals(savedJson, baseJson, StringComparison.Ordinal);
        try
        {
            UnityHasNewVersion = !string.Equals(GetUnityGraphJson(), baseJson, StringComparison.Ordinal);
        }
        catch
        {
            UnityHasNewVersion = false;
        }
    }

    // 正規化依 schema 的欄位順序，所以快取也要跟著 schema（每次 LoadGraph 都是新的物件）
    private CanonicalGraphDocument? _baseGraphJsonSource;
    private ProcessNodeSchemaDocument? _baseGraphJsonSchema;
    private string? _baseGraphJson;

    private string GetBaseGraphJson()
    {
        var schema = _model?.Schema;
        if (!ReferenceEquals(_baseGraphJsonSource, _baseGraph) || !ReferenceEquals(_baseGraphJsonSchema, schema)
            || _baseGraphJson == null)
        {
            _baseGraphJson = CanonicalGraphJson.Serialize(_baseGraph!, schema);
            _baseGraphJsonSource = _baseGraph;
            _baseGraphJsonSchema = schema;
        }
        return _baseGraphJson;
    }

    private (string Path, DateTime Modified, long Length, ProcessNodeSchemaDocument? Schema)? _unityGraphJsonKey;
    private string? _unityGraphJson;

    private string GetUnityGraphJson()
    {
        var info = new FileInfo(_unityGraphPath ?? string.Empty);
        if (!info.Exists)
            throw new FileNotFoundException("找不到 Unity 的流程圖 JSON。", _unityGraphPath);

        var key = (info.FullName, info.LastWriteTimeUtc, info.Length, _model?.Schema);
        if (_unityGraphJsonKey is not { } cached || cached.Path != key.FullName || cached.Modified != key.LastWriteTimeUtc
            || cached.Length != key.Length || !ReferenceEquals(cached.Schema, key.Schema) || _unityGraphJson == null)
        {
            _unityGraphJson = CanonicalGraphJson.Serialize(ReadUnityGraph(), _model?.Schema);
            _unityGraphJsonKey = key;
        }
        return _unityGraphJson;
    }

    private string DescribeSyncState()
    {
        var parts = new List<string>();
        if (HasUnexportedChanges)
            parts.Add("藍圖有未匯出的修改");
        if (UnityHasNewVersion)
            parts.Add("Unity 有新版本，可按「從Unity匯入」");
        return parts.Count == 0 ? string.Empty : "；" + string.Join("；", parts);
    }

    private void PersistBlueprint()
    {
        if (_model == null || _baseGraph == null)
            return;

        var graph = _model.BuildCanonicalDocument();
        WriteBlueprint(graph, _baseGraph);
        _model.MarkClean();
        RefreshSyncState();
    }

    private void WriteBlueprint(CanonicalGraphDocument graph, CanonicalGraphDocument baseGraph)
    {
        var blueprint = new GraphBlueprintDocument { Graph = graph, BaseGraph = baseGraph };
        CanonicalGraphJson.WriteAtomic(_blueprintPath!, CanonicalGraphJson.SerializeBlueprint(blueprint, _model?.Schema));
        _baseGraph = baseGraph;
    }

    // 衝突項目給使用者看的名稱與雙方內容
    private ExportConflictDto DescribeConflict(string entityKey, List<GraphMergeConflict> conflicts,
        CanonicalGraphDocument baseGraph, CanonicalGraphDocument local, CanonicalGraphDocument remote)
    {
        var dto = new ExportConflictDto { EntityKey = entityKey, Label = DescribeEntity(entityKey, baseGraph, local, remote) };
        var colorAdded = false;
        foreach (var conflict in conflicts)
        {
            var field = conflict.Path.Length > entityKey.Length ? conflict.Path[(entityKey.Length + 1)..] : string.Empty;

            // 群組顏色的 r/g/b/a 合成一列，用色碼顯示
            if (field.StartsWith("color", StringComparison.Ordinal) && entityKey.StartsWith("$.groups[", StringComparison.Ordinal))
            {
                if (colorAdded)
                    continue;

                colorAdded = true;
                var id = entityKey["$.groups[".Length..^1];
                dto.Fields.Add(new ExportConflictFieldDto
                {
                    Field = "顏色",
                    Unity = DescribeGroupColor(remote, id),
                    StoryForge = DescribeGroupColor(local, id),
                });
                continue;
            }

            dto.Fields.Add(new ExportConflictFieldDto
            {
                Field = DescribeField(field),
                Unity = DescribeValue(conflict.Remote),
                StoryForge = DescribeValue(conflict.Local),
            });
        }

        return dto;
    }

    private static string DescribeField(string field) => field switch
    {
        "" => "整個項目",
        "title" => "標題",
        "nodes" => "成員",
        "content" => "內容",
        _ when field.StartsWith("data.", StringComparison.Ordinal) => field["data.".Length..],
        _ => field,
    };

    private static string DescribeGroupColor(CanonicalGraphDocument document, string groupId)
    {
        var group = document.Groups.FirstOrDefault(g => g.Id == groupId);
        return group == null ? "（刪除）" : ToHexColor(group.Color.R, group.Color.G, group.Color.B);
    }

    private string DescribeEntity(string entityKey, params CanonicalGraphDocument[] documents)
    {
        var open = entityKey.IndexOf('[');
        var id = open >= 0 ? entityKey[(open + 1)..^1] : entityKey;
        if (entityKey.StartsWith("$.nodes[", StringComparison.Ordinal))
            return "節點 " + DescribeNode(id, documents);
        if (entityKey.StartsWith("$.groups[", StringComparison.Ordinal))
            return "群組 " + (documents.SelectMany(d => d.Groups).FirstOrDefault(g => g.Id == id)?.Title ?? id);
        if (entityKey.StartsWith("$.stickyNotes[", StringComparison.Ordinal))
            return "便利貼 " + (documents.SelectMany(d => d.StickyNotes).FirstOrDefault(n => n.Id == id)?.Title ?? id);
        if (entityKey.StartsWith("$.edges[", StringComparison.Ordinal))
        {
            var arrow = id.IndexOf("->", StringComparison.Ordinal);
            var from = arrow > 0 ? id[..id.IndexOf('.')] : id;
            var to = arrow > 0 ? id[(arrow + 2)..id.IndexOf('.', arrow)] : string.Empty;
            return $"連線 {DescribeNode(from, documents)} → {DescribeNode(to, documents)}";
        }

        return entityKey;
    }

    private string DescribeNode(string id, CanonicalGraphDocument[] documents)
    {
        var node = documents.SelectMany(d => d.Nodes).FirstOrDefault(n => n.Id == id);
        if (node == null)
            return id;

        var identity = node.Data.Value<string>("playscriptId") ?? node.Data.Value<string>("flagId");
        if (!string.IsNullOrEmpty(identity))
            return identity;

        var schema = _model?.GetSchema(node.Type);
        return string.IsNullOrEmpty(schema?.DisplayName) ? node.Type : schema.DisplayName;
    }

    private static string DescribeValue(JToken? token) => token switch
    {
        null => "（刪除）",
        JValue value => value.Value?.ToString() ?? "（空）",
        _ => "（修改）",
    };

    // The node/edge/group diff itself, shared by ApplyAndSave and PrepareExportToUnity — everything downstream of
    // "apply the canvas's export onto the live model" (persisting, status messages, snapshot export) is
    // each caller's own concern.
    private void ApplyExportDiff(GraphEditorExport export)
    {
        // New nodes first, before positions/edges: an edge exported alongside its brand-new endpoint
        // node needs that node to already exist in the model.
        foreach (var newNode in export.NewNodes)
        {
            if (_model!.FindNode(newNode.Guid) != null)
                continue;

            var schema = _model.GetSchema(newNode.TypeName);
            if (schema != null)
                _model.AddNode(schema, newNode.X, newNode.Y, newNode.Guid);
        }

        // A node the canvas no longer has (Delete key, or LiteGraph's own node-menu Remove) — mark it
        // and its edges for deletion the same way MarkNodeForDeletion always has; the edge-diff loop
        // below would eventually detach its edges too, but doing it here also gets the node itself.
        foreach (var deletedGuid in export.DeletedNodeGuids)
            _model!.MarkNodeForDeletion(deletedGuid);

        foreach (var position in export.NodePositions)
        {
            var node = _model!.FindNode(position.Guid);
            if (node != null)
            {
                node.X = position.X;
                node.Y = position.Y;
            }
        }

        var exportedKeys = new HashSet<string>(
            export.Edges.Select(e => $"{e.FromNodeGuid}.{e.FromPort}->{e.ToNodeGuid}.{e.ToPort}"));
        var currentKeys = new HashSet<string>(
            _model!.Edges.Where(e => !e.IsMarkedForDeletion)
                .Select(e => $"{e.FromNodeGuid}.{e.FromPort}->{e.ToNodeGuid}.{e.ToPort}"));

        foreach (var edge in _model.Edges.Where(e => !e.IsMarkedForDeletion).ToList())
        {
            var key = $"{edge.FromNodeGuid}.{edge.FromPort}->{edge.ToNodeGuid}.{edge.ToPort}";
            if (!exportedKeys.Contains(key))
                _model.DetachEdge(edge);
        }

        foreach (var edge in export.Edges)
        {
            var key = $"{edge.FromNodeGuid}.{edge.FromPort}->{edge.ToNodeGuid}.{edge.ToPort}";
            if (!currentKeys.Contains(key))
                _model.AddEdge(edge.FromNodeGuid, edge.FromPort, edge.ToNodeGuid, edge.ToPort);
        }

        // The canvas already recomputes each group's membership + tight bounding box live as nodes are
        // dragged in/out (graph-editor.js's recomputeGroupBounds, mirroring RecomputeGroupBounds below
        // exactly), so this just trusts that already-correct result rather than re-deriving it from a
        // position diff.
        //
        // 畫布上的每個群組都有 ClientId（新增群組時就會產生），所以：匯出裡沒有的群組 = 在畫布上刪除了；
        // 模型裡找不到的 ClientId = 這次新增的群組。標題與顏色由側邊欄修改，也一併寫回。
        var exportedGroupIds = new HashSet<string>(export.Groups.Select(g => g.ClientId), StringComparer.Ordinal);
        foreach (var removedGroup in _model.Groups
                     .Where(g => !g.IsMarkedForDeletion && !exportedGroupIds.Contains(g.ClientId)).ToList())
            _model.MarkGroupForDeletion(removedGroup.ClientId);

        foreach (var exportedGroup in export.Groups)
        {
            if (string.IsNullOrEmpty(exportedGroup.ClientId))
                continue;

            var group = _model.FindGroup(exportedGroup.ClientId)
                        ?? _model.AddGroup(exportedGroup.Title ?? string.Empty, exportedGroup.X, exportedGroup.Y, exportedGroup.ClientId);

            group.Title = exportedGroup.Title ?? string.Empty;
            if (!string.Equals(exportedGroup.Color, ToHexColor(group.ColorR, group.ColorG, group.ColorB),
                    StringComparison.OrdinalIgnoreCase) &&
                TryParseHexColor(exportedGroup.Color, out var r, out var g, out var b))
            {
                group.ColorR = r;
                group.ColorG = g;
                group.ColorB = b;
            }

            group.MemberNodeGuids.Clear();
            foreach (var guid in exportedGroup.MemberNodeGuids)
                group.MemberNodeGuids.Add(guid);

            var exportedBounds = (exportedGroup.X, exportedGroup.Y, exportedGroup.Width, exportedGroup.Height);
            if (!_canvasGroupBounds.TryGetValue(group.ClientId, out var shownBounds) ||
                !SameBounds(shownBounds, exportedBounds))
                (group.X, group.Y, group.Width, group.Height) = exportedBounds;

            _canvasGroupBounds[group.ClientId] = exportedBounds;
        }
    }

    // Persists the live model to blueprint.json as-is, with no canvas export to reconcile — used after a
    // field-only mutation (UpdateNodeField, e.g. from an AI-workspace import) that never goes through
    // ApplyAndSave's node/edge/group diffing, so those edits aren't silently lost until the user happens
    // to open the 流程圖 tab and press 存檔 themselves.
    public void SaveModelOnly()
    {
        try
        {
            var renameCollisions = SyncRenamedScriptFiles();
            PersistBlueprint();
            StatusMessage = $"已存檔：{_model?.Nodes.Count} 節點、{_model?.Edges.Count} 連線、{_model?.Groups.Count} 群組" +
                            DescribeSyncState();
            AppendRenameCollisionWarning(renameCollisions);
            LastSaveReport = BuildSaveReport("已存檔", renameCollisions);
        }
        catch (Exception e)
        {
            StatusMessage = $"存檔失敗：{e.Message}";
            LastSaveReport = new SaveReport(SaveOutcome.Failure, "存檔失敗", new[] { e.Message });
        }
    }

    private void AppendRenameCollisionWarning(List<RenameCollision> renameCollisions)
    {
        if (renameCollisions.Count > 0)
            StatusMessage += $"；{renameCollisions.Count} 個劇本檔改名衝突（新名稱已有其他檔案，請在存檔結果中選擇處理方式）：" +
                             string.Join("、", renameCollisions.Select(c => c.Title));
    }

    // 載入時附在狀態列的內容檔狀態：檔名待同步（節點在別處改了名）、孤兒內容檔（流程圖上已無對應節點）、
    // 重複 Guid 的檔案。孤兒檔不會自動刪除，避免切換分支或放棄的刪除讓劇情內容永久遺失。
    private string DescribeContentFileState()
    {
        var summary = GetContentFileState().Summary;
        return summary.Count == 0 ? string.Empty : "；" + string.Join("；", summary);
    }

    private sealed record ContentFileState(
        List<string> Summary,
        List<string> Details,
        List<OrphanContentFiles> Orphans);

    // Summary 是給狀態列的一句話摘要；Details 另外列出孤兒檔與重複檔的相對路徑，給存檔彈窗顯示。
    // rebuildIndex 用於真正清除前的最後確認，避免信任存檔當下留下的舊索引。
    private ContentFileState GetContentFileState(bool rebuildIndex = false)
    {
        var summary = new List<string>();
        var details = new List<string>();
        var orphans = new List<OrphanContentFiles>();
        if (_model == null)
            return new ContentFileState(summary, details, orphans);

        var dataFolder = _appSettings.ExternalDataFolder;
        if (rebuildIndex)
            NodeFileIndex.Rebuild(dataFolder);

        var index = NodeFileIndex.GetAll(dataFolder);
        var duplicateGuids = NodeFileIndex.DuplicateGuids;
        var liveNodes = _model.Nodes
            .Where(n => n.TypeName == PlayscriptProcessNodeTypeName && !n.IsMarkedForDeletion)
            .ToList();
        var liveGuids = new HashSet<string>(liveNodes.Select(n => n.Guid), StringComparer.Ordinal);

        var renamePlan = PlanRenames();
        foreach (var pair in index
                     .Where(pair => !liveGuids.Contains(pair.Key) && !duplicateGuids.Contains(pair.Key))
                     .OrderBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase))
        {
            var relativePaths = new List<string>
            {
                Path.GetRelativePath(dataFolder, pair.Value + ".json"),
            };
            if (File.Exists(pair.Value + ".md"))
                relativePaths.Add(Path.GetRelativePath(dataFolder, pair.Value + ".md"));

            orphans.Add(new OrphanContentFiles(pair.Key, pair.Value, relativePaths));
        }

        var duplicatePaths = NodeFileIndex.Duplicates
            .Select(path => Path.GetRelativePath(dataFolder, path))
            .ToList();

        if (renamePlan.Ready.Count > 0)
            summary.Add($"{renamePlan.Ready.Count} 個劇本檔檔名待同步（存檔時自動更新）");
        if (renamePlan.Collisions.Count > 0)
            summary.Add($"{renamePlan.Collisions.Count} 個劇本檔改名衝突（新名稱已有其他檔案，存檔後選擇處理方式）");
        if (orphans.Count > 0)
        {
            summary.Add($"{orphans.Count} 個孤兒內容檔（流程圖上已無對應節點）");
            details.AddRange(orphans.SelectMany(orphan => orphan.RelativePaths)
                .Select(path => $"孤兒內容檔：{path}"));
        }

        if (duplicatePaths.Count > 0)
        {
            summary.Add($"{duplicatePaths.Count} 個內容檔與其他檔案的 Guid 重複");
            details.AddRange(duplicatePaths.Select(path => $"Guid 重複：{path}"));
        }

        return new ContentFileState(summary, details, orphans);
    }

    // 存檔成功後的紀錄：數量、存檔位置、內容檔狀態；有改名衝突時為「有警告」，衝突本身由彈窗另外列出讓使用者選擇
    private SaveReport BuildSaveReport(string title, List<RenameCollision> renameCollisions,
        IEnumerable<string>? extraLines = null)
    {
        var details = new List<string>
        {
            $"{_model!.Nodes.Count} 節點、{_model.Edges.Count} 連線、{_model.Groups.Count} 群組",
            $"存檔位置：{_blueprintPath}",
        };
        if (extraLines != null)
            details.AddRange(extraLines);

        var fileState = GetContentFileState();
        details.AddRange(fileState.Summary);
        details.AddRange(fileState.Details);

        var hasWarnings = renameCollisions.Count > 0 || fileState.Summary.Count > 0;
        return new SaveReport(hasWarnings ? SaveOutcome.Warning : SaveOutcome.Success,
            hasWarnings ? title + "（有警告）" : title, details, fileState.Orphans, renameCollisions);
    }

    // 存檔結果彈窗確認後才會進到這裡。重新掃描，只處理和使用者看到時完全相同（同檔案、同大小與修改時間）的衝突；
    // 被取代的檔案一律移到 Windows 資源回收筒，不直接刪除。
    public SaveReport ResolveRenameCollisions(IReadOnlyList<RenameCollisionDecision> decisions)
    {
        if (_model == null)
            return new SaveReport(SaveOutcome.Failure, "改名衝突處理失敗", new[] { "流程圖尚未載入。" });

        var dataFolder = _appSettings.ExternalDataFolder;
        NodeFileIndex.Rebuild(dataFolder);
        var current = PlanRenames().Collisions.ToDictionary(c => c.Guid, StringComparer.Ordinal);
        var lines = new List<string>();

        foreach (var decision in decisions.Where(d => d.Choice != RenameCollisionChoice.Skip))
        {
            var expected = decision.Collision;
            if (!current.TryGetValue(expected.Guid, out var collision) || !collision.SameFilesAs(expected))
            {
                lines.Add($"略過（檔案在確認前已改變，請重新存檔後再選擇）：{expected.Title}");
                continue;
            }

            if (!collision.CanResolve)
            {
                lines.Add($"略過（新名稱屬於另一個節點）：{collision.Title}");
                continue;
            }

            try
            {
                if (decision.Choice == RenameCollisionChoice.Overwrite)
                {
                    RecycleContentFile(collision.TargetBase + ".json");
                    RecycleContentFile(collision.TargetBase + ".md");
                    RenameScriptFiles(dataFolder, collision.CurrentBase, collision.NewName, collision.Guid);
                    lines.Add($"已用目前的內容檔覆蓋：{collision.Title}");
                }
                else
                {
                    RecycleContentFile(collision.TargetBase + ".json");
                    RecycleContentFile(collision.CurrentBase + ".md");
                    RenameScriptFiles(dataFolder, collision.CurrentBase, collision.NewName, collision.Guid);
                    RetitleScript(collision.TargetBase + ".md", collision.NewName);
                    lines.Add($"已採用新位置的劇本：{collision.Title}");
                }
            }
            catch (Exception e)
            {
                lines.Add($"處理失敗：{collision.Title}（{e.Message}）");
            }
        }

        if (lines.Count == 0)
            lines.Add("沒有選擇要處理的改名衝突。");

        NodeFileIndex.Rebuild(dataFolder);
        return BuildSaveReport("改名衝突處理完成", PlanRenames().Collisions, lines);
    }

    // 只允許把 Nodes 資料夾內的內容檔移到資源回收筒；檔案不存在時什麼都不做
    private void RecycleContentFile(string path)
    {
        if (!File.Exists(path))
            return;

        var contentRoot = Path.GetFullPath(Path.Combine(_appSettings.ExternalDataFolder, "Nodes"))
                          + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"拒絕處理 Nodes 資料夾外的路徑：{fullPath}");

        FileSystem.DeleteFile(fullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }

    // 存檔結果 modal 的第二次確認才會進到這裡。重新掃描並逐項比對後，只有仍然是孤兒、
    // 且沒有重複 Guid 的 .json 與同基底 .md 才會送進 Windows 資源回收筒。
    public SaveReport ClearOrphanContentFiles(IReadOnlyList<OrphanContentFiles> expectedOrphans)
    {
        if (_model == null)
            return new SaveReport(SaveOutcome.Failure, "清除孤兒失敗", new[] { "流程圖尚未載入。" });

        var currentState = GetContentFileState(rebuildIndex: true);
        if (!SameOrphanSet(expectedOrphans, currentState.Orphans))
        {
            var changedDetails = new List<string>
            {
                "孤兒清單在確認前已改變，這次沒有移動任何檔案。請重新檢查後再執行。",
            };
            changedDetails.AddRange(currentState.Details);
            return new SaveReport(SaveOutcome.Warning, "孤兒清單已改變", changedDetails,
                currentState.Orphans);
        }

        var moved = new List<string>();
        var skipped = new List<string>();
        var failures = new List<string>();
        var contentRoot = Path.GetFullPath(Path.Combine(_appSettings.ExternalDataFolder, "Nodes"))
                          + Path.DirectorySeparatorChar;

        foreach (var orphan in currentState.Orphans)
        {
            foreach (var relativePath in orphan.RelativePaths)
            {
                var path = Path.Combine(_appSettings.ExternalDataFolder, relativePath);
                if (!File.Exists(path))
                {
                    skipped.Add($"略過（檔案已不存在）：{relativePath}");
                    continue;
                }

                var fullPath = Path.GetFullPath(path);
                if (!fullPath.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"拒絕處理 Nodes 資料夾外的路徑：{fullPath}");
                    continue;
                }

                try
                {
                    FileSystem.DeleteFile(fullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    moved.Add(Path.GetRelativePath(_appSettings.ExternalDataFolder, fullPath));
                }
                catch (Exception e)
                {
                    failures.Add($"移動失敗：{Path.GetRelativePath(_appSettings.ExternalDataFolder, fullPath)}（{e.Message}）");
                }
            }
        }

        var remainingState = GetContentFileState(rebuildIndex: true);
        var details = moved.Select(path => $"已移到資源回收筒：{path}").ToList();
        details.AddRange(skipped);
        details.AddRange(failures);
        details.AddRange(remainingState.Details);

        if (moved.Count == 0 && skipped.Count == 0 && failures.Count == 0)
            details.Insert(0, "沒有需要清除的孤兒內容檔。");

        var hasWarnings = skipped.Count > 0 || failures.Count > 0 || remainingState.Summary.Count > 0;
        return new SaveReport(hasWarnings ? SaveOutcome.Warning : SaveOutcome.Success,
            hasWarnings ? "孤兒清除完成（有警告）" : "孤兒清除完成",
            details, remainingState.Orphans, PlanRenames().Collisions);
    }

    private static bool SameOrphanSet(
        IReadOnlyList<OrphanContentFiles> expected,
        IReadOnlyList<OrphanContentFiles> current)
    {
        static string Key(OrphanContentFiles orphan) =>
            orphan.Guid + "\n" + Path.GetFullPath(orphan.BasePath) + "\n" +
            string.Join("\n", orphan.RelativePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));

        return expected.Select(Key).OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(current.Select(Key).OrderBy(key => key, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
    }

    // 內容檔已經在這個名稱應有的位置：一般是劇本名稱的路徑，劇本名稱重複時則是加上 Guid 前 8 碼的路徑
    private static bool IsAtExpectedPath(string dataFolder, string basePath, string playscriptName, string guid)
    {
        var plainBase = NodeContentStore.ResolvePlainBasePath(dataFolder, playscriptName);
        if (string.Equals(basePath, plainBase, StringComparison.OrdinalIgnoreCase))
            return true;

        var suffix = guid.Replace("-", "");
        suffix = suffix.Length > 8 ? suffix[..8] : suffix;
        return string.Equals(basePath, $"{plainBase}-{suffix}", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record RenameTarget(string Guid, string CurrentBase, string NewName);

    private sealed record RenamePlan(List<RenameTarget> Ready, List<RenameCollision> Collisions);

    // 依 GUID 索引找到每個劇本節點的內容檔，檔名和目前的劇本名稱對不上時（在 StoryForge、Unity 或切換分支時改了名）
    // 列為待改名。新名稱的位置已經有其他檔案時列為衝突：不搬也不覆蓋（對方可能仍有劇情內容），交給使用者選擇。
    private RenamePlan PlanRenames()
    {
        var plan = new RenamePlan(new List<RenameTarget>(), new List<RenameCollision>());
        if (_model == null)
            return plan;

        var dataFolder = _appSettings.ExternalDataFolder;
        foreach (var node in _model.Nodes)
        {
            if (node.TypeName != PlayscriptProcessNodeTypeName || node.IsMarkedForDeletion ||
                string.IsNullOrEmpty(node.IdentityValue))
                continue;

            var currentBase = NodeFileIndex.TryGetBasePath(dataFolder, node.Guid);
            if (currentBase == null || IsAtExpectedPath(dataFolder, currentBase, node.IdentityValue!, node.Guid))
                continue;

            var newName = node.IdentityValue!;
            var targetBase = NodeContentStore.ResolvePlainBasePath(dataFolder, newName);
            RenameCollisionKind? kind = null;

            var occupant = NodeContentStore.ReadAtPlainPath(dataFolder, newName);
            if (occupant != null && occupant.Guid != node.Guid)
            {
                kind = _model.Nodes.Any(n => n.Guid == occupant.Guid && !n.IsMarkedForDeletion)
                    ? RenameCollisionKind.TargetLiveNode
                    : RenameCollisionKind.TargetOrphan;
            }
            // 目標名稱只有 .md 劇本檔、沒有 .json 時（例如先從 Sheet 下載過劇本），同樣不覆蓋
            else if (occupant == null && File.Exists(targetBase + ".md"))
            {
                kind = RenameCollisionKind.TargetScriptOnly;
            }

            if (kind == null)
            {
                plan.Ready.Add(new RenameTarget(node.Guid, currentBase, newName));
                continue;
            }

            plan.Collisions.Add(new RenameCollision(
                node.Guid, currentBase, targetBase,
                Path.GetRelativePath(dataFolder, currentBase), Path.GetRelativePath(dataFolder, targetBase),
                newName, kind.Value,
                DescribeContentFiles(dataFolder, currentBase), DescribeContentFiles(dataFolder, targetBase)));
        }

        return plan;
    }

    // 每次存檔前執行：沒有衝突的改名直接搬檔，衝突回傳給呼叫端，由存檔結果彈窗讓使用者選擇
    private List<RenameCollision> SyncRenamedScriptFiles()
    {
        var plan = PlanRenames();
        foreach (var target in plan.Ready)
            RenameScriptFiles(_appSettings.ExternalDataFolder, target.CurrentBase, target.NewName, target.Guid);

        return plan.Collisions;
    }

    private static List<ContentFileInfo> DescribeContentFiles(string dataFolder, string basePath)
    {
        var files = new List<ContentFileInfo>();
        foreach (var extension in new[] { ".json", ".md" })
        {
            var path = basePath + extension;
            if (!File.Exists(path))
                continue;

            var info = new FileInfo(path);
            string summary;
            try
            {
                var text = File.ReadAllText(path);
                summary = extension == ".md" ? DescribeScriptText(text) : DescribeEntryText(text);
            }
            catch (Exception e)
            {
                summary = $"無法讀取（{e.Message}）";
            }

            files.Add(new ContentFileInfo(Path.GetRelativePath(dataFolder, path), info.Length,
                info.LastWriteTime, summary));
        }

        return files;
    }

    // 劇本表格中至少有一格有內容的列數（不含表頭）
    private static string DescribeScriptText(string text)
    {
        var rows = 0;
        var pastSeparator = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith('|'))
                continue;

            var cells = line.Trim('|').Split('|').Select(cell => cell.Trim()).ToList();
            if (!pastSeparator)
            {
                pastSeparator = cells.All(cell => cell.Length > 0 && cell.All(ch => ch == '-' || ch == ':'));
                continue;
            }

            if (cells.Any(cell => cell.Length > 0))
                rows++;
        }

        return rows == 0 ? "空白劇本" : $"{rows} 列有內容";
    }

    // 節點資料（備註、衝突、角色…）中有內容的欄位數
    private static string DescribeEntryText(string text)
    {
        var entry = JObject.Parse(text);
        var filled = entry.Properties().Count(property =>
            property.Name != nameof(NodeContentEntry.Guid) &&
            property.Name != nameof(NodeContentEntry.PlayscriptName) &&
            property.Value.Type == JTokenType.String &&
            !string.IsNullOrWhiteSpace(property.Value.Value<string>()));
        return filled == 0 ? "節點資料空白" : $"節點資料 {filled} 個欄位有內容";
    }

    // 把一個節點的 .json（更新劇本名稱）與 .md（只改第一行「# 標題」，其餘是 AI 工具寫的劇本內容不動）
    // 從目前的位置搬到新名稱的路徑。先寫新檔再刪舊檔，中途失敗也不會遺失內容。
    private static void RenameScriptFiles(string dataFolder, string oldBase, string newName, string guid)
    {
        var newBase = NodeContentStore.ResolvePlainBasePath(dataFolder, newName);
        var directory = Path.GetDirectoryName(newBase);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var oldJsonPath = oldBase + ".json";
        if (File.Exists(oldJsonPath))
        {
            var entry = JsonConvert.DeserializeObject<NodeContentEntry>(File.ReadAllText(oldJsonPath))
                        ?? new NodeContentEntry();
            entry.Guid = guid;
            entry.PlayscriptName = newName;
            File.WriteAllText(newBase + ".json", JsonConvert.SerializeObject(entry, Formatting.Indented));
            File.Delete(oldJsonPath);
            NodeFileIndex.Set(dataFolder, guid, newBase);
        }

        var oldMdPath = oldBase + ".md";
        if (File.Exists(oldMdPath))
        {
            File.WriteAllText(newBase + ".md", RetitleScriptText(File.ReadAllText(oldMdPath), newName));
            File.Delete(oldMdPath);
        }
    }

    private static void RetitleScript(string mdPath, string newName)
    {
        if (!File.Exists(mdPath))
            return;

        var content = File.ReadAllText(mdPath);
        var updated = RetitleScriptText(content, newName);
        if (updated != content)
            File.WriteAllText(mdPath, updated);
    }

    // 只改第一行「# 標題」，其餘是 AI 工具寫的劇本內容不動
    private static string RetitleScriptText(string content, string newName)
    {
        var newlineIndex = content.IndexOf('\n');
        var firstLine = newlineIndex >= 0 ? content[..newlineIndex] : content;
        var rest = newlineIndex >= 0 ? content[newlineIndex..] : string.Empty;
        return firstLine.TrimStart().StartsWith("# ") ? $"# {newName}{rest}" : content;
    }

    // One node queued for 建立劇本檔: either ready to sync immediately (no plain-path collision) or stuck
    // behind an orphaned file — content from some other, no-longer-live node's guid still occupying the
    // plain path — that needs the user's explicit go-ahead before ScriptFileSyncPlan.Ready's write would
    // otherwise overwrite it silently.
    public readonly record struct ScriptFileSyncItem(string Guid, string PlayscriptName);

    public sealed class ScriptFileSyncPlan
    {
        public List<ScriptFileSyncItem> Ready { get; init; } = new();
        public List<ScriptFileSyncItem> Conflicts { get; init; } = new();
    }

    // The only caller is PipelineStatusPanel's 建立劇本檔 button. It creates the separate StoryForge
    // content/template files for the selected playscript nodes; canonical Unity node data is untouched.
    //
    // playscriptId is enforced unique among live nodes at edit time (UpdateNodeField), so two selected
    // names can no longer collide with EACH OTHER — a plain-path collision found here can only be a
    // leftover file from a node that's no longer live, split into Conflicts for the caller to resolve via
    // an explicit overwrite/skip choice (PipelineStatusState's own conflict-queue UI) rather than silently
    // suffixed or silently skipped.
    public ScriptFileSyncPlan PlanNodeContentFilesSync(IReadOnlyCollection<string> selectedPlayscriptNames)
    {
        var plan = new ScriptFileSyncPlan();
        if (_model == null)
            return plan;

        var names = new HashSet<string>(selectedPlayscriptNames, StringComparer.Ordinal);

        var nodes = _model.Nodes
            .Where(n => !n.IsMarkedForDeletion && n.TypeName == PlayscriptProcessNodeTypeName &&
                        !string.IsNullOrEmpty(n.IdentityValue) && names.Contains(n.IdentityValue!))
            .ToList();

        foreach (var n in nodes)
        {
            var item = new ScriptFileSyncItem(n.Guid, n.IdentityValue!);

            var existing = NodeContentStore.ReadAtPlainPath(_appSettings.ExternalDataFolder, n.IdentityValue!);
            if (existing != null && existing.Guid != n.Guid)
                plan.Conflicts.Add(item);
            else
                plan.Ready.Add(item);
        }

        return plan;
    }

    // Writes the non-conflicting half of a ScriptFileSyncPlan. Safe to route straight through
    // NodeContentStore/NodeScriptTableStore's own "create if missing" SyncAll — PlanNodeContentFilesSync
    // has already guaranteed nothing else currently owns the plain path for any of these names.
    public int SyncNodeContentFiles(IReadOnlyCollection<ScriptFileSyncItem> ready)
    {
        NodeContentStore.SyncAll(_appSettings.ExternalDataFolder,
            ready.Select(i => (i.Guid, i.PlayscriptName)));
        NodeScriptTableStore.SyncAll(_appSettings.ExternalDataFolder,
            ready.Select(i => (i.Guid, i.PlayscriptName)));

        return ready.Count;
    }

    // Writes one Conflicts item after the user has explicitly confirmed overwriting whatever orphaned file
    // currently sits at the plain path — replaces it outright rather than routing through
    // NodeContentStore.ResolveBasePath's own suffix-on-collision fallback, since the whole point is to
    // reclaim the plain name for this node.
    public void OverwriteNodeContentFile(ScriptFileSyncItem item)
    {
        NodeContentStore.WriteAtPlainPath(_appSettings.ExternalDataFolder, new NodeContentEntry
        {
            Guid = item.Guid,
            PlayscriptName = item.PlayscriptName,
        });
        NodeScriptTableStore.WriteTemplateAtPlainPath(_appSettings.ExternalDataFolder, item.PlayscriptName);
    }

    // A node's memo/narrative-detail content, read straight from its NodeContentStore file — null for
    // anything that isn't a 劇本 node with an identity value yet (logic/condition nodes, or a brand-new
    // node whose playscriptId is still blank).
    public NodeContentEntry? GetNodeContent(string nodeGuid)
    {
        var node = _model?.FindNode(nodeGuid);
        if (node == null || node.TypeName != PlayscriptProcessNodeTypeName || string.IsNullOrEmpty(node.IdentityValue))
            return null;

        return NodeContentStore.Read(_appSettings.ExternalDataFolder, node.IdentityValue, node.Guid)
               ?? new NodeContentEntry { Guid = node.Guid, PlayscriptName = node.IdentityValue };
    }

    // Writes straight to disk immediately, like CharacterCardState's save-on-edit fields — no separate
    // 存檔 step for this content, since the file itself is already the one real copy.
    // The node's .md 劇本檔 (NodeScriptTableStore) path, only when that file already exists — null for a
    // non-劇本 node, a node with no identity value yet, or a 劇本 node whose file hasn't been created via
    // PipelineStatusPanel's 建立劇本檔 button yet. Used to gate the sidebar's 開啟劇本檔 button.
    public string? GetScriptTablePath(string nodeGuid)
    {
        var node = _model?.FindNode(nodeGuid);
        if (node == null || node.TypeName != PlayscriptProcessNodeTypeName || string.IsNullOrEmpty(node.IdentityValue))
            return null;

        var path = NodeContentStore.ResolveBasePath(_appSettings.ExternalDataFolder, node.IdentityValue, node.Guid) + ".md";
        return File.Exists(path) ? path : null;
    }

    // Opens the node's .md 劇本檔 in whatever program Windows has associated with .md files, the same way
    // double-clicking it in Explorer would. Errors (no association, file removed after the sidebar's own
    // existence check) are reported through StatusMessage like every other failure in this class rather
    // than thrown, since there's no useful recovery for the caller to do.
    public void OpenScriptTable(string nodeGuid)
    {
        var path = GetScriptTablePath(nodeGuid);
        if (path == null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            StatusMessage = $"開啟劇本檔失敗：{e.Message}";
        }
    }

    public void SetNodeMemo(string nodeGuid, string memo) => UpdateNodeContent(nodeGuid, entry => entry.Memo = memo);

    public void SetNodeConflict(string nodeGuid, string conflict) =>
        UpdateNodeContent(nodeGuid, entry => entry.Conflict = conflict);

    public void SetNodeChange(string nodeGuid, string change) =>
        UpdateNodeContent(nodeGuid, entry => entry.Change = change);

    private void UpdateNodeContent(string nodeGuid, Action<NodeContentEntry> apply)
    {
        var node = _model?.FindNode(nodeGuid);
        if (node == null || node.TypeName != PlayscriptProcessNodeTypeName || string.IsNullOrEmpty(node.IdentityValue))
            return;

        var entry = NodeContentStore.Read(_appSettings.ExternalDataFolder, node.IdentityValue, node.Guid)
                    ?? new NodeContentEntry { Guid = node.Guid, PlayscriptName = node.IdentityValue };
        apply(entry);
        NodeContentStore.Write(_appSettings.ExternalDataFolder, entry);
    }

    // Called the instant graph-editor.js creates a node via the add-node menu — without this, a
    // just-created node exists only in JS/LiteGraph's own memory until 存檔, so selecting it to edit its
    // fields would find nothing here (GetNodeFields/UpdateNodeField both read/write this live model, which
    // otherwise wouldn't know the node exists yet). Mirrors the exact same AddNode(schema, x, y, guid) call
    // ApplyAndSave's NewNodes loop makes at save time — that loop's own FindNode-not-null guard already
    // makes calling it again there for an already-created node a safe no-op.
    public void CreateNode(string guid, string typeName, float x, float y)
    {
        if (_model == null || _model.FindNode(guid) != null)
            return;

        var schema = _model.GetSchema(typeName);
        if (schema != null)
            _model.AddNode(schema, x, y, guid);
    }

    // The undo-direction counterpart to CreateNode above — called when graph-editor.js's own undo/redo
    // stack removes a node that only ever existed this session (never round-tripped through 存檔). Reuses
    // MarkNodeForDeletion rather than a bespoke removal path: it already fully deletes (not just soft-marks)
    // a node whose IsNew flag is still set, which is exactly this case, since a node that's never been
    // persisted has nothing to mark for a future deletion changelist — it should simply cease to exist.
    public void RemoveUnsavedNode(string guid) => _model?.MarkNodeForDeletion(guid);

    // The server-side half of the node right-click menu's 複製 action — registers the new node (mirroring
    // CreateNode) and copies every field, including playscriptId itself (UpdateNodeField's own uniqueness
    // rule immediately resolves the resulting collision with the source by appending "_1"/"_2"/... — see
    // MakeUniquePlayscriptId). Deliberately does NOT touch NodeContentStore/NodeScriptTableStore at all: a
    // 劇本 node's content file is only ever created by the explicit 建立劇本檔 button (PipelineStatusState),
    // never as a side effect of another action — a duplicated node starts with no memo/衝突/變化 content,
    // the user fills it in fresh. Edge duplication (both within the duplicated set and to untouched
    // neighbors) is handled entirely client-side in graph-editor.js, since links are canvas-owned state
    // this class never sees until 存檔. Returns null if the source node or its type schema can't be found.
    public NodeDuplicateResultDto? DuplicateNode(string sourceGuid, string newGuid, float x, float y)
    {
        var source = _model?.FindNode(sourceGuid);
        if (_model == null || source == null || _model.FindNode(newGuid) != null)
            return null;

        var schema = _model.GetSchema(source.TypeName);
        if (schema == null)
            return null;

        var newNode = _model.AddNode(schema, x, y, newGuid);
        foreach (var kv in source.Fields)
            UpdateNodeField(newGuid, kv.Key, kv.Value?.ToString() ?? string.Empty);

        var titleInfo = GetNodeTitleInfo(newGuid)!;

        return new NodeDuplicateResultDto
        {
            Title = titleInfo.Title,
            HasIdentityTitle = titleInfo.HasIdentityTitle,
            Fields = GetNodeFields(newGuid)?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, string>(),
        };
    }

    // The server-side half of the node right-click menu itself — a live (disk-checked) read of whatever
    // 開啟劇本檔/複製劇本名稱 need to decide their enabled state, fetched fresh each time the menu is about
    // to open rather than trusted from the initial payload (which can go stale after a sidebar edit).
    public NodeContextMenuInfoDto GetNodeContextMenuInfo(string guid) => new()
    {
        PlayscriptName = _model?.FindNode(guid)?.IdentityValue ?? string.Empty,
        HasScriptFile = GetScriptTablePath(guid) != null,
    };

    public string? GetNodeTypeName(string nodeGuid) => _model?.FindNode(nodeGuid)?.TypeName;

    // The raw live model, for a consumer (e.g. PipelineStatusState's AI-workspace export) that needs more
    // than one of the narrow projections below — null before LoadGraph() has ever run.
    public GraphDocumentModel? GetModel() => _model;

    // Reflects node identity/memo edits already round-tripped server-side (UpdateNodeField writes straight
    // into the model) and any canvas edit already applied via ApplyAndSave — but NOT a canvas-only edit
    // (drag/rewire) the user hasn't hit 存檔 for yet, since that never reaches the server until exported.
    public bool HasUnsavedChanges() => _model?.HasUnsavedChanges() ?? false;

    // Live Group titles — used by StoryOutlineState purely as soft chapter-combo suggestions (never a
    // hard requirement; any string can still be typed), mirroring the original tool's own loose coupling
    // between the two panels (StoryOutlinePanel.SetGraphModelProvider reading ProcessGraphPanel's model).
    public IEnumerable<string> GetGroupTitles() =>
        _model?.Groups.Where(g => !g.IsMarkedForDeletion && !string.IsNullOrWhiteSpace(g.Title))
            .Select(g => g.Title) ?? Enumerable.Empty<string>();

    // Every node's current identity value (playscriptId/flagId) — the live in-memory equivalent of the
    // original tool's ProcessGraphPanel.GetLivePlayscriptIds(), used by PipelineStatusState's 流程圖 column.
    public IEnumerable<string> GetPlayscriptIdentities() =>
        _model?.Nodes.Where(n => !n.IsMarkedForDeletion && DefinesPlayscript(n) &&
                                 !string.IsNullOrEmpty(n.IdentityValue))
            .Select(n => n.IdentityValue!) ?? Enumerable.Empty<string>();

    // Null when the playscript has no live graph node (listed in the Sheet index but never added to the
    // flow graph) — callers resolving a file path for it fall back to NodeContentStore's plain (non-guid)
    // path the same way PipelineStatusState's "本地劇本" column already does for a leftover file.
    public string? FindNodeGuidForPlayscript(string playscriptName) =>
        _model?.Nodes.FirstOrDefault(n => !n.IsMarkedForDeletion && DefinesPlayscript(n) &&
                                          n.IdentityValue == playscriptName)?.Guid;

    // 劇本 (playscript) and 開啟劇本 (unlock-passive) nodes each define a playscript — most of the graph's
    // _s/_t/_v functional playscripts exist only as a 開啟劇本 node. A 劇本條件 (playscript-flag) node's
    // playscriptId instead names some existing playscript whose 完成/解鎖 state it tests, so it is expected
    // to repeat another node's name: it is never made unique, never counts as the playscript being on the
    // graph, and is never the node a playscript name resolves to.
    private const string PlayscriptConditionNodeTypeName = "playscript-flag";

    private static bool DefinesPlayscript(GraphNodeVm node) => node.TypeName != PlayscriptConditionNodeTypeName;

    // Node identity/memo fields aren't part of the canvas rendering at all (LiteGraph never sees them), so
    // this itself needs no round trip through JS — it goes straight into the live model, the same one 存檔
    // already reads from. A title built from an edited identity field (e.g. playscriptId) does NOT update
    // on the canvas as a side effect of this call — the caller (FlowGraphPanel.OnFieldChanged) follows this
    // up with its own explicit GetNodeTitleInfo + storyForgeGraph.updateNodeTitle round trip so the canvas
    // stays live without every caller of UpdateNodeField having to remember to do that.
    // Returns the value actually written — usually just `value` unchanged, but for playscriptId a colliding
    // value comes back with an auto-appended "_1"/"_2".../suffix instead (see MakeUniquePlayscriptId), so
    // callers whose own UI mirrors the typed value (FlowGraphPanel's sidebar text box) can correct it back
    // to what the server actually stored rather than showing a value that silently drifted from reality.
    // Null only when nodeGuid doesn't resolve to a live node.
    public string? UpdateNodeField(string nodeGuid, string fieldName, string value)
    {
        var node = _model?.FindNode(nodeGuid);
        if (node == null)
            return null;

        if (string.IsNullOrEmpty(value))
        {
            node.Fields.Remove(fieldName);
            return value;
        }

        if (fieldName == "playscriptId" && DefinesPlayscript(node))
            value = MakeUniquePlayscriptId(nodeGuid, value);

        node.Fields[fieldName] = value;
        return value;
    }

    // playscriptId must be unique among currently-live nodes — a collision (typed by hand, or copied by
    // 複製 duplicating every field including playscriptId itself) is resolved by appending the lowest
    // unused "_N" suffix rather than allowing two live nodes to share a name, per explicit product
    // decision: a duplicate identity silently breaks the C# factory codegen (two playscripts would resolve
    // to the same output class/file) and confuses the Pipeline Status/本地劇本 file-per-name bookkeeping.
    // flagId nodes are exempt — flags are expected to repeat, and so are 劇本條件 nodes (see
    // DefinesPlayscript): only nodes that define a playscript have to be unique among themselves.
    private string MakeUniquePlayscriptId(string nodeGuid, string candidate)
    {
        if (_model == null)
            return candidate;

        bool CollidesWith(string v) => _model.Nodes.Any(n =>
            n.Guid != nodeGuid && !n.IsMarkedForDeletion && DefinesPlayscript(n) &&
            n.Fields.TryGetValue("playscriptId", out var existing) &&
            string.Equals(existing?.ToString(), v, StringComparison.Ordinal));

        if (!CollidesWith(candidate))
            return candidate;

        var suffix = 1;
        string next;
        do
        {
            next = $"{candidate}_{suffix}";
            suffix++;
        } while (CollidesWith(next));

        StatusMessage = $"識別碼「{candidate}」與其他節點重複，已自動改為「{next}」";
        return next;
    }

    public IReadOnlyDictionary<string, string>? GetNodeFields(string nodeGuid)
    {
        var node = _model?.FindNode(nodeGuid);
        return node?.Fields.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty);
    }

    // The node's current display title + whether it's a real identity value (playscriptId/flagId) or a
    // generic type-label fallback — the same formula ToPayload uses at load time and DuplicateNode uses for
    // a freshly duplicated node. Exposed here too so a plain field edit can push the same title live to the
    // canvas (see FlowGraphPanel.OnFieldChanged) instead of leaving it stale until the next reload.
    public NodeTitleInfoDto? GetNodeTitleInfo(string nodeGuid)
    {
        var node = _model?.FindNode(nodeGuid);
        if (_model == null || node == null)
            return null;

        var schema = _model.GetSchema(node.TypeName);
        var title = !string.IsNullOrEmpty(node.IdentityValue)
            ? node.IdentityValue!
            : (schema == null || string.IsNullOrEmpty(schema.DisplayName) ? node.TypeName : schema.DisplayName);

        return new NodeTitleInfoDto
        {
            Title = title,
            HasIdentityTitle = !string.IsNullOrEmpty(node.IdentityValue),
        };
    }

    private static bool SameBounds((float X, float Y, float Width, float Height) a,
        (float X, float Y, float Width, float Height) b) =>
        Math.Abs(a.X - b.X) < 0.01f && Math.Abs(a.Y - b.Y) < 0.01f &&
        Math.Abs(a.Width - b.Width) < 0.01f && Math.Abs(a.Height - b.Height) < 0.01f;

    private GraphEditorPayload ToPayload()
    {
        var model = _model!;
        DiagnosticsLog.Write(
            $"ToPayload: model.Settings.NodeFrozenTextSize={model.Settings.NodeFrozenTextSize}, " +
            $"model.Settings instance={model.Settings.GetHashCode()}");

        var payload = new GraphEditorPayload
        {
            Settings = new GraphSettingsDto
            {
                NodeFrozenTextSize = model.Settings.NodeFrozenTextSize,
                NodeMaxTextSize = model.Settings.NodeMaxTextSize,
                GroupFrozenTextSize = model.Settings.GroupFrozenTextSize,
                GroupMaxTextSize = model.Settings.GroupMaxTextSize,
                GroupPadding = model.Settings.Padding,
                GroupTitleBarHeight = model.Settings.TitleBarHeight,
                SearchMinZoom = model.Settings.SearchMinZoom,
            },
        };

        DiagnosticsLog.Write($"ToPayload: payload.Settings.NodeFrozenTextSize={payload.Settings.NodeFrozenTextSize}");

        foreach (var typeSchema in model.Schema.NodeTypes)
        {
            payload.NodeTypes.Add(new NodeTypeDto
            {
                TypeName = typeSchema.TypeName,
                DisplayName = string.IsNullOrEmpty(typeSchema.DisplayName) ? typeSchema.TypeName : typeSchema.DisplayName,
                MenuName = typeSchema.MenuName ?? string.Empty,
                Inputs = typeSchema.Inputs.Select(p => new PortDto
                {
                    FieldName = p.FieldName, PortName = p.PortName, AllowMultiple = p.AllowMultiple,
                }).ToList(),
                Outputs = typeSchema.Outputs.Select(p => new PortDto { FieldName = p.FieldName, PortName = p.PortName }).ToList(),
                IdentityFields = typeSchema.IdentityFields.Select(f => f.FieldName).ToList(),
            });
        }

        foreach (var node in model.Nodes.Where(n => !n.IsMarkedForDeletion))
        {
            var schema = model.GetSchema(node.TypeName);
            var title = !string.IsNullOrEmpty(node.IdentityValue)
                ? node.IdentityValue!
                : schema?.DisplayName ?? node.TypeName;

            payload.Nodes.Add(new NodeDto
            {
                Guid = node.Guid,
                TypeName = node.TypeName,
                Title = title,
                HasIdentityTitle = !string.IsNullOrEmpty(node.IdentityValue),
                X = node.X,
                Y = node.Y,
                Width = node.Width,
                Height = node.Height,
                Fields = node.Fields.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty),
            });
        }

        foreach (var edge in model.Edges.Where(e => !e.IsMarkedForDeletion))
        {
            payload.Edges.Add(new EdgeDto
            {
                FromNodeGuid = edge.FromNodeGuid,
                FromPort = edge.FromPort,
                ToNodeGuid = edge.ToNodeGuid,
                ToPort = edge.ToPort,
            });
        }

        // 群組範圍和 Unity 顯示時一樣依成員重新計算（檔案裡存的常常是舊範圍）；沒有成員時用檔案裡的範圍
        _canvasGroupBounds.Clear();
        foreach (var group in model.Groups.Where(g => !g.IsMarkedForDeletion))
        {
            var bounds = model.ComputeGroupBounds(group) ?? (group.X, group.Y, group.Width, group.Height);
            _canvasGroupBounds[group.ClientId] = bounds;
            payload.Groups.Add(new GroupDto
            {
                ClientId = group.ClientId,
                Title = group.Title,
                X = bounds.X,
                Y = bounds.Y,
                Width = bounds.Width,
                Height = bounds.Height,
                Color = ToHexColor(group.ColorR, group.ColorG, group.ColorB),
                MemberNodeGuids = group.MemberNodeGuids.ToList(),
            });
        }

        return payload;
    }

    // ToHexColor 的反向：接受 #rrggbb 或 #rgb，轉成 0–1 的 RGB（透明度不在畫布上編輯，維持原值）
    private static bool TryParseHexColor(string? hex, out float r, out float g, out float b)
    {
        r = g = b = 0;
        if (string.IsNullOrEmpty(hex) || hex[0] != '#')
            return false;

        var digits = hex[1..];
        if (digits.Length == 3)
            digits = string.Concat(digits.Select(c => $"{c}{c}"));
        if (digits.Length < 6 ||
            !int.TryParse(digits[..6], System.Globalization.NumberStyles.HexNumber, null, out var value))
            return false;

        r = ((value >> 16) & 0xFF) / 255f;
        g = ((value >> 8) & 0xFF) / 255f;
        b = (value & 0xFF) / 255f;
        return true;
    }

    private static string ToHexColor(float r, float g, float b)
    {
        return $"#{ToByte(r):x2}{ToByte(g):x2}{ToByte(b):x2}";

        static byte ToByte(float channel) => (byte)Math.Clamp(channel * 255f, 0, 255);
    }
}
