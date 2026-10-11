using Newtonsoft.Json;
using StoryForge.Core;

namespace StoryForge.Core.StoryOutline;

// Freeform story background — a whole-work outline plus one per-chapter outline (keyed the same way as
// everywhere else, e.g. "A7"). Lives in the standalone external data folder (AppSettings.
// ExternalDataFolder) alongside CharacterCardSettings and the flow graph — not inside the Unity project and
// not per-machine LocalAppData — so an external AI tool pointed at that folder can read/write it directly
// as the one real copy, no export/import round trip.
// The AI writing-guidelines block (tone, format expectations, house rules) used to live here as
// AiWritingGuidelines, but that duplicated — and drifted out of sync with — the same folder's AGENTS.md
// (the file an external Codex/Claude agent actually auto-loads). It now lives only in AGENTS.md; see
// AgentsMdStore.
public sealed class StoryOutlineSettings
{
    private const string FileName = "StoryOutline.json";

    public string GlobalOutline { get; set; } = string.Empty;
    public Dictionary<string, string> ChapterOutlines { get; set; } = new(StringComparer.Ordinal);

    public static StoryOutlineSettings LoadOrDefault(string dataFolder)
    {
        var path = GetFilePath(dataFolder);
        if (!File.Exists(path))
            return new StoryOutlineSettings();

        try
        {
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<StoryOutlineSettings>(json) ?? new StoryOutlineSettings();
        }
        catch
        {
            return new StoryOutlineSettings();
        }
    }

    public void Save(string dataFolder)
    {
        var path = GetFilePath(dataFolder);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
    }

    private static string GetFilePath(string dataFolder)
    {
        return Path.Combine(dataFolder, FileName);
    }
}
