using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StoryForge.Core.Pipeline;

namespace StoryForge.Core.Ldtk;

public sealed class LdtkTransport
{
    public required string Iid { get; init; }
    public required string Level { get; init; }

    // LocalEnum.TransportAppearance: "None" / "RedDoor" / "KaraokeDoor". Anything but "None" is a door with an
    // open animation — that, not the isDoor field (which disagrees with it on several real doors), is what
    // decides DoorLevelTransport vs LevelTransport.
    public required string Appearance { get; init; }
    public string? TargetIid { get; init; }
    public required IReadOnlyList<string> Playscripts { get; init; }

    public bool IsDoor => Appearance != "None";
}

public sealed class LdtkTransportTarget
{
    public required string Iid { get; init; }
    public required string Level { get; init; }
    public int Number { get; init; }

    // Not an LDtk field yet — picked up automatically if TransportTarget ever gets a "note" String field.
    public string? Note { get; init; }
}

// A guide-path point (PathPoint / TransportPathPoint). Key is its LocalEnum.GuidePathNodeKey, or null for "None" —
// only keyed points can be named in SetGuidePath by key, but any of them can be named by iid.
public sealed class LdtkGuidePathNode
{
    public required string Iid { get; init; }
    public required string Level { get; init; }
    public string? Key { get; init; }
}

// Every Transport / TransportTarget / guide-path point entity in Company.ldtk, for two consumers: the playscript codegen's
// LevelTransport/DoorLevelTransport iid checks, and LdtkTransports.json — the condensed copy written into
// AppSettings.ExternalDataFolder so an AI filling a playscript sheet can look up a door's target iid without
// reading the 8 MB .ldtk itself (see AGENTS.md there). Same single-world layout assumption as
// LdtkPlayscriptScanner.ScanRootEntries.
public sealed class LdtkTransportIndex
{
    public const string ExportFileName = "LdtkTransports.json";

    private readonly Dictionary<string, LdtkTransport> _transports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LdtkTransportTarget> _targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LdtkGuidePathNode> _guideNodes = new(StringComparer.Ordinal);

    // Any other entity's iid -> "<identifier> (<level>)", only so an error can say what a wrong iid actually is.
    private readonly Dictionary<string, string> _otherEntities = new(StringComparer.Ordinal);
    private readonly List<string> _levelOrder = new();

    private LdtkTransportIndex()
    {
    }

    public static LdtkTransportIndex FromRoot(JObject root)
    {
        var index = new LdtkTransportIndex();
        foreach (var level in root["levels"] as JArray ?? new JArray())
        {
            var levelName = level["identifier"]?.Value<string>() ?? level["iid"]?.Value<string>() ?? "?";
            index._levelOrder.Add(levelName);

            foreach (var layer in level["layerInstances"] as JArray ?? new JArray())
            foreach (var entity in layer["entityInstances"] as JArray ?? new JArray())
            {
                var iid = entity["iid"]?.Value<string>();
                var identifier = entity["__identifier"]?.Value<string>();
                if (iid == null)
                    continue;

                var fields = (entity["fieldInstances"] as JArray ?? new JArray())
                    .Where(f => f["__identifier"]?.Value<string>() != null)
                    .GroupBy(f => f["__identifier"]!.Value<string>()!)
                    .ToDictionary(g => g.Key, g => g.First()["__value"]);

                switch (identifier)
                {
                    case "Transport":
                        index._transports[iid] = new LdtkTransport
                        {
                            Iid = iid,
                            Level = levelName,
                            Appearance = fields.GetValueOrDefault("appearance")?.Value<string>() ?? "None",
                            TargetIid = (fields.GetValueOrDefault("target") as JObject)?["entityIid"]?.Value<string>(),
                            Playscripts = ReadPlayscripts(fields.GetValueOrDefault("playscript")),
                        };
                        break;
                    case "TransportTarget":
                        var number = fields.GetValueOrDefault("number");
                        var note = fields.GetValueOrDefault("note");
                        index._targets[iid] = new LdtkTransportTarget
                        {
                            Iid = iid,
                            Level = levelName,
                            Number = number is { Type: JTokenType.Integer } ? number.Value<int>() : 0,
                            Note = note is { Type: JTokenType.String } ? note.Value<string>() : null,
                        };
                        break;
                    case "PathPoint" or "TransportPathPoint":
                        var key = fields.GetValueOrDefault("GuidePathNodeKey");
                        index._guideNodes[iid] = new LdtkGuidePathNode
                        {
                            Iid = iid,
                            Level = levelName,
                            Key = key is { Type: JTokenType.String } && key.Value<string>() != "None" ? key.Value<string>() : null,
                        };
                        break;
                    default:
                        index._otherEntities[iid] = $"{identifier} ({levelName})";
                        break;
                }
            }
        }

        return index;
    }

    public static LdtkTransportIndex LoadFile(string ldtkFilePath) =>
        FromRoot(JObject.Parse(File.ReadAllText(ldtkFilePath)));

