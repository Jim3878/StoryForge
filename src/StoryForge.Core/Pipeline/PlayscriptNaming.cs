using System.Globalization;

namespace StoryForge.Core.Pipeline;

public static class PlayscriptNaming
{
    public static string Normalize(string? playscript)
    {
        if (string.IsNullOrWhiteSpace(playscript))
            return string.Empty;

        var result = RemoveInvisibleFormatCharacters(playscript).Trim();
        var parameterIndex = result.IndexOf('?');
        if (parameterIndex >= 0)
            result = result[..parameterIndex];

        return result.Trim();
    }

    public static bool IsValidChapterPlayscriptName(string playscript)
    {
        var slashIndex = playscript.IndexOf('/');
        return slashIndex > 0 && slashIndex < playscript.Length - 1;
    }

    public static string GetChapterName(string playscript)
    {
        var slashIndex = playscript.IndexOf('/');
        return slashIndex <= 0 ? string.Empty : playscript[..slashIndex];
    }

    // Shared with GooglePlayscriptSheetClient for cleaning up worksheet tab names read from the
    // downloaded index (they can carry the same kind of invisible characters as playscript names).
    public static string RemoveInvisibleFormatCharacters(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return string.Concat(value.Where(x =>
            CharUnicodeInfo.GetUnicodeCategory(x) != UnicodeCategory.Format));
    }
}
