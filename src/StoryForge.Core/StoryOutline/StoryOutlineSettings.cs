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
            return MigrateFromLegacyLocations(dataFolder);

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

    // One-time migration for a machine that already had this file at its old home — the Unity project,
    // where every version of this tool up to the node/save-location redesign kept it, tracked in git.
    // (An older fallback also migrated a legacy AiReferencePrompt/AiWritingGuidelines value here, but that
    // field no longer exists — see the type-level comment.)
    private static StoryOutlineSettings MigrateFromLegacyLocations(string dataFolder)
    {
        try
        {
            var projectRoot = ProjectPaths.ResolveUnityProjectRoot();
            var oldProjectPath = Path.Combine(projectRoot, "Assets", "06.Definition",
                "PlayscriptOfflineToolData", "StoryOutline.json");
            if (File.Exists(oldProjectPath))
            {
                var migrated = JsonConvert.DeserializeObject<StoryOutlineSettings>(File.ReadAllText(oldProjectPath))
                                ?? new StoryOutlineSettings();
                migrated.Save(dataFolder);
                return migrated;
            }
        }
        catch
        {
            // Best-effort — worst case the user retypes it in the 世界書 panel.
        }

        return new StoryOutlineSettings();
    }

    private static string GetFilePath(string dataFolder)
    {
        return Path.Combine(dataFolder, FileName);
    }
}
