using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace StoryForge.Core.Audio;

public sealed class AudioLoudness
{
    // EBU R128 integrated loudness; null when ffmpeg's gating left nothing to measure (clips much shorter
    // than one 400 ms block, or near-silent ones report -70 LUFS, which isn't a real reading).
    public double? IntegratedLufs { get; init; }
    public double? SamplePeakDb { get; init; }
}

public enum AudioGainOutcome
{
    Applied,
    Restored,
    NeedsAdopt,
    Unmeasurable,
    GainOutOfRange,
    NoMeta,
    LfsPointer,
    Collision,
    Failed,
}

public sealed class AudioGainResult
{
    public string FileName { get; init; } = string.Empty;
    public AudioGainOutcome Outcome { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool Succeeded => Outcome is AudioGainOutcome.Applied or AudioGainOutcome.Restored;
}

public sealed class AudioNormalizeBatchResult
{
    public int Applied { get; set; }
    public bool Cancelled { get; set; }
    public List<AudioGainResult> Skipped { get; } = new();
    public List<AudioGainResult> Failures { get; } = new();
}

public sealed class AudioAdjustmentStatus
{
    public string? Guid { get; init; }
    public AudioAdjustmentRecord? Record { get; init; }

    // The file in Assets is no longer the one this tool last wrote (or its backup is gone) — the record
    // can't be trusted to describe it until the user decides what the original now is.
    public bool ExternallyChanged { get; init; }
}

public sealed class AudioOrphanRecord
{
    public string Guid { get; init; } = string.Empty;
    public AudioAdjustmentRecord Record { get; init; } = new();
}

public sealed class AudioAdjustmentScan
{
    public AudioAdjustmentSettings Settings { get; init; } = new();
    public Dictionary<string, AudioAdjustmentStatus> ByFileName { get; init; } = new(StringComparer.Ordinal);
    public List<AudioOrphanRecord> Orphans { get; init; } = new();
}

// The BGM/音效 tabs' 標準化 and 調整音量 features. Both are one destructive write into Assets, always
// computed from a backed-up original (AudioOriginals/, outside Assets) in a single encode:
//   original -> volume=(normalize gain + manual gain) dB -> libvorbis -> overwrite Assets/<name>.ogg
// so re-adjusting never stacks lossy generations, and 還原 can put the original back. Normalizing is a
// fixed gain (target LUFS - measured integrated LUFS), never ffmpeg's dynamic loudnorm, which is what
// lets the browser preview (a plain GainNode) sound exactly like what gets written.
public static class AudioGainProcessor
{
    public const double MinManualGainDb = -24;
    public const double MaxManualGainDb = 24;

    // A normalize gain beyond this means the measurement is almost certainly meaningless (a tiny click, a
    // near-silent tail) rather than a clip that genuinely needs +25 dB.
    public const double MaxNormalizeGainDb = 20;

    private const int MeasureTimeoutMs = 120000;
    private const int EncodeTimeoutMs = 300000;
    private const double UnmeasurableLufs = -69.9;

    private static readonly Regex IntegratedRegex = new(@"^\s*I:\s*(-?[\d.]+|-inf)\s*LUFS", RegexOptions.Multiline);
    private static readonly Regex PeakRegex = new(@"^\s*Peak:\s*(-?[\d.]+|-inf)\s*dBFS", RegexOptions.Multiline);
    private static readonly Regex GuidRegex = new(@"^guid:\s*([0-9a-fA-F]{32})\s*$", RegexOptions.Multiline);

    // path -> (size, mtime, sha). Hashing every managed BGM on each rescan would otherwise re-read tens of
    // MB for nothing; a git checkout changes mtime, which just costs one re-hash.
    private static readonly ConcurrentDictionary<string, (long Size, DateTime Mtime, string Sha)> ShaCache = new(StringComparer.OrdinalIgnoreCase);

    public static double ClampManualGain(double db) =>
        Math.Round(Math.Clamp(db, MinManualGainDb, MaxManualGainDb) * 2, MidpointRounding.AwayFromZero) / 2;

