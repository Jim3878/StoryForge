using Newtonsoft.Json;

namespace StoryForge.Core.Audio;

public sealed class AudioColumnSetting
{
    public string Key { get; set; } = string.Empty;
    public double Width { get; set; }
}

public sealed class AudioPageSettings
{
    public const string ViewList = "list";
    public const string ViewCard = "card";

    public const double MinColumnWidth = 40;

    public string ViewMode { get; set; } = ViewList;
    public bool AutoPlay { get; set; } = true;
    public bool Loop { get; set; }

    // 0-1. Defaults to half so the very first hover doesn't blast at full volume.
    public double Volume { get; set; } = 0.5;
    public double CardSize { get; set; } = 260;

    // Display order AND widths, together — the list view's header is rendered straight from this.
    public List<AudioColumnSetting> Columns { get; set; } = new();
    public string? SortColumn { get; set; }
    public bool SortDescending { get; set; }

    public static readonly IReadOnlyList<AudioColumnSetting> DefaultColumns = new List<AudioColumnSetting>
    {
        new() { Key = "wave", Width = 260 },
        new() { Key = "name", Width = 260 },
        new() { Key = "duration", Width = 70 },
        new() { Key = "format", Width = 60 },
        new() { Key = "size", Width = 80 },
        new() { Key = "gain", Width = 130 },
        new() { Key = "note", Width = 380 },
    };

    public static AudioPageSettings CreateDefault(AudioCategory category)
    {
        var settings = new AudioPageSettings
        {
            // BGM is mostly auditioned as loops; SFX are one-shots.
            Loop = category == AudioCategory.Bgm,
        };
        settings.NormalizeColumns();
        return settings;
    }

    // Tolerates a saved column list from an older/newer build: unknown keys are dropped, missing ones are
    // appended at their default width, and widths are clamped so a column can never be saved unreachably thin.
    public void NormalizeColumns()
    {
        var known = DefaultColumns.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var result = new List<AudioColumnSetting>();
        foreach (var col in Columns ?? new List<AudioColumnSetting>())
        {
            if (col == null || !known.Contains(col.Key) || result.Any(r => r.Key == col.Key))
                continue;
            result.Add(new AudioColumnSetting { Key = col.Key, Width = Math.Max(MinColumnWidth, col.Width) });
        }

        foreach (var def in DefaultColumns)
        {
            if (result.All(r => r.Key != def.Key))
                result.Add(new AudioColumnSetting { Key = def.Key, Width = def.Width });
        }

        Columns = result;
    }
}

// Per-machine UI state for the BGM/音效 tabs (view mode, playback toggles, volume, column order/width,
// sort), each tab remembered separately. %LocalAppData% rather than the browser's localStorage because
// StoryForge.Launcher opens the app in an isolated Chrome profile that isn't a reliable place to keep it.
public sealed class AudioBrowserSettings
{
    private const string SettingsFileName = "audio-browser-settings.json";
    private static readonly object FileLock = new();

    public AudioPageSettings? Bgm { get; set; }
    public AudioPageSettings? Sfx { get; set; }

    public static AudioPageSettings LoadPage(AudioCategory category)
    {
        lock (FileLock)
        {
            var all = Load();
            var page = (category == AudioCategory.Bgm ? all.Bgm : all.Sfx) ?? AudioPageSettings.CreateDefault(category);
            page.NormalizeColumns();
            page.Volume = Math.Clamp(page.Volume, 0, 1);
            return page;
        }
    }

    // Read-modify-write of just this tab's section, so the BGM and 音效 tabs saving independently never
    // overwrite each other's half of the file.
    public static void SavePage(AudioCategory category, AudioPageSettings page)
    {
        lock (FileLock)
        {
            var all = Load();
            if (category == AudioCategory.Bgm)
                all.Bgm = page;
            else
                all.Sfx = page;

            var path = GetSettingsFilePath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonConvert.SerializeObject(all, Formatting.Indented));
        }
    }

    private static AudioBrowserSettings Load()
    {
        var path = GetSettingsFilePath();
        if (!File.Exists(path))
            return new AudioBrowserSettings();

        try
        {
            return JsonConvert.DeserializeObject<AudioBrowserSettings>(File.ReadAllText(path)) ?? new AudioBrowserSettings();
        }
        catch
        {
            return new AudioBrowserSettings();
        }
    }

    private static string GetSettingsFilePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StoryForge", SettingsFileName);
}
