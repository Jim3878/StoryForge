using Newtonsoft.Json;

namespace StoryForge.Core.Schemas;

public static class DocumentLoader
{
    public static ProcessNodeSchemaDocument LoadNodeSchemaDocument(string jsonFilePath)
    {
        var json = File.ReadAllText(jsonFilePath);
        var document = JsonConvert.DeserializeObject<ProcessNodeSchemaDocument>(json)
                       ?? new ProcessNodeSchemaDocument();

        // Unity's auxiliary schema predates the canonical graph format and still exports assembly-qualified
        // CLR names. StoryForge's model uses the stable JSON type ids exclusively; converting at this one
        // boundary keeps CLR implementation names out of blueprint files, node menus, and content bookkeeping.
        foreach (var nodeType in document.NodeTypes)
            nodeType.TypeName = ToCanonicalTypeId(nodeType.TypeName);
        return document;
    }

    private static string ToCanonicalTypeId(string legacyTypeName) => legacyTypeName.Split(',')[0].Trim() switch
    {
        "Definition.ProcessNode.PlayscriptProcessNode" => "playscript",
        "Definition.ProcessNode.UnlockPassivePlayscriptProcessNode" => "unlock-passive",
        "Definition.ProcessNode.FinishPassivePlayscriptProcessNode" => "finish-passive",
        "Definition.ProcessNode.PlayscriptFlagProcessNode" => "playscript-flag",
        "Definition.ProcessNode.BoolFlagProcessNode" => "bool-flag",
        "Definition.ProcessNode.OpenBoolFlagProcessNode" => "open-bool-flag",
        "Definition.ProcessNode.IntFlagProcessNode" => "int-flag",
        "Definition.ProcessNode.AndProcessNode" => "and",
        "Definition.ProcessNode.OrProcessNode" => "or",
        "Definition.ProcessNode.NotProcessNode" => "not",
        _ => legacyTypeName,
    };

    public static EnumLabelMapDocument LoadEnumLabelMapDocument(string jsonFilePath)
    {
        var json = File.ReadAllText(jsonFilePath);
        return JsonConvert.DeserializeObject<EnumLabelMapDocument>(json)
               ?? new EnumLabelMapDocument();
    }
}
