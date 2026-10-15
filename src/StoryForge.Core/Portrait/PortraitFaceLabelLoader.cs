using StoryForge.Core.Schemas;

namespace StoryForge.Core.Portrait;

// Shared between the portrait browser panel (its own "複製表情中文名") and the AI reference export — both
// need the same Face enum member-name -> Chinese label lookup, rebuilt from the Face enum's C# source
// (EnumLabelMapSource) since this standalone tool has no assembly reference to the actual Face enum type.
public static class PortraitFaceLabelLoader
{
    public static Dictionary<string, string> LoadFaceLabelMap(string projectRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var document = EnumLabelMapSource.RefreshOrLoadExisting(projectRoot, out _);
        if (document == null)
            return result;

        foreach (var entry in document.Enums.GetValueOrDefault("Face", new List<EnumLabelEntry>()))
            result[entry.Name] = entry.Label;

        return result;
    }
}
