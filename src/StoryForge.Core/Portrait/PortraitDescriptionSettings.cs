using Newtonsoft.Json;

namespace StoryForge.Core.Portrait;

// User-authored notes on what a specific portrait/expression variant actually conveys — a Face enum label
// like "生氣" doesn't fully capture usage context (e.g. "半嘲諷的生氣" vs "真的暴怒"), so this lets the user
// tag each variant with that nuance. Keyed by file name, which is unique per asset. Feeds the AI reference
// export so expression assignment isn't guesswork; kept separate from AppSettings since this is per-asset
// data, not a single tunable value.
public sealed class PortraitDescriptionSettings
{
    private const string SettingsFileName = "portrait-descriptions.json";

    public Dictionary<string, string> DescriptionsByFileName { get; set; } = new(StringComparer.Ordinal);

    public static PortraitDescriptionSettings LoadOrDefault()
    {
        var path = GetSettingsFilePath();
        if (!File.Exists(path))
            return new PortraitDescriptionSettings();

        try
        {
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<PortraitDescriptionSettings>(json) ?? new PortraitDescriptionSettings();
        }
        catch
        {
            return new PortraitDescriptionSettings();
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
