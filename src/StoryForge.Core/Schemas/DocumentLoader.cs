using Newtonsoft.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace StoryForge.Core.Schemas;

public static class DocumentLoader
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .WithDuplicateKeyChecking()
        .Build();

    public static PlayscriptGraphAiDocument LoadGraphDocument(string yamlFilePath)
    {
        var yaml = File.ReadAllText(yamlFilePath);
        return YamlDeserializer.Deserialize<PlayscriptGraphAiDocument>(yaml)
               ?? new PlayscriptGraphAiDocument();
    }

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
