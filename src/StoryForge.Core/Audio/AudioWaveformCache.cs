using Newtonsoft.Json;

namespace StoryForge.Core.Audio;

public sealed class AudioWaveformCacheEntry
{
    // Validity stamp — the entry only counts for the exact file it was computed from, so a re-exported or
    // converted file with the same name gets recomputed instead of showing the old waveform.
    public long SizeBytes { get; set; }
    public long LastWriteUtcTicks { get; set; }

    public double DurationSeconds { get; set; }

    // One byte per bucket, 0-255, each bucket's peak absolute amplitude normalized against the file's own
    // loudest bucket (so a quiet SFX still draws a readable shape). Computed browser-side — see
    // audio-browser.js's computePeaks — since the browser can already decode ogg/mp3/wav natively and the
    // server would otherwise need extra Vorbis/MP3 decoder packages. Serialized as base64 by Newtonsoft.
    public byte[] Peaks { get; set; } = Array.Empty<byte>();

    public bool Matches(AudioEntry entry) =>
        SizeBytes == entry.SizeBytes && LastWriteUtcTicks == entry.LastWriteUtc.Ticks;
}

// Derived data (recomputable from the audio files at any time), so it's per-machine in
// %LocalAppData%\StoryForge rather than in the external data folder or the Unity project. Written only by
// the app's own process, which is what keeps it clear of the sandboxed-%LocalAppData% redirection gotcha.
// Keyed "<category>/<file name>" (with extension — a .wav and its converted .ogg are different files).
public sealed class AudioWaveformCache
{
    private const string FileName = "audio-waveform-cache.json";

    public Dictionary<string, AudioWaveformCacheEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string GetKey(AudioCategory category, string fileName) => $"{category}/{fileName}";

    // Unlike the notes file, a corrupt cache is just thrown away — worst case every waveform recomputes.
    public static AudioWaveformCache LoadOrDefault()
    {
        var path = GetFilePath();
        if (!File.Exists(path))
            return new AudioWaveformCache();

        try
        {
            var loaded = JsonConvert.DeserializeObject<AudioWaveformCache>(File.ReadAllText(path));
            if (loaded == null)
                return new AudioWaveformCache();

            loaded.Entries = new Dictionary<string, AudioWaveformCacheEntry>(
                loaded.Entries ?? new(), StringComparer.OrdinalIgnoreCase);
            return loaded;
        }
        catch
        {
            return new AudioWaveformCache();
        }
    }

    public void Save()
    {
        var path = GetFilePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonConvert.SerializeObject(this));
    }

    private static string GetFilePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StoryForge", FileName);
}
