namespace StoryForge.Core.Changelist;

public sealed class GraphChangelistDocument
{
    public int Version { get; set; } = 1;
    public DateTime CreatedAtUtc { get; set; }
    public string GraphName { get; set; } = string.Empty;

    // SHA-256 hash of the raw AI YAML text this changelist was built against. Unity re-exports and
    // re-hashes its current live graph the same way at import time — if the hashes don't match, the
    // graph (nodes, edges, or groups) changed underneath this changelist since it was built, which makes
    // the group operations below (identified by list position, since GraphProcessor's Group has no GUID
    // of its own) unreliable.
    public string BaseYamlHash { get; set; } = string.Empty;

    public List<GraphChangelistNode> AddedNodes { get; set; } = new();
    public List<string> DeletedNodeGuids { get; set; } = new();
    public List<GraphChangelistNodeMove> ModifiedNodes { get; set; } = new();
    public List<GraphChangelistEdge> AddedEdges { get; set; } = new();
    public List<GraphChangelistEdge> DeletedEdges { get; set; } = new();
    public List<GraphChangelistGroup> AddedGroups { get; set; } = new();
    public List<int> DeletedGroupSourceIndices { get; set; } = new();
    public List<GraphChangelistGroupModification> ModifiedGroups { get; set; } = new();

    public bool HasAnyChange =>
        AddedNodes.Count > 0 || DeletedNodeGuids.Count > 0 || ModifiedNodes.Count > 0 ||
        AddedEdges.Count > 0 || DeletedEdges.Count > 0 ||
        AddedGroups.Count > 0 || DeletedGroupSourceIndices.Count > 0 || ModifiedGroups.Count > 0;
}

public sealed class GraphChangelistNode
{
    public string Guid { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public Dictionary<string, object?> Data { get; set; } = new();
}

// An existing (not new, not deleted) node whose position differs from where it was when this session was
// loaded — Guid identifies it in Unity, X/Y is only its new position.
public sealed class GraphChangelistNodeMove
{
    public string Guid { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
}

public sealed class GraphChangelistEdge
{
    public string FromNode { get; set; } = string.Empty;
    public string FromPort { get; set; } = string.Empty;
    public string? FromPortId { get; set; }
    public string ToNode { get; set; } = string.Empty;
    public string ToPort { get; set; } = string.Empty;
    public string? ToPortId { get; set; }
}

public sealed class GraphChangelistGroup
{
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
}

// SourceIndex identifies the existing group by its position in Unity's graph.groups list at the time
// this changelist was built — see the BaseYamlHash remark above for why that can go stale.
public sealed class GraphChangelistGroupModification
{
    public int SourceIndex { get; set; }
    public string Title { get; set; } = string.Empty;
    public List<string> MemberNodeGuids { get; set; } = new();
}
