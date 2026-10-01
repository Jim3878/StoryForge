using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StoryForge.Core.Schemas;

namespace StoryForge.Core.Graph;

public sealed class GraphNodeVm
{
    public required string Guid { get; init; }
    public required string TypeName { get; init; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; } = 220;
    public float Height { get; set; } = 140;
    public Dictionary<string, object?> Fields { get; init; } = new();
    public Dictionary<string, JToken> Extra { get; init; } = new();
    public Dictionary<string, JToken> PositionExtra { get; init; } = new();
    public bool IsNew { get; init; }
    public bool IsMarkedForDeletion { get; set; }

    // Position exactly as loaded from the document, for an existing (!IsNew) node — used only to detect
    // whether it was actually moved this session when building a changelist. Null for a node created this
    // session (IsNew), which has no "as loaded" position to compare against.
    public float? BaselineX { get; init; }
    public float? BaselineY { get; init; }

    public string? IdentityValue => Fields.TryGetValue("playscriptId", out var v) ? v?.ToString()
        : Fields.TryGetValue("flagId", out var f) ? f?.ToString() : null;
}

public sealed class GraphGroupVm
{
    // Placeholder size for a group with no members yet — there's nothing to fit a bounding box to.
    public const float EmptyPlaceholderWidth = 200f;
    public const float EmptyPlaceholderHeight = 100f;

    public required string ClientId { get; init; }

    // Index in the exported document's Groups list at load time; null for a group created in this session.
    // GraphProcessor's Group has no GUID of its own, so this is the closest thing to a stable identity.
    public int? SourceIndex { get; init; }

    public string Title { get; set; } = "群組";
    // 預設黑色、透明度 0.3，與 Unity GraphProcessor 新建群組的預設色一致
    public float ColorR { get; set; } = 0f;
    public float ColorG { get; set; } = 0f;
    public float ColorB { get; set; } = 0f;
    public float ColorA { get; set; } = 0.3f;

    // Always derived from member node positions (see GraphDocumentModel.RecomputeGroupBounds) except
    // for the placeholder state of a brand-new, still-empty group — never set directly from the UI.
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; } = EmptyPlaceholderWidth;
    public float Height { get; set; } = EmptyPlaceholderHeight;

    public HashSet<string> MemberNodeGuids { get; init; } = new();
    public Dictionary<string, JToken> Extra { get; init; } = new();
    public Dictionary<string, JToken> PositionExtra { get; init; } = new();
    public Dictionary<string, JToken> ColorExtra { get; init; } = new();
    public bool IsNew { get; init; }
    public bool IsMarkedForDeletion { get; set; }

    // Title/members exactly as loaded from the document, for an existing (SourceIndex != null) group —
    // used only to detect whether the group was actually modified this session when building a
    // changelist. Null for a group created this session (IsNew), which has no "as loaded" state.
    public string? BaselineTitle { get; init; }
    public HashSet<string>? BaselineMemberNodeGuids { get; init; }
}

public sealed class GraphEdgeVm
{
    public required string FromNodeGuid { get; init; }
    public required string FromPort { get; init; }
    public string? FromPortId { get; init; }
    public required string ToNodeGuid { get; init; }
    public required string ToPort { get; init; }
    public string? ToPortId { get; init; }
    public bool IsNew { get; init; }
    public bool IsMarkedForDeletion { get; set; }
    public Dictionary<string, JToken> Extra { get; init; } = new();

    public string EdgeKey => $"{FromNodeGuid}.{FromPort}.{FromPortId}->{ToNodeGuid}.{ToPort}.{ToPortId}";
}

public sealed class GraphStickyNoteVm
{
    public required string Id { get; init; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public Dictionary<string, JToken> Extra { get; init; } = new();
    public Dictionary<string, JToken> PositionExtra { get; init; } = new();
}

// A deep-cloned copy of a document's editable state, used as one entry in the undo/redo history.
public sealed class GraphDocumentSnapshot
{
    public required List<GraphNodeVm> Nodes { get; init; }
    public required List<GraphEdgeVm> Edges { get; init; }
    public required List<GraphGroupVm> Groups { get; init; }
}

public sealed class GraphDocumentModel
{
    public ProcessNodeSchemaDocument Schema { get; }
    public AppSettings Settings { get; set; }
    public List<GraphNodeVm> Nodes { get; } = new();
    public List<GraphEdgeVm> Edges { get; } = new();
    public List<GraphGroupVm> Groups { get; } = new();
    public List<GraphStickyNoteVm> StickyNotes { get; } = new();
    public Dictionary<string, JToken> DocumentExtra { get; private set; } = new();

