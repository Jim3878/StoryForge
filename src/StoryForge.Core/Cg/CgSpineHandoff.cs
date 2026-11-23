using System.Text;

namespace StoryForge.Core.Cg;

// 「生成 spine 發包檔」：每月把畫好的 CG 交給 spine 外包用的資料夾。規則見 docs/cg-manager-spec.md。
// 版面照以前手動整理的：<發包資料夾>\<月>月Spine\<CG 去掉 CG_ 前綴>\{CG}.psd、{CG}_{差分}.png、{CG}.txt。
public static class CgSpineHandoff
{
    private const string CgPrefix = "CG_";

    public static string MonthFolder(string handoffRoot, DateTime now) => Path.Combine(handoffRoot, $"{now.Month}月Spine");

    public static string CgFolderName(string cgId)
    {
        var name = cgId.StartsWith(CgPrefix, StringComparison.OrdinalIgnoreCase) ? cgId[CgPrefix.Length..] : cgId;
        return CgNaming.SafeFileName(name);
    }

    // 每個差分一段「# {CG}_{差分}」，底下是劇本 OpenCg / Diff 的備註（一行一個 *）；沒有備註的差分只留標題給人補。
    public static string BuildNotes(string cgId, IEnumerable<string> diffs, Func<string, IEnumerable<string>> getMemos)
    {
        var text = new StringBuilder();
        foreach (var diff in diffs)
        {
            text.Append("# ").Append(cgId).Append('_').Append(diff).Append("\r\n");
            var lines = getMemos(diff)
                .SelectMany(memo => memo.Split('\n'))
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Distinct(StringComparer.Ordinal);
            foreach (var line in lines)
                text.Append("* ").Append(line).Append("\r\n");
            text.Append("\r\n");
        }

        return text.ToString();
    }

    // 說明檔可能被人手動補過：內容不同就保留原檔，回傳 false。
    public static bool WriteNotes(string path, string text)
    {
        if (File.Exists(path))
            return File.ReadAllText(path, Encoding.UTF8) == text;

        File.WriteAllText(path, text, new UTF8Encoding(false));
        return true;
    }
}
