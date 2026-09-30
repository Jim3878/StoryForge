using System.Diagnostics;
using StoryForge.Core;
using StoryForge.Core.Audio;
using StoryForge.Core.Graph;

namespace StoryForge.Web.Audio;

// One instance per BGM/音效 panel (the panel news it up with its own Category), holding that tab's scanned
// file list, notes, remembered UI settings and the 轉檔成 OGG run. Not registered in DI directly because
// the two tabs need two independent instances within the same circuit.
public sealed class AudioLibraryState
{
    private readonly AppSettings _appSettings;
    private readonly AudioWaveformCacheService _waveformCache;
    private string? _projectRoot;

    public AudioLibraryState(AudioCategory category, AppSettings appSettings, AudioWaveformCacheService waveformCache)
    {
        Category = category;
        _appSettings = appSettings;
        _waveformCache = waveformCache;
        Page = AudioBrowserSettings.LoadPage(category);
    }

    public AudioCategory Category { get; }
    public string CategorySegment => Category == AudioCategory.Bgm ? "bgm" : "sfx";
    public string StatusMessage { get; private set; } = string.Empty;
    public List<AudioEntry> Entries { get; private set; } = new();
    public AudioPageSettings Page { get; }
    private Dictionary<string, string> _notes = new(StringComparer.Ordinal);

    public void Load()
    {
        try
        {
            _projectRoot = ProjectPaths.ResolveUnityProjectRoot();
            Entries = AudioAssetScanner.ScanFolder(AudioAssetScanner.GetFolderPath(_projectRoot, Category));
        }
        catch (Exception e)
        {
            Entries = new List<AudioEntry>();
            StatusMessage = $"載入失敗：{e.Message}";
            return;
        }

        try
        {
            _notes = AudioNotesSettings.LoadOrDefault(_appSettings.ExternalDataFolder).Get(Category);
            StatusMessage = $"已載入：{Entries.Count} 個檔案";
        }
        catch (Exception e)
        {
            _notes = new Dictionary<string, string>(StringComparer.Ordinal);
            StatusMessage = $"讀取 AudioNotes.json 失敗：{e.Message}";
        }

        RefreshAdjustments();
    }

    // Load() overwrites StatusMessage with its own "已載入" line; a caller reloading right after a write
    // puts the write's own outcome back.
    public void SetStatusMessage(string message) => StatusMessage = message;

    public AudioEntry? Find(string fileName) =>
        Entries.FirstOrDefault(e => string.Equals(e.FileName, fileName, StringComparison.Ordinal));

    public string GetUrl(AudioEntry entry) => $"/api/audio/{CategorySegment}/{Uri.EscapeDataString(entry.FileName)}";

    public AudioWaveformCacheEntry? GetWaveform(AudioEntry entry) => _waveformCache.TryGet(Category, entry);

    public void StoreWaveform(string fileName, double durationSeconds, byte[] peaks)
    {
        var entry = Find(fileName);
        if (entry != null)
            _waveformCache.Put(Category, entry, durationSeconds, peaks);
    }

    public void InvalidateWaveform(string fileName) => _waveformCache.Remove(Category, fileName);

    public string GetNote(AudioEntry entry) => _notes.TryGetValue(entry.Name, out var note) ? note : string.Empty;

    public void SetNote(AudioEntry entry, string text)
    {
        if (GetNote(entry) == text.Trim())
            return;

        try
        {
            _notes = AudioNotesSettings.SetNote(_appSettings.ExternalDataFolder, Category, entry.Name, text).Get(Category);
            StatusMessage = $"已儲存備註：{entry.Name}";
        }
        catch (Exception e)
        {
            StatusMessage = $"儲存備註失敗：{e.Message}";
        }
    }

    public void SavePage()
    {
        try
        {
            AudioBrowserSettings.SavePage(Category, Page);
        }
        catch (Exception e)
        {
            StatusMessage = $"儲存分頁設定失敗：{e.Message}";
        }
    }

