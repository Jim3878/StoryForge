using StoryForge.Core.Graph;

namespace StoryForge.Core.Cg;

public sealed record CgScriptRef(string PlayscriptName, string FilePath, string Chapter);

// 某個 CG 在劇本裡的使用情況（所有 .md 的聯集）。
public sealed class CgUsage
{
    public CgUsage(string cgId) => CgId = cgId;

    public string CgId { get; }

    // 依劇本中第一次出現的順序。
    public List<string> Diffs { get; } = new();
    public SortedSet<string> Chapters { get; } = new(NaturalStringComparer.Instance);
    public List<CgScriptRef> Scripts { get; } = new();

    // 差分 → 備註（OpenCg 沒帶差分時寫的備註放在 ""，算第一個差分）。
    public Dictionary<string, List<string>> Memos { get; } = new(StringComparer.Ordinal);

    internal void AddDiff(string diff)
    {
        if (!Diffs.Contains(diff))
            Diffs.Add(diff);
    }

    internal void AddMemo(string diff, string memo)
    {
        if (!Memos.TryGetValue(diff, out var list))
            Memos[diff] = list = new List<string>();
        if (!list.Contains(memo))
            list.Add(memo);
    }

    internal void AddScript(CgScriptRef script)
    {
        Chapters.Add(script.Chapter);
        if (Scripts.All(x => x.FilePath != script.FilePath))
            Scripts.Add(script);
    }

    public string GetMemo(string diff, bool isFirstDiff)
    {
        var memos = new List<string>();
        if (Memos.TryGetValue(diff, out var diffMemos))
            memos.AddRange(diffMemos);
        if (isFirstDiff && Memos.TryGetValue(string.Empty, out var defaultMemos))
            memos.AddRange(defaultMemos.Where(x => !memos.Contains(x)));
        return string.Join("\n", memos);
    }
}

public sealed class CgScriptScanResult
{
    public Dictionary<string, CgUsage> Usages { get; } = new(StringComparer.Ordinal);
    public SortedSet<string> Chapters { get; } = new(NaturalStringComparer.Instance);
    public List<string> Messages { get; } = new();
}

// 掃 AI劇本\Nodes\**\*.md 的 OpenCg / Diff / CloseCg（功能欄），參數欄優先、台詞欄後備（與
// PlayscriptFactoryCodeGenerator 一致），可帶「;備註」。Forge 的 .md 是產 C# 前的中間資料，C# 不掃。
public static class CgScriptScanner
{
    public static CgScriptScanResult Scan(string externalDataFolder)
    {
        var result = new CgScriptScanResult();
        var nodesFolder = Path.Combine(externalDataFolder, "Nodes");
        if (!Directory.Exists(nodesFolder))
        {
            result.Messages.Add($"找不到劇本資料夾：{nodesFolder}");
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(nodesFolder, "*.md", SearchOption.AllDirectories)
                     .OrderBy(x => x, NaturalStringComparer.Instance))
        {
            var relative = Path.GetRelativePath(nodesFolder, file);
            var chapter = relative.Contains(Path.DirectorySeparatorChar) ? relative[..relative.IndexOf(Path.DirectorySeparatorChar)] : "(無章節)";
            var script = new CgScriptRef(ReadPlayscriptName(file, relative), file, chapter);
            result.Chapters.Add(chapter);
            CollectUsages(result, script);
        }

        return result;
    }

    // .md 第一行是「# 章節/名稱」，就是流程圖上的劇本名稱。
    private static string ReadPlayscriptName(string file, string relative)
    {
        var firstLine = File.ReadLines(file).FirstOrDefault() ?? string.Empty;
        if (firstLine.StartsWith("# ", StringComparison.Ordinal))
            return firstLine[2..].Trim();

        return Path.ChangeExtension(relative, null).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static void CollectUsages(CgScriptScanResult result, CgScriptRef script)
    {
        CgUsage? current = null;
        var currentDiff = string.Empty;
        var rows = NodeScriptTableStore.ReadRowsFromFile(script.FilePath);
        for (var i = 0; i < rows.Count; i++)
        {
            var cells = rows[i];
            var function = Cell(cells, NodeScriptTableStore.FunctionColumn).ToLowerInvariant();
            if (function is not ("opencg" or "diff" or "closecg"))
                continue;

            if (function == "closecg")
            {
                current = null;
                continue;
            }

            var raw = Cell(cells, NodeScriptTableStore.ParameterColumn);
            if (string.IsNullOrEmpty(raw))
                raw = Cell(cells, NodeScriptTableStore.TextColumn);
            var parts = raw.Split(';');
            var value = CgNaming.StripZeroWidth(parts[0]).Trim();
            var memo = parts.Length > 1 ? parts[1].Trim() : string.Empty;
            if (string.IsNullOrEmpty(value))
            {
                result.Messages.Add($"[劇本] {script.PlayscriptName} 第 {i + 1} 列 {Cell(cells, NodeScriptTableStore.FunctionColumn)} 沒有填參數。");
                continue;
            }

            if (function == "opencg")
            {
                if (!result.Usages.TryGetValue(value, out current))
                    result.Usages[value] = current = new CgUsage(value);
                current.AddScript(script);
                currentDiff = string.Empty;
                if (!string.IsNullOrEmpty(memo))
                    current.AddMemo(currentDiff, memo);
                continue;
            }

            if (current == null)
            {
                result.Messages.Add($"[劇本] {script.PlayscriptName} 第 {i + 1} 列 Diff {value} 之前沒有 OpenCg，未計入任何 CG。");
                continue;
            }

            current.AddDiff(value);
            currentDiff = value;
            if (!string.IsNullOrEmpty(memo))
                current.AddMemo(currentDiff, memo);
        }
    }

    private static string Cell(string[] cells, int index) => index < cells.Length ? cells[index].Trim() : string.Empty;
}