    public string GraphName { get; private set; } = string.Empty;

    private readonly Stack<GraphDocumentSnapshot> _undoStack = new();
    private readonly Stack<GraphDocumentSnapshot> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    // Fires whenever the set of nodes could have changed (added, deleted, or restored via undo/redo) —
    // not edges/groups, since the only current listener (the pipeline status panel's "流程圖" column) only
    // cares about which playscript identities exist in the graph.
    public event Action? NodesChanged;

    // 最後一次載入或存檔時的正規化 JSON，HasUnsavedChanges 只要和它比較
    private string? _lastCleanJson;

    private GraphDocumentModel(ProcessNodeSchemaDocument schema, AppSettings settings)
    {
        Schema = schema;
        Settings = settings;
    }

    public static GraphDocumentModel LoadCanonical(CanonicalGraphDocument document,
        CanonicalGraphDocument baseDocument, ProcessNodeSchemaDocument schema, AppSettings settings)
    {
        var model = new GraphDocumentModel(schema, settings)
        {
            GraphName = document.GraphId,
            DocumentExtra = CloneExtra(document.Extra),
        };
        var baseNodes = baseDocument.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var baseEdges = baseDocument.Edges.ToDictionary(edge => edge.Key, StringComparer.Ordinal);
        var baseGroups = baseDocument.Groups.Select((group, index) => (group, index))
            .ToDictionary(item => item.group.Id, StringComparer.Ordinal);

        foreach (var node in document.Nodes)
        {
            baseNodes.TryGetValue(node.Id, out var baseline);
            model.Nodes.Add(new GraphNodeVm
            {
                Guid = node.Id,
                TypeName = node.Type,
                X = node.Position.X,
                Y = node.Position.Y,
                Width = node.Position.Width,
                Height = node.Position.Height,
                Fields = node.Data.Properties().ToDictionary(property => property.Name,
                    property => property.Value.ToObject<object>(), StringComparer.Ordinal),
                Extra = CloneExtra(node.Extra),
                PositionExtra = CloneExtra(node.Position.Extra),
                IsNew = baseline == null,
                BaselineX = baseline?.Position.X,
                BaselineY = baseline?.Position.Y,
            });
        }

        foreach (var edge in document.Edges)
        {
            model.Edges.Add(new GraphEdgeVm
            {
                FromNodeGuid = edge.FromNode,
                FromPort = edge.FromPort,
                FromPortId = edge.FromPortId,
                ToNodeGuid = edge.ToNode,
                ToPort = edge.ToPort,
                ToPortId = edge.ToPortId,
                Extra = CloneExtra(edge.Extra),
                IsNew = !baseEdges.ContainsKey(edge.Key),
            });
        }

        foreach (var group in document.Groups)
        {
            baseGroups.TryGetValue(group.Id, out var baseline);
            var hasBaseline = baseline.group != null;
            model.Groups.Add(new GraphGroupVm
            {
                ClientId = group.Id,
                SourceIndex = hasBaseline ? baseline.index : null,
                Title = group.Title,
                ColorR = group.Color.R,
                ColorG = group.Color.G,
                ColorB = group.Color.B,
                ColorA = group.Color.A,
                X = group.Position.X,
                Y = group.Position.Y,
                Width = group.Position.Width,
                Height = group.Position.Height,
                MemberNodeGuids = new HashSet<string>(group.Nodes, StringComparer.Ordinal),
                Extra = CloneExtra(group.Extra),
                PositionExtra = CloneExtra(group.Position.Extra),
                ColorExtra = CloneExtra(group.Color.Extra),
                IsNew = !hasBaseline,
                BaselineTitle = hasBaseline ? baseline.group.Title : null,
                BaselineMemberNodeGuids = hasBaseline
                    ? new HashSet<string>(baseline.group.Nodes, StringComparer.Ordinal)
                    : null,
            });
        }

        foreach (var note in document.StickyNotes)
        {
            model.StickyNotes.Add(new GraphStickyNoteVm
            {
                Id = note.Id,
                Title = note.Title,
                Content = note.Content,
                X = note.Position.X,
                Y = note.Position.Y,
                Width = note.Position.Width,
                Height = note.Position.Height,
                Extra = CloneExtra(note.Extra),
                PositionExtra = CloneExtra(note.Position.Extra),
            });
        }

        model.MarkClean();
        return model;
    }

