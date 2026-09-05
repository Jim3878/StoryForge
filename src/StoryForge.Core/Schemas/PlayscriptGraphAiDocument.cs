namespace StoryForge.Core.Schemas;

public sealed class PlayscriptGraphAiDocument
{
    public int Version { get; set; }
    public string? GraphName { get; set; }
    public List<PlayscriptGraphAiNode> Nodes { get; set; } = new();
    public List<PlayscriptGraphAiEdge> Edges { get; set; } = new();

    // Index in this list is the group's identity (GraphProcessor's Group has no GUID of its own).
    public List<PlayscriptGraphAiGroup> Groups { get; set; } = new();
}

public sealed class PlayscriptGraphAiGroup
{
    public string? Title { get; set; }
    public PlayscriptGraphAiColor? Color { get; set; }
    public PlayscriptGraphAiPosition? Position { get; set; }
    public List<string> InnerNodeGuids { get; set; } = new();
}

public sealed class PlayscriptGraphAiColor
{
    public float R { get; set; }
    public float G { get; set; }
    public float B { get; set; }
    public float A { get; set; } = 1f;
}

public sealed class PlayscriptGraphAiNode
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? CustomName { get; set; }
    public int ComputeOrder { get; set; }
    public PlayscriptGraphAiPosition? Position { get; set; }
    public bool Expanded { get; set; }
    public bool Debug { get; set; }
    public bool Locked { get; set; }
    public Dictionary<string, object> Data { get; set; } = new();
}

public sealed class PlayscriptGraphAiPosition
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}

public sealed class PlayscriptGraphAiEdge
{
    public string FromNode { get; set; } = string.Empty;
    public string FromPort { get; set; } = string.Empty;
    public string? FromPortId { get; set; }
    public string ToNode { get; set; } = string.Empty;
    public string ToPort { get; set; } = string.Empty;
    public string? ToPortId { get; set; }
}
