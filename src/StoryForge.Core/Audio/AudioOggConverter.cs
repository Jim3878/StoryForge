using System.Text;

namespace StoryForge.Core.Audio;

public sealed class AudioConvertProgress
{
    public int Processed { get; init; }
    public int Total { get; init; }
    public string CurrentFileName { get; init; } = string.Empty;
}

public sealed class AudioConvertFailure
{
    public string FileName { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
}

public sealed class AudioConvertResult
{
    public int Converted { get; set; }
    public int Skipped { get; set; }
    public int Collisions { get; set; }
    public int Failed => Failures.Count;
    public bool Cancelled { get; set; }
    public List<AudioConvertFailure> Failures { get; } = new();
    public string? LogPath { get; set; }
}

// Port of the Unity project's AudioBatchConvertOgg editor window (Assets/Editor/EditorWindow/
// AudioBatchConvertInPlaceWindow.cs, menu 素材/Audio/Batch Convert To OGG), with the defaults that window
// actually runs with (skip .ogg, delete source after convert, collisions moved to 08.Audio/Collision,
// libvorbis q5, log to 08.Audio/_convert_logs) and two deliberate differences:
//
// 1. The source's .meta is RENAMED onto the new .ogg instead of deleted, so the asset keeps its GUID and
//    any scene/prefab that references the clip directly stays linked. The Unity window deleted it, which
//    handed the new .ogg a fresh GUID and silently broke those references.
// 2. The Unity window's ApplyImporterSettings step used AudioImporter/AssetDatabase, which only exist
//    inside the editor — here the same values are written straight into that renamed .meta's text instead
//    (see PatchImporterSettings), and Unity reimports from it the next time the editor gets focus.
//
// Scoped to one category folder per run (the BGM tab converts Clip/BGM only), so BGM vs SFX importer
// settings come from the category rather than the Unity window's "/bgm/ in path" check.
public static class AudioOggConverter
{
    private const int TimeoutMs = 30000;
    internal const int VorbisQuality = 5;

    public static List<AudioEntry> GetPendingFiles(IEnumerable<AudioEntry> entries) =>
        entries.Where(e => !e.IsOgg).ToList();

