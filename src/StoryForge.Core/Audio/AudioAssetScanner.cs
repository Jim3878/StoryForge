namespace StoryForge.Core.Audio;

public enum AudioCategory
{
    Bgm,
    Sfx,
}

public sealed class AudioEntry
{
    public string FileName { get; init; } = string.Empty;

    // File name without extension — the same string a playscript passes to Bgm("...") and the key
    // AudioNotes.json files a note under, so it's what "複製檔名" copies too.
    public string Name { get; init; } = string.Empty;

    // Lower-case, no leading dot ("ogg"/"wav"/"mp3").
    public string Format { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTime LastWriteUtc { get; init; }

    public bool IsOgg => Format == "ogg";
}

// Flat scan of the game's own audio folders (Assets/08.Audio/Clip/BGM and /SFX) — deliberately not the
// asset-store packs elsewhere under Assets (Universal Sound FX etc., 10k+ raw source files the game never
// references directly) or Clip/Test. Both folders are flat today, so no subfolder recursion.
public static class AudioAssetScanner
{
    public const string BgmFolderRelativePath = "08.Audio/Clip/BGM";
    public const string SfxFolderRelativePath = "08.Audio/Clip/SFX";

    // Same list AudioBatchConvertOgg (the Unity editor window this tool's 轉檔 button was ported from)
    // accepts as conversion input — listing all of them, not just what's already .ogg, is what lets the
    // 轉檔 button's pending count and the 格式 column show what still needs converting.
    private static readonly string[] SupportedExtensions =
    {
        ".wav", ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".wma",
    };

    public static string GetFolderRelativePath(AudioCategory category) =>
        category == AudioCategory.Bgm ? BgmFolderRelativePath : SfxFolderRelativePath;

    public static string GetFolderPath(string projectRoot, AudioCategory category) =>
        Path.Combine(projectRoot, "Assets", GetFolderRelativePath(category).Replace('/', Path.DirectorySeparatorChar));

    public static bool IsSupportedAudioFile(string path)
    {
        var ext = Path.GetExtension(path);
        return SupportedExtensions.Any(x => x.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    public static List<AudioEntry> ScanFolder(string folder)
    {
        if (!Directory.Exists(folder))
            return new List<AudioEntry>();

        return Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(IsSupportedAudioFile)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new AudioEntry
                {
                    FileName = info.Name,
                    Name = Path.GetFileNameWithoutExtension(info.Name),
                    Format = info.Extension.TrimStart('.').ToLowerInvariant(),
                    FullPath = info.FullName,
                    SizeBytes = info.Length,
                    LastWriteUtc = info.LastWriteTimeUtc,
                };
            })
            .OrderBy(e => e.FileName, NaturalStringComparer.Instance)
            .ToList();
    }
}
