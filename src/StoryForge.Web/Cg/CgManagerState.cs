using System.Diagnostics;
using StoryForge.Core;
using StoryForge.Core.Cg;
using StoryForge.Core.Graph;

namespace StoryForge.Web.Cg;

public enum CgLogLevel
{
    Info,
    Done,
    Missing,
    MissingSpine,
    Anomaly,
    Error
}

public sealed record CgLogLine(DateTime Time, CgLogLevel Level, string Text);

// 「輸出至 Unity」開始前，Unity 已經有這張 CG 時要問的一題。
public sealed record CgOverwriteQuestion(CgEntry Entry, string Detail);

// CG 管理分頁（scoped per circuit）。工作（更新預覽、輸出 PNG、輸出至 Unity）全部進同一個佇列、一次跑一個，
// 不鎖畫面；clip 渲染吃 GPU/記憶體，Unity batchmode 也不能同時開兩個。
public sealed class CgManagerState : IDisposable
{
    private readonly AppSettings _settings;
    private readonly object _queueLock = new();
    private readonly Queue<CgJob> _queue = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private CancellationTokenSource? _cancel;
    private bool _workerRunning;
    private string? _projectRoot;
    private CgLocalState _local = CgLocalState.Load();

    public CgManagerState(AppSettings settings)
    {
        _settings = settings;
    }

    public event Action? Changed;

    public List<CgEntry> Entries { get; private set; } = new();
    public List<string> AllChapters { get; private set; } = new();
    public HashSet<string> SelectedChapters { get; private set; } = new(StringComparer.Ordinal);
    public CgSourceStatus? StatusFilter { get; set; }
    public string SearchText { get; set; } = string.Empty;
    public List<string> PrefabNames { get; private set; } = new();
    public List<CgShare> Shares { get; private set; } = new();
    public List<CgLogLine> Log { get; } = new();
    public bool IsScanning { get; private set; }
    public bool HasScanned { get; private set; }
    public string? ScanError { get; private set; }

    // 狀態列
    public bool IsBusy => _workerRunning;
    public string CurrentJob { get; private set; } = string.Empty;
    public string CurrentStage { get; private set; } = string.Empty;
    public string CurrentDetail { get; private set; } = string.Empty;
    public DateTime CurrentStageStarted { get; private set; }
    public int QueuedJobCount { get { lock (_queueLock) return _queue.Count; } }

    // 卡片遮罩：CG → 「處理中：xxx」/「排隊中」
    public Dictionary<string, string> CardActivity { get; } = new(StringComparer.Ordinal);

    public float PreviewSize
    {
        get => _local.PreviewSize;
        set => _local.PreviewSize = Math.Clamp(value, 160f, 640f);
    }

    public void SavePreviewSize() => SaveLocal();

