using System.Globalization;
using System.Text;
using Newtonsoft.Json;

namespace StoryForge.Core.Schemas;

// Port of Unity's PlayscriptEnumLabelMapExportUtility (the 精簡劇本管理器's 匯出Enum對照表 button), so that
// PlayscriptEnumLabelMaps.json can no longer go stale: Unity builds it by reflection over the DialogName/Portrait/
// ChatName/Face enums, which this standalone tool can't reference, so instead it reads the same [LabelText]
// attributes straight out of the enums' C# source. The JSON is still written back to its usual place in Assets
// (only when its content changed) in exactly Unity's format, so either side regenerating it gives an empty diff.
public static class EnumLabelMapSource
{
    public static readonly string RelativeJsonPath =
        Path.Combine("Assets", "06.Definition", "PlayscriptOfflineExport", "PlayscriptEnumLabelMaps.json");

    public static readonly string RelativeSourcePath =
        Path.Combine("Assets", "13.Scripts", "Utility", "CoreConfig.cs");

    // Same order as Unity's ExportedEnumTypes, which is also the key order in the JSON.
    private static readonly string[] ExportedEnumNames = { "DialogName", "Portrait", "ChatName", "Face" };

    private const int CurrentVersion = 1;

    public static string JsonPath(string projectRoot) => Path.Combine(projectRoot, RelativeJsonPath);

    // Rebuilds the document from CoreConfig.cs and rewrites the JSON if it changed. Throws (with a message meant
    // for the user) when the source can't be read — callers decide whether the old JSON is an acceptable fallback.
    public static EnumLabelMapDocument Refresh(string projectRoot)
    {
        var sourcePath = Path.Combine(projectRoot, RelativeSourcePath);
        if (!File.Exists(sourcePath))
            throw new InvalidOperationException($"找不到 enum 原始碼 {RelativeSourcePath}");

        var document = BuildDocument(File.ReadAllText(sourcePath));

        var jsonPath = JsonPath(projectRoot);
        // Newtonsoft's indented output, LF line endings, no trailing newline, no BOM — byte-identical to what
        // Unity's exporter leaves in the repo.
        var json = JsonConvert.SerializeObject(document, Formatting.Indented).Replace("\r\n", "\n");
        if (!File.Exists(jsonPath) || File.ReadAllText(jsonPath) != json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
            File.WriteAllText(jsonPath, json, new UTF8Encoding(false));
        }

        return document;
    }

    // Refresh(), falling back to whatever JSON is already on disk if the source can't be parsed. Returns null
    // only when neither works; warning is set whenever the fresh rebuild failed.
    public static EnumLabelMapDocument? RefreshOrLoadExisting(string projectRoot, out string? warning)
    {
        try
        {
            warning = null;
            return Refresh(projectRoot);
        }
        catch (Exception e)
        {
            warning = $"無法從原始碼更新 Enum 對照表（{e.Message}）";
            var jsonPath = JsonPath(projectRoot);
            return File.Exists(jsonPath) ? DocumentLoader.LoadEnumLabelMapDocument(jsonPath) : null;
        }
    }

    public static EnumLabelMapDocument BuildDocument(string source)
    {
        var code = StripComments(source);
        var document = new EnumLabelMapDocument { Version = CurrentVersion };
        foreach (var enumName in ExportedEnumNames)
            document.Enums[enumName] = ParseEnum(code, enumName);

        return document;
    }

    private static List<EnumLabelEntry> ParseEnum(string code, string enumName)
    {
        var body = FindEnumBody(code, enumName)
                   ?? throw new InvalidOperationException($"{RelativeSourcePath} 裡找不到 enum {enumName}");

        var members = new List<(string Name, string Label, ulong Value, int Order)>();
        ulong nextValue = 0;
        foreach (var rawMember in SplitTopLevel(body, ','))
        {
            var member = rawMember.Trim();
            if (member.Length == 0)
                continue;

            string? label = null;
            while (member.StartsWith('['))
            {
                var close = FindMatching(member, 0, '[', ']');
                label ??= FindLabelText(member[1..close]);
                member = member[(close + 1)..].TrimStart();
            }

            var equals = member.IndexOf('=');
            var name = (equals < 0 ? member : member[..equals]).Trim();
            if (name.Length == 0)
                continue;

            var value = nextValue;
            if (equals >= 0)
            {
                var expression = member[(equals + 1)..].Trim();
                value = TryParseInteger(expression, out var parsed)
                    ? parsed
                    : members.FirstOrDefault(m => m.Name == expression) is { Name: not null } referenced
                        ? referenced.Value
                        : throw new InvalidOperationException($"enum {enumName}.{name} 的值「{expression}」無法解析");
            }

            members.Add((name, string.IsNullOrEmpty(label) ? name : Sanitize(label), value, members.Count));
            nextValue = value + 1;
        }

        // Enum.GetNames orders by unsigned underlying value; ties keep declaration order.
        return members.OrderBy(m => m.Value).ThenBy(m => m.Order)
            .Select(m => new EnumLabelEntry { Name = m.Name, Label = m.Label })
            .ToList();
    }

