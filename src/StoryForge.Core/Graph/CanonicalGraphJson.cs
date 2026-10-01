using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StoryForge.Core.Schemas;

namespace StoryForge.Core.Graph;

public sealed record CanonicalGraphMergeResult(CanonicalGraphDocument? Graph, IReadOnlyList<string> Conflicts)
{
    public bool Succeeded => Graph != null && Conflicts.Count == 0;
}

public sealed class CanonicalGraphValidation
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool IsValid => Errors.Count == 0;
}

public static class CanonicalGraphJson
{
    public const int SchemaVersion = 1;
    public const string GraphId = "playscript-processor";

    private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        Culture = CultureInfo.InvariantCulture,
        NullValueHandling = NullValueHandling.Include,
    });

    public static CanonicalGraphDocument Parse(string json)
    {
        JObject root;
        try
        {
            root = JObject.Parse(json ?? string.Empty,
                new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"流程圖 JSON 語法錯誤：{e.Message}", e);
        }

        var document = root.ToObject<CanonicalGraphDocument>(Serializer)
                       ?? throw new InvalidDataException("流程圖 JSON 根物件無法解析。");
        document.Nodes ??= new();
        document.Edges ??= new();
        document.Groups ??= new();
        document.StickyNotes ??= new();
        return document;
    }

    public static GraphDraftDocument ParseDraft(string json)
    {
        JObject root;
        try
        {
            root = JObject.Parse(json ?? string.Empty,
                new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"StoryForge draft JSON 語法錯誤：{e.Message}", e);
        }

        if (root["baseGraph"] is not JObject || root["graph"] is not JObject)
            throw new InvalidDataException("StoryForge draft 必須包含 baseGraph 與 graph 物件。");

        return root.ToObject<GraphDraftDocument>(Serializer)
               ?? throw new InvalidDataException("StoryForge draft 無法解析。");
    }

    public static string Serialize(CanonicalGraphDocument document, ProcessNodeSchemaDocument? schema = null)
    {
        var clone = Clone(document);
        Normalize(clone, schema);
        return SerializeToken(JObject.FromObject(clone, Serializer));
    }

    public static string SerializeDraft(GraphDraftDocument draft, ProcessNodeSchemaDocument? schema = null)
    {
        var clone = new GraphDraftDocument
        {
            BaseGraph = Clone(draft.BaseGraph),
            Graph = Clone(draft.Graph),
            Extra = draft.Extra.ToDictionary(pair => pair.Key, pair => pair.Value.DeepClone()),
        };
        Normalize(clone.BaseGraph, schema);
        Normalize(clone.Graph, schema);
        return SerializeToken(JObject.FromObject(clone, Serializer));
    }

    public static CanonicalGraphDocument Clone(CanonicalGraphDocument document) =>
        JObject.FromObject(document, Serializer).ToObject<CanonicalGraphDocument>(Serializer)!;

    public static bool Equivalent(CanonicalGraphDocument left, CanonicalGraphDocument right,
        ProcessNodeSchemaDocument? schema = null) =>
        string.Equals(Serialize(left, schema), Serialize(right, schema), StringComparison.Ordinal);

    public static void WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        try
        {
            if (File.Exists(path))
                File.Move(temp, path, true);
            else
                File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public static void Normalize(CanonicalGraphDocument document, ProcessNodeSchemaDocument? schema = null)
    {
        document.SchemaVersion = SchemaVersion;
        document.GraphId = string.IsNullOrEmpty(document.GraphId) ? GraphId : document.GraphId;
        document.Nodes.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        foreach (var node in document.Nodes)
        {
            var fieldOrder = schema?.NodeTypes.FirstOrDefault(type => type.TypeName == node.Type)?.IdentityFields
                .Select((field, index) => (field.FieldName, index))
                .ToDictionary(item => item.FieldName, item => item.index, StringComparer.Ordinal)
                ?? new Dictionary<string, int>(StringComparer.Ordinal);
            var ordered = node.Data.Properties()
                .Select((property, index) => (property, index))
                .OrderBy(item => fieldOrder.TryGetValue(item.property.Name, out var order) ? order : int.MaxValue)
                .ThenBy(item => item.index)
                .Select(item => new JProperty(item.property.Name, item.property.Value.DeepClone()))
                .ToList();
            node.Data = new JObject(ordered);
        }

        document.Edges.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        foreach (var group in document.Groups)
            group.Nodes.Sort(StringComparer.Ordinal);
    }

    public static CanonicalGraphValidation Validate(CanonicalGraphDocument document,
        ProcessNodeSchemaDocument schema)
    {
        var result = new CanonicalGraphValidation();
        if (document.SchemaVersion != SchemaVersion)
            result.Errors.Add($"不支援 schemaVersion {document.SchemaVersion}；目前只支援 {SchemaVersion}。");
        if (!string.Equals(document.GraphId, GraphId, StringComparison.Ordinal))
            result.Errors.Add($"graphId 必須是「{GraphId}」。");

        var nodeById = new Dictionary<string, CanonicalGraphNode>(StringComparer.Ordinal);
        foreach (var node in document.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                result.Errors.Add("節點 GUID 空白。");
                continue;
            }
            if (!nodeById.TryAdd(node.Id, node))
                result.Errors.Add($"節點 GUID 重複：{node.Id}。");

            var type = schema.NodeTypes.FirstOrDefault(candidate => candidate.TypeName == node.Type);
            if (type == null)
            {
                result.Errors.Add($"節點 {node.Id} 使用未知型別「{node.Type}」。");
                continue;
            }

            var fields = type.IdentityFields.ToDictionary(field => field.FieldName, StringComparer.Ordinal);
            foreach (var property in node.Data.Properties())
            {
                if (!fields.TryGetValue(property.Name, out var field))
                {
                    result.Warnings.Add($"節點 {node.Id} 的未知資料欄位「{property.Name}」會原樣保留。");
                    continue;
                }

                if (field.FieldType == "System.String" && property.Value.Type is not (JTokenType.String or JTokenType.Null))
                    result.Errors.Add($"節點 {node.Id}.{property.Name} 必須是字串。");
                if (field.FieldType == "System.Int32" && property.Value.Type != JTokenType.Integer)
                    result.Errors.Add($"節點 {node.Id}.{property.Name} 必須是整數。");
            }
        }

        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in document.Edges)
        {
            if (!nodeById.TryGetValue(edge.FromNode, out var from))
                result.Errors.Add($"連線來源節點不存在：{edge.FromNode}。");
            if (!nodeById.TryGetValue(edge.ToNode, out var to))
                result.Errors.Add($"連線目標節點不存在：{edge.ToNode}。");
            if (!edgeKeys.Add(edge.Key))
                result.Errors.Add($"連線重複：{edge.Key}。");

            var fromSchema = from == null ? null : schema.NodeTypes.FirstOrDefault(type => type.TypeName == from.Type);
            var toSchema = to == null ? null : schema.NodeTypes.FirstOrDefault(type => type.TypeName == to.Type);
            if (fromSchema != null && fromSchema.Outputs.All(port => port.FieldName != edge.FromPort))
                result.Errors.Add($"節點 {edge.FromNode} 沒有輸出 port「{edge.FromPort}」。");
            if (toSchema != null && toSchema.Inputs.All(port => port.FieldName != edge.ToPort))
                result.Errors.Add($"節點 {edge.ToNode} 沒有輸入 port「{edge.ToPort}」。");
        }

        ValidateOwnedIds(document.Groups.Select(group => group.Id), "群組", result.Errors);
        ValidateOwnedIds(document.StickyNotes.Select(note => note.Id), "便利貼", result.Errors);
        foreach (var group in document.Groups)
        foreach (var member in group.Nodes)
            if (!nodeById.ContainsKey(member))
                result.Errors.Add($"群組 {group.Id} 包含不存在的節點 {member}。");

        return result;
    }

    public static CanonicalGraphMergeResult Merge(CanonicalGraphDocument baseGraph,
        CanonicalGraphDocument localGraph, CanonicalGraphDocument remoteGraph,
        ProcessNodeSchemaDocument? schema = null)
    {
        var conflicts = new List<string>();
        var merged = MergeToken(
            JObject.Parse(Serialize(baseGraph, schema)),
            JObject.Parse(Serialize(localGraph, schema)),
            JObject.Parse(Serialize(remoteGraph, schema)),
            "$", conflicts) as JObject;
        if (merged == null || conflicts.Count > 0)
            return new CanonicalGraphMergeResult(null, conflicts);

        var document = merged.ToObject<CanonicalGraphDocument>(Serializer)!;
        Normalize(document, schema);
        return new CanonicalGraphMergeResult(document, conflicts);
    }

    public static IReadOnlyList<string> DescribeChanges(CanonicalGraphDocument before,
        CanonicalGraphDocument after, ProcessNodeSchemaDocument? schema = null)
    {
        var lines = new List<string>();
        DescribeCollection("節點", before.Nodes, after.Nodes, node => node.Id, lines);
        DescribeCollection("連線", before.Edges, after.Edges, edge => edge.Key, lines);
        DescribeCollection("群組", before.Groups, after.Groups, group => group.Id, lines);
        DescribeCollection("便利貼", before.StickyNotes, after.StickyNotes, note => note.Id, lines);
        if (lines.Count == 0 && !Equivalent(before, after, schema))
            lines.Add("流程圖中有格式或擴充欄位變更。");
        return lines;
    }

    private static void DescribeCollection<T>(string label, IEnumerable<T> before, IEnumerable<T> after,
        Func<T, string> key, ICollection<string> lines)
    {
        var oldMap = before.ToDictionary(key, item => JToken.FromObject(item!, Serializer), StringComparer.Ordinal);
        var newMap = after.ToDictionary(key, item => JToken.FromObject(item!, Serializer), StringComparer.Ordinal);
        var added = newMap.Keys.Except(oldMap.Keys, StringComparer.Ordinal).Count();
        var removed = oldMap.Keys.Except(newMap.Keys, StringComparer.Ordinal).Count();
        var changed = oldMap.Keys.Intersect(newMap.Keys, StringComparer.Ordinal)
            .Count(id => !JToken.DeepEquals(oldMap[id], newMap[id]));
        if (added + removed + changed > 0)
            lines.Add($"{label}：新增 {added}、刪除 {removed}、修改 {changed}");
    }

    private static JToken? MergeToken(JToken? baseToken, JToken? localToken, JToken? remoteToken,
        string path, ICollection<string> conflicts)
    {
        if (JToken.DeepEquals(localToken, remoteToken))
            return localToken?.DeepClone();
        if (JToken.DeepEquals(localToken, baseToken))
            return remoteToken?.DeepClone();
        if (JToken.DeepEquals(remoteToken, baseToken))
            return localToken?.DeepClone();

        if (baseToken is JObject baseObject && localToken is JObject localObject && remoteToken is JObject remoteObject)
        {
            var merged = new JObject();
            var names = baseObject.Properties().Select(p => p.Name)
                .Concat(localObject.Properties().Select(p => p.Name))
                .Concat(remoteObject.Properties().Select(p => p.Name))
                .Distinct(StringComparer.Ordinal);
            foreach (var name in names)
            {
                var value = MergeToken(baseObject[name], localObject[name], remoteObject[name], $"{path}.{name}", conflicts);
                if (value != null)
                    merged[name] = value;
            }
            return merged;
        }

        if (baseToken is JArray baseArray && localToken is JArray localArray && remoteToken is JArray remoteArray &&
            TryGetArrayKey(path, out var keySelector))
        {
            return MergeKeyedArray(baseArray, localArray, remoteArray, path, keySelector, conflicts);
        }

        conflicts.Add(path);
        return null;
    }

    private static JArray MergeKeyedArray(JArray baseArray, JArray localArray, JArray remoteArray, string path,
        Func<JToken, string> keySelector, ICollection<string> conflicts)
    {
        var baseMap = baseArray.ToDictionary(keySelector, StringComparer.Ordinal);
        var localMap = localArray.ToDictionary(keySelector, StringComparer.Ordinal);
        var remoteMap = remoteArray.ToDictionary(keySelector, StringComparer.Ordinal);
        var keys = localArray.Select(keySelector)
            .Concat(remoteArray.Select(keySelector))
            .Concat(baseArray.Select(keySelector))
            .Distinct(StringComparer.Ordinal);
        var merged = new JArray();
        foreach (var key in keys)
        {
            baseMap.TryGetValue(key, out var baseValue);
            localMap.TryGetValue(key, out var localValue);
            remoteMap.TryGetValue(key, out var remoteValue);
            var value = MergeToken(baseValue, localValue, remoteValue, $"{path}[{key}]", conflicts);
            if (value != null)
                merged.Add(value);
        }
        return merged;
    }

    private static bool TryGetArrayKey(string path, out Func<JToken, string> selector)
    {
        if (path is "$.nodes" or "$.groups" or "$.stickyNotes")
        {
            selector = token => token.Value<string>("id") ?? string.Empty;
            return true;
        }
        if (path == "$.edges")
        {
            selector = token => $"{token.Value<string>("fromNode")}.{token.Value<string>("fromPort")}." +
                                $"{token.Value<string>("fromPortId") ?? string.Empty}->" +
                                $"{token.Value<string>("toNode")}.{token.Value<string>("toPort")}." +
                                $"{token.Value<string>("toPortId") ?? string.Empty}";
            return true;
        }
        selector = _ => string.Empty;
        return false;
    }

    private static void ValidateOwnedIds(IEnumerable<string> ids, string label, ICollection<string> errors)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
                errors.Add($"{label} ID 空白。");
            else if (!used.Add(id))
                errors.Add($"{label} ID 重複：{id}。");
        }
    }

    private static string SerializeToken(JToken token)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        using (var writer = new JsonTextWriter(text)
               {
                   Formatting = Formatting.Indented,
                   Indentation = 2,
                   IndentChar = ' ',
               })
        {
            token.WriteTo(writer);
        }
        return text.ToString().Replace("\r\n", "\n") + "\n";
    }
}
