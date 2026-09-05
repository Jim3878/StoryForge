namespace StoryForge.Core.Schemas;

public sealed class EnumLabelMapDocument
{
    public int Version { get; set; }
    public Dictionary<string, List<EnumLabelEntry>> Enums { get; set; } = new();
}

public sealed class EnumLabelEntry
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
