namespace StoryForge.Web.GraphEditor;

// Wire format handed to graph-editor.js in one shot on load, and read back from it in one shot on save —
// deliberately flat/plain so System.Text.Json can (de)serialize it with zero attributes.
public sealed class GraphEditorPayload
{
    public List<NodeTypeDto> NodeTypes { get; set; } = new();
    public List<NodeDto> Nodes { get; set; } = new();
    public List<EdgeDto> Edges { get; set; } = new();
    public List<GroupDto> Groups { get; set; } = new();
    public GraphSettingsDto Settings { get; set; } = new();
}

// The subset of AppSettings the canvas itself needs at render time — the JS side reads these instead of
// hardcoding them, so the 設定 page's values take effect on the next 重新載入 with no code change.
public sealed class GraphSettingsDto
{
    public float NodeFrozenTextSize { get; set; }
    public float NodeMaxTextSize { get; set; }
    public float GroupFrozenTextSize { get; set; }
    public float GroupMaxTextSize { get; set; }

    // Needed client-side to replicate GraphDocumentModel.RecomputeGroupBounds exactly when a node is
    // dragged into/out of a group — see graph-editor.js's own recomputeGroupBounds.
    public float GroupPadding { get; set; }
    public float GroupTitleBarHeight { get; set; }
}

public sealed class NodeTypeDto
{
    public string TypeName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    // Groups this type under a submenu in the canvas's "add node" menu, matching the original WinForms
    // tool's ShowWiringCreateNodeMenu grouping. Falls back to "其他" client-side when empty.
    public string MenuName { get; set; } = string.Empty;
    public List<PortDto> Inputs { get; set; } = new();
    public List<PortDto> Outputs { get; set; } = new();
    public List<string> IdentityFields { get; set; } = new();
}

public sealed class PortDto
{
    public string FieldName { get; set; } = string.Empty;
    public string PortName { get; set; } = string.Empty;

    // Inputs only: LiteGraph.js input slots each accept one wire, but some of our ports (e.g. an AND
    // node's "input") are meant to take many at once — the client renders one slot per connected wire
    // plus a spare one, all mapped back to this same field name, instead of one slot the wires overwrite.
    public bool AllowMultiple { get; set; }
}

public sealed class NodeDto
{
    public string Guid { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    // True when Title came from a real identity value (playscriptId/flagId) rather than falling back to
    // the node type's generic display name (e.g. "結束劇本", "AND") — the client uses this to decide
    // whether this node's title is allowed to grow past NodeFrozenTextSize when zoomed in at all.
    public bool HasIdentityTitle { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public Dictionary<string, string> Fields { get; set; } = new();
}

public sealed class EdgeDto
{
    public string FromNodeGuid { get; set; } = string.Empty;
    public string FromPort { get; set; } = string.Empty;
    public string ToNodeGuid { get; set; } = string.Empty;
    public string ToPort { get; set; } = string.Empty;
}

public sealed class GroupDto
{
    public string ClientId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public string Color { get; set; } = "#4d80cc";
    public List<string> MemberNodeGuids { get; set; } = new();
}

// What graph-editor.js sends back on export — positions + connections, plus any nodes the user created
// on the canvas itself via the "add node" menu (not full node/edge/group CRUD yet).
public sealed class GraphEditorExport
{
    public List<NodePositionDto> NodePositions { get; set; } = new();
    public List<EdgeDto> Edges { get; set; } = new();
    public List<NewNodeDto> NewNodes { get; set; } = new();
    public List<string> DeletedNodeGuids { get; set; } = new();

    // Client-computed final bounds + membership for each group the client knows about (matched back to
    // _model.Groups by ClientId in ApplyAndSave) — the canvas recomputes these live as nodes are dragged
    // in/out of a group (see graph-editor.js's recomputeGroupBounds), so the export just carries the
    // already-correct end result rather than the server re-deriving it from a diff.
    public List<GroupDto> Groups { get; set; } = new();
}

public sealed class NodePositionDto
{
    public string Guid { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
}

// A node created client-side (via the canvas's add-node menu) since the graph was loaded. The client
// generates the guid itself so it can immediately wire edges to/from the new node before this export
// round-trip ever reaches the server.
public sealed class NewNodeDto
{
    public string Guid { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
}