    public static string? ReadGuid(string assetPath)
    {
        var meta = assetPath + ".meta";
        if (!File.Exists(meta))
            return null;
        var match = GuidRegex.Match(File.ReadAllText(meta));
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    // A clip whose LFS content was never pulled is a ~130-byte text pointer; encoding that would "succeed"
    // at nothing and then back the pointer up as the original.
    public static bool IsLfsPointer(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 1024)
            return false;
        var head = new byte[64];
        using var stream = info.OpenRead();
        var read = stream.Read(head, 0, head.Length);
        return Encoding.ASCII.GetString(head, 0, read).StartsWith("version https://git-lfs", StringComparison.Ordinal);
    }

    public static string ComputeSha256(string path)
    {
        var info = new FileInfo(path);
        if (ShaCache.TryGetValue(info.FullName, out var cached) && cached.Size == info.Length && cached.Mtime == info.LastWriteTimeUtc)
            return cached.Sha;

        using var stream = info.OpenRead();
        var sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        ShaCache[info.FullName] = (info.Length, info.LastWriteTimeUtc, sha);
        return sha;
    }

    public static string? FindOriginal(string projectRoot, AudioCategory category, string guid)
    {
        var folder = AudioAdjustmentSettings.GetOriginalsFolder(projectRoot, category);
        if (!Directory.Exists(folder))
            return null;
        return Directory.EnumerateFiles(folder, guid + ".*").FirstOrDefault(AudioAssetScanner.IsSupportedAudioFile);
    }

    public static AudioLoudness? Measure(string ffmpegPath, string path, out string error)
    {
        var args = new[] { "-hide_banner", "-nostats", "-i", path, "-vn", "-af", "ebur128=peak=sample:framelog=quiet", "-f", "null", "-" };
        if (!FfmpegProcess.Run(ffmpegPath, args, MeasureTimeoutMs, out var stderr, out error))
            return null;

        var summary = stderr.LastIndexOf("Summary:", StringComparison.Ordinal) is var at and >= 0 ? stderr[at..] : stderr;
        var lufs = ParseNumber(IntegratedRegex.Match(summary));
        var peak = ParseNumber(PeakRegex.Match(summary));
        return new AudioLoudness
        {
            IntegratedLufs = lufs is { } value && value > UnmeasurableLufs ? value : null,
            SamplePeakDb = peak,
        };
    }

    private static double? ParseNumber(Match match) =>
        match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    // Matches each scanned clip to its record (by .meta GUID), flags ones changed outside the tool, lists
    // records whose clip is gone, and refreshes LastKnownName after a rename inside Unity.
    public static AudioAdjustmentScan Inspect(string projectRoot, AudioCategory category, IReadOnlyList<AudioEntry> entries)
    {
        var settings = AudioAdjustmentSettings.Load(projectRoot);
        var records = settings.Get(category);
        var byFileName = new Dictionary<string, AudioAdjustmentStatus>(StringComparer.Ordinal);
        var seenGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var guid = ReadGuid(entry.FullPath);
            if (guid == null || !records.TryGetValue(guid, out var record))
            {
                byFileName[entry.FileName] = new AudioAdjustmentStatus { Guid = guid };
                continue;
            }

            seenGuids.Add(guid);
            if (record.LastKnownName != entry.Name)
                renamed[guid] = entry.Name;

            bool changed;
            try
            {
                changed = FindOriginal(projectRoot, category, guid) == null
                          || !string.Equals(ComputeSha256(entry.FullPath), record.WrittenSha256, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                changed = true;
            }
            byFileName[entry.FileName] = new AudioAdjustmentStatus { Guid = guid, Record = record, ExternallyChanged = changed };
        }

        if (renamed.Count > 0)
        {
            settings = AudioAdjustmentSettings.Update(projectRoot, s =>
            {
                foreach (var (guid, name) in renamed)
                    if (s.Get(category).TryGetValue(guid, out var r))
                        r.LastKnownName = name;
            });
        }

        var orphans = records.Where(r => !seenGuids.Contains(r.Key))
            .Select(r => new AudioOrphanRecord { Guid = r.Key, Record = r.Value })
            .OrderBy(o => o.Record.LastKnownName, NaturalStringComparer.Instance)
            .ToList();

        return new AudioAdjustmentScan { Settings = settings, ByFileName = byFileName, Orphans = orphans };
    }

