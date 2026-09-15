using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StoryForge.Core;
using StoryForge.Core.Changelist;
using StoryForge.Core.Graph;
using StoryForge.Core.Pipeline;
using StoryForge.Core.Schemas;

namespace StoryForge.Web.GraphEditor;

// Scoped per user circuit (registered AddScoped in Program.cs) — one browser tab, one loaded graph.
// Mirrors ProcessGraphPanel's own LoadGraph/SaveSession from the old WinForms tool, minus undo/redo and
// the Unity-changelist export, which haven't been ported to this canvas yet.
public sealed class GraphEditorState
{
    // Only a 劇本 node represents a story beat — 劇本條件/公用開關/公用變數/AND/OR/... logic nodes are
    // condition sources, never narrative content, so they never get a NodeContentStore file. Sourced from
    // NodeContentStore itself so Core (GraphDocumentModel's deletion cleanup) and this class agree.
    private const string PlayscriptProcessNodeTypeName = NodeContentStore.PlayscriptProcessNodeTypeName;

    private readonly AppSettings _appSettings;
    private string? _sessionPath;
    private string? _connectionsPath;
    private GraphDocumentModel? _model;

    public GraphEditorState(AppSettings appSettings)
    {
        _appSettings = appSettings;
    }

    public string? StatusMessage { get; private set; }

