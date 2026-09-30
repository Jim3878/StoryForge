using Newtonsoft.Json;

namespace StoryForge.Core.Audio;

// What the 音量 features did to one clip, keyed (in AudioAdjustmentSettings) by the clip's Unity .meta GUID
// so a rename inside Unity keeps the record attached. The written file in Assets is always derived from
// the backed-up original in one pass: original -> volume(NormalizeGain + ManualGainDb) -> one encode.
public sealed class AudioAdjustmentRecord
{
    // File name without extension at the last scan — for a human reading the JSON, and for listing orphans.
    public string LastKnownName { get; set; } = string.Empty;

    // Extension of the backed-up original, lower-case, no dot ("ogg"/"wav"...). The backup lives at
    // AudioOriginals/{BGM|SFX}/<guid>.<this>.
    public string OriginalExtension { get; set; } = string.Empty;

    // The original's EBU R128 integrated loudness; null when it couldn't be measured (too short/silent).
    public double? MeasuredLufs { get; set; }

    // The original's sample peak in dBFS.
    public double? OriginalPeakDb { get; set; }

    public bool Normalize { get; set; }

    // The target the normalize gain was computed against when last written. Differing from the current
    // target is what makes a file count as needing re-normalizing.
    public double? NormalizeTargetLufs { get; set; }

    public double ManualGainDb { get; set; }

    // SHA-256 (hex) of the file this tool last wrote into Assets — a mismatch means something outside the
    // tool replaced it, and the backup is no longer that file's original.
    public string WrittenSha256 { get; set; } = string.Empty;

    [JsonIgnore]
    public double NormalizeGainDb =>
        Normalize && MeasuredLufs is { } lufs && NormalizeTargetLufs is { } target ? target - lufs : 0;

    [JsonIgnore]
    public double TotalGainDb => NormalizeGainDb + ManualGainDb;
}

// Shared, tracked in the Unity project's git (at the project root, outside Assets so Unity never imports
// the backups as game assets): the loudness targets and every clip's adjustment record. Same
// read-modify-write-under-lock pattern as AudioNotesSettings, since the BGM and 音效 tabs both write it.
public sealed class AudioAdjustmentSettings
{
    public const string FileName = "AudioAdjustments.json";
    public const string OriginalsFolderName = "AudioOriginals";
    public const double DefaultBgmTargetLufs = -20;
    public const double DefaultSfxTargetLufs = -16;

    private static readonly object FileLock = new();

    public double BgmTargetLufs { get; set; } = DefaultBgmTargetLufs;
    public double SfxTargetLufs { get; set; } = DefaultSfxTargetLufs;
    public Dictionary<string, AudioAdjustmentRecord> Bgm { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AudioAdjustmentRecord> Sfx { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, AudioAdjustmentRecord> Get(AudioCategory category) => category == AudioCategory.Bgm ? Bgm : Sfx;

    public double GetTarget(AudioCategory category) => category == AudioCategory.Bgm ? BgmTargetLufs : SfxTargetLufs;

    public void SetTarget(AudioCategory category, double lufs)
    {
        if (category == AudioCategory.Bgm)
            BgmTargetLufs = lufs;
        else
            SfxTargetLufs = lufs;
    }

    public static string GetFilePath(string projectRoot) => Path.Combine(projectRoot, FileName);

    public static string GetOriginalsFolder(string projectRoot, AudioCategory category) =>
        Path.Combine(projectRoot, OriginalsFolderName, category == AudioCategory.Bgm ? "BGM" : "SFX");

    // A real file that fails to parse is NOT swallowed into an empty default — saving that default back
    // would forget which backups belong to which clip.
    public static AudioAdjustmentSettings Load(string projectRoot)
    {
        lock (FileLock)
            return LoadUnlocked(projectRoot);
    }

    public static AudioAdjustmentSettings Update(string projectRoot, Action<AudioAdjustmentSettings> mutate)
    {
        lock (FileLock)
        {
            var settings = LoadUnlocked(projectRoot);
            mutate(settings);
            File.WriteAllText(GetFilePath(projectRoot), JsonConvert.SerializeObject(settings, Formatting.Indented));
            return settings;
        }
    }

    private static AudioAdjustmentSettings LoadUnlocked(string projectRoot)
    {
        var path = GetFilePath(projectRoot);
        if (!File.Exists(path))
            return new AudioAdjustmentSettings();

        var loaded = JsonConvert.DeserializeObject<AudioAdjustmentSettings>(File.ReadAllText(path)) ?? new AudioAdjustmentSettings();
        // Newtonsoft replaces the initializer dictionaries wholesale, dropping the comparer.
        loaded.Bgm = new Dictionary<string, AudioAdjustmentRecord>(loaded.Bgm ?? new(), StringComparer.OrdinalIgnoreCase);
        loaded.Sfx = new Dictionary<string, AudioAdjustmentRecord>(loaded.Sfx ?? new(), StringComparer.OrdinalIgnoreCase);
        return loaded;
    }
}
