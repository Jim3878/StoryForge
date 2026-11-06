namespace StoryForge.Core.Cg;

public enum CgFolderAction
{
    // 已經有 clip：不需要建。
    HasClip,

    // 沒有對得上的 clip，但 CG 根目錄下有名字對得上的資料夾（clip 檔名多半錯了或還沒放）：只打開它，不建新的。
    OpenExisting,

    // 都沒有：從範本建立。
    Create
}

public sealed record CgFolderPlan(CgFolderAction Action, string? ExistingFolder);

// 右鍵「從範本建立資料夾」：把範本資料夾整個複製到 CG 根目錄、改名成 CG 名稱，再把裡面的範本 clip 改名成 {CG}.clip。
// 範本 clip 一律是範本資料夾底下的相對路徑（例如 01_source\FalseBadgeCG.clip）。
public static class CgFolderTemplate
{
    public static CgFolderPlan Plan(string cgRoot, string cgId, bool hasClip)
    {
        if (hasClip)
            return new CgFolderPlan(CgFolderAction.HasClip, null);

        var existing = FindExistingFolder(cgRoot, cgId);
        return existing != null
            ? new CgFolderPlan(CgFolderAction.OpenExisting, existing)
            : new CgFolderPlan(CgFolderAction.Create, null);
    }

    // CG 根目錄第一層（CG 資料夾都平放一層）裡，名字依 CgNaming 規則對得上這張 CG 的資料夾。
    public static string? FindExistingFolder(string cgRoot, string cgId)
    {
        if (!Directory.Exists(cgRoot))
            return null;

        var key = CgNaming.Normalize(cgId);
        return Directory.EnumerateDirectories(cgRoot)
            .FirstOrDefault(folder => CgNaming.Normalize(Path.GetFileName(folder)) == key);
    }

    // 新資料夾 / clip 的名稱：CG 名稱去掉零寬字元，不再產生肉眼看不出來的怪名字。
    public static string FolderName(string cgId) => CgNaming.SafeFileName(CgNaming.StripZeroWidth(cgId).Trim());

    // 設定頁與執行前共用的檢查；沒問題回傳 null。
    public static string? Validate(string templateFolder, string templateClipRelativePath)
    {
        if (string.IsNullOrWhiteSpace(templateFolder) || !Directory.Exists(templateFolder))
            return $"範本資料夾不存在：{templateFolder}";

        try
        {
            var clip = GetTemplateClipPath(templateFolder, templateClipRelativePath);
            return File.Exists(clip) ? null : $"範本 clip 不存在：{clip}";
        }
        catch (InvalidOperationException e)
        {
            return e.Message;
        }
    }

    // 相對路徑不能跳出範本資料夾（絕對路徑或 ..\ 都擋掉）。
    public static string GetTemplateClipPath(string templateFolder, string templateClipRelativePath)
    {
        var relative = (templateClipRelativePath ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("範本 clip 要填範本資料夾底下的相對路徑，例如 01_source\\FalseBadgeCG.clip。");

        var template = Path.GetFullPath(templateFolder).TrimEnd(Path.DirectorySeparatorChar);
        var clip = Path.GetFullPath(Path.Combine(template, relative));
        if (!clip.StartsWith(template + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"範本 clip 必須在範本資料夾底下：{relative}");
        if (!clip.EndsWith(".clip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"範本 clip 必須是 .clip 檔：{relative}");
        return clip;
    }

    // 回傳新資料夾與新 clip 的完整路徑；前提不成立就丟 InvalidOperationException（訊息直接給使用者看）。
    // 複製到一半失敗時，把這次建了一半的資料夾刪掉，不留半成品。
    public static (string Folder, string Clip) Create(string cgRoot, string templateFolder, string templateClipRelativePath, string cgId)
    {
        var name = FolderName(cgId);
        if (string.IsNullOrEmpty(name))
            throw new InvalidOperationException("CG 名稱是空的。");
        if (!Directory.Exists(cgRoot))
            throw new InvalidOperationException($"CG 根目錄不存在：{cgRoot}（請到設定修改）");
        var problem = Validate(templateFolder, templateClipRelativePath);
        if (problem != null)
            throw new InvalidOperationException($"{problem}（請到設定修改）");
        var existing = FindExistingFolder(cgRoot, cgId);
        if (existing != null)
            throw new InvalidOperationException($"已經有對得上的資料夾：{existing}");

        var template = Path.GetFullPath(templateFolder).TrimEnd(Path.DirectorySeparatorChar);
        var templateClip = GetTemplateClipPath(template, templateClipRelativePath);
        var target = Path.Combine(Path.GetFullPath(cgRoot), name);
        if (Directory.Exists(target) || File.Exists(target))
            throw new InvalidOperationException($"資料夾已經存在：{target}");

        try
        {
            CopyDirectory(template, target);
            var copiedClip = Path.Combine(target, Path.GetRelativePath(template, templateClip));
            var newClip = Path.Combine(Path.GetDirectoryName(copiedClip)!, name + ".clip");
            if (!string.Equals(copiedClip, newClip, StringComparison.OrdinalIgnoreCase))
                File.Move(copiedClip, newClip);
            return (target, newClip);
        }
        catch
        {
            try { Directory.Delete(target, true); } catch { /* 清不掉就留著，錯誤訊息照樣往上丟 */ }
            throw;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
    }
}
