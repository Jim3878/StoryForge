using Newtonsoft.Json;

namespace StoryForge.Core.Audio;

// Per-file "給 AI 看" notes for the BGM/音效 tabs. Lives in AppSettings.ExternalDataFolder next to
// CharacterCards.json/AGENTS.md — the folder an AI writing playscripts is already working in — keyed by
// file name WITHOUT extension, the same string a playscript's Bgm("...") passes, so an AI reading this can
// use a key verbatim. AGENTS.md documents this file's shape for that reader; keep the two in sync.
public sealed class AudioNotesSettings
{
    private const string FileName = "AudioNotes.json";

    public Dictionary<string, string> Bgm { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Sfx { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Get(AudioCategory category) => category == AudioCategory.Bgm ? Bgm : Sfx;

    // Like CharacterCardSettings, a real file that fails to parse is deliberately NOT swallowed into an
    // empty default — that empty default would get saved straight over the real notes on the next edit.
    public static AudioNotesSettings LoadOrDefault(string dataFolder)
    {
        var path = GetFilePath(dataFolder);
        if (!File.Exists(path))
            return new AudioNotesSettings();

        var loaded = JsonConvert.DeserializeObject<AudioNotesSettings>(File.ReadAllText(path)) ?? new AudioNotesSettings();

        // Newtonsoft replaces the initializer dictionaries wholesale, dropping the Ordinal comparer and
        // possibly leaving null for a section missing from the file.
        loaded.Bgm = new Dictionary<string, string>(loaded.Bgm ?? new(), StringComparer.Ordinal);
        loaded.Sfx = new Dictionary<string, string>(loaded.Sfx ?? new(), StringComparer.Ordinal);
        return loaded;
    }

    // Read-modify-write against the file on disk rather than a copy held in memory: the BGM and 音效 tabs
    // each hold their own view of this one shared file, and an external AI tool may edit it too, so saving
    // a whole stale in-memory copy would silently revert whatever the other side wrote since it loaded.
    // Empty text removes the key instead of storing "", keeping the file to notes that actually exist.
    public static AudioNotesSettings SetNote(string dataFolder, AudioCategory category, string name, string text)
    {
        var settings = LoadOrDefault(dataFolder);
        var section = settings.Get(category);
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            section.Remove(name);
        else
            section[name] = trimmed;

        settings.Save(dataFolder);
        return settings;
    }

    public void Save(string dataFolder)
    {
        var path = GetFilePath(dataFolder);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Sorted so the file diffs cleanly in the external data folder's git history instead of reshuffling
        // on every edit.
        var sorted = new
        {
            Bgm = new SortedDictionary<string, string>(Bgm, StringComparer.Ordinal),
            Sfx = new SortedDictionary<string, string>(Sfx, StringComparer.Ordinal),
        };
        File.WriteAllText(path, JsonConvert.SerializeObject(sorted, Formatting.Indented));
    }

    private static string GetFilePath(string dataFolder) => Path.Combine(dataFolder, FileName);
}