    // 調整音量's 套用 (and the batch's per-file step). adoptCurrent = the user confirmed the file now in
    // Assets is the new original, discarding the stale backup/record before anything else happens.
    // Normalize off + 0 dB is the same as 還原.
    public static AudioGainResult Apply(string projectRoot, AudioCategory category, string ffmpegPath, AudioEntry entry,
        bool normalize, double manualGainDb, bool adoptCurrent) =>
        Write(projectRoot, category, ffmpegPath, entry, normalize, ClampManualGain(manualGainDb), adoptCurrent, ignoreExternalChange: false);

    // 還原原檔: write the original back (bit-exact when it was an .ogg; one encode when it was e.g. a .wav
    // that adjusting converted), then drop the record and the backup. overwriteExternalChange = the user
    // confirmed replacing a file changed outside the tool with the old original anyway.
    public static AudioGainResult Restore(string projectRoot, AudioCategory category, string ffmpegPath, AudioEntry entry,
        bool overwriteExternalChange) =>
        Write(projectRoot, category, ffmpegPath, entry, normalize: false, manualGainDb: 0, adoptCurrent: false, ignoreExternalChange: overwriteExternalChange);

    // Accept the file now in Assets as-is: forget the record and delete the backup, touching nothing else.
    public static void Forget(string projectRoot, AudioCategory category, string guid)
    {
        AudioAdjustmentSettings.Update(projectRoot, s => s.Get(category).Remove(guid));
        DeleteBackups(projectRoot, category, guid);
    }