    public CanonicalGraphDocument BuildCanonicalDocument()
    {
        var document = new CanonicalGraphDocument
        {
            GraphId = CanonicalGraphJson.GraphId,
            Extra = CloneExtra(DocumentExtra),
        };
        foreach (var node in Nodes.Where(node => !node.IsMarkedForDeletion))
        {
            document.Nodes.Add(new CanonicalGraphNode
            {
                Id = node.Guid,
                Type = node.TypeName,
                Position = new CanonicalGraphRect
                {
                    X = node.X, Y = node.Y, Width = node.Width, Height = node.Height,
                    Extra = CloneExtra(node.PositionExtra),
                },
                Data = BuildNodeData(node),
                Extra = CloneExtra(node.Extra),
            });
        }
        foreach (var edge in Edges.Where(edge => !edge.IsMarkedForDeletion))
        {
            document.Edges.Add(new CanonicalGraphEdge
            {
                FromNode = edge.FromNodeGuid, FromPort = edge.FromPort, FromPortId = edge.FromPortId,
                ToNode = edge.ToNodeGuid, ToPort = edge.ToPort, ToPortId = edge.ToPortId,
                Extra = CloneExtra(edge.Extra),
            });
        }
        foreach (var group in Groups.Where(group => !group.IsMarkedForDeletion))
        {
            document.Groups.Add(new CanonicalGraphGroup
            {
                Id = group.ClientId,
                Title = group.Title,
                Color = new CanonicalGraphColor
                {
                    R = group.ColorR, G = group.ColorG, B = group.ColorB, A = group.ColorA,
                    Extra = CloneExtra(group.ColorExtra),
                },
                Position = new CanonicalGraphRect
                {
                    X = group.X, Y = group.Y, Width = group.Width, Height = group.Height,
                    Extra = CloneExtra(group.PositionExtra),
                },
                Nodes = group.MemberNodeGuids.ToList(),
                Extra = CloneExtra(group.Extra),
            });
        }
        foreach (var note in StickyNotes)
        {
            document.StickyNotes.Add(new CanonicalGraphStickyNote
            {
                Id = note.Id, Title = note.Title, Content = note.Content,
                Position = new CanonicalGraphRect
                {
                    X = note.X, Y = note.Y, Width = note.Width, Height = note.Height,
                    Extra = CloneExtra(note.PositionExtra),
                },
                Extra = CloneExtra(note.Extra),
            });
        }
        CanonicalGraphJson.Normalize(document, Schema);
        return document;
    }

    // Unity 會把節點型別的每個資料欄位都寫出來：字串（null 也寫成 ""）與整數。這裡依 schema 產生同樣的內容，
    // 側邊欄清空欄位（Fields 裡沒有這個 key）或用字串存的數字都會被轉成 Unity 的寫法。
    private JObject BuildNodeData(GraphNodeVm node)
    {
        var schema = GetSchema(node.TypeName);
        if (schema == null)
            return JObject.FromObject(node.Fields);

        var data = new JObject();
        foreach (var field in schema.IdentityFields)
        {
            node.Fields.TryGetValue(field.FieldName, out var value);
            data[field.FieldName] = field.FieldType == "System.Int32"
                ? new JValue(ToInt(value))
                : new JValue(value?.ToString() ?? string.Empty);
        }

        return data;
    }

    private static int ToInt(object? value) => value switch
    {
        null => 0,
        int i => i,
        long l => (int)l,
        _ => int.TryParse(value.ToString(), out var parsed) ? parsed : 0,
    };

    private static Dictionary<string, JToken> CloneExtra(IEnumerable<KeyValuePair<string, JToken>> extra) =>
        extra.ToDictionary(pair => pair.Key, pair => pair.Value.DeepClone(), StringComparer.Ordinal);

    // 未存修改：把目前狀態轉成正規化 JSON，和最後一次載入／存檔時的版本比較
    public bool HasUnsavedChanges() => SerializeCanonical() != _lastCleanJson;

