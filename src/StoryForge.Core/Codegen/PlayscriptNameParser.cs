using System.Text;

namespace StoryForge.Core.Codegen;

public sealed class ParsedPlayscriptName
{
    public ParsedPlayscriptName(
        string chapterId,
        string scriptId,
        string className,
        string playscriptName,
        bool isSideStory,
        string namespaceName,
        string outputFolderName,
        bool shouldEndRpgRoleWithoutFinish)
    {
        ChapterId = chapterId;
        ScriptId = scriptId;
        ClassName = className;
        PlayscriptName = playscriptName;
        IsSideStory = isSideStory;
        NamespaceName = namespaceName;
        OutputFolderName = outputFolderName;
        ShouldEndRpgRoleWithoutFinish = shouldEndRpgRoleWithoutFinish;
    }

    public string ChapterId { get; }
    public string ScriptId { get; }
    public string ClassName { get; }
    public string PlayscriptName { get; }
    public bool IsSideStory { get; }
    public string NamespaceName { get; }
    public string OutputFolderName { get; }
    public bool ShouldEndRpgRoleWithoutFinish { get; }

    // H場景 naming convention — every real H-scene script's ScriptId observed so far (H1/H2/HN10/...) starts
    // with a literal capital "H", with zero non-H-scene counter-examples across the whole PlayscriptFactory
    // output folder. Drives GenerateCode's choice of StartVideoAndFadeOn()/EndVideoAndFadeOff() in place of
    // the normal StartRpgRole()/EndRpgRole() wrapper — the dialogue body itself (RpgRole calls included)
    // stays exactly the same as any other script; only the start/end wrapper differs.
    public bool IsHScene => ScriptId.StartsWith("H", StringComparison.Ordinal);
}

public static class PlayscriptNameParser
{
    private const string WorldChapterId = "World";

    public static ParsedPlayscriptName Parse(string playscriptName)
    {
        var clean = CodegenTextUtility.SanitizeText(playscriptName).Replace("\\", "/");
        var parts = clean.Split('/').Select(CodegenTextUtility.SanitizeText).Where(x => !string.IsNullOrEmpty(x))
            .ToList();
        if (parts.Count < 2)
            throw new InvalidOperationException($"劇本名稱格式錯誤：{playscriptName}");

        if (IsWorldPlayscript(parts))
            return ParseWorldPlayscriptName(clean, parts);

        var chapterId = parts[0];
        var fileName = parts[^1];
        var fileParts = fileName.Split('_').Select(CodegenTextUtility.SanitizeText).ToList();
        if (fileParts.Count < 2)
            throw new InvalidOperationException($"劇本名稱缺少 ID 或標題：{playscriptName}");

        var scriptId = string.Join("_", fileParts.Take(fileParts.Count - 1));
        var className = $"{ToIdentifier(chapterId)}_{ToIdentifier(scriptId)}";

        return new ParsedPlayscriptName(
            chapterId,
            scriptId,
            className,
            clean,
            IsSideStoryPlayscript(clean),
            $"PlayscriptFactory.{ToIdentifier(chapterId)}",
            chapterId,
            false);
    }

    public static bool IsSideStoryPlayscript(string fullPlayscriptName)
    {
        fullPlayscriptName = (fullPlayscriptName ?? string.Empty).Trim();
        var pathTokens = SplitTokens(fullPlayscriptName, '/');
        var playscriptEntryName = pathTokens.Length > 0 ? pathTokens[^1] : string.Empty;
        var nameTokens = SplitTokens(playscriptEntryName, '_');
        var chapterPlayscriptId = nameTokens.Length > 1
            ? string.Join("_", nameTokens.Take(nameTokens.Length - 1))
            : playscriptEntryName;

        return fullPlayscriptName.Contains("閒話") || SplitTokens(chapterPlayscriptId, '_').Length > 1;
    }

    public static string ToIdentifier(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value)
            builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');

        return builder.ToString();
    }

    private static bool IsWorldPlayscript(List<string> parts)
    {
        return parts.Count >= 3 && string.Equals(parts[0], WorldChapterId, StringComparison.Ordinal);
    }

    // World playscripts end with EndRpgRoleWithoutFinish() (shouldEndRpgRoleWithoutFinish=true),
    // unlike chapter playscripts which end with EndRpgRole()/EndRpgRole(false).
    private static ParsedPlayscriptName ParseWorldPlayscriptName(string clean, List<string> parts)
    {
        var scriptId = string.Join("_", parts.Skip(1));
        var className = string.Join("_", parts.Skip(1).Select(ToIdentifier));

        return new ParsedPlayscriptName(
            WorldChapterId,
            scriptId,
            className,
            clean,
            IsSideStoryPlayscript(clean),
            "PlayscriptFactory",
            WorldChapterId,
            true);
    }

    private static string[] SplitTokens(string value, char separator)
    {
        return (value ?? string.Empty).Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries);
    }
}
