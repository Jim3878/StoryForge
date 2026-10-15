namespace StoryForge.Core.Cg;

public enum CgSourceStatus
{
    Missing,
    Anomaly,
    MissingSpine,
    Done
}

public enum CgUnityStatus
{
    NotExported,
    Placeholder,
    IncompleteDiffs,
    SourceNewer,
    Synced
}

// 輸出至 Unity 時走哪條路：spine（靜態圖時忽略）> clip > png > 暫代圖；共用其他 CG prefab 的只寫 definition。
public enum CgExportKind
{
    Spine,
    Clip,
    Png,
    Placeholder,
    Share
}

public sealed class CgDiffView
{
    public string Diff { get; init; } = string.Empty;

    // null = 顯示暫代圖。
    public string? PreviewPath { get; init; }
    public string PreviewSource { get; init; } = string.Empty;
    public bool PreviewMissing { get; init; }
    public bool UsedByScript { get; init; }
    public bool MissingInSource { get; init; }
    public bool NotOwned { get; init; }
    public string Memo { get; init; } = string.Empty;
}

public sealed class CgEntry
{
    public string CgId { get; init; } = string.Empty;
    public CgUsage? Usage { get; init; }
    public bool IsStatic { get; init; }
    public int HueIndex { get; init; }

    // 這張 CG 共用誰的 prefab（共用表的一列）；null = 用自己的 prefab。
    public CgShare? Share { get; init; }

    // 美術檔歸屬的 CG：共用者看主 CG 的檔案，檔名（含預覽圖）也用主 CG 的名稱。
    public string OwnerId => Share?.Prefab ?? CgId;
    public List<CgShare> SharedBy { get; init; } = new();
    public CgSourceFiles Sources { get; init; } = new();
    public string? Clip { get; init; }
    public string? Spine { get; init; }

    // 來源的差分清單（clip 構圖 / spine 動畫 / png）；null = 還不知道（clip/spine 尚未讀過）。
    public List<string>? SourceDiffs { get; init; }

    // 這張 CG 擁有、會寫進 definition 的差分；null = 全部（沒有人共用它的 prefab）。
    public List<string>? DefinitionDiffs { get; init; }
    public bool ContainerOnly { get; init; }

    public CgExportKind ExportKind { get; init; }
    public CgSourceStatus SourceStatus { get; init; }
    public List<string> SourceIssues { get; init; } = new();
    public CgUnityStatus UnityStatus { get; init; }
    public string UnityDetail { get; init; } = string.Empty;
    public DateTime SourceTimeUtc { get; init; }
    public List<CgDiffView> Diffs { get; init; } = new();

    public IReadOnlyCollection<string> Chapters => Usage?.Chapters ?? (IReadOnlyCollection<string>)Array.Empty<string>();
    public IReadOnlyList<CgScriptRef> Scripts => Usage?.Scripts ?? (IReadOnlyList<CgScriptRef>)Array.Empty<CgScriptRef>();
    public bool HasPng => Sources.Pngs.Count > 0;
}

public sealed class CgCatalogInput
{
    public required CgScriptScanResult Scripts { get; init; }
    public required CgSourceIndex SourceIndex { get; init; }
    public required CgSpecialCasesFile SpecialCases { get; init; }
    public required CgTypeSettings Types { get; init; }
    public required CgUnityState Unity { get; init; }
    public required CgLocalState Local { get; init; }
}