    public void MarkClean() => _lastCleanJson = SerializeCanonical();

    public string SerializeCanonical() => CanonicalGraphJson.Serialize(BuildCanonicalDocument(), Schema);

    public ProcessNodeTypeSchema? GetSchema(string typeName)
    {
        return Schema.NodeTypes.FirstOrDefault(t => t.TypeName == typeName);
    }

    public GraphNodeVm? FindNode(string guid)
    {
        return Nodes.FirstOrDefault(n => n.Guid == guid);
    }

    public GraphNodeVm AddNode(ProcessNodeTypeSchema schema, float x, float y, string? guid = null)
    {
        var node = new GraphNodeVm
        {
            Guid = guid ?? System.Guid.NewGuid().ToString("D"),
            TypeName = schema.TypeName,
            X = x,
            Y = y,
            IsNew = true,
        };

        foreach (var field in schema.IdentityFields)
            node.Fields[field.FieldName] = field.FieldType == "System.Int32" ? 0 : string.Empty;

        Nodes.Add(node);
        NodesChanged?.Invoke();
        return node;
    }

    public GraphEdgeVm? AddEdge(string fromNodeGuid, string fromPort, string toNodeGuid, string toPort)
    {
        if (Edges.Any(e => !e.IsMarkedForDeletion && e.FromNodeGuid == fromNodeGuid && e.FromPort == fromPort &&
                            e.ToNodeGuid == toNodeGuid && e.ToPort == toPort))
            return null;

        var edge = new GraphEdgeVm
        {
            FromNodeGuid = fromNodeGuid,
            FromPort = fromPort,
            ToNodeGuid = toNodeGuid,
            ToPort = toPort,
            IsNew = true,
        };
        Edges.Add(edge);
        return edge;
    }

    // Single-edge ports only (AllowMultiple=false) — used to decide whether clicking that port's dot
    // should detach its one existing edge or just start a new one, since there's nothing to disambiguate.
    public GraphEdgeVm? FindEdgeAtPort(string nodeGuid, string portField, bool isOutput)
    {
        return isOutput
            ? Edges.FirstOrDefault(e => !e.IsMarkedForDeletion && e.FromNodeGuid == nodeGuid && e.FromPort == portField)
            : Edges.FirstOrDefault(e => !e.IsMarkedForDeletion && e.ToNodeGuid == nodeGuid && e.ToPort == portField);
    }

    // Grabbing an existing edge by its input end detaches it: a new edge (re-routed elsewhere, or none if
    // the user drops in empty space) replaces it, this original is just removed/marked deleted.
    public void DetachEdge(GraphEdgeVm edge)
    {
        if (edge.IsNew)
            Edges.Remove(edge);
        else
            edge.IsMarkedForDeletion = true;
    }

    // Reverts a DetachEdge call — used when a rewiring drag ends up reconnecting the exact same two
    // endpoints it started from, so that doesn't leave the original marked-deleted while an identical
    // new edge sits on top of it.
    public void UndoDetachEdge(GraphEdgeVm edge)
    {
        if (!Edges.Contains(edge))
            Edges.Add(edge);

        edge.IsMarkedForDeletion = false;
    }

    // Marks the node and every edge touching it for deletion. GraphProcessor's BaseGraph.RemoveNode does not
    // auto-disconnect edges, so the deletion changelist must carry these edges explicitly.
    public void MarkNodeForDeletion(string guid)
    {
        var node = FindNode(guid);
        if (node == null)
            return;

        // 不刪除節點的內容檔（.json/.md）：刪除可能還沒存檔就被放棄，或只是切換分支造成節點暫時不存在，
        // 直接刪檔會讓劇情內容永久遺失。留下的檔案成為孤兒，由 GraphEditorState 列出數量，交給使用者處理；
        // 同名的新節點要使用原本的檔名時，會走「建立劇本檔」的覆蓋確認流程。

        var affectedGroups = Groups.Where(g => g.MemberNodeGuids.Contains(guid)).ToList();
        foreach (var group in affectedGroups)
            group.MemberNodeGuids.Remove(guid);

        if (node.IsNew)
        {
            Nodes.Remove(node);
            Edges.RemoveAll(e => e.FromNodeGuid == guid || e.ToNodeGuid == guid);
        }
        else
        {
            node.IsMarkedForDeletion = true;

            // A brand-new edge into a node that's about to be deleted never existed upstream, so it's just
            // dropped; a pre-existing edge is marked so the changelist carries an explicit deletion for it.
            Edges.RemoveAll(e => e.IsNew && (e.FromNodeGuid == guid || e.ToNodeGuid == guid));
            foreach (var edge in Edges.Where(e => e.FromNodeGuid == guid || e.ToNodeGuid == guid))
                edge.IsMarkedForDeletion = true;
        }

        foreach (var group in affectedGroups)
            RecomputeGroupBounds(group);

        NodesChanged?.Invoke();
    }

