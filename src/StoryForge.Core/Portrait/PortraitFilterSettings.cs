using Newtonsoft.Json;

namespace StoryForge.Core.Portrait;

// Remembered category-filter checkboxes for the portrait browser panel — kept separate from AppSettings
// since this is auto-remembered UI state, not a user-tunable setting surfaced in the Settings dialog.
public sealed class PortraitFilterSettings
{
    private const string SettingsFileName = "portrait-filter.json";

    public bool IncludePortrait { get; set; } = true;
    public bool IncludeBattlePortrait { get; set; } = true;
    public bool IncludeOther { get; set; } = true;

    public static PortraitFilterSettings LoadOrDefault()
    {
        var path = GetSettingsFilePath();
        if (!File.Exists(path))
            return new PortraitFilterSettings();

        try
        {
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<PortraitFilterSettings>(json) ?? new PortraitFilterSettings();
        }
        catch
        {
            return new PortraitFilterSettings();
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
