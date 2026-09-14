using Newtonsoft.Json;
using StoryForge.Core.Changelist;
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
    public float ColorR { get; set; } = 0.3f;
    public float ColorG { get; set; } = 0.5f;
    public float ColorB { get; set; } = 0.8f;
    public float ColorA { get; set; } = 0.3f;

    // Always derived from member node positions (see GraphDocumentModel.RecomputeGroupBounds) except
    // for the placeholder state of a brand-new, still-empty group — never set directly from the UI.
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; } = EmptyPlaceholderWidth;
    public float Height { get; set; } = EmptyPlaceholderHeight;

    public HashSet<string> MemberNodeGuids { get; init; } = new();
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

    public string EdgeKey => $"{FromNodeGuid}.{FromPort}.{FromPortId}->{ToNodeGuid}.{ToPort}.{ToPortId}";
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

    public string GraphName { get; private set; } = string.Empty;

    // SHA-256 of the raw AI YAML text this model was loaded from — carried into any changelist built
    // from this model so Unity can detect at import time whether the graph changed since export.
    public string BaseYamlHash { get; private set; } = string.Empty;

    private readonly Stack<GraphDocumentSnapshot> _undoStack = new();
    private readonly Stack<GraphDocumentSnapshot> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    // Fires whenever the set of nodes could have changed (added, deleted, or restored via undo/redo) —
    // not edges/groups, since the only current listener (the pipeline status panel's "流程圖" column) only
    // cares about which playscript identities exist in the graph.
    public event Action? NodesChanged;

    // Serialized fingerprint of the state as of the last load or local session save — HasUnsavedChanges
    // just compares the current state against this, rather than tracking dirtiness on every mutation.
    private string? _lastCleanSessionJson;
    private string? _lastCleanConnectionsJson;

    private GraphDocumentModel(ProcessNodeSchemaDocument schema, AppSettings settings)
    {
        Schema = schema;
        Settings = settings;
    }

    public static GraphDocumentModel Load(
        PlayscriptGraphAiDocument document, ProcessNodeSchemaDocument schema, AppSettings settings,
        string baseYamlHash)
    {
        var model = new GraphDocumentModel(schema, settings)
        {
            GraphName = document.GraphName ?? string.Empty,
            BaseYamlHash = baseYamlHash,
        };

        foreach (var node in document.Nodes)
        {
            var x = node.Position?.X ?? 0;
            var y = node.Position?.Y ?? 0;
            model.Nodes.Add(new GraphNodeVm
            {
                Guid = node.Id,
                TypeName = node.Type,
                X = x,
                Y = y,
                Width = node.Position?.Width > 0 ? node.Position.Width : 220,
                Height = node.Position?.Height > 0 ? node.Position.Height : 140,
                Fields = node.Data.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                BaselineX = x,
                BaselineY = y,
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
            });
        }

        // GraphProcessor's own GroupView visually auto-fits to its members live in the Unity editor, but the
        // serialized group.position/size only gets written back at specific trigger points — it can be stale
        // relative to where the members actually are. So the loaded Position/Size is never trusted for layout;
        // it's discarded in favor of a fresh RecomputeGroupBounds pass below, matching what Unity shows live.
        for (var i = 0; i < document.Groups.Count; i++)
        {
            var group = document.Groups[i];
            var title = string.IsNullOrEmpty(group.Title) ? "群組" : group.Title!;
            var memberGuids = new HashSet<string>(group.InnerNodeGuids);
            var vm = new GraphGroupVm
            {
                ClientId = System.Guid.NewGuid().ToString("D"),
                SourceIndex = i,
                Title = title,
                ColorR = group.Color?.R ?? 0.3f,
                ColorG = group.Color?.G ?? 0.5f,
                ColorB = group.Color?.B ?? 0.8f,
                ColorA = group.Color?.A ?? 0.3f,
                X = group.Position?.X ?? 0,
                Y = group.Position?.Y ?? 0,
                MemberNodeGuids = memberGuids,
                BaselineTitle = title,
                BaselineMemberNodeGuids = new HashSet<string>(memberGuids),
            };
            model.Groups.Add(vm);
            model.RecomputeGroupBounds(vm);
        }

        model.MarkClean();
        return model;
    }

    // Resumes an editing session from a local save file instead of Unity's exported YAML — used when the
    // app finds a saved session on startup. Trusts the session's own IsNew/IsMarkedForDeletion/baseline
    // fields verbatim rather than re-deriving anything, since it's meant to reproduce exactly the state
    // that was saved.
    public static GraphDocumentModel LoadFromSession(
        GraphSessionDocument session, GraphConnectionsDocument connections, ProcessNodeSchemaDocument schema,
        AppSettings settings)
    {
        var model = new GraphDocumentModel(schema, settings)
        {
            GraphName = session.GraphName,
            BaseYamlHash = session.BaseYamlHash,
        };

        foreach (var node in session.Nodes)
        {
            // A session file saved before move-tracking existed has no BaselineX/Y at all (both come back
            // null here) — falling back to the node's current position as its baseline can't recover
            // movement that already happened before this fix, but it stops every existing node from being
            // permanently unable to have movement detected ever again; any further move from here on is
            // tracked correctly.
            var baselineX = node.IsNew ? (float?)null : node.BaselineX ?? node.X;
            var baselineY = node.IsNew ? (float?)null : node.BaselineY ?? node.Y;

            model.Nodes.Add(new GraphNodeVm
            {
                Guid = node.Guid,
                TypeName = node.TypeName,
                X = node.X,
                Y = node.Y,
                Width = node.Width,
                Height = node.Height,
                Fields = new Dictionary<string, object?>(node.Fields),
                IsNew = node.IsNew,
                IsMarkedForDeletion = node.IsMarkedForDeletion,
                BaselineX = baselineX,
                BaselineY = baselineY,
            });
        }

        foreach (var edge in connections.Edges)
        {
            model.Edges.Add(new GraphEdgeVm
            {
                FromNodeGuid = edge.FromNodeGuid,
                FromPort = edge.FromPort,
                FromPortId = edge.FromPortId,
                ToNodeGuid = edge.ToNodeGuid,
                ToPort = edge.ToPort,
                ToPortId = edge.ToPortId,
                IsNew = edge.IsNew,
                IsMarkedForDeletion = edge.IsMarkedForDeletion,
            });
        }

        foreach (var group in session.Groups)
        {
            model.Groups.Add(new GraphGroupVm
            {
                ClientId = group.ClientId,
                SourceIndex = group.SourceIndex,
                Title = group.Title,
                ColorR = group.ColorR,
                ColorG = group.ColorG,
                ColorB = group.ColorB,
                ColorA = group.ColorA,
                X = group.X,
                Y = group.Y,
                Width = group.Width,
                Height = group.Height,
                MemberNodeGuids = new HashSet<string>(group.MemberNodeGuids),
                IsNew = group.IsNew,
                IsMarkedForDeletion = group.IsMarkedForDeletion,
                BaselineTitle = group.BaselineTitle,
                BaselineMemberNodeGuids = group.BaselineMemberNodeGuids == null
                    ? null
                    : new HashSet<string>(group.BaselineMemberNodeGuids),
            });
        }

        model.MarkClean();
        return model;
    }

    public GraphSessionDocument CreateSessionDocument()
    {
        return new GraphSessionDocument
        {
            SavedAtUtc = DateTime.UtcNow,
            GraphName = GraphName,
            BaseYamlHash = BaseYamlHash,
            Nodes = Nodes.Select(n => new GraphSessionNode
            {
                Guid = n.Guid,
                TypeName = n.TypeName,
                X = n.X,
                Y = n.Y,
                Width = n.Width,
                Height = n.Height,
                Fields = new Dictionary<string, object?>(n.Fields),
                IsNew = n.IsNew,
                IsMarkedForDeletion = n.IsMarkedForDeletion,
                BaselineX = n.BaselineX,
                BaselineY = n.BaselineY,
            }).ToList(),
            Groups = Groups.Select(g => new GraphSessionGroup
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
                MemberNodeGuids = g.MemberNodeGuids.ToList(),
                IsNew = g.IsNew,
                IsMarkedForDeletion = g.IsMarkedForDeletion,
                BaselineTitle = g.BaselineTitle,
                BaselineMemberNodeGuids = g.BaselineMemberNodeGuids?.ToList(),
            }).ToList(),
        };
    }

    // Sibling of CreateSessionDocument — see GraphConnectionsDocument's own comment for why edges are
    // serialized into their own document/file instead of being part of the one above.
    public GraphConnectionsDocument CreateConnectionsDocument()
    {
        return new GraphConnectionsDocument
        {
            SavedAtUtc = DateTime.UtcNow,
            Edges = Edges.Select(e => new GraphSessionEdge
            {
                FromNodeGuid = e.FromNodeGuid,
                FromPort = e.FromPort,
                FromPortId = e.FromPortId,
                ToNodeGuid = e.ToNodeGuid,
                ToPort = e.ToPort,
                ToPortId = e.ToPortId,
                IsNew = e.IsNew,
                IsMarkedForDeletion = e.IsMarkedForDeletion,
            }).ToList(),
        };
    }

    // Cheap dirty-check: re-serializes the current state and compares it against the fingerprint captured
    // at the last load or local save, rather than tracking dirtiness through every individual mutation.
    // Checked against both documents now that edges live apart from nodes/groups — an edge-only change
    // (e.g. a rewired wire, no node moved) must still count as unsaved.
    public bool HasUnsavedChanges()
    {
        return SerializeForFingerprint(CreateSessionDocument()) != _lastCleanSessionJson ||
               SerializeForFingerprint(CreateConnectionsDocument()) != _lastCleanConnectionsJson;
    }

    public void MarkClean()
    {
        _lastCleanSessionJson = SerializeForFingerprint(CreateSessionDocument());
        _lastCleanConnectionsJson = SerializeForFingerprint(CreateConnectionsDocument());
    }

    private static string SerializeForFingerprint(GraphSessionDocument document)
    {
        // SavedAtUtc always differs between calls and isn't part of what "unsaved changes" means — excluded
        // by serializing a copy with it zeroed out rather than by hand-writing a separate comparator.
        var normalized = new GraphSessionDocument
        {
            Version = document.Version,
            SavedAtUtc = default,
            GraphName = document.GraphName,
            BaseYamlHash = document.BaseYamlHash,
            Nodes = document.Nodes,
            Groups = document.Groups,
        };
        return JsonConvert.SerializeObject(normalized);
    }

    private static string SerializeForFingerprint(GraphConnectionsDocument document)
    {
        var normalized = new GraphConnectionsDocument
        {
            Version = document.Version,
            SavedAtUtc = default,
            Edges = document.Edges,
        };
        return JsonConvert.SerializeObject(normalized);
    }

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
            node.Fields[field.FieldName] = string.Empty;

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

        // The node is actually being removed here (either purged immediately, IsNew, or marked and
        // excluded from the next persisted session) — its content files (if any) would otherwise become
        // orphans, permanently occupying their plain-name path for whichever unrelated node types that
        // name next (see NodeContentStore.ResolveBasePath's own collision-suffix comment).
        if (node.TypeName == NodeContentStore.PlayscriptProcessNodeTypeName && !string.IsNullOrEmpty(node.IdentityValue))
        {
            NodeContentStore.DeleteFile(Settings.ExternalDataFolder, node.IdentityValue!, node.Guid);
            NodeScriptTableStore.DeleteFile(Settings.ExternalDataFolder, node.IdentityValue!, node.Guid);
        }

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

    public GraphGroupVm AddGroup(string title, float x, float y)
    {
        var group = new GraphGroupVm
        {
            ClientId = System.Guid.NewGuid().ToString("D"),
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
        var members = Nodes.Where(n => group.MemberNodeGuids.Contains(n.Guid)).ToList();
        if (members.Count == 0)
            return;

        var minX = members.Min(n => n.X);
        var minY = members.Min(n => n.Y);
        var maxX = members.Max(n => n.X + n.Width);
        var maxY = members.Max(n => n.Y + n.Height);

        var padding = Settings.Padding;
        var titleBarHeight = Settings.TitleBarHeight;

        group.X = minX - padding;
        group.Y = minY - titleBarHeight;
        group.Width = maxX - minX + padding * 2f;
        group.Height = maxY - minY + titleBarHeight + padding;
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
        IsNew = g.IsNew,
        IsMarkedForDeletion = g.IsMarkedForDeletion,
        BaselineTitle = g.BaselineTitle,
        BaselineMemberNodeGuids = g.BaselineMemberNodeGuids == null ? null : new HashSet<string>(g.BaselineMemberNodeGuids),
    };

    // Projects the current session's edits (IsNew/IsMarkedForDeletion nodes and edges, plus groups whose
    // title or membership differ from their as-loaded baseline) into a standalone changelist document —
    // the only thing the standalone tool ever writes for Unity to import back.
    public GraphChangelistDocument BuildChangelist()
    {
        var doc = new GraphChangelistDocument
        {
            CreatedAtUtc = DateTime.UtcNow,
            GraphName = GraphName,
            BaseYamlHash = BaseYamlHash,
        };

        foreach (var node in Nodes.Where(n => n.IsNew && !n.IsMarkedForDeletion))
        {
            doc.AddedNodes.Add(new GraphChangelistNode
            {
                Guid = node.Guid,
                Type = node.TypeName,
                X = node.X,
                Y = node.Y,
                Width = node.Width,
                Height = node.Height,
                Data = new Dictionary<string, object?>(node.Fields),
            });
        }

        foreach (var node in Nodes.Where(n => n.IsMarkedForDeletion && !n.IsNew))
            doc.DeletedNodeGuids.Add(node.Guid);

        foreach (var node in Nodes.Where(n => !n.IsNew && !n.IsMarkedForDeletion))
        {
            if (node.BaselineX == null || node.BaselineY == null)
                continue;
            if (node.X == node.BaselineX.Value && node.Y == node.BaselineY.Value)
                continue;

            doc.ModifiedNodes.Add(new GraphChangelistNodeMove
            {
                Guid = node.Guid,
                X = node.X,
                Y = node.Y,
            });
        }

        foreach (var edge in Edges.Where(e => e.IsNew && !e.IsMarkedForDeletion))
            doc.AddedEdges.Add(ToChangelistEdge(edge));

        foreach (var edge in Edges.Where(e => e.IsMarkedForDeletion && !e.IsNew))
            doc.DeletedEdges.Add(ToChangelistEdge(edge));

        foreach (var group in Groups.Where(g => g.IsNew && !g.IsMarkedForDeletion))
        {
            doc.AddedGroups.Add(new GraphChangelistGroup
            {
                Title = group.Title,
                ColorR = group.ColorR,
                ColorG = group.ColorG,
                ColorB = group.ColorB,
                ColorA = group.ColorA,
                X = group.X,
                Y = group.Y,
                Width = group.Width,
                Height = group.Height,
                MemberNodeGuids = group.MemberNodeGuids.ToList(),
            });
        }

        foreach (var group in Groups.Where(g => g.SourceIndex != null && g.IsMarkedForDeletion))
            doc.DeletedGroupSourceIndices.Add(group.SourceIndex!.Value);

        foreach (var group in Groups.Where(g => g.SourceIndex != null && !g.IsMarkedForDeletion))
        {
            var titleChanged = group.Title != group.BaselineTitle;
            var membersChanged = group.BaselineMemberNodeGuids == null ||
                                  !group.MemberNodeGuids.SetEquals(group.BaselineMemberNodeGuids);
            if (!titleChanged && !membersChanged)
                continue;

            doc.ModifiedGroups.Add(new GraphChangelistGroupModification
            {
                SourceIndex = group.SourceIndex!.Value,
                Title = group.Title,
                MemberNodeGuids = group.MemberNodeGuids.ToList(),
            });
        }

        return doc;
    }

    // Full snapshot of the current session's live state (every node/edge/group, not just what changed) —
    // used by the "整批匯出" flow so Unity always overwrites its graph from this as the single source of
    // truth, instead of relying on a diff against a possibly-stale baseline (see BuildChangelist above,
    // which still exists only to drive the human-readable review list, not what gets written to disk).
    public PlayscriptGraphAiDocument BuildFullDocument()
    {
        var doc = new PlayscriptGraphAiDocument
        {
            Version = 1,
            GraphName = GraphName,
        };

        foreach (var node in Nodes.Where(n => !n.IsMarkedForDeletion))
        {
            doc.Nodes.Add(new PlayscriptGraphAiNode
            {
                Id = node.Guid,
                Type = node.TypeName,
                Position = new PlayscriptGraphAiPosition
                {
                    X = node.X,
                    Y = node.Y,
                    Width = node.Width,
                    Height = node.Height,
                },
                Data = node.Fields.ToDictionary(kv => kv.Key, kv => kv.Value!),
            });
        }

        foreach (var edge in Edges.Where(e => !e.IsMarkedForDeletion))
            doc.Edges.Add(ToAiEdge(edge));

        foreach (var group in Groups.Where(g => !g.IsMarkedForDeletion))
        {
            doc.Groups.Add(new PlayscriptGraphAiGroup
            {
                Title = group.Title,
                Color = new PlayscriptGraphAiColor
                {
                    R = group.ColorR,
                    G = group.ColorG,
                    B = group.ColorB,
                    A = group.ColorA,
                },
                Position = new PlayscriptGraphAiPosition
                {
                    X = group.X,
                    Y = group.Y,
                    Width = group.Width,
                    Height = group.Height,
                },
                InnerNodeGuids = group.MemberNodeGuids.ToList(),
            });
        }

        return doc;
    }

    private static PlayscriptGraphAiEdge ToAiEdge(GraphEdgeVm edge) => new()
    {
        FromNode = edge.FromNodeGuid,
        FromPort = edge.FromPort,
        FromPortId = edge.FromPortId,
        ToNode = edge.ToNodeGuid,
        ToPort = edge.ToPort,
        ToPortId = edge.ToPortId,
    };

    private static GraphChangelistEdge ToChangelistEdge(GraphEdgeVm edge) => new()
    {
        FromNode = edge.FromNodeGuid,
        FromPort = edge.FromPort,
        FromPortId = edge.FromPortId,
        ToNode = edge.ToNodeGuid,
        ToPort = edge.ToPort,
        ToPortId = edge.ToPortId,
    };
}
