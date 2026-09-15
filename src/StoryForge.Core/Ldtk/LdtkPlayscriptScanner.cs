using Newtonsoft.Json.Linq;
using StoryForge.Core.Pipeline;

namespace StoryForge.Core.Ldtk;

// Where a playscript name's FIRST matching Entity (in file traversal order — the same order ScanRoot's own
// nested foreach already visits levels/layers/entities in) lives, for "定位到LDtk" to hand to LDtk's own
// "--goto-level"/"--goto-entity" CLI args. iid (not identifier/name) is what LDtk's gotoLevelEntity actually
// keys off of — a level/entity's identifier isn't guaranteed unique or stable the way its iid is. The two
// *Identifier fields are kept purely for human-readable status text (e.g. "已定位到：X 所在的 Y")
// alongside the iid StoryForge actually needs to launch LDtk with.
public sealed class LdtkPlayscriptLocation
{
    public required string LevelIid { get; init; }
    public required string LevelIdentifier { get; init; }
    public required string EntityIid { get; init; }
    public required string EntityIdentifier { get; init; }
}

public static class LdtkPlayscriptScanner
{
    private static readonly string[] PlayscriptFieldIdentifiers = { "playscript", "playscripts" };

    public static HashSet<string> ScanFile(string ldtkFilePath) =>
        new HashSet<string>(ScanFileEntries(ldtkFilePath).Keys, StringComparer.Ordinal);

    public static HashSet<string> ScanRoot(JObject root) =>
        new HashSet<string>(ScanRootEntries(root).Keys, StringComparer.Ordinal);

    public static Dictionary<string, LdtkPlayscriptLocation> ScanFileEntries(string ldtkFilePath)
    {
        var json = File.ReadAllText(ldtkFilePath);
        var root = JObject.Parse(json);
        return ScanRootEntries(root);
    }

    // Richer scan than ScanRoot — also keeps the first matching Entity's location per playscript name, for
    // "定位到LDtk". This project's own Company.ldtk uses the single-world layout (levels directly under the
    // root, no "worlds" array), which is all this walks — a multi-world project would need levels read from
    // root["worlds"][*]["levels"] too, not handled here since nothing in this codebase has that layout yet.
    public static Dictionary<string, LdtkPlayscriptLocation> ScanRootEntries(JObject root)
    {
        var result = new Dictionary<string, LdtkPlayscriptLocation>(StringComparer.Ordinal);
        var levels = root["levels"] as JArray ?? new JArray();

        foreach (var level in levels)
        {
            var levelIid = level["iid"]?.Value<string>();
            var levelIdentifier = level["identifier"]?.Value<string>();
            if (levelIid == null)
                continue;

            var layerInstances = level["layerInstances"] as JArray ?? new JArray();
            foreach (var layer in layerInstances)
            {
                var entityInstances = layer["entityInstances"] as JArray ?? new JArray();
                foreach (var entity in entityInstances)
                {
                    var entityIid = entity["iid"]?.Value<string>();
                    var entityIdentifier = entity["__identifier"]?.Value<string>();
                    if (entityIid == null)
                        continue;

                    var fieldInstances = entity["fieldInstances"] as JArray ?? new JArray();
                    foreach (var field in fieldInstances)
                    {
                        var identifier = field["__identifier"]?.Value<string>();
                        if (identifier == null || !PlayscriptFieldIdentifiers.Contains(identifier))
                            continue;

                        foreach (var raw in ExtractRawValues(field["__value"]))
                        {
                            var normalized = PlayscriptNaming.Normalize(raw);
                            if (!PlayscriptNaming.IsValidChapterPlayscriptName(normalized))
                                continue;

                            // TryAdd, not indexer assignment — a later duplicate occurrence of the same
                            // playscript name never displaces the FIRST one this scan already found.
                            result.TryAdd(normalized, new LdtkPlayscriptLocation
                            {
                                LevelIid = levelIid,
                                LevelIdentifier = levelIdentifier ?? levelIid,
                                EntityIid = entityIid,
                                EntityIdentifier = entityIdentifier ?? entityIid,
                            });
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