    public GraphEditorPayload LoadGraph()
    {
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
                throw new FileNotFoundException("找不到節點 Schema，請先在 Unity 按「編輯器/劇本流程圖/匯出節點Schema」。", schemaJsonPath);

            var schemaDocument = DocumentLoader.LoadNodeSchemaDocument(schemaJsonPath);

            Directory.CreateDirectory(_appSettings.ExternalDataFolder);
            _sessionPath = Path.Combine(_appSettings.ExternalDataFolder, "session.json");
            _connectionsPath = Path.Combine(_appSettings.ExternalDataFolder, "connections.json");

            if (File.Exists(_sessionPath))
            {
                var (session, connections) = LoadSessionAndConnections(_sessionPath, _connectionsPath);
                _model = GraphDocumentModel.LoadFromSession(session, connections, schemaDocument, _appSettings);
                SeedScriptFileNameTracking();
                StatusMessage = $"已從本機存檔繼續：{_model.Nodes.Count} 節點、{_model.Edges.Count} 連線、{_model.Groups.Count} 群組";
                return ToPayload(_model);
            }

            // One-time migration: before 角色卡/世界書/流程圖 all became the real save location under
            // AppSettings.ExternalDataFolder, the flow graph's resume file lived at
            // %LocalAppData%\StoryForge\ instead — pick it up from there once and persist forward to the
            // new location, rather than silently falling through to a fresh Unity-YAML load and losing
            // every local edit that was never round-tripped back to Unity.
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var oldSessionPath = Path.Combine(localAppData, "StoryForge", "session.json");
            var oldConnectionsPath = Path.Combine(localAppData, "StoryForge", "connections.json");
            if (File.Exists(oldSessionPath))
            {
                var (session, connections) = LoadSessionAndConnections(oldSessionPath, oldConnectionsPath);
                _model = GraphDocumentModel.LoadFromSession(session, connections, schemaDocument, _appSettings);
                SeedScriptFileNameTracking();
                PersistModel();
                StatusMessage = $"已從舊位置搬移本機存檔：{_model.Nodes.Count} 節點、{_model.Edges.Count} 連線、{_model.Groups.Count} 群組";
                return ToPayload(_model);
            }

            var graphYamlPath = Path.Combine(assetsRoot, "06.Definition", "PlayscriptGraphYamlDefinition",
                "PlayscriptProcessor.yaml");
            if (!File.Exists(graphYamlPath))
                throw new FileNotFoundException("找不到流程圖 YAML，請先在 Unity 的流程圖視窗按「匯出至外部工具」。", graphYamlPath);

            var graphYamlText = File.ReadAllText(graphYamlPath);
            var baseYamlHash = GraphHashUtility.ComputeHash(graphYamlText);
            var graphDocument = DocumentLoader.LoadGraphDocument(graphYamlPath);

            _model = GraphDocumentModel.Load(graphDocument, schemaDocument, _appSettings, baseYamlHash);
            SeedScriptFileNameTracking();
            StatusMessage = $"已載入：{_model.Nodes.Count} 節點、{_model.Edges.Count} 連線、{_model.Groups.Count} 群組";
            return ToPayload(_model);
        }
        catch (Exception e)
        {
            StatusMessage = $"載入失敗：{e.Message}";
            return new GraphEditorPayload();
        }
    }

    // Applies what the canvas actually lets a user change in this first pass — node positions and
    // connections — onto the live model, then saves it the same way ProcessGraphPanel's own SaveSession
    // did (a local resume file, not a changelist for Unity yet).
    public void ApplyAndSave(GraphEditorExport export)
    {
        if (_model == null || _sessionPath == null || _connectionsPath == null)
            return;

        try
        {
            ApplyExportDiff(export);

            var renameCollisions = SyncRenamedScriptFiles();
            PersistModel();

            StatusMessage = $"已存檔：{_model.Nodes.Count} 節點、{_model.Edges.Count} 連線、{_model.Groups.Count} 群組";
            AppendRenameCollisionWarning(renameCollisions);
        }
        catch (Exception e)
        {
            StatusMessage = $"存檔失敗：{e.Message}";
        }
    }

    // Same canvas-sync-then-save flow as ApplyAndSave above, plus a full-document snapshot dropped into
    // Unity's own hand-off folder — mirrors the legacy WinForms tool's 匯出變更清單 (ChangelistReviewForm.
    // ExportAndClose), which writes the exact same {timestamp}-graphsnapshot.json shape to the exact same
    // "<UnityProjectRoot>/PlayscriptOfflineTool/Changelists/" folder that Unity's own
    // PlayscriptGraphChangelistAutoImporter (Assets/Editor/EditorWindow/PlayscriptGraph, polls every few
    // seconds) already watches and fully overwrites the live graph from. StoryForge never talks to Unity
    // directly — if the Editor isn't currently running, the file just waits there until it is, so this can
    // only confirm the snapshot was written, never that Unity actually applied it.
    public void ExportToUnity(GraphEditorExport export)
    {
        if (_model == null || _sessionPath == null || _connectionsPath == null)
            return;

        try
        {
            ApplyExportDiff(export);

            var renameCollisions = SyncRenamedScriptFiles();
            PersistModel();

            var projectRoot = ProjectPaths.ResolveUnityProjectRoot();
            var changelistFolder = Path.Combine(projectRoot, "PlayscriptOfflineTool", "Changelists");
            Directory.CreateDirectory(changelistFolder);

            var fullDocument = _model.BuildFullDocument();
            var fileName = $"{DateTime.Now:yyyyMMdd-HHmmss}-graphsnapshot.json";
            File.WriteAllText(Path.Combine(changelistFolder, fileName),
                JsonConvert.SerializeObject(fullDocument, Formatting.Indented));

            StatusMessage = $"已存檔並匯出快照至 Unity（{fileName}），將於 Unity 開啟時自動套用：" +
                            $"{_model.Nodes.Count} 節點、{_model.Edges.Count} 連線、{_model.Groups.Count} 群組";
            AppendRenameCollisionWarning(renameCollisions);
        }
        catch (Exception e)
        {
            StatusMessage = $"匯出至 Unity 失敗：{e.Message}";
        }
    }

    // The node/edge/group diff itself, shared by ApplyAndSave and ExportToUnity — everything downstream of
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
        // position diff. A group the client doesn't have a matching ClientId for yet (created this
        // session via 新增群組, never round-tripped through 存檔 before) is silently skipped — same
        // known v1 gap as new nodes had before OnNodeCreated, not yet wired up for groups.
        foreach (var exportedGroup in export.Groups)
        {
            var group = _model.FindGroup(exportedGroup.ClientId);
            if (group == null)
                continue;

            group.MemberNodeGuids.Clear();
            foreach (var guid in exportedGroup.MemberNodeGuids)
                group.MemberNodeGuids.Add(guid);

            group.X = exportedGroup.X;
            group.Y = exportedGroup.Y;
            group.Width = exportedGroup.Width;
            group.Height = exportedGroup.Height;
        }
    }

    // Persists the live model to session.json as-is, with no canvas export to reconcile — used after a
    // field-only mutation (UpdateNodeField, e.g. from an AI-workspace import) that never goes through
    // ApplyAndSave's node/edge/group diffing, so those edits aren't silently lost until the user happens
    // to open the 流程圖 tab and press 存檔 themselves.
    public void SaveModelOnly()
    {
        try
        {
            var renameCollisions = SyncRenamedScriptFiles();
            PersistModel();
            StatusMessage = $"已存檔：{_model?.Nodes.Count} 節點、{_model?.Edges.Count} 連線、{_model?.Groups.Count} 群組";
            AppendRenameCollisionWarning(renameCollisions);
        }
        catch (Exception e)
        {
            StatusMessage = $"存檔失敗：{e.Message}";
        }
    }

    private void AppendRenameCollisionWarning(List<string> renameCollisions)
    {
        if (renameCollisions.Count > 0)
            StatusMessage += $"；{renameCollisions.Count} 個劇本檔改名失敗（目標名稱已有別的節點在用）：{string.Join("、", renameCollisions)}";
    }

    private void PersistModel()
    {
        if (_model == null || _sessionPath == null || _connectionsPath == null)
            return;

        var directory = Path.GetDirectoryName(_sessionPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var sessionDocument = _model.CreateSessionDocument();
        File.WriteAllText(_sessionPath, JsonConvert.SerializeObject(sessionDocument, Formatting.Indented));

        var connectionsDocument = _model.CreateConnectionsDocument();
        File.WriteAllText(_connectionsPath, JsonConvert.SerializeObject(connectionsDocument, Formatting.Indented));

        _model.MarkClean();
    }

    // Snapshot of every 劇本 node's playscriptId as last known to match its .json/.md 劇本檔 on disk —
    // (re)established once whenever a fresh model is loaded (every _model = ... assignment above calls
    // this), then compared against each node's CURRENT IdentityValue on every real 存檔
    // (SyncRenamedScriptFiles) so a sidebar rename can bring its files along instead of leaving them
    // orphaned under the old name. A guid missing from here has never been compared yet — treated as
    // "nothing to rename FROM", not as "definitely unchanged".
    private Dictionary<string, string> _lastKnownScriptNameByGuid = new();

    private void SeedScriptFileNameTracking()
    {
        _lastKnownScriptNameByGuid = _model!.Nodes
            .Where(n => n.TypeName == PlayscriptProcessNodeTypeName && !string.IsNullOrEmpty(n.IdentityValue))
            .ToDictionary(n => n.Guid, n => n.IdentityValue!);
    }

    // Called right before every real 存檔 (ApplyAndSave/SaveModelOnly — never the load paths themselves,
    // see SeedScriptFileNameTracking) — moves a node's .json/.md 劇本檔 to follow its playscriptId if it
    // changed since the graph was loaded (or since the last save), instead of leaving the old-named file
    // orphaned on disk. A rename that would land on a name already claimed by a DIFFERENT node's file is
    // never attempted — silently taking NodeContentStore.ResolveBasePath's own guid-suffixed fallback here
    // would just be a different flavor of "自己生一個名字", the exact thing 建立劇本檔's own duplicate
    // handling exists to avoid — so it's reported back for the caller to warn about instead, and the OLD
    // name stays tracked so the same collision keeps getting flagged on every subsequent save until the
    // user resolves it (renames one of the two nodes to something else).
    private List<string> SyncRenamedScriptFiles()
    {
        if (_model == null)
            return new List<string>();

        var dataFolder = _appSettings.ExternalDataFolder;
        var collisions = new List<string>();

        foreach (var node in _model.Nodes)
        {
            if (node.TypeName != PlayscriptProcessNodeTypeName || node.IsMarkedForDeletion)
                continue;

            var currentName = node.IdentityValue;
            var hadPrevious = _lastKnownScriptNameByGuid.TryGetValue(node.Guid, out var previousName);

            if (string.IsNullOrEmpty(currentName))
                continue; // cleared, not renamed — nothing to move TO; keep tracking the last real name

            if (!hadPrevious)
            {
                _lastKnownScriptNameByGuid[node.Guid] = currentName;
                continue; // first time this guid has ever been compared — nothing to rename FROM yet
            }

            if (previousName == currentName)
                continue;

            // A guid mismatch only blocks the rename if that OTHER guid is still a live node on the graph —
            // playscriptId is enforced unique among live nodes at edit time (see UpdateNodeField), so this
            // branch is now only ever reachable via a leftover file from a node that no longer exists (e.g.
            // deleted before this cleanup existed). An orphaned file like that is reclaimed silently rather
            // than blocking the rename the user actually asked for.
            var existingAtNewName = NodeContentStore.ReadAtPlainPath(dataFolder, currentName);
            if (existingAtNewName != null && existingAtNewName.Guid != node.Guid &&
                _model.Nodes.Any(n => n.Guid == existingAtNewName.Guid && !n.IsMarkedForDeletion))
            {
                collisions.Add($"{previousName} → {currentName}");
                continue;
            }

            RenameScriptFiles(dataFolder, previousName!, currentName, node.Guid);
            _lastKnownScriptNameByGuid[node.Guid] = currentName;
        }

        return collisions;
    }

    // The actual file move for one node: its NodeContentStore .json (identity fields refreshed to the new
    // name) and its NodeScriptTableStore .md (just the "# 標題" first line rewritten — the rest is an AI
    // tool's real dialogue content, untouched). Both land at the new name's PLAIN base path — the caller
    // has already confirmed nothing else owns it.
    private static void RenameScriptFiles(string dataFolder, string oldName, string newName, string guid)
    {
        var newBase = NodeContentStore.ResolvePlainBasePath(dataFolder, newName);
        var directory = Path.GetDirectoryName(newBase);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var oldJsonPath = NodeContentStore.ResolveBasePath(dataFolder, oldName, guid) + ".json";
        if (File.Exists(oldJsonPath))
        {
            var entry = NodeContentStore.Read(dataFolder, oldName, guid) ?? new NodeContentEntry { Guid = guid };
            entry.Guid = guid;
            entry.PlayscriptName = newName;
            File.Delete(oldJsonPath);
            File.WriteAllText(newBase + ".json", JsonConvert.SerializeObject(entry, Formatting.Indented));
        }

        var oldMdPath = NodeScriptTableStore.ResolvePath(dataFolder, oldName, guid);
        if (File.Exists(oldMdPath))
        {
            var content = File.ReadAllText(oldMdPath);
            var newlineIndex = content.IndexOf('\n');
            var firstLine = newlineIndex >= 0 ? content[..newlineIndex] : content;
            var rest = newlineIndex >= 0 ? content[newlineIndex..] : string.Empty;
            var updated = firstLine.TrimStart().StartsWith("# ") ? $"# {newName}{rest}" : content;
            File.Delete(oldMdPath);
            File.WriteAllText(newBase + ".md", updated);
        }
    }

    // Reads a graph's node/group document plus its sibling connections document from disk, tolerating a
    // pre-split save (no connections file yet, edges still embedded inline under the session document's
    // own "Edges" key) by pulling them out of there instead — used both for the normal load path and for
    // the one-time migration off the old %LocalAppData% location, which is exactly this same shape.
    private static (GraphSessionDocument Session, GraphConnectionsDocument Connections) LoadSessionAndConnections(
        string sessionPath, string connectionsPath)
    {
        var sessionRoot = JObject.Parse(File.ReadAllText(sessionPath));
        var session = sessionRoot.ToObject<GraphSessionDocument>() ?? new GraphSessionDocument();

        GraphConnectionsDocument connections;
        if (File.Exists(connectionsPath))
        {
            connections = JsonConvert.DeserializeObject<GraphConnectionsDocument>(File.ReadAllText(connectionsPath))
                          ?? new GraphConnectionsDocument();
        }
        else
        {
            var legacyEdges = sessionRoot["Edges"]?.ToObject<List<GraphSessionEdge>>() ?? new List<GraphSessionEdge>();
            connections = new GraphConnectionsDocument { SavedAtUtc = session.SavedAtUtc, Edges = legacyEdges };
        }

        return (session, connections);
    }

    // One node queued for 建立劇本檔: either ready to sync immediately (no plain-path collision) or stuck
    // behind an orphaned file — content from some other, no-longer-live node's guid still occupying the
    // plain path — that needs the user's explicit go-ahead before ScriptFileSyncPlan.Ready's write would
    // otherwise overwrite it silently.
    public readonly record struct ScriptFileSyncItem(string Guid, string PlayscriptName, string CurrentMemo);

    public sealed class ScriptFileSyncPlan
    {
        public List<ScriptFileSyncItem> Ready { get; init; } = new();
        public List<ScriptFileSyncItem> Conflicts { get; init; } = new();
    }

    // One-time-per-node migration + on-demand sync: memo used to live inside the node's own Fields (a real
    // Unity schema field that never should have been one — see conversation history; Unity's own schema
    // still declares it, but that's a Unity-side mistake outside this tool's reach to fix). It now lives
    // only in its own NodeContentStore file, so syncing both seeds a brand-new file from whatever's still in
    // Fields (once) and strips the key out of Fields going forward so it doesn't linger duplicated in
    // session.json. The only caller is PipelineStatusPanel's own 建立劇本檔 button (not flow-graph
    // load/save) — that panel owns the row-checkbox selection in its grid, so this only touches nodes whose
    // playscriptId is in selectedPlayscriptNames, matching whatever rows the user currently has checked
    // there.
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
            var memo = n.Fields.TryGetValue("memo", out var m) ? m?.ToString() ?? string.Empty : string.Empty;
            var item = new ScriptFileSyncItem(n.Guid, n.IdentityValue!, memo);

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
        foreach (var item in ready)
            _model?.FindNode(item.Guid)?.Fields.Remove("memo");

        NodeContentStore.SyncAll(_appSettings.ExternalDataFolder,
            ready.Select(i => (i.Guid, i.PlayscriptName, i.CurrentMemo)));
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
        _model?.FindNode(item.Guid)?.Fields.Remove("memo");

        NodeContentStore.WriteAtPlainPath(_appSettings.ExternalDataFolder, new NodeContentEntry
        {
            Guid = item.Guid,
            PlayscriptName = item.PlayscriptName,
            Memo = item.CurrentMemo,
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
        _model?.Nodes.Where(n => !n.IsMarkedForDeletion && !string.IsNullOrEmpty(n.IdentityValue))
            .Select(n => n.IdentityValue!) ?? Enumerable.Empty<string>();

    // Null when the playscript has no live graph node (listed in the Sheet index but never added to the
    // flow graph) — callers resolving a file path for it fall back to NodeContentStore's plain (non-guid)
    // path the same way PipelineStatusState's "本地劇本" column already does for a leftover file.
    public string? FindNodeGuidForPlayscript(string playscriptName) =>
        _model?.Nodes.FirstOrDefault(n => !n.IsMarkedForDeletion && n.IdentityValue == playscriptName)?.Guid;

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

        if (fieldName == "playscriptId")
            value = MakeUniquePlayscriptId(nodeGuid, value);

        node.Fields[fieldName] = value;
        return value;
    }

    // playscriptId must be unique among currently-live nodes — a collision (typed by hand, or copied by
    // 複製 duplicating every field including playscriptId itself) is resolved by appending the lowest
    // unused "_N" suffix rather than allowing two live nodes to share a name, per explicit product
    // decision: a duplicate identity silently breaks the C# factory codegen (two playscripts would resolve
    // to the same output class/file) and confuses the Pipeline Status/本地劇本 file-per-name bookkeeping.
    // flagId nodes are exempt — flags are expected to repeat.
    private string MakeUniquePlayscriptId(string nodeGuid, string candidate)
    {
        if (_model == null)
            return candidate;

        bool CollidesWith(string v) => _model.Nodes.Any(n =>
            n.Guid != nodeGuid && !n.IsMarkedForDeletion &&
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

    private static GraphEditorPayload ToPayload(GraphDocumentModel model)
    {
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

        foreach (var group in model.Groups.Where(g => !g.IsMarkedForDeletion))
        {
            payload.Groups.Add(new GroupDto
            {
                ClientId = group.ClientId,
                Title = group.Title,
                X = group.X,
                Y = group.Y,
                Width = group.Width,
                Height = group.Height,
                Color = ToHexColor(group.ColorR, group.ColorG, group.ColorB),
                MemberNodeGuids = group.MemberNodeGuids.ToList(),
            });
        }

        return payload;
    }

    private static string ToHexColor(float r, float g, float b)
    {
        return $"#{ToByte(r):x2}{ToByte(g):x2}{ToByte(b):x2}";

        static byte ToByte(float channel) => (byte)Math.Clamp(channel * 255f, 0, 255);
    }
}