    // An orphan (its clip deleted and re-added under a new GUID, say) re-attached to an existing clip.
    public static string? Rebind(string projectRoot, AudioCategory category, string orphanGuid, AudioEntry target)
    {
        var targetGuid = ReadGuid(target.FullPath);
        if (targetGuid == null)
            return $"{target.FileName} 沒有 .meta（還沒被 Unity 匯入過）";

        string? error = null;
        AudioAdjustmentSettings.Update(projectRoot, s =>
        {
            var records = s.Get(category);
            if (records.ContainsKey(targetGuid))
            {
                error = $"{target.FileName} 已經有自己的調整記錄";
                return;
            }
            if (!records.Remove(orphanGuid, out var record))
            {
                error = "找不到這筆記錄";
                return;
            }
            record.LastKnownName = target.Name;
            records[targetGuid] = record;
        });
        if (error != null)
            return error;

        var folder = AudioAdjustmentSettings.GetOriginalsFolder(projectRoot, category);
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, orphanGuid + ".*").ToList())
                File.Move(file, Path.Combine(folder, targetGuid + Path.GetExtension(file)), overwrite: true);
        }
        return null;
    }

    // 標準化（N）: turn normalizing on for every given file, keeping each file's own manual gain.
    // Anything that needs a human decision (changed outside the tool, unmeasurable, absurd gain) is skipped
    // and reported, never guessed at.
    public static AudioNormalizeBatchResult NormalizeAll(string projectRoot, AudioCategory category, string ffmpegPath,
        IReadOnlyList<AudioEntry> files, IProgress<AudioConvertProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new AudioNormalizeBatchResult();
        for (var i = 0; i < files.Count; i++)
        {
            // Checked between files only, so a cancel never leaves a half-written clip.
            if (cancellationToken.IsCancellationRequested)
            {
                result.Cancelled = true;
                break;
            }

            var entry = files[i];
            progress?.Report(new AudioConvertProgress { Processed = i, Total = files.Count, CurrentFileName = entry.FileName });

            AudioGainResult fileResult;
            try
            {
                var guid = ReadGuid(entry.FullPath);
                var manual = guid != null && AudioAdjustmentSettings.Load(projectRoot).Get(category).TryGetValue(guid, out var record)
                    ? record.ManualGainDb
                    : 0;
                fileResult = Apply(projectRoot, category, ffmpegPath, entry, normalize: true, manual, adoptCurrent: false);
            }
            catch (Exception e)
            {
                fileResult = Fail(entry, e.Message);
            }

            if (fileResult.Succeeded)
                result.Applied++;
            else if (fileResult.Outcome == AudioGainOutcome.Failed)
                result.Failures.Add(fileResult);
            else
                result.Skipped.Add(fileResult);
        }

        progress?.Report(new AudioConvertProgress { Processed = files.Count, Total = files.Count });
        return result;
    }

    private static AudioGainResult Write(string projectRoot, AudioCategory category, string ffmpegPath, AudioEntry entry,
        bool normalize, double manualGainDb, bool adoptCurrent, bool ignoreExternalChange)
    {
        if (!File.Exists(entry.FullPath))
            return Fail(entry, "檔案不存在（可能已被移動或刪除，請重新掃描）");

        var guid = ReadGuid(entry.FullPath);
        if (guid == null)
            return Result(entry, AudioGainOutcome.NoMeta, "沒有 .meta（還沒被 Unity 匯入過），無法追蹤這個檔案");
        if (IsLfsPointer(entry.FullPath))
            return Result(entry, AudioGainOutcome.LfsPointer, "這是還沒下載的 Git LFS 指標檔，請先在 Unity 專案執行 git lfs pull");

        if (adoptCurrent)
            Forget(projectRoot, category, guid);

        var settings = AudioAdjustmentSettings.Load(projectRoot);
        var target = settings.GetTarget(category);
        settings.Get(category).TryGetValue(guid, out var record);
        var backupPath = record != null ? FindOriginal(projectRoot, category, guid) : null;

        if (record != null && backupPath == null)
            return Result(entry, AudioGainOutcome.NeedsAdopt, "備份原檔不見了");
        if (record != null && !ignoreExternalChange
            && !string.Equals(ComputeSha256(entry.FullPath), record.WrittenSha256, StringComparison.OrdinalIgnoreCase))
            return Result(entry, AudioGainOutcome.NeedsAdopt, "檔案在工具外被換過了（和上次寫入的不同）");

        var isRestore = !normalize && manualGainDb == 0;
        if (record == null && isRestore && entry.IsOgg)
            return Result(entry, AudioGainOutcome.Restored, "沒有變更");

        var destPath = Path.Combine(Path.GetDirectoryName(entry.FullPath)!, entry.Name + ".ogg");
        if (!entry.IsOgg && File.Exists(destPath))
            return Result(entry, AudioGainOutcome.Collision, $"同資料夾已經有 {entry.Name}.ogg");

        AudioLoudness loudness;
        if (record != null)
        {
            loudness = new AudioLoudness { IntegratedLufs = record.MeasuredLufs, SamplePeakDb = record.OriginalPeakDb };
        }
        else
        {
            var measured = Measure(ffmpegPath, entry.FullPath, out var measureError);
            if (measured == null)
                return Fail(entry, "量測響度失敗：" + measureError);
            loudness = measured;
        }

        var normalizeGain = 0.0;
        if (normalize)
        {
            if (loudness.IntegratedLufs is not { } lufs)
                return Result(entry, AudioGainOutcome.Unmeasurable, "太短或太安靜，量不出整合響度，請改用手動增益");
            normalizeGain = target - lufs;
            if (Math.Abs(normalizeGain) > MaxNormalizeGainDb)
                return Result(entry, AudioGainOutcome.GainOutOfRange,
                    $"標準化需要 {normalizeGain:+0.0;-0.0} dB（量到 {lufs:0.0} LUFS），超出 ±{MaxNormalizeGainDb} dB，量測可能不可靠");
        }
        var totalGain = normalizeGain + manualGainDb;

        string sourcePath;
        string originalExtension;
        if (record != null)
        {
            sourcePath = backupPath!;
            originalExtension = record.OriginalExtension;
        }
        else if (isRestore)
        {
            // Unmanaged non-.ogg at 0 dB: just the format conversion, nothing worth backing up.
            sourcePath = entry.FullPath;
            originalExtension = entry.Format;
        }
        else
        {
            // Back up first and record the file as it stands (0 dB), so a failed encode below still leaves
            // the record describing Assets truthfully.
            originalExtension = entry.Format;
            var folder = AudioAdjustmentSettings.GetOriginalsFolder(projectRoot, category);
            Directory.CreateDirectory(folder);
            DeleteBackups(projectRoot, category, guid);
            sourcePath = Path.Combine(folder, guid + "." + originalExtension);
            File.Copy(entry.FullPath, sourcePath, overwrite: true);

            var currentSha = ComputeSha256(entry.FullPath);
            AudioAdjustmentSettings.Update(projectRoot, s => s.Get(category)[guid] = new AudioAdjustmentRecord
            {
                LastKnownName = entry.Name,
                OriginalExtension = originalExtension,
                MeasuredLufs = loudness.IntegratedLufs,
                OriginalPeakDb = loudness.SamplePeakDb,
                WrittenSha256 = currentSha,
            });
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"storyforge-gain-{System.Guid.NewGuid():N}.ogg");
        try
        {
            if (Math.Abs(totalGain) < 0.005 && originalExtension == "ogg")
            {
                // The original itself, bit for bit — no encode at all.
                if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                    File.Copy(sourcePath, destPath, overwrite: true);
            }
            else
            {
                var args = new[]
                {
                    "-y", "-hide_banner", "-i", sourcePath, "-vn",
                    "-af", "volume=" + totalGain.ToString("0.###", CultureInfo.InvariantCulture) + "dB",
                    "-c:a", "libvorbis", "-qscale:a", AudioOggConverter.VorbisQuality.ToString(CultureInfo.InvariantCulture),
                    "-f", "ogg", tempPath,
                };
                if (!FfmpegProcess.Run(ffmpegPath, args, EncodeTimeoutMs, out _, out var encodeError))
                    return Fail(entry, encodeError);
                // Encoded outside Assets and copied over in one step, so Unity never sees a half-written clip.
                File.Copy(tempPath, destPath, overwrite: true);
            }
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }

        if (!entry.IsOgg)
        {
            AudioOggConverter.CarryOverMeta(entry.FullPath, destPath, category, new StringBuilder());
            File.Delete(entry.FullPath);
        }

        if (isRestore)
        {
            Forget(projectRoot, category, guid);
            return Result(entry, AudioGainOutcome.Restored, "已還原");
        }

        var writtenSha = ComputeSha256(destPath);
        AudioAdjustmentSettings.Update(projectRoot, s => s.Get(category)[guid] = new AudioAdjustmentRecord
        {
            LastKnownName = entry.Name,
            OriginalExtension = originalExtension,
            MeasuredLufs = loudness.IntegratedLufs,
            OriginalPeakDb = loudness.SamplePeakDb,
            Normalize = normalize,
            NormalizeTargetLufs = normalize ? target : null,
            ManualGainDb = manualGainDb,
            WrittenSha256 = writtenSha,
        });
        return Result(entry, AudioGainOutcome.Applied, $"總增益 {totalGain:+0.0;-0.0;0.0} dB");
    }

    private static void DeleteBackups(string projectRoot, AudioCategory category, string guid)
    {
        var folder = AudioAdjustmentSettings.GetOriginalsFolder(projectRoot, category);
        if (!Directory.Exists(folder))
            return;
        foreach (var file in Directory.EnumerateFiles(folder, guid + ".*").ToList())
            File.Delete(file);
    }

    private static AudioGainResult Result(AudioEntry entry, AudioGainOutcome outcome, string message) =>
        new() { FileName = entry.FileName, Outcome = outcome, Message = message };

    private static AudioGainResult Fail(AudioEntry entry, string message) => Result(entry, AudioGainOutcome.Failed, message);
}