    public GraphGroupVm? FindGroup(string clientId)
    {
        return Groups.FirstOrDefault(g => g.ClientId == clientId);
    }

    // clientId：畫布上新增群組時已經產生的 ID（存檔時沿用，之後才找得到同一個群組）；不指定則新產生一個
    public GraphGroupVm AddGroup(string title, float x, float y, string? clientId = null)
    {
        var group = new GraphGroupVm
        {
            ClientId = string.IsNullOrEmpty(clientId) ? System.Guid.NewGuid().ToString("D") : clientId,
            SourceIndex = null,
            Title = title,
            X = x,
            Y = y,
            Width = GraphGroupVm.EmptyPlaceholderWidth,
            Height = GraphGroupVm.EmptyPlaceholderHeight,
            IsNew = true,
        };
        Groups.Add(group);
        return group;
    }

    public void MarkGroupForDeletion(string clientId)
    {
        var group = FindGroup(clientId);
        if (group == null)
            return;

        if (group.IsNew)
            Groups.Remove(group);
        else
            group.IsMarkedForDeletion = true;
    }

    // Recomputes a group's box tightly around its current members plus the configured padding/title bar
    // allowance. A still-empty group keeps whatever placeholder position/size it was created with.
    public void RecomputeGroupBounds(GraphGroupVm group)
    {
        var bounds = ComputeGroupBounds(group);
        if (bounds != null)
            (group.X, group.Y, group.Width, group.Height) = bounds.Value;
    }

    // 依成員算出群組應有的範圍，不修改群組本身。Unity 檔案裡存的群組範圍常常是舊的（Unity 顯示時會自動框住成員），
    // 畫布顯示用這個值；沒有成員時為 null。
    public (float X, float Y, float Width, float Height)? ComputeGroupBounds(GraphGroupVm group)
    {
        var members = Nodes.Where(n => group.MemberNodeGuids.Contains(n.Guid)).ToList();
        if (members.Count == 0)
            return null;

        var minX = members.Min(n => n.X);
        var minY = members.Min(n => n.Y);
        var maxX = members.Max(n => n.X + n.Width);
        var maxY = members.Max(n => n.Y + n.Height);

        var padding = Settings.Padding;
        var titleBarHeight = Settings.TitleBarHeight;

        return (minX - padding, minY - titleBarHeight, maxX - minX + padding * 2f, maxY - minY + titleBarHeight + padding);
    }

    // A node joins a group once its own center point lands inside the group's box — a plain overlap test
    // triggered too easily just from a node's edge brushing past a group it wasn't meant to enter.
    private static bool CenterIsInside(GraphGroupVm group, GraphNodeVm node)
    {
        var centerX = node.X + node.Width / 2f;
        var centerY = node.Y + node.Height / 2f;
        return centerX >= group.X && centerX <= group.X + group.Width &&
               centerY >= group.Y && centerY <= group.Y + group.Height;
    }

    public GraphGroupVm? FindJoinableGroup(GraphNodeVm node)
    {
        return Groups
            .Where(g => !g.IsMarkedForDeletion && CenterIsInside(g, node))
            .OrderByDescending(g => g.SourceIndex ?? int.MaxValue)
            .FirstOrDefault();
    }

    // Same resolution as FindJoinableGroup but driven by a raw point rather than a node's center — used
    // by multi-node-selection dragging, where "join a group" is decided by where the cursor currently is
    // rather than any single node's own position.
    public GraphGroupVm? FindGroupContainingPoint(float worldX, float worldY)
    {
        return Groups
            .Where(g => !g.IsMarkedForDeletion &&
                        worldX >= g.X && worldX <= g.X + g.Width &&
                        worldY >= g.Y && worldY <= g.Y + g.Height)
            .OrderByDescending(g => g.SourceIndex ?? int.MaxValue)
            .FirstOrDefault();
    }

