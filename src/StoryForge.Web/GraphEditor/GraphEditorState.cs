using Newtonsoft.Json;
using StoryForge.Core;
using StoryForge.Core.Changelist;
using StoryForge.Core.Graph;
using StoryForge.Core.Schemas;

namespace StoryForge.Web.GraphEditor;

// Scoped per user circuit (registered AddScoped in Program.cs) — one browser tab, one loaded graph.
// Mirrors ProcessGraphPanel's own LoadGraph/SaveSession from the old WinForms tool, minus undo/redo and
// the Unity-changelist export, which haven't been ported to this canvas yet.
public sealed class GraphEditorState
{
    private readonly AppSettings _appSettings;
    private string? _sessionPath;
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

            _sessionPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StoryForge", "session.json");

            if (File.Exists(_sessionPath))
            {
                var sessionJson = File.ReadAllText(_sessionPath);
                var session = JsonConvert.DeserializeObject<GraphSessionDocument>(sessionJson)
                              ?? new GraphSessionDocument();
                _model = GraphDocumentModel.LoadFromSession(session, schemaDocument, _appSettings);
                StatusMessage = $"已從本機存檔繼續：{_model.Nodes.Count} 節點、{_model.Edges.Count} 連線、{_model.Groups.Count} 群組";
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
        if (_model == null || _sessionPath == null)
            return;

        try
        {
            // New nodes first, before positions/edges: an edge exported alongside its brand-new endpoint
            // node needs that node to already exist in the model.
            foreach (var newNode in export.NewNodes)
            {
                if (_model.FindNode(newNode.Guid) != null)
                    continue;

                var schema = _model.GetSchema(newNode.TypeName);
                if (schema != null)
                    _model.AddNode(schema, newNode.X, newNode.Y, newNode.Guid);
            }

            // A node the canvas no longer has (Delete key, or LiteGraph's own node-menu Remove) — mark it
            // and its edges for deletion the same way MarkNodeForDeletion always has; the edge-diff loop
            // below would eventually detach its edges too, but doing it here also gets the node itself.
            foreach (var deletedGuid in export.DeletedNodeGuids)
                _model.MarkNodeForDeletion(deletedGuid);

            foreach (var position in export.NodePositions)
            {
                var node = _model.FindNode(position.Guid);
                if (node != null)
                {
                    node.X = position.X;
                    node.Y = position.Y;
                }
            }

            var exportedKeys = new HashSet<string>(
                export.Edges.Select(e => $"{e.FromNodeGuid}.{e.FromPort}->{e.ToNodeGuid}.{e.ToPort}"));
            var currentKeys = new HashSet<string>(
                _model.Edges.Where(e => !e.IsMarkedForDeletion)
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

            var directory = Path.GetDirectoryName(_sessionPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var sessionDocument = _model.CreateSessionDocument();
            File.WriteAllText(_sessionPath, JsonConvert.SerializeObject(sessionDocument, Formatting.Indented));
            _model.MarkClean();

            StatusMessage = $"已存檔：{_model.Nodes.Count} 節點、{_model.Edges.Count} 連線、{_model.Groups.Count} 群組";
        }
        catch (Exception e)
        {
            StatusMessage = $"存檔失敗：{e.Message}";
        }
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

    public string? GetNodeTypeName(string nodeGuid) => _model?.FindNode(nodeGuid)?.TypeName;

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

    // Node identity/memo fields aren't part of the canvas rendering at all (LiteGraph never sees them), so
    // editing one here doesn't need any round trip through JS — it goes straight into the live model, the
    // same one 存檔 already reads from. A title built from an edited identity field (e.g. playscriptId)
    // won't update on the canvas until the next reload — an accepted v1 gap, not an oversight.
    public void UpdateNodeField(string nodeGuid, string fieldName, string value)
    {
        var node = _model?.FindNode(nodeGuid);
        if (node == null)
            return;

        if (string.IsNullOrEmpty(value))
            node.Fields.Remove(fieldName);
        else
            node.Fields[fieldName] = value;
    }

    public IReadOnlyDictionary<string, string>? GetNodeFields(string nodeGuid)
    {
        var node = _model?.FindNode(nodeGuid);
        return node?.Fields.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty);
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
