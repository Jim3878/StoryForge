using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StoryForge.Core.StoryOutline;

// Freeform story background for the AI reference export — a whole-work outline plus one per-chapter
// outline (keyed the same way as everywhere else, e.g. "A7"), plus a single global AI writing-guidelines
// block (tone, format expectations, house rules — not per-chapter, applies to every export). Stored inside
// the Unity project (not per-machine LocalAppData) and tracked in git, same reasoning as
// CharacterCardSettings: this is shared, collaboratively-written content, and a plain JSON file lets an
// external AI tool edit it directly too.
public sealed class StoryOutlineSettings
{
    private const string RelativeFilePath = "Assets/06.Definition/PlayscriptOfflineToolData/StoryOutline.json";

    public string GlobalOutline { get; set; } = string.Empty;
    public string AiWritingGuidelines { get; set; } = string.Empty;
    public Dictionary<string, string> ChapterOutlines { get; set; } = new(StringComparer.Ordinal);

    public static StoryOutlineSettings LoadOrDefault(string projectRoot)
    {
        var path = GetFilePath(projectRoot);
        if (!File.Exists(path))
            return MigrateFromLegacyAppSettings(projectRoot);

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

    public void Save(string projectRoot)
    {
        var path = GetFilePath(projectRoot);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
    }

    // One-time migration from AppSettings.AiReferencePrompt (removed now that this panel is the one place
    // that field lives) — reads the legacy value directly out of the raw per-machine settings.json rather
    // than a typed AppSettings property, since the property itself no longer exists.
    private static StoryOutlineSettings MigrateFromLegacyAppSettings(string projectRoot)
    {
        var result = new StoryOutlineSettings();

        try
        {
            var legacySettingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StoryForge", "settings.json");
            if (!File.Exists(legacySettingsPath))
                return result;

            var root = JObject.Parse(File.ReadAllText(legacySettingsPath));
            var prompt = root["AiReferencePrompt"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                result.AiWritingGuidelines = prompt;
                result.Save(projectRoot);
            }
        }
        catch
        {
            // Best-effort migration — worst case the user retypes it in the 世界書 panel.
        }

        return result;
    }

    private static string GetFilePath(string projectRoot)
    {
        return Path.Combine(projectRoot, RelativeFilePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