    public GraphGroupVm? FindContainingGroup(string nodeGuid)
    {
        return Groups.FirstOrDefault(g => g.MemberNodeGuids.Contains(nodeGuid));
    }

    // Called continuously while a node is being dragged (not just on release), so the owning/target group's
    // box visibly tracks the node in real time:
    // - a node that's already a member never switches groups here — its current group just grows to follow it
    // - a free node joins whichever group it now overlaps, if any
    // suppressMembershipChange (Shift currently held — re-evaluated live every call, not just once at
    // drag-start) instead detaches the node from its current group if it's in one, so pressing Shift
    // partway through an already-moving drag still triggers the removal rather than requiring Shift to
    // have been held before the drag even started.
    public void UpdateNodeDragLive(string nodeGuid, bool suppressMembershipChange)
    {
        var node = FindNode(nodeGuid);
        if (node == null)
            return;

        if (suppressMembershipChange)
        {
            RemoveNodeFromCurrentGroup(nodeGuid);
            return;
        }

        var currentGroup = FindContainingGroup(nodeGuid);
        if (currentGroup != null)
        {
            RecomputeGroupBounds(currentGroup);
            return;
        }

        var target = FindJoinableGroup(node);
        if (target == null)
            return;

        target.MemberNodeGuids.Add(nodeGuid);
        RecomputeGroupBounds(target);
    }

    // Shift+drag start: detach the node from whatever group currently owns it, shrinking that group back
    // down to fit its remaining members (or leaving it at its last size if it becomes empty).
    public void RemoveNodeFromCurrentGroup(string nodeGuid)
    {
        var group = FindContainingGroup(nodeGuid);
        if (group == null)
            return;

        group.MemberNodeGuids.Remove(nodeGuid);
        RecomputeGroupBounds(group);
    }

    // Dragging a group by its title bar translates every member by the same delta, then re-fits the box —
    // this should be a no-op on the size/position beyond the translation itself, but keeps everything in sync
    // if padding/title-bar-height settings changed since the last recompute.
    public void RecomputeAllGroupBounds()
    {
        foreach (var group in Groups)
            RecomputeGroupBounds(group);
    }

    // Call once, right before the first mutation of a discrete user action (a whole drag gesture, one
    // menu command, etc.) — captures what Undo() should roll back to for that action. Any pending redo
    // history is discarded, matching standard undo/redo behavior once a new edit branches off.
    public void BeginUndoableChange()
    {
        _undoStack.Push(CreateSnapshot());
        _redoStack.Clear();
    }

    public void Undo()
    {
        if (_undoStack.Count == 0)
            return;

        var snapshot = _undoStack.Pop();
        _redoStack.Push(CreateSnapshot());
        RestoreSnapshot(snapshot);
    }

    public void Redo()
    {
        if (_redoStack.Count == 0)
            return;

        var snapshot = _redoStack.Pop();
        _undoStack.Push(CreateSnapshot());
        RestoreSnapshot(snapshot);
    }

    public GraphDocumentSnapshot CreateSnapshot()
    {
        return new GraphDocumentSnapshot
        {
            Nodes = Nodes.Select(CloneNode).ToList(),
            Edges = Edges.Select(CloneEdge).ToList(),
            Groups = Groups.Select(CloneGroup).ToList(),
        };
    }

    // Restores state from a snapshot by syncing each collection in place (update existing objects by
    // key, add/remove only what actually appeared or disappeared) rather than replacing the lists
    // wholesale. This keeps object identity stable across an undo/redo for anything still present
    // afterwards — e.g. the inspector panel's currently-selected node reference — instead of leaving it
    // pointing at an orphaned clone that silently no longer belongs to the live graph.
    public void RestoreSnapshot(GraphDocumentSnapshot snapshot)
    {
        SyncNodes(snapshot.Nodes);
        SyncEdges(snapshot.Edges);
        SyncGroups(snapshot.Groups);
        NodesChanged?.Invoke();
    }

