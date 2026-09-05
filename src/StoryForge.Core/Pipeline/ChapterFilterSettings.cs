using Newtonsoft.Json;

namespace StoryForge.Core.Pipeline;

// Remembered chapter-filter selection for the pipeline status panel — kept separate from AppSettings
// since this is auto-remembered UI state, not a user-tunable setting surfaced in the Settings dialog.
public sealed class ChapterFilterSettings
{
    private const string SettingsFileName = "pipeline-filter.json";

    public List<string> SelectedChapters { get; set; } = new();

    // Null means "never saved before" (first run, or the file is missing/corrupt) — distinct from a saved
    // empty list, which means the user deliberately deselected every chapter last time.
    public static ChapterFilterSettings? LoadOrNull()
    {
        var path = GetSettingsFilePath();
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<ChapterFilterSettings>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Save()
    {
        var path = GetSettingsFilePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
    }

    private static string GetSettingsFilePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StoryForge",
            SettingsFileName);
    }
}
