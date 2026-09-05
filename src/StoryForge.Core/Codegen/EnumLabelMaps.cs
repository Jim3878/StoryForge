using StoryForge.Core.Schemas;

namespace StoryForge.Core.Codegen;

public sealed class EnumLabelMaps
{
    public Dictionary<string, string> DialogNameMap { get; }
    public Dictionary<string, string> PortraitMap { get; }
    public Dictionary<string, string> ChatNameMap { get; }
    public Dictionary<string, string> FaceMap { get; }

    private EnumLabelMaps(
        Dictionary<string, string> dialogNameMap,
        Dictionary<string, string> portraitMap,
        Dictionary<string, string> chatNameMap,
        Dictionary<string, string> faceMap)
    {
        DialogNameMap = dialogNameMap;
        PortraitMap = portraitMap;
        ChatNameMap = chatNameMap;
        FaceMap = faceMap;
    }

    // PortraitMap intentionally reuses DialogName's labels (not Portrait's own), restricted to the
    // DialogName entries whose enum member name also exists on Portrait — this mirrors
    // GoogleSheetPlayscriptExporter.PortraitMap = CreateEnumLabelMap<DialogName>(typeof(Portrait)) exactly.
    public static EnumLabelMaps FromDocument(EnumLabelMapDocument document)
    {
        var dialogEntries = document.Enums.GetValueOrDefault("DialogName", new List<EnumLabelEntry>());
        var portraitEntries = document.Enums.GetValueOrDefault("Portrait", new List<EnumLabelEntry>());
        var chatEntries = document.Enums.GetValueOrDefault("ChatName", new List<EnumLabelEntry>());
        var faceEntries = document.Enums.GetValueOrDefault("Face", new List<EnumLabelEntry>());

        var portraitNames = portraitEntries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);

        return new EnumLabelMaps(
            BuildLabelMap(dialogEntries),
            BuildLabelMap(dialogEntries.Where(e => portraitNames.Contains(e.Name))),
            BuildLabelMap(chatEntries),
            BuildLabelMap(faceEntries));
    }

    private static Dictionary<string, string> BuildLabelMap(IEnumerable<EnumLabelEntry> entries)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
            result[entry.Label] = entry.Name;

        return result;
    }
}
