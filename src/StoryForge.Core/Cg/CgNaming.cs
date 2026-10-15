using System.Text.RegularExpressions;

namespace StoryForge.Core.Cg;

// CG 檔名比對規則：只看檔名，不看資料夾。容錯：去零寬字元、'-' 視為 '_'、CG_ 前綴可有可無、不分大小寫。
public static class CgNaming
{
    public const string PreviewSuffix = ".preview.png";
    private const string CgPrefix = "CG_";
    private static readonly Regex ZeroWidth = new("[​-‍﻿]", RegexOptions.Compiled);

    public static string StripZeroWidth(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : ZeroWidth.Replace(value, string.Empty);

    // 比對用的 key，不是顯示用的名稱。
    public static string Normalize(string name)
    {
        var normalized = StripZeroWidth(name).Trim().Replace('-', '_').ToUpperInvariant();
        return normalized.StartsWith(CgPrefix, StringComparison.Ordinal) ? normalized : CgPrefix + normalized;
    }

    public static bool IsPreviewFile(string path) =>
        path.EndsWith(PreviewSuffix, StringComparison.OrdinalIgnoreCase);

    public static string PreviewFileName(string cgId, string diff) => $"{SafeFileName(cgId)}_{SafeFileName(diff)}{PreviewSuffix}";

    public static string DiffFileName(string cgId, string diff) => $"{SafeFileName(cgId)}_{SafeFileName(diff)}.png";

    public static string SafeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value;
    }
}
