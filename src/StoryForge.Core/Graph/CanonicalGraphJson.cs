using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StoryForge.Core.Schemas;

namespace StoryForge.Core.Graph;

public enum GraphMergeSide
{
    Local,
    Remote,
}

// 一筆合併衝突：同一個項目（節點、連線、群組、便利貼）在 StoryForge（local）和 Unity（remote）被改成不同內容。
// EntityKey 例如 "$.nodes[<guid>]"；Path 是實際衝突的欄位；三個值是該欄位在基準、local、remote 的內容（null = 不存在／已刪除）。
public sealed record GraphMergeConflict(string EntityKey, string Path, JToken? Base, JToken? Local, JToken? Remote);

public sealed record CanonicalGraphMergeResult(
    CanonicalGraphDocument? Graph,
    IReadOnlyList<GraphMergeConflict> Conflicts,
    IReadOnlyList<GraphMergeConflict> AutoResolved)
{
    public bool Succeeded => Graph != null && Conflicts.Count == 0;
}

public sealed class CanonicalGraphValidation
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool IsValid => Errors.Count == 0;
}

// 和 Unity 的 PlayscriptGraphJson 相同的正規化 JSON：同樣內容一定寫出同樣的文字（節點依 GUID、連線依端點排序，
// 縮排 2 格、換行 \n、結尾一個換行），所以 StoryForge 原樣讀寫不會讓 Unity 的檔案產生差異。
public static class CanonicalGraphJson
{
    public const int SchemaVersion = 1;
    public const string GraphId = "playscript-processor";

