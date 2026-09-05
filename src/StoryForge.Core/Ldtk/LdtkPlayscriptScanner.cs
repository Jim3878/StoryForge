using Newtonsoft.Json.Linq;
using StoryForge.Core.Pipeline;

namespace StoryForge.Core.Ldtk;

public static class LdtkPlayscriptScanner
{
    private static readonly string[] PlayscriptFieldIdentifiers = { "playscript", "playscripts" };

    public static HashSet<string> ScanFile(string ldtkFilePath)
    {
        var json = File.ReadAllText(ldtkFilePath);
        var root = JObject.Parse(json);
        return ScanRoot(root);
    }

    public static HashSet<string> ScanRoot(JObject root)
    {
        var result = new HashSet<string>();
        var levels = root["levels"] as JArray ?? new JArray();

        foreach (var level in levels)
        {
            var layerInstances = level["layerInstances"] as JArray ?? new JArray();
            foreach (var layer in layerInstances)
            {
                var entityInstances = layer["entityInstances"] as JArray ?? new JArray();
                foreach (var entity in entityInstances)
                {
                    var fieldInstances = entity["fieldInstances"] as JArray ?? new JArray();
                    foreach (var field in fieldInstances)
                    {
                        var identifier = field["__identifier"]?.Value<string>();
                        if (identifier == null || !PlayscriptFieldIdentifiers.Contains(identifier))
                            continue;

                        foreach (var raw in ExtractRawValues(field["__value"]))
                        {
                            var normalized = PlayscriptNaming.Normalize(raw);
                            if (PlayscriptNaming.IsValidChapterPlayscriptName(normalized))
                                result.Add(normalized);
                        }
                    }
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> ExtractRawValues(JToken? value)
    {
        switch (value)
        {
            case null:
                yield break;
            case JArray array:
                foreach (var item in array)
                {
                    var text = item.Value<string>();
                    if (!string.IsNullOrEmpty(text))
                        yield return text;
                }

                break;
            default:
                var single = value.Value<string>();
                if (!string.IsNullOrEmpty(single))
                    yield return single;

                break;
        }
    }
}