    public bool TryGetTransport(string iid, out LdtkTransport transport) => _transports.TryGetValue(iid, out transport!);

    public bool TryGetTarget(string iid, out LdtkTransportTarget target) => _targets.TryGetValue(iid, out target!);

    public bool TryGetGuidePathNode(string iid, out LdtkGuidePathNode node) => _guideNodes.TryGetValue(iid, out node!);

    public bool TryGetGuidePathNodeByKey(string key, out LdtkGuidePathNode node)
    {
        node = _guideNodes.Values.FirstOrDefault(n => n.Key == key)!;
        return node != null;
    }

    public IEnumerable<string> GuidePathNodeKeys => _guideNodes.Values.Where(n => n.Key != null).Select(n => n.Key!).Distinct();

    // "RyojiHouse 的 Transport" style description of whatever entity an iid is, for codegen error messages.
    public string DescribeIid(string iid)
    {
        if (_transports.TryGetValue(iid, out var transport))
            return $"{transport.Level} 的 Transport";
        if (_targets.TryGetValue(iid, out var target))
            return $"{target.Level} 的 TransportTarget";
        if (_guideNodes.TryGetValue(iid, out var guideNode))
            return $"{guideNode.Level} 的引導點";
        return _otherEntities.TryGetValue(iid, out var other) ? other : "LDtk 裡不存在的 iid";
    }

    // Only writes when the content actually changed — the external data folder is a git repo, and this runs
    // on every 流程狀態 refresh.
    public bool WriteExportIfChanged(string folder)
    {
        var path = Path.Combine(folder, ExportFileName);
        var json = BuildExportJson();
        if (File.Exists(path) && File.ReadAllText(path) == json)
            return false;

        File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
        return true;
    }

    public string BuildExportJson()
    {
        var levels = new JObject();
        foreach (var level in _levelOrder.Distinct())
        {
            var transports = _transports.Values.Where(t => t.Level == level).Select(BuildTransportJson).ToList();
            var targets = _targets.Values.Where(t => t.Level == level).OrderBy(t => t.Number).Select(BuildTargetJson).ToList();
            if (transports.Count == 0 && targets.Count == 0)
                continue;

            levels[level] = new JObject
            {
                ["Transports"] = new JArray(transports),
                ["TransportTargets"] = new JArray(targets),
            };
        }

        var playscripts = new JObject();
        foreach (var name in _transports.Values.SelectMany(t => t.Playscripts).Distinct().OrderBy(x => x, StringComparer.Ordinal))
        {
            playscripts[name] = new JArray(_transports.Values
                .Where(t => t.Playscripts.Contains(name))
                .Select(t => new JObject
                {
                    ["transport"] = t.Iid,
                    ["level"] = t.Level,
                    ["appearance"] = t.Appearance,
                    ["target"] = t.TargetIid,
                    ["targetLevel"] = TargetLevel(t.TargetIid),
                }));
        }

        // Only the keyed points — the unkeyed ones are just intermediate path vertices, useless to pick by hand.
        var guidePathNodes = new JObject();
        foreach (var node in _guideNodes.Values.Where(n => n.Key != null))
        {
            guidePathNodes[node.Key!] = new JObject
            {
                ["iid"] = node.Iid,
                ["level"] = node.Level,
            };
        }

        var root = new JObject
        {
            ["Levels"] = levels,
            ["Playscripts"] = playscripts,
            ["GuidePathNodes"] = guidePathNodes,
        };
        return root.ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n";
    }

    private JObject BuildTransportJson(LdtkTransport t) => new()
    {
        ["iid"] = t.Iid,
        ["appearance"] = t.Appearance,
        ["target"] = t.TargetIid,
        ["targetLevel"] = TargetLevel(t.TargetIid),
        ["playscripts"] = new JArray(t.Playscripts),
    };

    private JObject BuildTargetJson(LdtkTransportTarget t)
    {
        var json = new JObject
        {
            ["iid"] = t.Iid,
            ["number"] = t.Number,
        };
        if (!string.IsNullOrEmpty(t.Note))
            json["note"] = t.Note;
        json["enteredFrom"] = new JArray(_transports.Values
            .Where(x => x.TargetIid == t.Iid)
            .Select(x => new JObject
            {
                ["transport"] = x.Iid,
                ["level"] = x.Level,
                ["appearance"] = x.Appearance,
            }));
        return json;
    }

    private string? TargetLevel(string? targetIid) =>
        targetIid != null && _targets.TryGetValue(targetIid, out var target) ? target.Level : null;

    private static IReadOnlyList<string> ReadPlayscripts(JToken? value)
    {
        var raw = value switch
        {
            JArray array => array.Select(x => x.Type == JTokenType.String ? x.Value<string>() : null),
            { Type: JTokenType.String } => new[] { value.Value<string>() },
            _ => Enumerable.Empty<string?>(),
        };
        return raw
            .Select(PlayscriptNaming.Normalize)
            .Where(PlayscriptNaming.IsValidChapterPlayscriptName)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