    private void SyncNodes(List<GraphNodeVm> snapshotNodes)
    {
        var snapshotByGuid = snapshotNodes.ToDictionary(n => n.Guid);
        Nodes.RemoveAll(n => !snapshotByGuid.ContainsKey(n.Guid));

        var liveByGuid = Nodes.ToDictionary(n => n.Guid);
        foreach (var snap in snapshotNodes)
        {
            if (liveByGuid.TryGetValue(snap.Guid, out var live))
            {
                live.X = snap.X;
                live.Y = snap.Y;
                live.Width = snap.Width;
                live.Height = snap.Height;
                live.IsMarkedForDeletion = snap.IsMarkedForDeletion;
                live.Fields.Clear();
                foreach (var kv in snap.Fields)
                    live.Fields[kv.Key] = kv.Value;
            }
            else
            {
                Nodes.Add(CloneNode(snap));
            }
        }
    }

    // Edges have no external references worth preserving, so a wholesale replace is simplest.
    private void SyncEdges(List<GraphEdgeVm> snapshotEdges)
    {
        Edges.Clear();
        Edges.AddRange(snapshotEdges.Select(CloneEdge));
    }

    private void SyncGroups(List<GraphGroupVm> snapshotGroups)
    {
        var snapshotByClientId = snapshotGroups.ToDictionary(g => g.ClientId);
        Groups.RemoveAll(g => !snapshotByClientId.ContainsKey(g.ClientId));

        var liveByClientId = Groups.ToDictionary(g => g.ClientId);
        foreach (var snap in snapshotGroups)
        {
            if (liveByClientId.TryGetValue(snap.ClientId, out var live))
            {
                live.Title = snap.Title;
                live.ColorR = snap.ColorR;
                live.ColorG = snap.ColorG;
                live.ColorB = snap.ColorB;
                live.ColorA = snap.ColorA;
                live.X = snap.X;
                live.Y = snap.Y;
                live.Width = snap.Width;
                live.Height = snap.Height;
                live.IsMarkedForDeletion = snap.IsMarkedForDeletion;
                live.MemberNodeGuids.Clear();
                foreach (var guid in snap.MemberNodeGuids)
                    live.MemberNodeGuids.Add(guid);
            }
            else
            {
                Groups.Add(CloneGroup(snap));
            }
        }
    }

    private static GraphNodeVm CloneNode(GraphNodeVm n) => new()
    {
        Guid = n.Guid,
        TypeName = n.TypeName,
        X = n.X,
        Y = n.Y,
        Width = n.Width,
        Height = n.Height,
        Fields = new Dictionary<string, object?>(n.Fields),
        Extra = CloneExtra(n.Extra),
        PositionExtra = CloneExtra(n.PositionExtra),
        IsNew = n.IsNew,
        IsMarkedForDeletion = n.IsMarkedForDeletion,
        BaselineX = n.BaselineX,
        BaselineY = n.BaselineY,
    };

    private static GraphEdgeVm CloneEdge(GraphEdgeVm e) => new()
    {
        FromNodeGuid = e.FromNodeGuid,
        FromPort = e.FromPort,
        FromPortId = e.FromPortId,
        ToNodeGuid = e.ToNodeGuid,
        ToPort = e.ToPort,
        ToPortId = e.ToPortId,
        IsNew = e.IsNew,
        IsMarkedForDeletion = e.IsMarkedForDeletion,
        Extra = CloneExtra(e.Extra),
    };

    private static GraphGroupVm CloneGroup(GraphGroupVm g) => new()
    {
        ClientId = g.ClientId,
        SourceIndex = g.SourceIndex,
        Title = g.Title,
        ColorR = g.ColorR,
        ColorG = g.ColorG,
        ColorB = g.ColorB,
        ColorA = g.ColorA,
        X = g.X,
        Y = g.Y,
        Width = g.Width,
        Height = g.Height,
        MemberNodeGuids = new HashSet<string>(g.MemberNodeGuids),
        Extra = CloneExtra(g.Extra),
        PositionExtra = CloneExtra(g.PositionExtra),
        ColorExtra = CloneExtra(g.ColorExtra),
        IsNew = g.IsNew,
        IsMarkedForDeletion = g.IsMarkedForDeletion,
        BaselineTitle = g.BaselineTitle,
        BaselineMemberNodeGuids = g.BaselineMemberNodeGuids == null ? null : new HashSet<string>(g.BaselineMemberNodeGuids),
    };
}
