namespace StoryForge.Core.Codegen;

public static class CodegenTextUtility
{
    private static readonly char[] ZeroWidthCharacters =
    {
        (char)0x200B,
        (char)0x200C,
        (char)0x200D,
        (char)0xFEFF,
        (char)0x2060,
    };

    public static string SanitizeText(object? text)
    {
        var result = text?.ToString() ?? string.Empty;
        foreach (var zeroWidthChar in ZeroWidthCharacters)
            result = result.Replace(zeroWidthChar.ToString(), string.Empty);

        return result.Trim();
    }

    public static string EscapeCSharpString(string text)
    {
        return SanitizeText(text)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", string.Empty)
            .Replace("\n", "\\n");
    }

    public static string EscapeComment(string text)
    {
        return SanitizeText(text)
            .Replace("\r", string.Empty)
            .Replace("\n", " ");
    }
}