    private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        Culture = CultureInfo.InvariantCulture,
        NullValueHandling = NullValueHandling.Include,
        FloatParseHandling = FloatParseHandling.Double,
    });

    public static CanonicalGraphDocument Parse(string json)
    {
        var root = ParseObject(json, "流程圖 JSON");
        var document = root.ToObject<CanonicalGraphDocument>(Serializer)
                       ?? throw new InvalidDataException("流程圖 JSON 根物件無法解析。");
        document.Nodes ??= new();
        document.Edges ??= new();
        document.Groups ??= new();
        document.StickyNotes ??= new();
        return document;
    }

    public static GraphBlueprintDocument ParseBlueprint(string json)
    {
        var root = ParseObject(json, "藍圖 JSON");
        if (root["graph"] is not JObject || root["baseGraph"] is not JObject)
            throw new InvalidDataException("藍圖必須包含 graph 與 baseGraph 物件。");

        return root.ToObject<GraphBlueprintDocument>(Serializer)
               ?? throw new InvalidDataException("藍圖無法解析。");
    }

    private static JObject ParseObject(string json, string label)
    {
        try
        {
            return JObject.Parse(json ?? string.Empty,
                new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{label} 語法錯誤：{e.Message}", e);
        }
    }

    public static string Serialize(CanonicalGraphDocument document, ProcessNodeSchemaDocument? schema = null)
    {
        var clone = Clone(document);
        Normalize(clone, schema);
        return SerializeToken(JObject.FromObject(clone, Serializer));
    }

    public static string SerializeBlueprint(GraphBlueprintDocument blueprint, ProcessNodeSchemaDocument? schema = null)
    {
        var clone = new GraphBlueprintDocument
        {
            Graph = Clone(blueprint.Graph),
            BaseGraph = Clone(blueprint.BaseGraph),
            Extra = blueprint.Extra.ToDictionary(pair => pair.Key, pair => pair.Value.DeepClone()),
        };
        Normalize(clone.Graph, schema);
        Normalize(clone.BaseGraph, schema);
        return SerializeToken(JObject.FromObject(clone, Serializer));
    }

    public static CanonicalGraphDocument Clone(CanonicalGraphDocument document) =>
        JObject.FromObject(document, Serializer).ToObject<CanonicalGraphDocument>(Serializer)!;

    public static bool Equivalent(CanonicalGraphDocument left, CanonicalGraphDocument right,
        ProcessNodeSchemaDocument? schema = null) =>
        string.Equals(Serialize(left, schema), Serialize(right, schema), StringComparison.Ordinal);

    // 先寫暫存檔再取代，寫到一半失敗也不會留下壞掉的檔案。不加 BOM，和 Unity 寫出的檔案相同。
    public static void WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        try
        {
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    // 與 Unity PlayscriptGraphJson.Normalize 相同：節點依 GUID、節點資料依欄位定義順序、連線依端點、群組成員依 GUID；
    // 群組與便利貼維持原本順序
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

        document.Edges.Sort(CompareEdges);
        foreach (var group in document.Groups)
            group.Nodes.Sort(StringComparer.Ordinal);
    }

    // 逐欄位比較，和 Unity 的 CompareEdges 相同（不能用串起來的 Key 字串比較，分隔符號會讓順序不同）
    private static int CompareEdges(CanonicalGraphEdge a, CanonicalGraphEdge b)
    {
        var result = string.CompareOrdinal(a.FromNode, b.FromNode);
        if (result == 0) result = string.CompareOrdinal(a.FromPort, b.FromPort);
        if (result == 0) result = string.CompareOrdinal(a.FromPortId ?? string.Empty, b.FromPortId ?? string.Empty);
        if (result == 0) result = string.CompareOrdinal(a.ToNode, b.ToNode);
        if (result == 0) result = string.CompareOrdinal(a.ToPort, b.ToPort);
        if (result == 0) result = string.CompareOrdinal(a.ToPortId ?? string.Empty, b.ToPortId ?? string.Empty);
        return result;
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

            // Unity 遇到不認識的資料欄位會拒絕載入整張流程圖，所以這裡也當成錯誤
            var fields = type.IdentityFields.ToDictionary(field => field.FieldName, StringComparer.Ordinal);
            foreach (var property in node.Data.Properties())
            {
                if (!fields.TryGetValue(property.Name, out var field))
                {
                    result.Errors.Add($"節點 {node.Id} 有 Unity 不認識的資料欄位「{property.Name}」。");
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

    // 三方合併：雙方改到不同地方就兩邊都保留。同一個欄位被改成不同內容時：
    // - 節點／群組／便利貼的位置衝突一律採用 local（StoryForge），記在 AutoResolved
    // - resolutions 有指定該項目要用哪一邊時照指定
    // - 其他列進 Conflicts，Graph 為 null
    // 合併後指向已不存在節點的連線與群組成員會被移除（例如一邊刪了節點、另一邊加了連到它的線）。
    public static CanonicalGraphMergeResult Merge(CanonicalGraphDocument baseGraph,
        CanonicalGraphDocument localGraph, CanonicalGraphDocument remoteGraph,
        ProcessNodeSchemaDocument? schema = null,
        IReadOnlyDictionary<string, GraphMergeSide>? resolutions = null)
    {
        var context = new MergeContext(resolutions ?? new Dictionary<string, GraphMergeSide>());
        var merged = MergeToken(
            JObject.Parse(Serialize(baseGraph, schema)),
            JObject.Parse(Serialize(localGraph, schema)),
            JObject.Parse(Serialize(remoteGraph, schema)),
            "$", null, context) as JObject;
        if (merged == null || context.Conflicts.Count > 0)
            return new CanonicalGraphMergeResult(null, context.Conflicts, context.AutoResolved);

        var document = merged.ToObject<CanonicalGraphDocument>(Serializer)!;
        RemoveDanglingReferences(document);
        Normalize(document, schema);
        return new CanonicalGraphMergeResult(document, context.Conflicts, context.AutoResolved);
    }

    private sealed class MergeContext(IReadOnlyDictionary<string, GraphMergeSide> resolutions)
    {
        public IReadOnlyDictionary<string, GraphMergeSide> Resolutions { get; } = resolutions;
        public List<GraphMergeConflict> Conflicts { get; } = new();
        public List<GraphMergeConflict> AutoResolved { get; } = new();
    }

    private static void RemoveDanglingReferences(CanonicalGraphDocument document)
    {
        var nodeIds = new HashSet<string>(document.Nodes.Select(node => node.Id), StringComparer.Ordinal);
        document.Edges.RemoveAll(edge => !nodeIds.Contains(edge.FromNode) || !nodeIds.Contains(edge.ToNode));
        foreach (var group in document.Groups)
            group.Nodes.RemoveAll(id => !nodeIds.Contains(id));
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

    // entityKey：目前所在的項目（"$.nodes[<guid>]" 等），在進入 nodes/edges/groups/stickyNotes 陣列的元素時決定
    private static JToken? MergeToken(JToken? baseToken, JToken? localToken, JToken? remoteToken,
        string path, string? entityKey, MergeContext context)
    {
        if (JToken.DeepEquals(localToken, remoteToken))
            return localToken?.DeepClone();
        if (JToken.DeepEquals(localToken, baseToken))
            return remoteToken?.DeepClone();
        if (JToken.DeepEquals(remoteToken, baseToken))
            return localToken?.DeepClone();

        // 顏色與位置要整組取捨：只合併其中幾個分量會拼出兩邊都沒選過的值（例如 r 用 Unity、g/b 用 StoryForge）
        var isAtomic = path.EndsWith(".color", StringComparison.Ordinal) ||
                       path.EndsWith(".position", StringComparison.Ordinal);

        if (!isAtomic && baseToken is JObject baseObject && localToken is JObject localObject &&
            remoteToken is JObject remoteObject)
        {
            var merged = new JObject();
            var names = localObject.Properties().Select(p => p.Name)
                .Concat(remoteObject.Properties().Select(p => p.Name))
                .Concat(baseObject.Properties().Select(p => p.Name))
                .Distinct(StringComparer.Ordinal);
            foreach (var name in names)
            {
                var value = MergeToken(baseObject[name], localObject[name], remoteObject[name], $"{path}.{name}",
                    entityKey, context);
                if (value != null)
                    merged[name] = value;
            }
            return merged;
        }

        if (baseToken is JArray baseArray && localToken is JArray localArray && remoteToken is JArray remoteArray &&
            TryGetArrayKey(path, out var keySelector))
        {
            return MergeKeyedArray(baseArray, localArray, remoteArray, path, keySelector, context);
        }

        return ResolveConflict(baseToken, localToken, remoteToken, path, entityKey ?? path, context);
    }

    private static JToken? ResolveConflict(JToken? baseToken, JToken? localToken, JToken? remoteToken,
        string path, string entityKey, MergeContext context)
    {
        var conflict = new GraphMergeConflict(entityKey, path, baseToken?.DeepClone(), localToken?.DeepClone(),
            remoteToken?.DeepClone());

        // 只有位置不同（雙方都移動了同一個節點／群組）不需要使用者決定，採用 StoryForge 的位置
        if (localToken != null && remoteToken != null && path.Contains(".position", StringComparison.Ordinal))
        {
            context.AutoResolved.Add(conflict);
            return localToken.DeepClone();
        }

        if (context.Resolutions.TryGetValue(entityKey, out var side))
            return (side == GraphMergeSide.Local ? localToken : remoteToken)?.DeepClone();

        context.Conflicts.Add(conflict);
        return localToken?.DeepClone();
    }

    private static JArray MergeKeyedArray(JArray baseArray, JArray localArray, JArray remoteArray, string path,
        Func<JToken, string> keySelector, MergeContext context)
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
            var itemPath = $"{path}[{key}]";
            var value = MergeToken(baseValue, localValue, remoteValue, itemPath, itemPath, context);
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
            selector = EdgeKey;
            return true;
        }
        selector = _ => string.Empty;
        return false;
    }

    public static string EdgeKey(JToken token) =>
        $"{token.Value<string>("fromNode")}.{token.Value<string>("fromPort")}." +
        $"{token.Value<string>("fromPortId") ?? string.Empty}->" +
        $"{token.Value<string>("toNode")}.{token.Value<string>("toPort")}." +
        $"{token.Value<string>("toPortId") ?? string.Empty}";

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

    // Unity（Mono）的 float "R" 格式：先用 7 位有效數字，讀回來不等於原值才用 9 位（例如 16385.0078）。
    // .NET 8 的 Newtonsoft 會寫最短的表示法（16385.008），值相同但文字不同，會讓 Unity 的檔案每次匯出都產生差異。
    private sealed class UnityFloatJsonTextWriter(TextWriter writer) : JsonTextWriter(writer)
    {
        public override void WriteValue(float value) => WriteRawValue(FormatUnityFloat(value));

        public override void WriteValue(float? value)
        {
            if (value.HasValue)
                WriteValue(value.Value);
            else
                WriteNull();
        }
    }

    public static string FormatUnityFloat(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return value.ToString(CultureInfo.InvariantCulture);

        var text = FormatSignificant(value, 7);
        if (float.Parse(text, CultureInfo.InvariantCulture) != value)
            text = FormatSignificant(value, 9);

        // 和 Newtonsoft 一樣，整數值補上 ".0"
        return text.IndexOfAny(new[] { '.', 'E' }) >= 0 ? text : text + ".0";
    }

    // 和 Mono 的 "G<n>" 相同：取精確的十進位展開後「四捨五入」到 n 位有效數字（.NET 8 是捨入到偶數，
    // 例如 133.6015625 在 Mono 是 133.601563，在 .NET 8 是 133.601562）
    private static string FormatSignificant(float value, int digits)
    {
        if (value == 0)
            return "0";

        // float 轉 double 不失真；.NET 8 的 "E" 格式會給出精確的十進位位數
        var exact = Math.Abs((double)value).ToString("E60", CultureInfo.InvariantCulture);
        var ePos = exact.IndexOf('E');
        var mantissa = exact[..ePos].Replace(".", string.Empty);
        var exponent = int.Parse(exact[(ePos + 1)..], CultureInfo.InvariantCulture);

        var kept = mantissa[..digits].Select(c => c - '0').ToArray();
        if (mantissa[digits] >= '5')
        {
            var i = digits - 1;
            while (i >= 0 && ++kept[i] == 10)
            {
                kept[i] = 0;
                i--;
            }

            if (i < 0)
            {
                kept = new[] { 1 }.Concat(kept[..^1]).ToArray();
                exponent++;
            }
        }

        var significant = string.Concat(kept).TrimEnd('0');
        if (significant.Length == 0)
            significant = "0";

        string body;
        if (exponent >= -5 && exponent < digits)
        {
            if (exponent >= 0)
            {
                var intPart = significant.Length > exponent + 1
                    ? significant[..(exponent + 1)]
                    : significant.PadRight(exponent + 1, '0');
                var fraction = significant.Length > exponent + 1 ? significant[(exponent + 1)..] : string.Empty;
                body = fraction.Length > 0 ? $"{intPart}.{fraction}" : intPart;
            }
            else
            {
                body = "0." + new string('0', -exponent - 1) + significant;
            }
        }
        else
        {
            var fraction = significant.Length > 1 ? "." + significant[1..] : string.Empty;
            body = $"{significant[0]}{fraction}E{(exponent < 0 ? "-" : "+")}{Math.Abs(exponent):00}";
        }

        return value < 0 ? "-" + body : body;
    }

    private static string SerializeToken(JToken token)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        using (var writer = new UnityFloatJsonTextWriter(text)
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
