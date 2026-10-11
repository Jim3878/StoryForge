using Newtonsoft.Json;

namespace StoryForge.Core.Schemas;

public static class DocumentLoader
{
    public static ProcessNodeSchemaDocument LoadNodeSchemaDocument(string jsonFilePath)
    {
        var json = File.ReadAllText(jsonFilePath);
        return JsonConvert.DeserializeObject<ProcessNodeSchemaDocument>(json)
               ?? new ProcessNodeSchemaDocument();
    }

    public static EnumLabelMapDocument LoadEnumLabelMapDocument(string jsonFilePath)
    {
        var json = File.ReadAllText(jsonFilePath);
        return JsonConvert.DeserializeObject<EnumLabelMapDocument>(json)
               ?? new EnumLabelMapDocument();
    }
}
