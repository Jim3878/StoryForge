namespace StoryForge.Core.Codegen;

public static class PlayscriptTextReplacementUtility
{
    private static readonly (string From, string To)[] CommonPlayscriptCharacterReplacements =
    {
        ("❤︎", "♥"),
        ("❤️", "♥"),
        ("❤", "♥"),
    };

    public static string ApplyCommonPlayscriptCharacterReplacements(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var result = text;
        foreach (var (from, to) in CommonPlayscriptCharacterReplacements)
        {
            if (string.IsNullOrEmpty(from))
                continue;

            result = result.Replace(from, to ?? string.Empty);
        }

        return result;
    }
}
