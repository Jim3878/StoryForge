using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StoryForge.Core.Schemas;

namespace StoryForge.Core.Codegen;

// PlayscriptNames.json for the AI filling playscript sheets: the 名字/表情 values PlayscriptFactoryCodeGenerator
// accepts, taken from the same enum label map (EnumLabelMapSource, via EnumLabelMaps, so the lists can
// never drift from what the C# export actually checks). Without it the AI has no way to know the 名字 column is a
// closed list and only finds out at C# export time.
public static class PlayscriptNameListExport
{
    public const string ExportFileName = "PlayscriptNames.json";

    // Only writes when the content actually changed — the external data folder is a git repo, and this runs
    // on every 流程狀態 refresh.
    public static bool WriteIfChanged(EnumLabelMapDocument document, string folder)
    {
        var path = Path.Combine(folder, ExportFileName);
        var json = BuildJson(EnumLabelMaps.FromDocument(document));
        if (File.Exists(path) && File.ReadAllText(path) == json)
            return false;

        File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
        return true;
    }

    public static string BuildJson(EnumLabelMaps maps)
    {
        var root = new JObject
        {
            ["DialogNames"] = new JArray(maps.DialogNameMap.Keys),
            ["PortraitNames"] = new JArray(maps.PortraitMap.Keys),
            ["Faces"] = new JArray(maps.FaceMap.Keys),
            ["ChatNames"] = new JArray(maps.ChatNameMap.Keys),
        };
        return root.ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n";
    }
}