    public void OpenLogFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            StatusMessage = $"開啟記錄檔失敗：{e.Message}";
        }
    }

    // ---- 音量（標準化／調整音量／還原） ----

    private static readonly AudioAdjustmentStatus NoAdjustment = new();

    public AudioAdjustmentScan Adjustments { get; private set; } = new();
    public bool IsGainWriting { get; private set; }
    public bool IsBusy => IsConvertRunning || IsNormalizeRunning || IsGainWriting;

    public double TargetLufs => Adjustments.Settings.GetTarget(Category);

    public AudioAdjustmentStatus GetAdjustment(AudioEntry entry) =>
        Adjustments.ByFileName.TryGetValue(entry.FileName, out var status) ? status : NoAdjustment;

    public bool IsTargetOutdated(AudioAdjustmentStatus status) =>
        status.Record is { Normalize: true } record && record.NormalizeTargetLufs != TargetLufs;

    // 標準化（N）: not normalized yet, or normalized against a target that has since changed.
    public List<AudioEntry> PendingNormalizeFiles => Entries.Where(e =>
    {
        var record = GetAdjustment(e).Record;
        return record is not { Normalize: true } || record.NormalizeTargetLufs != TargetLufs;
    }).ToList();

    private void RefreshAdjustments()
    {
        if (_projectRoot == null)
            return;
        try
        {
            Adjustments = AudioGainProcessor.Inspect(_projectRoot, Category, Entries);
        }
        catch (Exception e)
        {
            Adjustments = new AudioAdjustmentScan();
            StatusMessage = $"讀取 {AudioAdjustmentSettings.FileName} 失敗：{e.Message}";
        }
    }

    // Everything that makes a clip impossible to adjust, checked before the dialog opens rather than as a
    // failure after the user has already dialled in a gain.
    public string? CheckCanAdjust(AudioEntry entry)
    {
        if (ValidateFfmpeg() is { } ffmpegError)
            return ffmpegError;
        if (GetAdjustment(entry).Guid == null)
            return $"{entry.FileName} 沒有 .meta（還沒被 Unity 匯入過），無法追蹤。請先切回 Unity 讓它匯入一次。";
        try
        {
            if (AudioGainProcessor.IsLfsPointer(entry.FullPath))
                return $"{entry.FileName} 是還沒下載的 Git LFS 指標檔，請先在 Unity 專案執行 git lfs pull。";
        }
        catch (Exception e)
        {
            return e.Message;
        }
        return null;
    }

    // What the 調整音量 dialog previews from: the backed-up original when there is one (and the user
    // has not just chosen to adopt the current file instead), otherwise the file in Assets itself.
    public string GetOriginalUrl(AudioEntry entry, bool adoptCurrent)
    {
        var status = GetAdjustment(entry);
        return status.Record != null && !adoptCurrent && status.Guid != null
            ? $"/api/audio-original/{CategorySegment}/{status.Guid}"
            : GetUrl(entry);
    }

    public async Task<(AudioLoudness? Loudness, string Error)> MeasureAsync(AudioEntry entry)
    {
        var ffmpeg = _appSettings.FfmpegPath;
        var error = string.Empty;
        var loudness = await Task.Run(() => AudioGainProcessor.Measure(ffmpeg, entry.FullPath, out error));
        return (loudness, error);
    }

    public Task<AudioGainResult> ApplyGainAsync(AudioEntry entry, bool normalize, double manualGainDb, bool adoptCurrent) =>
        RunGainWrite(entry, root => AudioGainProcessor.Apply(root, Category, _appSettings.FfmpegPath, entry, normalize, manualGainDb, adoptCurrent));

    public Task<AudioGainResult> RestoreAsync(AudioEntry entry, bool overwriteExternalChange) =>
        RunGainWrite(entry, root => AudioGainProcessor.Restore(root, Category, _appSettings.FfmpegPath, entry, overwriteExternalChange));

    private async Task<AudioGainResult> RunGainWrite(AudioEntry entry, Func<string, AudioGainResult> write)
    {
        if (IsBusy || _projectRoot == null)
            return new AudioGainResult { FileName = entry.FileName, Outcome = AudioGainOutcome.Failed, Message = "另一個作業還在進行中" };

        IsGainWriting = true;
        try
        {
            var root = _projectRoot;
            var result = await Task.Run(() => write(root));
            StatusMessage = $"{entry.Name}：{result.Message}";
            return result;
        }
        catch (Exception e)
        {
            return new AudioGainResult { FileName = entry.FileName, Outcome = AudioGainOutcome.Failed, Message = e.Message };
        }
        finally
        {
            IsGainWriting = false;
        }
    }

    // 採用目前檔案: the file in Assets becomes the truth; the record and stale backup are dropped.
    public void ForgetAdjustment(AudioEntry entry)
    {
        var guid = GetAdjustment(entry).Guid;
        if (_projectRoot == null || guid == null)
            return;
        var root = _projectRoot;
        RunSettingsAction(() => AudioGainProcessor.Forget(root, Category, guid), $"已清除調整記錄：{entry.Name}");
    }

    public void SetTargetLufs(double lufs)
    {
        if (_projectRoot == null || lufs == TargetLufs)
            return;
        var root = _projectRoot;
        RunSettingsAction(() => AudioAdjustmentSettings.Update(root, s => s.SetTarget(Category, lufs)),
            $"目標響度已設為 {lufs:0.#} LUFS");
    }

    public void DeleteOrphan(string guid)
    {
        if (_projectRoot == null)
            return;
        var root = _projectRoot;
        RunSettingsAction(() => AudioGainProcessor.Forget(root, Category, guid), "已刪除孤兒記錄");
    }

    public void RebindOrphan(string guid, AudioEntry target)
    {
        if (_projectRoot == null)
            return;
        var root = _projectRoot;
        string? error = null;
        RunSettingsAction(() => error = AudioGainProcessor.Rebind(root, Category, guid, target), $"已接到 {target.Name}");
        if (error != null)
            StatusMessage = $"接上失敗：{error}";
    }

    private void RunSettingsAction(Action action, string successMessage)
    {
        try
        {
            action();
            StatusMessage = successMessage;
        }
        catch (Exception e)
        {
            StatusMessage = $"失敗：{e.Message}";
        }
        RefreshAdjustments();
    }

    public bool IsNormalizeRunning { get; private set; }
    public AudioNormalizeBatchResult? NormalizeResult { get; private set; }

    public async Task RunNormalizeAsync(IReadOnlyList<AudioEntry> files)
    {
        if (IsBusy || _projectRoot == null)
            return;

        IsNormalizeRunning = true;
        NormalizeResult = null;
        ConvertProgress = new AudioConvertProgress { Processed = 0, Total = files.Count };
        _convertCts = new CancellationTokenSource();
        await RaiseConvertProgress();

        var progress = new Progress<AudioConvertProgress>(p =>
        {
            ConvertProgress = p;
            _ = RaiseConvertProgress();
        });

        try
        {
            var projectRoot = _projectRoot;
            var ffmpeg = _appSettings.FfmpegPath;
            var token = _convertCts.Token;
            NormalizeResult = await Task.Run(() => AudioGainProcessor.NormalizeAll(projectRoot, Category, ffmpeg, files, progress, token));
        }
        catch (Exception e)
        {
            NormalizeResult = new AudioNormalizeBatchResult();
            NormalizeResult.Failures.Add(new AudioGainResult { FileName = "(整批)", Outcome = AudioGainOutcome.Failed, Message = e.Message });
        }
        finally
        {
            IsNormalizeRunning = false;
            _convertCts.Dispose();
            _convertCts = null;
        }

        await RaiseConvertProgress();
    }

    public void ClearNormalizeResult() => NormalizeResult = null;

    // ---- 轉檔成 OGG ----

    public List<AudioEntry> PendingConvertFiles => AudioOggConverter.GetPendingFiles(Entries);
    public bool IsConvertRunning { get; private set; }
    public AudioConvertProgress? ConvertProgress { get; private set; }
    public AudioConvertResult? ConvertResult { get; private set; }
    public event Func<Task>? OnConvertProgress;
    private CancellationTokenSource? _convertCts;

    // Validated up front rather than per file so a missing ffmpeg is one clear error before anything is
    // touched, not N identical failures.
    public string? ValidateFfmpeg() =>
        File.Exists(_appSettings.FfmpegPath)
            ? null
            : $"找不到 ffmpeg：{_appSettings.FfmpegPath}\n請到「設定」修改 ffmpeg 執行檔路徑。";

    public async Task RunConvertAsync(IReadOnlyList<AudioEntry> files)
    {
        if (IsBusy || _projectRoot == null)
            return;

        IsConvertRunning = true;
        ConvertResult = null;
        ConvertProgress = new AudioConvertProgress { Processed = 0, Total = files.Count };
        _convertCts = new CancellationTokenSource();
        await RaiseConvertProgress();

        var progress = new Progress<AudioConvertProgress>(p =>
        {
            ConvertProgress = p;
            _ = RaiseConvertProgress();
        });

        try
        {
            var projectRoot = _projectRoot;
            var ffmpeg = _appSettings.FfmpegPath;
            var token = _convertCts.Token;
            ConvertResult = await Task.Run(() => AudioOggConverter.Convert(projectRoot, Category, ffmpeg, files, progress, token));
        }
        catch (Exception e)
        {
            ConvertResult = new AudioConvertResult();
            ConvertResult.Failures.Add(new AudioConvertFailure { FileName = "(整批)", Error = e.Message });
        }
        finally
        {
            IsConvertRunning = false;
            _convertCts.Dispose();
            _convertCts = null;
        }

        await RaiseConvertProgress();
    }

    public void CancelConvert() => _convertCts?.Cancel();

    public bool IsCancelRequested => _convertCts?.IsCancellationRequested == true;

    public void ClearConvertResult() => ConvertResult = null;

    private Task RaiseConvertProgress() => OnConvertProgress?.Invoke() ?? Task.CompletedTask;
}