    public static AudioConvertResult Convert(
        string projectRoot,
        AudioCategory category,
        string ffmpegPath,
        IReadOnlyList<AudioEntry> files,
        IProgress<AudioConvertProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new AudioConvertResult();
        var clipRoot = Path.Combine(projectRoot, "Assets", "08.Audio", "Clip");
        var collisionRoot = Path.Combine(projectRoot, "Assets", "08.Audio", "Collision");

        var log = new StringBuilder();
        log.AppendLine($"Audio Convert (StoryForge) - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine($"Target: Assets/{AudioAssetScanner.GetFolderRelativePath(category)}");
        log.AppendLine("CollisionAction: MoveToFolder");
        log.AppendLine();

        for (var i = 0; i < files.Count; i++)
        {
            // Checked between files only — a file mid-conversion always finishes, so cancelling never
            // leaves a half-written .ogg or a source deleted without its replacement.
            if (cancellationToken.IsCancellationRequested)
            {
                result.Cancelled = true;
                log.AppendLine("[CANCELLED]");
                break;
            }

            var src = files[i].FullPath;
            progress?.Report(new AudioConvertProgress { Processed = i, Total = files.Count, CurrentFileName = files[i].FileName });

            if (Path.GetExtension(src).Equals(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                result.Skipped++;
                log.AppendLine($"[SKIP OGG] {src}");
                continue;
            }

            var dst = Path.Combine(Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(src) + ".ogg");

            if (File.Exists(dst))
            {
                result.Collisions++;
                MoveCollision(src, dst, clipRoot, collisionRoot, log);
                continue;
            }

            if (!RunFfmpeg(ffmpegPath, src, dst, out var error))
            {
                result.Failures.Add(new AudioConvertFailure { FileName = files[i].FileName, Error = error });
                log.AppendLine($"[FAILED] {src}");
                log.AppendLine(error);
                log.AppendLine();
                TryDelete(dst);
                continue;
            }

            result.Converted++;
            log.AppendLine($"[CONVERTED] {src} -> {dst}");

            try
            {
                CarryOverMeta(src, dst, category, log);
                File.Delete(src);
                log.AppendLine($"[DELETE SOURCE] {src}");
            }
            catch (Exception e)
            {
                log.AppendLine($"[POST-CONVERT FAILED] {e}");
                result.Failures.Add(new AudioConvertFailure { FileName = files[i].FileName, Error = $"轉檔成功，但搬移 .meta／刪除原檔失敗：{e.Message}" });
            }

            log.AppendLine();
        }

        progress?.Report(new AudioConvertProgress { Processed = files.Count, Total = files.Count });

        log.AppendLine();
        log.AppendLine($"Converted: {result.Converted}, Skipped: {result.Skipped}, Collisions: {result.Collisions}, Failed: {result.Failed}");
        result.LogPath = WriteLog(projectRoot, log.ToString());
        return result;
    }

    // Also used by the 音量 features when adjusting a not-yet-.ogg file converts it on the way.
    internal static void CarryOverMeta(string src, string dst, AudioCategory category, StringBuilder log)
    {
        var srcMeta = src + ".meta";
        var dstMeta = dst + ".meta";

        if (!File.Exists(srcMeta))
        {
            // Never imported by Unity yet — nothing to preserve; Unity generates a fresh .meta with default
            // importer settings on its next refresh.
            log.AppendLine("[NO META] source had no .meta; Unity will create one with default import settings");
            return;
        }

        if (File.Exists(dstMeta))
        {
            // An orphan left behind with no .ogg next to it. The source's .meta wins because its GUID is the
            // one the project actually references.
            File.Delete(dstMeta);
            log.AppendLine($"[REPLACED ORPHAN META] {dstMeta}");
        }

        File.Move(srcMeta, dstMeta);
        log.AppendLine($"[MOVE META] {srcMeta} -> {dstMeta} (GUID kept)");

        var text = File.ReadAllText(dstMeta);
        var patched = PatchImporterSettings(text, category);
        if (patched != text)
        {
            File.WriteAllText(dstMeta, patched);
            log.AppendLine("[IMPORTER SETTINGS] applied");
        }
    }

    // Same values the Unity window's ApplyImporterSettings set, as the raw enum ints Unity serializes:
    // compressionFormat 1 = Vorbis; loadType 0 = DecompressOnLoad, 1 = CompressedInMemory. Only keys inside
    // the defaultSettings: block are touched for the per-sample settings (platformSettingOverrides can hold
    // the same key names for per-platform overrides, which the Unity window never touched either);
    // loadInBackground/forceToMono are AudioImporter-level keys one indent level up.
    public static string PatchImporterSettings(string metaText, AudioCategory category)
    {
        var isBgm = category == AudioCategory.Bgm;
        var sampleValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["compressionFormat"] = "1",
            ["loadType"] = isBgm ? "1" : "0",
            ["quality"] = isBgm ? "0.85" : "0.7",
            ["preloadAudioData"] = "1",
        };
        var importerValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["loadInBackground"] = "1",
            ["forceToMono"] = "0",
        };

        var newline = metaText.Contains("\r\n") ? "\r\n" : "\n";
        var lines = metaText.Split(newline);
        var defaultSettingsIndent = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
                continue;

            var indent = line.Length - line.TrimStart(' ').Length;
            var trimmed = line.Trim();

            if (defaultSettingsIndent >= 0 && indent <= defaultSettingsIndent)
                defaultSettingsIndent = -1;

            if (trimmed == "defaultSettings:")
            {
                defaultSettingsIndent = indent;
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = trimmed[..colon];

            if (defaultSettingsIndent >= 0 && indent == defaultSettingsIndent + 2 && sampleValues.TryGetValue(key, out var sampleValue))
                lines[i] = new string(' ', indent) + key + ": " + sampleValue;
            else if (indent == 2 && importerValues.TryGetValue(key, out var importerValue))
                lines[i] = new string(' ', indent) + key + ": " + importerValue;
        }

        return string.Join(newline, lines);
    }

    private static void MoveCollision(string src, string dst, string clipRoot, string collisionRoot, StringBuilder log)
    {
        log.AppendLine($"[COLLISION] {src}");
        log.AppendLine($"[TARGET] {dst}");
        try
        {
            var relativeDir = Path.GetRelativePath(clipRoot, Path.GetDirectoryName(src)!);
            var finalDir = Path.Combine(collisionRoot, relativeDir);
            Directory.CreateDirectory(finalDir);

            var movedPath = GetUniquePath(Path.Combine(finalDir, Path.GetFileName(src)));
            File.Move(src, movedPath);
            if (File.Exists(src + ".meta"))
                File.Move(src + ".meta", movedPath + ".meta");

            log.AppendLine("[ACTION] MoveToFolder");
            log.AppendLine($"[MOVED] {src} -> {movedPath}");
        }
        catch (Exception e)
        {
            log.AppendLine("[MOVE FAILED]");
            log.AppendLine(e.ToString());
        }
        log.AppendLine();
    }

    private static bool RunFfmpeg(string ffmpegPath, string input, string output, out string error) =>
        FfmpegProcess.Run(ffmpegPath,
            new[] { "-y", "-i", input, "-vn", "-c:a", "libvorbis", "-qscale:a", VorbisQuality.ToString(), output },
            TimeoutMs, out _, out error);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A leftover partial output is reported through the failure itself; nothing more to do here.
        }
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    private static string? WriteLog(string projectRoot, string content)
    {
        try
        {
            var folder = Path.Combine(projectRoot, "Assets", "08.Audio", "_convert_logs");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"audio_convert_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            File.WriteAllText(path, content, Encoding.UTF8);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
