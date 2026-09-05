using System.Text.RegularExpressions;
using StoryForge.Core.Pipeline;

namespace StoryForge.Core.CSharpSource;

public static class CSharpFactoryScanner
{
    private static readonly Regex GetPlayscriptNameReturnPattern = new(
        @"GetPlayscriptName\s*\(\s*\)\s*\{[\s\S]*?return\s+""((?:\\.|[^""\\])*)""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static HashSet<string> ScanFolder(string playscriptFactoryFolder)
    {
        return new HashSet<string>(ScanFolderEntries(playscriptFactoryFolder).Keys, StringComparer.Ordinal);
    }

    // Richer scan than ScanFolder — also keeps the source file path per playscript, for "開啟C#檔案" /
    // "刪除C#檔案".
    public static Dictionary<string, string> ScanFolderEntries(string playscriptFactoryFolder)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(playscriptFactoryFolder))
            return result;

        foreach (var file in Directory.GetFiles(playscriptFactoryFolder, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in GetPlayscriptNameReturnPattern.Matches(text))
            {
                var playscript = PlayscriptNaming.Normalize(Regex.Unescape(match.Groups[1].Value));
                if (PlayscriptNaming.IsValidChapterPlayscriptName(playscript))
                    result[playscript] = file;
            }
        }

        return result;
    }
}