    private static string? FindEnumBody(string code, string enumName)
    {
        var search = 0;
        while (true)
        {
            var index = code.IndexOf("enum", search, StringComparison.Ordinal);
            if (index < 0)
                return null;

            search = index + 4;
            if (index > 0 && IsIdentifierChar(code[index - 1]))
                continue;

            var position = SkipWhitespace(code, index + 4);
            if (position == index + 4 || string.CompareOrdinal(code, position, enumName, 0, enumName.Length) != 0)
                continue;

            position += enumName.Length;
            if (position < code.Length && IsIdentifierChar(code[position]))
                continue;

            var open = code.IndexOf('{', position);
            if (open < 0)
                return null;

            var close = FindMatching(code, open, '{', '}');
            return code[(open + 1)..close];
        }
    }

    // First string argument of a LabelText(...) inside one [...] attribute list, or null.
    private static string? FindLabelText(string attributeList)
    {
        foreach (var rawAttribute in SplitTopLevel(attributeList, ','))
        {
            var attribute = rawAttribute.Trim();
            var paren = attribute.IndexOf('(');
            if (paren < 0)
                continue;

            var attributeName = attribute[..paren].Trim();
            var dot = attributeName.LastIndexOf('.');
            if (dot >= 0)
                attributeName = attributeName[(dot + 1)..];
            if (attributeName is not ("LabelText" or "LabelTextAttribute"))
                continue;

            var arguments = attribute[(paren + 1)..FindMatching(attribute, paren, '(', ')')];
            var first = SplitTopLevel(arguments, ',').FirstOrDefault()?.Trim();
            if (first != null && TryReadStringLiteral(first, out var text))
                return text;
        }

        return null;
    }

    private static bool TryReadStringLiteral(string token, out string text)
    {
        text = string.Empty;
        if (token.StartsWith("@\"") && token.EndsWith('"') && token.Length >= 3)
        {
            text = token[2..^1].Replace("\"\"", "\"");
            return true;
        }

        if (!token.StartsWith('"') || !token.EndsWith('"') || token.Length < 2)
            return false;

        var builder = new StringBuilder();
        for (var i = 1; i < token.Length - 1; i++)
        {
            var c = token[i];
            if (c != '\\' || i + 1 >= token.Length - 1)
            {
                builder.Append(c);
                continue;
            }

            var escape = token[++i];
            switch (escape)
            {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case '0': builder.Append('\0'); break;
                case 'u' when i + 4 < token.Length - 1:
                    builder.Append((char)int.Parse(token.AsSpan(i + 1, 4), NumberStyles.HexNumber));
                    i += 4;
                    break;
                default: builder.Append(escape); break;
            }
        }

        text = builder.ToString();
        return true;
    }

    private static bool TryParseInteger(string expression, out ulong value)
    {
        var text = expression.Replace("_", string.Empty).TrimEnd('u', 'U', 'l', 'L');
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var signed))
        {
            value = unchecked((ulong)signed);
            return true;
        }

        value = 0;
        return false;
    }

    private static readonly char[] ZeroWidthCharacters = { '​', '‌', '‍', '﻿', '⁠' };

    // Same cleanup as Unity's SanitizeText.
    private static string Sanitize(string text)
    {
        foreach (var c in ZeroWidthCharacters)
            text = text.Replace(c.ToString(), string.Empty);

        return text.Trim();
    }

    // Removes // and /* */ comments while leaving string/char literals untouched (a label could contain "//").
    private static string StripComments(string source)
    {
        var builder = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                    i++;
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
                builder.Append(' ');
            }
            else if (c is '"' or '\'')
            {
                var end = SkipLiteral(source, i);
                builder.Append(source, i, end - i);
                i = end;
            }
            else
            {
                builder.Append(c);
                i++;
            }
        }

        return builder.ToString();
    }

    // Index just past the string/char literal starting at start (verbatim strings handled via the preceding '@').
    private static int SkipLiteral(string text, int start)
    {
        var quote = text[start];
        var verbatim = quote == '"' && start > 0 && text[start - 1] == '@';
        var i = start + 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (verbatim)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    return i + 1;
                }
            }
            else if (c == '\\')
            {
                i += 2;
                continue;
            }
            else if (c == quote || c == '\n')
            {
                return i + 1;
            }

            i++;
        }

        return text.Length;
    }

    private static int FindMatching(string text, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(text, i) - 1;
                continue;
            }

            if (c == open)
                depth++;
            else if (c == close && --depth == 0)
                return i;
        }

        throw new InvalidOperationException($"{RelativeSourcePath} 的括號不成對");
    }

    private static IEnumerable<string> SplitTopLevel(string text, char separator)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(text, i) - 1;
                continue;
            }

            if (c is '(' or '[' or '{')
                depth++;
            else if (c is ')' or ']' or '}')
                depth--;
            else if (c == separator && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }

        yield return text[start..];
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        return index;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
