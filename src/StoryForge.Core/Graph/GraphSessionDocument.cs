namespace StoryForge.Core.Graph;

// A full snapshot of a GraphDocumentModel's editable state — unlike GraphChangelistDocument (which only
// carries the delta to send to Unity), this captures everything needed to resume exactly where an
// editing session left off, purely locally. Saving/loading this never touches Unity in any way.
//
// Edges live in the sibling GraphConnectionsDocument (its own file), not here — split out so the
// node/group canvas data (positions, sizes, identity fields — pure "what does the graph look like")
// and the connection/logic data (which node feeds into which — "what does the graph mean") can
// eventually be handed to different consumers independently, without one giant file mixing rendering
// noise (X/Y/Width/Height) into what an external reader actually cares about.
public sealed class GraphSessionDocument
{
    public int Version { get; set; } = 1;
    public DateTime SavedAtUtc { get; set; }
    public string GraphName { get; set; } = string.Empty;
    public string BaseYamlHash { get; set; } = string.Empty;
    public List<GraphSessionNode> Nodes { get; set; } = new();
    public List<GraphSessionGroup> Groups { get; set; } = new();
}

// Sibling of GraphSessionDocument — see its own comment for why edges live in their own file/document
// rather than inline here.
public sealed class GraphConnectionsDocument
{
    public int Version { get; set; } = 1;
    public DateTime SavedAtUtc { get; set; }
    public List<GraphSessionEdge> Edges { get; set; } = new();
}

public sealed class GraphSessionNode
{
    public string Guid { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public Dictionary<string, object?> Fields { get; set; } = new();
    public bool IsNew { get; set; }
    public bool IsMarkedForDeletion { get; set; }

    // Preserved as-is from GraphNodeVm's own baseline fields so changelist "moved node" detection stays
    // correct across a save/reload of the session — see GraphSessionGroup's matching remark.
    public float? BaselineX { get; set; }
    public float? BaselineY { get; set; }
}

public sealed class GraphSessionEdge
{
    public string FromNodeGuid { get; set; } = string.Empty;
    public string FromPort { get; set; } = string.Empty;
    public string? FromPortId { get; set; }
    public string ToNodeGuid { get; set; } = string.Empty;
    public string ToPort { get; set; } = string.Empty;
    public string? ToPortId { get; set; }
    public bool IsNew { get; set; }
    public bool IsMarkedForDeletion { get; set; }
}

public sealed class GraphSessionGroup
{
    public string ClientId { get; set; } = string.Empty;
    public int? SourceIndex { get; set; }
    public string Title { get; set; } = string.Empty;
    public float ColorR { get; set; }
    public float ColorG { get; set; }
    public float ColorB { get; set; }
    public float ColorA { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public List<string> MemberNodeGuids { get; set; } = new();
    public bool IsNew { get; set; }
    public bool IsMarkedForDeletion { get; set; }

    // Preserved as-is from GraphNodeVm/GraphGroupVm's own baseline fields (see GraphDocumentModel.Load) so
    // changelist "modified group" detection stays correct across a save/reload of the session — it must
    // keep comparing against what Unity actually had, not against whatever the last local save looked like.
    public string? BaselineTitle { get; set; }
    public List<string>? BaselineMemberNodeGuids { get; set; }
}
