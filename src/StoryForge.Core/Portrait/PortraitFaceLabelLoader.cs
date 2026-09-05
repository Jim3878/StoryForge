using StoryForge.Core.Schemas;

namespace StoryForge.Core.Portrait;

// Shared between the portrait browser panel (its own "複製表情中文名") and the AI reference export — both
// need the same Face enum member-name -> Chinese label lookup, read from the already-exported enum map
// JSON since this standalone tool has no assembly reference to the actual Face enum type.
public static class PortraitFaceLabelLoader
{
    public static Dictionary<string, string> LoadFaceLabelMap(string projectRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var enumMapPath = Path.Combine(projectRoot, "Assets", "06.Definition", "PlayscriptOfflineExport",
            "PlayscriptEnumLabelMaps.json");
        if (!File.Exists(enumMapPath))
            return result;

        var document = DocumentLoader.LoadEnumLabelMapDocument(enumMapPath);
        foreach (var entry in document.Enums.GetValueOrDefault("Face", new List<EnumLabelEntry>()))
            result[entry.Name] = entry.Label;

        return result;
    }
}