    public IEnumerable<CgEntry> FilteredEntries => Entries.Where(e =>
        (e.Chapters.Count == 0 ? SelectedChapters.Contains(NoChapter) : e.Chapters.Any(SelectedChapters.Contains)) &&
        (StatusFilter == null || e.SourceStatus == StatusFilter) &&
        (string.IsNullOrWhiteSpace(SearchText) || e.CgId.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)));

    // 沒有劇本使用、只因共用表出現的 CG（例如只當容器的主 CG）。
    public const string NoChapter = "(劇本未使用)";

    public int CountByStatus(CgSourceStatus status) =>
        Entries.Where(e => e.Chapters.Count == 0 ? SelectedChapters.Contains(NoChapter) : e.Chapters.Any(SelectedChapters.Contains))
            .Count(e => e.SourceStatus == status);

    public string ProjectRoot => _projectRoot ??= ProjectPaths.ResolveUnityProjectRoot();

    public string UnityExePath => !string.IsNullOrWhiteSpace(_settings.UnityExePath)
        ? _settings.UnityExePath
        : UnityCgImportClient.GuessUnityExePath(ProjectRoot);

    public async Task ScanAsync()
    {
        if (IsScanning)
            return;

        IsScanning = true;
        ScanError = null;
        NotifyChanged();
        try
        {
            await Task.Run(Rebuild);
            HasScanned = true;
            EnsureWatchers();
        }
        catch (Exception e)
        {
            ScanError = e.Message;
            AddLog(CgLogLevel.Error, $"掃描失敗：{e.Message}");
        }
        finally
        {
            IsScanning = false;
            NotifyChanged();
        }
    }

    private void Rebuild()
    {
        _local = CgLocalState.Load();
        var scripts = CgScriptScanner.Scan(_settings.ExternalDataFolder);
        var index = new CgSourceIndex(_settings.CgRootFolder);
        var unity = new CgUnityState(ProjectRoot);
        var specialCases = CgSpecialCasesFile.Load(ProjectRoot);
        var entries = CgCatalog.Build(new CgCatalogInput
        {
            Scripts = scripts,
            SourceIndex = index,
            SpecialCases = specialCases,
            Types = CgTypeSettings.Load(_settings.ExternalDataFolder),
            Unity = unity,
            Local = _local
        });

        var chapters = scripts.Chapters.Where(c => entries.Any(e => e.Chapters.Contains(c))).ToList();
        if (entries.Any(e => e.Chapters.Count == 0))
            chapters.Add(NoChapter);

        // 新出現的章節預設勾選；使用者取消勾選過的章節維持取消（與劇本管線狀態的章節篩選相同行為）。
        var known = new HashSet<string>(AllChapters, StringComparer.Ordinal);
        if (!HasScanned)
        {
            SelectedChapters = _local.SelectedChapters != null
                ? new HashSet<string>(_local.SelectedChapters.Where(chapters.Contains), StringComparer.Ordinal)
                : new HashSet<string>(chapters, StringComparer.Ordinal);
            if (_local.SelectedChapters != null)
                SelectedChapters.UnionWith(chapters.Where(c => !_local.SelectedChapters.Contains(c) && !(_local.KnownChapters?.Contains(c) ?? false)));
        }
        else
        {
            SelectedChapters.UnionWith(chapters.Where(c => !known.Contains(c)));
        }

        Entries = entries;
        AllChapters = chapters;
        Shares = specialCases.Shares;
        PrefabNames = unity.PrefabNames.ToList();

        if (!HasScanned)
        {
            if (!index.RootExists)
                AddLog(CgLogLevel.Error, $"CG 根目錄不存在：{_settings.CgRootFolder}（請到設定修改）");
            foreach (var message in scripts.Messages.Concat(unity.Messages))
                AddLog(CgLogLevel.Anomaly, message);
        }
    }

    public void ToggleChapter(string chapter, bool selected)
    {
        if (selected)
            SelectedChapters.Add(chapter);
        else
            SelectedChapters.Remove(chapter);
        SaveChapterSelection();
    }

    public void SetAllChapters(bool selected)
    {
        SelectedChapters = selected ? new HashSet<string>(AllChapters, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        SaveChapterSelection();
    }

    private void SaveChapterSelection()
    {
        _local.SelectedChapters = SelectedChapters.ToList();
        _local.KnownChapters = AllChapters.ToList();
        SaveLocal();
    }

    public async Task SetStaticAsync(CgEntry entry, bool isStatic)
    {
        CgTypeSettings.SetStatic(_settings.ExternalDataFolder, entry.CgId, isStatic);
        await ScanAsync();
    }

    // ---- CG 共用表 ----

    public string? SaveShare(string? previousCgId, CgShare share)
    {
        share.CgId = share.CgId.Trim();
        if (string.IsNullOrEmpty(share.CgId))
            return "CG 名稱不能是空的";
        if (string.IsNullOrEmpty(share.Prefab))
            return "請選擇 prefab";
        if (share.CgId == share.Prefab)
            return "不能共用自己的 prefab";

        CgSpecialCasesFile.SaveRow(ProjectRoot, previousCgId, share);
        return null;
    }

    public void DeleteShare(string cgId) => CgSpecialCasesFile.SaveRow(ProjectRoot, cgId, null);

    // 共用表的差分選項：主 CG 來源（clip 構圖 / spine 動畫 / png）或 Unity prefab 已知的差分。
    public List<string> GetPrefabDiffOptions(string prefab)
    {
        var entry = Entries.FirstOrDefault(e => e.CgId == prefab);
        var diffs = new List<string>();
        if (entry?.SourceDiffs != null)
            diffs.AddRange(entry.SourceDiffs);
        diffs.AddRange(new CgUnityState(ProjectRoot).GetPrefabDiffs(prefab));
        if (entry?.Usage != null)
            diffs.AddRange(entry.Usage.Diffs);
        foreach (var share in Shares.Where(s => s.Prefab == prefab))
            diffs.AddRange(share.Diffs);
        return diffs.Distinct(StringComparer.Ordinal).OrderBy(x => x, NaturalStringComparer.Instance).ToList();
    }

    public IEnumerable<string> ShareTargetOptions =>
        PrefabNames.Concat(Entries.Where(e => e.Share == null).Select(e => e.CgId)).Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, NaturalStringComparer.Instance);

    private void EnsureWatchers()
    {
        if (_watchers.Count > 0)
            return;

        void Watch(string path)
        {
            var folder = Path.GetDirectoryName(path);
            if (folder == null || !Directory.Exists(folder))
                return;

            var watcher = new FileSystemWatcher(folder, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            watcher.Changed += OnSettingsFileChanged;
            watcher.Created += OnSettingsFileChanged;
            watcher.Renamed += OnSettingsFileChanged;
            _watchers.Add(watcher);
        }

        Watch(CgSpecialCasesFile.GetPath(ProjectRoot));
        Watch(Path.Combine(_settings.ExternalDataFolder, CgTypeSettings.FileName));
    }

    private int _reloadPending;

    // 另一邊（Unity 的 CG 共用表、AI、另一個分頁）改了 JSON：稍等寫完再重掃。
    private async void OnSettingsFileChanged(object sender, FileSystemEventArgs e)
    {
        if (Interlocked.Exchange(ref _reloadPending, 1) == 1)
            return;

        await Task.Delay(400);
        Interlocked.Exchange(ref _reloadPending, 0);
        await ScanAsync();
    }

    // ---- 檔案操作（右鍵選單） ----

    public void OpenFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void OpenFolder(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        var folder = File.Exists(path) ? Path.GetDirectoryName(path) : path;
        if (folder != null && Directory.Exists(folder))
            Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{folder}\"") { UseShellExecute = true });
    }

    // ---- 工作佇列 ----

    private sealed class CgJob
    {
        public required string Name { get; init; }
        public required List<CgEntry> Entries { get; init; }
        public required Func<CgJob, CancellationToken, Task> Run { get; init; }
    }

    public void EnqueuePreview(IEnumerable<CgEntry> entries) =>
        Enqueue("更新預覽圖", entries.ToList(), RunPreviewAsync);

    public void EnqueueExportPng(IEnumerable<CgEntry> entries) =>
        Enqueue("輸出 PNG", entries.ToList(), RunExportPngAsync);

    // 回傳要先問「覆蓋 / 略過」的 CG；暫代圖不問，直接覆蓋。
    public List<CgOverwriteQuestion> GetOverwriteQuestions(IEnumerable<CgEntry> entries)
    {
        var unity = new CgUnityState(ProjectRoot);
        var questions = new List<CgOverwriteQuestion>();
        foreach (var entry in entries)
        {
            if (entry.UnityStatus is CgUnityStatus.NotExported or CgUnityStatus.Placeholder)
                continue;

            var current = unity.GetPrefabKind(entry.OwnerId);
            var target = entry.ExportKind switch
            {
                CgExportKind.Spine => CgPrefabKind.Spine,
                CgExportKind.Clip or CgExportKind.Png or CgExportKind.Placeholder => CgPrefabKind.Sprite,
                _ => current
            };
            var detail = current != CgPrefabKind.None && target != current
                ? $"類型將由{KindName(current)}變更為{KindName(target)}，舊類型的資料會刪除。"
                : string.Empty;
            questions.Add(new CgOverwriteQuestion(entry, detail));
        }

        return questions;
    }

    private static string KindName(CgPrefabKind kind) => kind == CgPrefabKind.Spine ? "動態圖" : "靜態圖";

    public void EnqueueUnityExport(IEnumerable<CgEntry> entries) =>
        Enqueue("輸出至 Unity", entries.ToList(), RunUnityExportAsync);

    private void Enqueue(string name, List<CgEntry> entries, Func<CgJob, CancellationToken, Task> run)
    {
        if (entries.Count == 0)
        {
            AddLog(CgLogLevel.Info, $"{name}：目前篩選沒有 CG。");
            return;
        }

        lock (_queueLock)
        {
            // 同一張 CG 的同一種工作已在排隊就不重複排。
            if (_queue.Any(j => j.Name == name && j.Entries.Select(e => e.CgId).SequenceEqual(entries.Select(e => e.CgId))))
                return;

            _queue.Enqueue(new CgJob { Name = name, Entries = entries, Run = run });
            foreach (var entry in entries)
                CardActivity.TryAdd(entry.CgId, "排隊中");
        }

        NotifyChanged();
        if (!_workerRunning)
            _ = RunWorkerAsync();
    }

    // 取消 = 目前這一步（Csp / Spine 程序）中止，佇列清空；已經交給 Unity 的部分不能取消，會等它跑完。
    public void Cancel()
    {
        lock (_queueLock)
        {
            foreach (var job in _queue)
                foreach (var entry in job.Entries)
                    CardActivity.Remove(entry.CgId);
            _queue.Clear();
        }

        _cancel?.Cancel();
        AddLog(CgLogLevel.Info, "已取消；已完成的部分保留。");
        NotifyChanged();
    }

    private async Task RunWorkerAsync()
    {
        _workerRunning = true;
        try
        {
            while (true)
            {
                CgJob? job;
                lock (_queueLock)
                    job = _queue.Count > 0 ? _queue.Dequeue() : null;
                if (job == null)
                    break;

                _cancel = new CancellationTokenSource();
                CurrentJob = job.Name;
                SetStage("開始", string.Empty);
                foreach (var entry in job.Entries)
                    CardActivity[entry.CgId] = "排隊中";
                try
                {
                    await Task.Run(() => job.Run(job, _cancel.Token));
                }
                catch (OperationCanceledException)
                {
                    AddLog(CgLogLevel.Info, $"{job.Name}：已取消。");
                }
                catch (Exception e)
                {
                    AddLog(CgLogLevel.Error, $"{job.Name} 失敗：{e.Message}");
                }
                finally
                {
                    foreach (var entry in job.Entries)
                        CardActivity.Remove(entry.CgId);
                    _cancel.Dispose();
                    _cancel = null;
                }

                await Task.Run(Rebuild);
                NotifyChanged();
            }
        }
        finally
        {
            _workerRunning = false;
            CurrentJob = string.Empty;
            CurrentStage = string.Empty;
            CurrentDetail = string.Empty;
            NotifyChanged();
        }
    }

    private void SetStage(string stage, string detail)
    {
        if (stage != CurrentStage)
            CurrentStageStarted = DateTime.Now;
        CurrentStage = stage;
        CurrentDetail = detail;
        NotifyChanged();
    }

    private void OnToolProgress(CgToolProgress progress) => SetStage(progress.Stage, progress.Detail);

    private void MarkActive(IEnumerable<CgEntry> entries, string activity)
    {
        foreach (var entry in entries)
            CardActivity[entry.CgId] = activity;
        NotifyChanged();
    }

    // ---- 更新預覽圖 ----

    private async Task RunPreviewAsync(CgJob job, CancellationToken token)
    {
        var owners = OwnersOf(job.Entries);
        var clipOwners = owners.Where(e => e.Clip != null).ToList();
        if (clipOwners.Count > 0)
        {
            MarkActive(clipOwners, "處理中：clip 預覽");
            await RenderClipsAsync(clipOwners, full: false, token);
        }

        foreach (var owner in owners.Where(e => e.Spine != null && !e.IsStatic))
        {
            token.ThrowIfCancellationRequested();
            MarkActive(new[] { owner }, "處理中：spine 預覽");
            await ExportSpinePreviewAsync(owner, token);
        }

        foreach (var owner in owners.Where(e => e.Clip == null && (e.Spine == null || e.IsStatic)))
            AddLog(CgLogLevel.Info, $"[{owner.CgId}] 沒有 clip / spine，預覽直接使用{(owner.HasPng ? " png" : "暫代圖")}。");
    }

    // 共用者的美術檔屬於主 CG：預覽 / 輸出 PNG 都對主 CG 做。
    private List<CgEntry> OwnersOf(IEnumerable<CgEntry> entries) =>
        entries.Select(e => e.Share == null ? e : Entries.FirstOrDefault(x => x.CgId == e.OwnerId) ?? e)
            .DistinctBy(e => e.CgId).ToList();

    private async Task<Dictionary<string, CspClipResult>> RenderClipsAsync(List<CgEntry> owners, bool full, CancellationToken token)
    {
        var jobs = new List<CspClipJob>();
        foreach (var owner in owners)
        {
            var folder = Path.GetDirectoryName(owner.Clip!)!;
            if (full)
                DeleteClipExports(folder, owner.CgId);

            var outputs = new List<CspOutput> { CspOutput.Preview(Path.Combine(folder, $"{CgNaming.SafeFileName(owner.CgId)}_{{comp}}{CgNaming.PreviewSuffix}")) };
            if (full)
                outputs.Add(CspOutput.Full(Path.Combine(folder, $"{CgNaming.SafeFileName(owner.CgId)}_{{comp}}.png")));
            jobs.Add(new CspClipJob { Clip = owner.Clip!, RenderAll = outputs });
        }

        SetStage("啟動 Csp 工具", string.Empty);
        var results = await CspBatchClient.RunAsync(_settings.CspExePath, jobs, OnToolProgress, token);
        var byClip = new Dictionary<string, CspClipResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            byClip[result.Clip] = result;
            var owner = owners.FirstOrDefault(o => string.Equals(Path.GetFullPath(o.Clip!), Path.GetFullPath(result.Clip), StringComparison.OrdinalIgnoreCase));
            if (owner == null)
                continue;

            byClip[owner.Clip!] = result;
            if (result.Comps.Count > 0)
                _local.SetCachedDiffs(owner.Clip!, result.Comps.Where(c => !string.IsNullOrWhiteSpace(c)).ToList());
            foreach (var error in result.Errors)
                AddLog(CgLogLevel.Error, $"[{owner.CgId}] {error}");
            if (result.Ok)
                AddLog(CgLogLevel.Info, $"[{owner.CgId}] clip {(full ? "已輸出 PNG 與預覽" : "預覽已更新")}：{string.Join(", ", result.Comps)}");
        }

        SaveLocal();
        return byClip;
    }

    // clip 同級資料夾裡這張 CG 的舊匯出（{CG}_*.png，不含預覽圖）先刪掉，避免殘留已刪除的差分。
    private static void DeleteClipExports(string folder, string cgId)
    {
        foreach (var file in Directory.GetFiles(folder, $"{CgNaming.SafeFileName(cgId)}_*.png"))
        {
            if (!CgNaming.IsPreviewFile(file) && !file.EndsWith("_thumbnail.png", StringComparison.OrdinalIgnoreCase))
                File.Delete(file);
        }
    }

    private async Task<List<string>> ExportSpinePreviewAsync(CgEntry owner, CancellationToken token)
    {
        var animations = await SpinePreviewExporter.ExportAsync(_settings.SpineCliPath, owner.Spine!, owner.CgId, OnToolProgress, token);
        _local.SetCachedDiffs(owner.Spine!, animations);
        SaveLocal();
        AddLog(CgLogLevel.Info, $"[{owner.CgId}] spine 預覽已更新：{string.Join(", ", animations)}");
        return animations;
    }

    // ---- 輸出 PNG ----

    private async Task RunExportPngAsync(CgJob job, CancellationToken token)
    {
        var owners = OwnersOf(job.Entries);
        foreach (var skipped in owners.Where(e => e.Clip == null))
            AddLog(CgLogLevel.Info, $"[{skipped.CgId}] 沒有 clip，略過輸出 PNG。");

        var clipOwners = owners.Where(e => e.Clip != null).ToList();
        if (clipOwners.Count == 0)
            return;

        MarkActive(clipOwners, "處理中：輸出 PNG");
        await RenderClipsAsync(clipOwners, full: true, token);
    }

    // ---- 輸出至 Unity ----

    private async Task RunUnityExportAsync(CgJob job, CancellationToken token)
    {
        // 共用者需要主 CG 的 prefab：主 CG 還沒進 Unity 就一起輸出。
        var targets = job.Entries.ToList();
        var unity = new CgUnityState(ProjectRoot);
        foreach (var share in job.Entries.Where(e => e.Share != null))
        {
            var owner = Entries.FirstOrDefault(x => x.CgId == share.OwnerId);
            if (owner != null && !unity.HasPrefab(owner.CgId) && targets.All(t => t.CgId != owner.CgId))
            {
                targets.Insert(0, owner);
                AddLog(CgLogLevel.Info, $"[{share.CgId}] 主 CG {owner.CgId} 還沒進 Unity，一起輸出。");
            }
        }

        targets = targets.Where(t => t.Share == null).Concat(targets.Where(t => t.Share != null)).ToList();
        var staging = Path.Combine(Path.GetTempPath(), "StoryForgeCgExport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var request = new UnityImportRequest();
        var prepared = new List<CgEntry>();

        try
        {
            // 1. clip 一次批次渲染（1920 正式 + 預覽），存一份在 clip 同級資料夾。
            var clipTargets = targets.Where(t => t.ExportKind == CgExportKind.Clip).ToList();
            var clipResults = new Dictionary<string, CspClipResult>(StringComparer.OrdinalIgnoreCase);
            if (clipTargets.Count > 0)
            {
                MarkActive(clipTargets, "處理中：clip 匯出");
                clipResults = await RenderClipsAsync(clipTargets, full: true, token);
            }

            // 2. 逐張準備 Unity 匯入項目。
            for (var i = 0; i < targets.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var entry = targets[i];
                MarkActive(new[] { entry }, "處理中：準備檔案");
                SetStage($"準備檔案 {i + 1}/{targets.Count} · {entry.CgId}", string.Empty);
                try
                {
                    var item = await PrepareItemAsync(entry, staging, clipResults, token);
                    if (item != null)
                    {
                        request.Items.Add(item);
                        prepared.Add(entry);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    AddLog(CgLogLevel.Error, $"[{entry.CgId}] 失敗：{e.Message}");
                }
            }

            if (request.Items.Count == 0)
                return;

            // 3. 交給 Unity（不能取消）。
            MarkActive(prepared, "處理中：Unity 匯入");
            var result = await UnityCgImportClient.RunAsync(ProjectRoot, UnityExePath, request, OnToolProgress, CancellationToken.None);
            foreach (var entry in prepared)
            {
                var itemResult = result.Items.FirstOrDefault(r => r.CgId == entry.CgId);
                if (itemResult == null || !itemResult.Ok)
                {
                    AddLog(CgLogLevel.Error, $"[{entry.CgId}] 失敗：{itemResult?.Error ?? "Unity 沒有回報結果"}");
                    continue;
                }

                _local.ExportedSourceTimes[entry.CgId] = entry.SourceTimeUtc;
                LogExported(entry);
            }

            SaveLocal();
        }
        finally
        {
            try { Directory.Delete(staging, true); } catch { /* 暫存清不掉不影響結果 */ }
        }
    }

    private void LogExported(CgEntry entry)
    {
        var via = entry.ExportKind switch
        {
            CgExportKind.Spine => "spine",
            CgExportKind.Clip => "clip",
            CgExportKind.Png => "png",
            CgExportKind.Share => $"共用 {entry.OwnerId} 的 prefab",
            _ => "暫代圖"
        };
        switch (entry.SourceStatus)
        {
            case CgSourceStatus.Missing:
                AddLog(CgLogLevel.Missing, $"[{entry.CgId}] 缺檔：找不到 clip / png / spine，已放暫代圖。");
                break;
            case CgSourceStatus.MissingSpine:
                AddLog(CgLogLevel.MissingSpine, $"[{entry.CgId}] 缺 spine：已輸出 {via}。");
                break;
            case CgSourceStatus.Anomaly:
                AddLog(CgLogLevel.Anomaly, $"[{entry.CgId}] 異常（已輸出 {via}）：{string.Join("；", entry.SourceIssues)}");
                break;
            default:
                AddLog(CgLogLevel.Done, $"[{entry.CgId}] 已完成（{via}）。");
                break;
        }
    }

    private async Task<UnityImportItem?> PrepareItemAsync(CgEntry entry, string staging, Dictionary<string, CspClipResult> clipResults,
        CancellationToken token)
    {
        var folder = Path.Combine(staging, CgNaming.SafeFileName(entry.CgId));
        Directory.CreateDirectory(folder);
        var thumbnail = Path.Combine(folder, "thumbnail.png");
        var item = new UnityImportItem
        {
            CgId = entry.CgId,
            ThumbnailPath = thumbnail,
            DefinitionDiffs = entry.DefinitionDiffs,
            NoDefinition = entry.ContainerOnly
        };
        var firstDiff = entry.Diffs.FirstOrDefault(d => !d.NotOwned)?.Diff ?? CgPlaceholderRenderer.DefaultDiffId;

        switch (entry.ExportKind)
        {
            case CgExportKind.Spine:
            {
                item.Kind = UnityImportKind.Spine;
                item.SpineProjectPath = entry.Spine;
                item.SpineCliPath = _settings.SpineCliPath;
                var preview = Path.Combine(Path.GetDirectoryName(entry.Spine!)!, CgNaming.PreviewFileName(entry.CgId, firstDiff));
                if (!File.Exists(preview))
                {
                    var animations = await ExportSpinePreviewAsync(entry, token);
                    if (!animations.Contains(firstDiff) && animations.Count > 0)
                        preview = Path.Combine(Path.GetDirectoryName(entry.Spine!)!, CgNaming.PreviewFileName(entry.CgId, animations[0]));
                }
                File.Copy(preview, thumbnail, true);
                break;
            }
            case CgExportKind.Clip:
            {
                if (!clipResults.TryGetValue(entry.Clip!, out var result) || result.Outputs.Count == 0)
                    throw new InvalidOperationException("clip 沒有輸出任何構圖。");
                item.Kind = UnityImportKind.Sprite;
                item.PngFolder = folder;
                var clipFolder = Path.GetDirectoryName(entry.Clip!)!;
                foreach (var comp in result.Comps.Where(c => !string.IsNullOrWhiteSpace(c)))
                {
                    var exported = Path.Combine(clipFolder, CgNaming.DiffFileName(entry.CgId, comp));
                    if (File.Exists(exported))
                        File.Copy(exported, Path.Combine(folder, CgNaming.DiffFileName(entry.CgId, comp)), true);
                }
                var preview = Path.Combine(clipFolder, CgNaming.PreviewFileName(entry.CgId, result.Comps.Contains(firstDiff) ? firstDiff : result.Comps[0]));
                File.Copy(preview, thumbnail, true);
                break;
            }
            case CgExportKind.Png:
            {
                item.Kind = UnityImportKind.Sprite;
                item.PngFolder = folder;
                foreach (var (diff, files) in entry.Sources.Pngs)
                    File.Copy(files[0], Path.Combine(folder, CgNaming.DiffFileName(entry.CgId, diff)), true);
                var thumbDiff = entry.Sources.Pngs.ContainsKey(firstDiff) ? firstDiff : entry.Sources.Pngs.Keys.First();
                CgImageUtility.SaveLetterboxed(entry.Sources.Pngs[thumbDiff][0], thumbnail, CgCatalogConstants.PreviewWidth, CgCatalogConstants.PreviewHeight);
                break;
            }
            case CgExportKind.Placeholder:
            {
                item.Kind = UnityImportKind.Placeholder;
                item.PngFolder = folder;
                var diffs = entry.Diffs.Where(d => !d.NotOwned).ToList();
                for (var i = 0; i < diffs.Count; i++)
                {
                    var png = CgPlaceholderRenderer.RenderPng(entry.CgId, diffs[i].Diff, i, diffs.Count,
                        CgPlaceholderRenderer.GetHue(entry.HueIndex), diffs[i].Memo, 1920, 1080);
                    File.WriteAllBytes(Path.Combine(folder, CgNaming.DiffFileName(entry.CgId, diffs[i].Diff)), png);
                    if (i == 0)
                        File.WriteAllBytes(thumbnail, CgPlaceholderRenderer.RenderPng(entry.CgId, diffs[i].Diff, i, diffs.Count,
                            CgPlaceholderRenderer.GetHue(entry.HueIndex), diffs[i].Memo, CgCatalogConstants.PreviewWidth, CgCatalogConstants.PreviewHeight));
                }
                break;
            }
            case CgExportKind.Share:
            {
                item.Kind = UnityImportKind.Share;
                item.SharePrefabOf = entry.OwnerId;
                var view = entry.Diffs.FirstOrDefault(d => !d.NotOwned);
                if (view?.PreviewPath != null && File.Exists(view.PreviewPath))
                    CgImageUtility.SaveLetterboxed(view.PreviewPath, thumbnail, CgCatalogConstants.PreviewWidth, CgCatalogConstants.PreviewHeight);
                else
                    File.WriteAllBytes(thumbnail, CgPlaceholderRenderer.RenderPng(entry.CgId, firstDiff, 0, 1,
                        CgPlaceholderRenderer.GetHue(entry.HueIndex), view?.Memo ?? string.Empty, CgCatalogConstants.PreviewWidth, CgCatalogConstants.PreviewHeight));
                break;
            }
        }

        return item;
    }

    // ---- Log ----

    public void AddLog(CgLogLevel level, string text)
    {
        lock (Log)
        {
            Log.Add(new CgLogLine(DateTime.Now, level, text));
            if (Log.Count > 2000)
                Log.RemoveRange(0, Log.Count - 2000);
        }
        NotifyChanged();
    }

    public void ClearLog()
    {
        lock (Log)
            Log.Clear();
        NotifyChanged();
    }

    private void SaveLocal()
    {
        try
        {
            _local.Save();
        }
        catch (Exception e)
        {
            AddLog(CgLogLevel.Error, $"無法寫入本機快取：{e.Message}");
        }
    }

    private void NotifyChanged() => Changed?.Invoke();

    public void Dispose()
    {
        _cancel?.Cancel();
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
    }
}
