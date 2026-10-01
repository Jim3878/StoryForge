using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StoryForge.Core.Graph;

public sealed class CanonicalGraphDocument
{
    [JsonProperty("schemaVersion", Order = 1)]
    public int SchemaVersion { get; set; } = CanonicalGraphJson.SchemaVersion;

    [JsonProperty("graphId", Order = 2)]
    public string GraphId { get; set; } = CanonicalGraphJson.GraphId;

    [JsonProperty("nodes", Order = 3)]
    public List<CanonicalGraphNode> Nodes { get; set; } = new();

    [JsonProperty("edges", Order = 4)]
    public List<CanonicalGraphEdge> Edges { get; set; } = new();

    [JsonProperty("groups", Order = 5)]
    public List<CanonicalGraphGroup> Groups { get; set; } = new();

    [JsonProperty("stickyNotes", Order = 6)]
    public List<CanonicalGraphStickyNote> StickyNotes { get; set; } = new();

    [JsonExtensionData]
    public IDictionary<string, JToken> Extra { get; set; } = new Dictionary<string, JToken>();
}

public sealed class CanonicalGraphNode
{
    [JsonProperty("id", Order = 1)] public string Id { get; set; } = string.Empty;
    [JsonProperty("type", Order = 2)] public string Type { get; set; } = string.Empty;
    [JsonProperty("position", Order = 3)] public CanonicalGraphRect Position { get; set; } = new();
    [JsonProperty("data", Order = 4)] public JObject Data { get; set; } = new();
    [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; } = new Dictionary<string, JToken>();
}

public sealed class CanonicalGraphEdge
{
    [JsonProperty("fromNode", Order = 1)] public string FromNode { get; set; } = string.Empty;
    [JsonProperty("fromPort", Order = 2)] public string FromPort { get; set; } = string.Empty;
    [JsonProperty("fromPortId", Order = 3, NullValueHandling = NullValueHandling.Ignore)] public string? FromPortId { get; set; }
    [JsonProperty("toNode", Order = 4)] public string ToNode { get; set; } = string.Empty;
    [JsonProperty("toPort", Order = 5)] public string ToPort { get; set; } = string.Empty;
    [JsonProperty("toPortId", Order = 6, NullValueHandling = NullValueHandling.Ignore)] public string? ToPortId { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; } = new Dictionary<string, JToken>();

    [JsonIgnore]
    public string Key => $"{FromNode}.{FromPort}.{FromPortId ?? string.Empty}->{ToNode}.{ToPort}.{ToPortId ?? string.Empty}";
}

public sealed class CanonicalGraphGroup
{
    [JsonProperty("id", Order = 1)] public string Id { get; set; } = string.Empty;
    [JsonProperty("title", Order = 2)] public string Title { get; set; } = string.Empty;
    [JsonProperty("color", Order = 3)] public CanonicalGraphColor Color { get; set; } = new();
    [JsonProperty("position", Order = 4)] public CanonicalGraphRect Position { get; set; } = new();
    [JsonProperty("nodes", Order = 5)] public List<string> Nodes { get; set; } = new();
    [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; } = new Dictionary<string, JToken>();
}

public sealed class CanonicalGraphStickyNote
{
    [JsonProperty("id", Order = 1)] public string Id { get; set; } = string.Empty;
    [JsonProperty("title", Order = 2)] public string Title { get; set; } = string.Empty;
    [JsonProperty("content", Order = 3)] public string Content { get; set; } = string.Empty;
    [JsonProperty("position", Order = 4)] public CanonicalGraphRect Position { get; set; } = new();
    [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; } = new Dictionary<string, JToken>();
}

public sealed class CanonicalGraphRect
{
    [JsonProperty("x", Order = 1)] public float X { get; set; }
    [JsonProperty("y", Order = 2)] public float Y { get; set; }
    [JsonProperty("width", Order = 3)] public float Width { get; set; }
    [JsonProperty("height", Order = 4)] public float Height { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; } = new Dictionary<string, JToken>();
}

public sealed class CanonicalGraphColor
{
    [JsonProperty("r", Order = 1)] public float R { get; set; }
    [JsonProperty("g", Order = 2)] public float G { get; set; }
    [JsonProperty("b", Order = 3)] public float B { get; set; }
    [JsonProperty("a", Order = 4)] public float A { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; } = new Dictionary<string, JToken>();
}

public sealed class GraphDraftDocument
{
    [JsonProperty("baseGraph", Order = 1)] public CanonicalGraphDocument BaseGraph { get; set; } = new();
    [JsonProperty("graph", Order = 2)] public CanonicalGraphDocument Graph { get; set; } = new();
    [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; } = new Dictionary<string, JToken>();
}
