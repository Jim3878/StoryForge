namespace StoryForge.Core.Schemas;

public sealed class ProcessNodeSchemaDocument
{
    public int Version { get; set; }
    public List<ProcessNodeTypeSchema> NodeTypes { get; set; } = new();
}

public sealed class ProcessNodeTypeSchema
{
    public string TypeName { get; set; } = string.Empty;
    public string? MenuName { get; set; }
    public string? DisplayName { get; set; }
    public List<ProcessNodePortSchema> Inputs { get; set; } = new();
    public List<ProcessNodePortSchema> Outputs { get; set; } = new();
    public List<ProcessNodeFieldSchema> IdentityFields { get; set; } = new();
}

public sealed class ProcessNodePortSchema
{
    public string FieldName { get; set; } = string.Empty;
    public string PortName { get; set; } = string.Empty;
    public string FieldType { get; set; } = string.Empty;
    public bool AllowMultiple { get; set; }
}

public sealed class ProcessNodeFieldSchema
{
    public string FieldName { get; set; } = string.Empty;
    public string FieldType { get; set; } = string.Empty;
    public int ProtoTag { get; set; }
}
