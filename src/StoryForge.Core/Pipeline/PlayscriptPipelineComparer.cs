namespace StoryForge.Core.Pipeline;

public static class PlayscriptPipelineComparer
{
    public static List<PlayscriptPipelineInfo> Compare(
        IEnumerable<string> ldtkPlayscripts,
        IEnumerable<string> graphPlayscripts,
        IEnumerable<string> sheetPlayscripts,
        IEnumerable<string> cSharpPlayscripts)
    {
        var byName = new Dictionary<string, PlayscriptPipelineInfo>(StringComparer.Ordinal);

        PlayscriptPipelineInfo GetOrCreate(string name)
        {
            var normalized = PlayscriptNaming.Normalize(name);
            if (!byName.TryGetValue(normalized, out var info))
            {
                info = new PlayscriptPipelineInfo(normalized);
                byName[normalized] = info;
            }

            return info;
        }

        foreach (var name in ldtkPlayscripts)
            GetOrCreate(name).IsOnLdtk = true;

        foreach (var name in graphPlayscripts)
        {
            var normalized = PlayscriptNaming.Normalize(name);
            if (PlayscriptNaming.IsValidChapterPlayscriptName(normalized))
                GetOrCreate(normalized).IsOnGraph = true;
        }

        foreach (var name in sheetPlayscripts)
            GetOrCreate(name).IsOnSheet = true;

        foreach (var name in cSharpPlayscripts)
            GetOrCreate(name).IsOnCSharp = true;

        return byName.Values
            .OrderBy(x => x.Chapter, NaturalStringComparer.Instance)
            .ThenBy(x => x.PlayscriptName, NaturalStringComparer.Instance)
            .ToList();
    }
}