// 把劇本、美術檔、共用表、Unity 現況合成每張 CG 的狀態。規則見 docs/cg-manager-spec.md。
public static class CgCatalog
{
    public static List<CgEntry> Build(CgCatalogInput input)
    {
        var shares = input.SpecialCases.Shares.Where(x => !string.IsNullOrWhiteSpace(x.CgId)).ToList();
        var cgIds = input.Scripts.Usages.Keys
            .Concat(shares.Select(x => x.CgId))
            .Concat(shares.Select(x => x.Prefab).Where(x => !string.IsNullOrWhiteSpace(x)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, NaturalStringComparer.Instance)
            .ToList();
        var sources = input.SourceIndex.Resolve(cgIds);
        var statics = new HashSet<string>(input.Types.Static, StringComparer.Ordinal);

        var entries = new List<CgEntry>();
        for (var i = 0; i < cgIds.Count; i++)
        {
            var cgId = cgIds[i];
            var share = shares.FirstOrDefault(x => x.CgId == cgId);
            var ownerId = share?.Prefab ?? cgId;
            entries.Add(BuildEntry(input, cgId, i, share, shares.Where(x => x.Prefab == cgId && x.CgId != cgId).ToList(),
                sources.GetValueOrDefault(ownerId) ?? new CgSourceFiles(), statics.Contains(cgId) || (share != null && statics.Contains(ownerId))));
        }

        return entries;
    }

    private static CgEntry BuildEntry(CgCatalogInput input, string cgId, int hueIndex, CgShare? share, List<CgShare> sharedBy,
        CgSourceFiles sources, bool isStatic)
    {
        var usage = input.Scripts.Usages.GetValueOrDefault(cgId);
        var ownerId = share?.Prefab ?? cgId;
        var issues = new List<string>();
        var scriptDiffs = usage?.Diffs ?? new List<string>();

        if (sources.Clips.Count > 1)
            issues.Add("clip 重複：" + string.Join("、", sources.Clips));
        if (sources.Spines.Count > 1)
            issues.Add("spine 重複：" + string.Join("、", sources.Spines));
        if (isStatic && sources.Spines.Count > 0)
            issues.Add("設為靜態圖但有 spine，輸出時不使用 spine");

        var clip = sources.Clips.Count == 1 ? sources.Clips[0] : null;
        var spine = sources.Spines.Count == 1 ? sources.Spines[0] : null;
        var useSpine = spine != null && !isStatic;
        if (useSpine && !string.Equals(CgNaming.StripZeroWidth(Path.GetFileNameWithoutExtension(spine)), ownerId, StringComparison.Ordinal))
            issues.Add($"spine 檔名必須是 {ownerId}.spine（Unity 依檔名決定 CG 名稱）：{spine}");

        var kind = share != null ? CgExportKind.Share
            : useSpine ? CgExportKind.Spine
            : clip != null ? CgExportKind.Clip
            : sources.Pngs.Count > 0 ? CgExportKind.Png
            : CgExportKind.Placeholder;
        var sourceKind = share != null
            ? useSpine ? CgExportKind.Spine : clip != null ? CgExportKind.Clip : sources.Pngs.Count > 0 ? CgExportKind.Png : CgExportKind.Placeholder
            : kind;

        if (sourceKind == CgExportKind.Png)
        {
            foreach (var (diff, files) in sources.Pngs.Where(x => x.Value.Count > 1))
                issues.Add($"png 差分 {diff} 重複：" + string.Join("、", files));
        }

        var sourceDiffs = sourceKind switch
        {
            CgExportKind.Spine => input.Local.GetCachedDiffs(spine!),
            CgExportKind.Clip => input.Local.GetCachedDiffs(clip!),
            CgExportKind.Png => sources.Pngs.Keys.OrderBy(x => x, NaturalStringComparer.Instance).ToList(),
            _ => null
        };

        // 差分歸屬：共用者擁有共用表上的差分，主 CG 擁有其餘差分。
        List<string>? definitionDiffs = null;
        var containerOnly = false;
        if (share != null)
        {
            definitionDiffs = share.Diffs.ToList();
            if (string.IsNullOrWhiteSpace(share.Prefab))
                issues.Add("共用表沒有指定 prefab");
            if (sourceDiffs != null)
            {
                var notInSource = share.Diffs.Where(x => !sourceDiffs.Contains(x)).ToList();
                if (notInSource.Count > 0)
                    issues.Add($"共用表指定的差分在 {ownerId} 的來源裡不存在：{string.Join(", ", notInSource)}");
            }
        }
        else if (sharedBy.Count > 0)
        {
            var assigned = sharedBy.SelectMany(x => x.Diffs.Select(d => (Diff: d, x.CgId))).ToList();
            foreach (var duplicate in assigned.GroupBy(x => x.Diff).Where(x => x.Count() > 1))
                issues.Add($"差分 {duplicate.Key} 同時分給 {string.Join("、", duplicate.Select(x => x.CgId))}");

            var all = sourceDiffs ?? scriptDiffs.Concat(assigned.Select(x => x.Diff)).Distinct().ToList();
            definitionDiffs = all.Where(x => assigned.All(a => a.Diff != x)).ToList();
            containerOnly = definitionDiffs.Count == 0 && sourceDiffs != null;
            var unused = definitionDiffs.Where(x => !scriptDiffs.Contains(x)).ToList();
            if (unused.Count > 0)
                issues.Add($"剩餘差分沒有劇本使用：{string.Join(", ", unused)}");
        }

        if (definitionDiffs != null)
        {
            var polluted = scriptDiffs.Where(x => !definitionDiffs.Contains(x)).ToList();
            if (polluted.Count > 0)
                issues.Add($"劇本呼叫了不屬於這張 CG 的差分：{string.Join(", ", polluted)}");
        }

        if (sourceDiffs != null && sourceKind != CgExportKind.Placeholder)
        {
            var missing = scriptDiffs.Where(x => !sourceDiffs.Contains(x) && (definitionDiffs == null || definitionDiffs.Contains(x))).ToList();
            if (missing.Count > 0)
                issues.Add($"來源缺劇本要的差分：{string.Join(", ", missing)}");
        }

        var hasAnySource = clip != null || spine != null || sources.Pngs.Count > 0;
        var status = !hasAnySource ? CgSourceStatus.Missing
            : issues.Count > 0 ? CgSourceStatus.Anomaly
            : !isStatic && spine == null ? CgSourceStatus.MissingSpine
            : CgSourceStatus.Done;
        if (!hasAnySource)
            issues.Insert(0, "找不到 clip / png / spine，輸出時放暫代圖");
        else if (!isStatic && spine == null)
            issues.Add("動態圖但沒有 spine，輸出時改用 " + (clip != null ? "clip" : "png"));

        var sourceTime = sourceKind switch
        {
            CgExportKind.Spine => File.GetLastWriteTimeUtc(spine!),
            CgExportKind.Clip => File.GetLastWriteTimeUtc(clip!),
            CgExportKind.Png => sources.Pngs.Values.SelectMany(x => x).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max(),
            _ => DateTime.MinValue
        };

        var (unityStatus, unityDetail) = EvaluateUnity(input, cgId, ownerId, containerOnly, definitionDiffs ?? scriptDiffs, scriptDiffs, sourceTime);

        return new CgEntry
        {
            CgId = cgId,
            Usage = usage,
            IsStatic = isStatic,
            HueIndex = hueIndex,
            Share = share,
            SharedBy = sharedBy,
            Sources = sources,
            Clip = clip,
            Spine = spine,
            SourceDiffs = sourceDiffs,
            DefinitionDiffs = definitionDiffs,
            ContainerOnly = containerOnly,
            ExportKind = kind,
            SourceStatus = status,
            SourceIssues = issues,
            UnityStatus = unityStatus,
            UnityDetail = unityDetail,
            SourceTimeUtc = sourceTime,
            Diffs = BuildDiffViews(usage, ownerId, clip, useSpine ? spine : null, sources, sourceDiffs, definitionDiffs, scriptDiffs)
        };
    }

    private static (CgUnityStatus, string) EvaluateUnity(CgCatalogInput input, string cgId, string ownerId, bool containerOnly,
        List<string> ownedDiffs, List<string> scriptDiffs, DateTime sourceTime)
    {
        var definition = input.Unity.GetDefinition(cgId);
        var hasPrefab = input.Unity.HasPrefab(ownerId);
        if (!hasPrefab || (definition == null && !containerOnly))
            return (CgUnityStatus.NotExported, hasPrefab ? "有 prefab 但沒有 definition" : "Unity 裡沒有這張 CG");

        if (input.Unity.IsPlaceholder(ownerId))
            return (CgUnityStatus.Placeholder, "Unity 裡是暫代圖");

        if (definition != null)
        {
            var missing = scriptDiffs.Where(x => ownedDiffs.Contains(x) && !definition.Diffs.Contains(x)).ToList();
            if (missing.Count > 0)
                return (CgUnityStatus.IncompleteDiffs, "Unity 缺差分：" + string.Join(", ", missing));
        }

        if (input.Local.ExportedSourceTimes.TryGetValue(cgId, out var exported) && sourceTime > exported.AddSeconds(1))
            return (CgUnityStatus.SourceNewer, "來源檔在上次輸出後被修改過");

        return (CgUnityStatus.Synced, containerOnly ? "僅供共用（不佔畫廊格子）" : "已同步");
    }

    private static List<CgDiffView> BuildDiffViews(CgUsage? usage, string ownerId, string? clip, string? spine, CgSourceFiles sources,
        List<string>? sourceDiffs, List<string>? definitionDiffs, List<string> scriptDiffs)
    {
        var order = new List<string>();
        order.AddRange(scriptDiffs.Where(x => definitionDiffs == null || definitionDiffs.Contains(x)));
        var extra = (definitionDiffs ?? sourceDiffs ?? new List<string>()).Where(x => !order.Contains(x)).OrderBy(x => x, NaturalStringComparer.Instance);
        order.AddRange(extra);
        order.AddRange(scriptDiffs.Where(x => !order.Contains(x)));
        if (order.Count == 0)
            order.Add(CgPlaceholderRenderer.DefaultDiffId);

        var views = new List<CgDiffView>();
        for (var i = 0; i < order.Count; i++)
        {
            var diff = order[i];
            string? path = null;
            var source = "暫代圖";
            var previewMissing = false;

            var clipPreview = clip != null ? Path.Combine(Path.GetDirectoryName(clip)!, CgNaming.PreviewFileName(ownerId, diff)) : null;
            var spinePreview = spine != null ? Path.Combine(Path.GetDirectoryName(spine)!, CgNaming.PreviewFileName(ownerId, diff)) : null;
            if (clipPreview != null && File.Exists(clipPreview))
                (path, source) = (clipPreview, "clip");
            else if (sources.Pngs.TryGetValue(diff, out var pngs) && pngs.Count > 0)
                (path, source) = (pngs[0], "png");
            else if (spinePreview != null && File.Exists(spinePreview))
                (path, source) = (spinePreview, "spine");

            if (clip != null && source != "clip" || spine != null && path == null)
                previewMissing = true;

            views.Add(new CgDiffView
            {
                Diff = diff,
                PreviewPath = path,
                PreviewSource = source,
                PreviewMissing = previewMissing,
                UsedByScript = scriptDiffs.Contains(diff),
                MissingInSource = sourceDiffs != null && !sourceDiffs.Contains(diff),
                NotOwned = definitionDiffs != null && !definitionDiffs.Contains(diff),
                Memo = usage?.GetMemo(diff, i == 0) ?? string.Empty
            });
        }

        return views;
    }
}
