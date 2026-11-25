using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StoryForge.Core.Graph;
using StoryForge.Core.Schemas;

namespace StoryForge.Core.Cloud;

// One side of the flow graph sync: the canonical graph plus the node texts that live outside it (Memo, and
// 衝突/變化/角色/地點 under the cloud's field names). As a file: { "graph": {...}, "memos": { nodeId: text },
// "content": { nodeId: { conflict, change, characters, location } } }.
public sealed class FlowGraphSnapshot
{
    public const string MemoField = "memo";
    public static readonly string[] ContentFields = { "conflict", "change", "characters", "location" };

    public CanonicalGraphDocument Graph { get; set; } = new();
    public Dictionary<string, string> Memos { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, string>> Content { get; set; } = new(StringComparer.Ordinal);

    public string GetText(string nodeId, string field)
    {
        if (field == MemoField)
            return Memos.TryGetValue(nodeId, out var memo) ? memo ?? string.Empty : string.Empty;
        return Content.TryGetValue(nodeId, out var c) && c.TryGetValue(field, out var v) ? v ?? string.Empty : string.Empty;
    }

    public void SetText(string nodeId, string field, string value)
    {
        if (field == MemoField)
        {
            if (value.Length == 0) Memos.Remove(nodeId);
            else Memos[nodeId] = value;
            return;
        }
        if (value.Length == 0)
        {
            if (Content.TryGetValue(nodeId, out var existing) && existing.Remove(field) && existing.Count == 0)
                Content.Remove(nodeId);
            return;
        }
        if (!Content.TryGetValue(nodeId, out var c))
            Content[nodeId] = c = new Dictionary<string, string>(StringComparer.Ordinal);
        c[field] = value;
    }

    /// <summary>Every (node, field) that has a non-empty text.</summary>
    public IEnumerable<(string NodeId, string Field)> TextKeys()
    {
        foreach (var (id, memo) in Memos)
            if (!string.IsNullOrEmpty(memo))
                yield return (id, MemoField);
        foreach (var (id, fields) in Content)
        foreach (var (field, value) in fields)
            if (!string.IsNullOrEmpty(value) && Array.IndexOf(ContentFields, field) >= 0)
                yield return (id, field);
    }

    public FlowGraphSnapshot Clone() => new()
    {
        Graph = CanonicalGraphJson.Clone(Graph),
        Memos = new Dictionary<string, string>(Memos, StringComparer.Ordinal),
        Content = Content.ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value, StringComparer.Ordinal), StringComparer.Ordinal),
    };

    public JObject ToJObject(ProcessNodeSchemaDocument? schema)
    {
        var memos = new JObject();
        var content = new JObject();
        foreach (var group in TextKeys().OrderBy(k => k.NodeId, StringComparer.Ordinal).GroupBy(k => k.NodeId))
        foreach (var (id, field) in group)
        {
            if (field == MemoField)
            {
                memos[id] = GetText(id, field);
                continue;
            }
            if (content[id] is not JObject fields)
                content[id] = fields = new JObject();
            fields[field] = GetText(id, field);
        }
        return new JObject
        {
            ["graph"] = JObject.Parse(CanonicalGraphJson.Serialize(Graph, schema)),
            ["memos"] = memos,
            ["content"] = content,
        };
    }

    public string ToJson(ProcessNodeSchemaDocument? schema) => ToJObject(schema).ToString(Formatting.Indented);

    public static FlowGraphSnapshot Parse(string json, ProcessNodeSchemaDocument? schema)
    {
        var root = JObject.Parse(json);
        if (root["graph"] is not JObject graph)
            throw new InvalidDataException("缺少 graph 物件。");
        return From(graph,
            root["memos"]?.ToObject<Dictionary<string, string>>(),
            root["content"]?.ToObject<Dictionary<string, Dictionary<string, string>>>(), schema);
    }

    public static FlowGraphSnapshot From(JObject graph, IReadOnlyDictionary<string, string>? memos,
        IReadOnlyDictionary<string, Dictionary<string, string>>? content, ProcessNodeSchemaDocument? schema)
    {
        var document = CanonicalGraphJson.Parse(graph.ToString(Formatting.None));
        CanonicalGraphJson.Normalize(document, schema);
        var snapshot = new FlowGraphSnapshot { Graph = document };
        foreach (var (id, memo) in memos ?? new Dictionary<string, string>())
            snapshot.SetText(id, MemoField, memo ?? string.Empty);
        foreach (var (id, fields) in content ?? new Dictionary<string, Dictionary<string, string>>())
        foreach (var (field, value) in fields ?? new Dictionary<string, string>())
            if (Array.IndexOf(ContentFields, field) >= 0)
                snapshot.SetText(id, field, value ?? string.Empty);
        return snapshot;
    }
}

/// <summary>A node text (Memo / 衝突 / ...) both sides changed to different values.</summary>
public sealed record FlowGraphTextConflict(string NodeId, string Field, string Base, string Mine, string Theirs);

public sealed record FlowGraphMergeOutcome(FlowGraphSnapshot Merged, IReadOnlyList<GraphMergeConflict> GraphConflicts,
    IReadOnlyList<FlowGraphTextConflict> TextConflicts)
{
    public bool HasConflicts => GraphConflicts.Count > 0 || TextConflicts.Count > 0;
    public int ConflictCount => GraphConflicts.Select(c => c.EntityKey).Distinct(StringComparer.Ordinal).Count() + TextConflicts.Count;
}

// Three-way merge of the local flow graph (mine) with the cloud's (theirs) on top of the base both last
// agreed on. Changes to different nodes / fields merge by themselves; a field both sides changed differently
// is a conflict (except positions, where mine wins — CanonicalGraphJson.Merge's rule). With conflicts,
// Merged still holds everything that did merge, with mine at each conflicting place.
public static class FlowGraphMerge
{
    public static FlowGraphMergeOutcome Merge(FlowGraphSnapshot baseSnap, FlowGraphSnapshot mine, FlowGraphSnapshot theirs,
        ProcessNodeSchemaDocument? schema)
    {
        var result = CanonicalGraphJson.Merge(baseSnap.Graph, mine.Graph, theirs.Graph, schema);
        var graphConflicts = result.Conflicts;
        var graph = result.Graph;
        if (graph == null)
        {
            var useMine = graphConflicts.Select(c => c.EntityKey).Distinct(StringComparer.Ordinal)
                .ToDictionary(k => k, _ => GraphMergeSide.Local, StringComparer.Ordinal);
            graph = CanonicalGraphJson.Merge(baseSnap.Graph, mine.Graph, theirs.Graph, schema, useMine).Graph
                    ?? CanonicalGraphJson.Clone(mine.Graph);
        }

        var merged = new FlowGraphSnapshot { Graph = graph };
        var alive = graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var textConflicts = new List<FlowGraphTextConflict>();
        var keys = baseSnap.TextKeys().Concat(mine.TextKeys()).Concat(theirs.TextKeys()).Distinct()
            .Where(k => alive.Contains(k.NodeId))
            .OrderBy(k => k.NodeId, StringComparer.Ordinal).ThenBy(k => k.Field, StringComparer.Ordinal);
        foreach (var (id, field) in keys)
        {
            string b = baseSnap.GetText(id, field), m = mine.GetText(id, field), t = theirs.GetText(id, field);
            string value;
            if (m == t || t == b) value = m;
            else if (m == b) value = t;
            else
            {
                textConflicts.Add(new FlowGraphTextConflict(id, field, b, m, t));
                value = m;
            }
            merged.SetText(id, field, value);
        }

        return new FlowGraphMergeOutcome(merged, graphConflicts, textConflicts);
    }

    public static bool AreEqual(FlowGraphSnapshot a, FlowGraphSnapshot b, ProcessNodeSchemaDocument? schema) =>
        CanonicalGraphJson.Equivalent(a.Graph, b.Graph, schema) && TextsEqual(a, b);

    private static bool TextsEqual(FlowGraphSnapshot a, FlowGraphSnapshot b)
    {
        // Texts of nodes that are not in the graph any more are leftovers, not content.
        var aliveA = a.Graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var aliveB = b.Graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        return a.TextKeys().Where(k => aliveA.Contains(k.NodeId))
            .Concat(b.TextKeys().Where(k => aliveB.Contains(k.NodeId))).Distinct()
            .All(k => a.GetText(k.NodeId, k.Field) == b.GetText(k.NodeId, k.Field));
    }

    private static readonly Dictionary<string, string> FieldLabels = new()
    {
        [FlowGraphSnapshot.MemoField] = "Memo", ["conflict"] = "衝突", ["change"] = "變化", ["characters"] = "角色", ["location"] = "地點",
    };

    /// <summary>The conflict list an AI (or a person) works through, as markdown.</summary>
    public static string DescribeConflicts(FlowGraphMergeOutcome outcome, FlowGraphSnapshot mine, FlowGraphSnapshot theirs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 流程圖衝突清單").AppendLine();
        sb.AppendLine($"共 {outcome.ConflictCount} 處。每一處列出三個版本：基準（上次同步時）、本機、雲端。`merged.json` 裡這些地方目前都是**本機**的值。").AppendLine();
        var n = 0;
        foreach (var group in outcome.GraphConflicts.GroupBy(c => c.EntityKey))
        {
            sb.AppendLine($"## {++n}. {DescribeEntity(group.Key, mine, theirs)}").AppendLine();
            sb.AppendLine($"`{group.Key}`").AppendLine();
            foreach (var c in group)
            {
                sb.AppendLine($"- 欄位 `{c.Path}`");
                sb.AppendLine($"  - 基準：{Show(c.Base)}");
                sb.AppendLine($"  - 本機：{Show(c.Local)}");
                sb.AppendLine($"  - 雲端：{Show(c.Remote)}");
            }
            sb.AppendLine();
        }
        foreach (var c in outcome.TextConflicts)
        {
            var label = FieldLabels.TryGetValue(c.Field, out var l) ? l : c.Field;
            var where = c.Field == FlowGraphSnapshot.MemoField ? $"memos[\"{c.NodeId}\"]" : $"content[\"{c.NodeId}\"].{c.Field}";
            sb.AppendLine($"## {++n}. {DescribeEntity($"$.nodes[{c.NodeId}]", mine, theirs)} 的{label}").AppendLine();
            sb.AppendLine($"`{where}`").AppendLine();
            sb.AppendLine("基準：").AppendLine(Fence(c.Base));
            sb.AppendLine("本機：").AppendLine(Fence(c.Mine));
            sb.AppendLine("雲端：").AppendLine(Fence(c.Theirs));
        }
        return sb.ToString();
    }

    private static string Fence(string text) => text.Length == 0 ? "（空白）\n" : $"```\n{text}\n```\n";

    private static string Show(JToken? token) =>
        token == null ? "（不存在／已刪除）" : $"`{token.ToString(Formatting.None)}`";

    private static string DescribeEntity(string entityKey, FlowGraphSnapshot mine, FlowGraphSnapshot theirs)
    {
        var open = entityKey.IndexOf('[');
        var id = open >= 0 && entityKey.EndsWith(']') ? entityKey[(open + 1)..^1] : entityKey;
        if (entityKey.StartsWith("$.nodes[", StringComparison.Ordinal))
        {
            var node = mine.Graph.Nodes.FirstOrDefault(x => x.Id == id) ?? theirs.Graph.Nodes.FirstOrDefault(x => x.Id == id);
            var name = node?.Data.Value<string>("playscriptId") ?? node?.Data.Value<string>("flagId");
            return string.IsNullOrEmpty(name) ? $"節點（{node?.Type ?? "?"}）{id}" : $"節點「{name}」";
        }
        if (entityKey.StartsWith("$.groups[", StringComparison.Ordinal))
        {
            var group = mine.Graph.Groups.FirstOrDefault(x => x.Id == id) ?? theirs.Graph.Groups.FirstOrDefault(x => x.Id == id);
            return $"群組「{group?.Title ?? id}」";
        }
        if (entityKey.StartsWith("$.stickyNotes[", StringComparison.Ordinal))
            return $"便利貼 {id}";
        if (entityKey.StartsWith("$.edges[", StringComparison.Ordinal))
            return $"連線 {id}";
        return entityKey;
    }
}

public sealed class FlowGraphWaitState
{
    public DateTime CreatedAt { get; set; }
    public int ConflictCount { get; set; }
}

// The flow graph's wait merge: both sides changed the same place, so the sync stops and leaves files for an
// external (AI) merge, like WaitMergeStore does for a playscript table:
//   <ExternalDataFolder>\Conflicts\_flowgraph\base.json     last synced version
//                                            \mine.json     the local version when the wait started
//                                            \theirs.json   the cloud version (refreshed if the cloud changes again)
//                                            \merged.json   the working file: everything that merged by itself, mine at each conflict
//                                            \conflicts.md  the conflicts, one by one
//                                            \README.md     what to do, for the AI
//                                            \flowgraph-state.json
// (Not state.json: WaitMergeStore.All reads every Conflicts\*\state.json as a playscript wait.)
// While it exists the local flow graph tab is read-only and the flow graph is left out of 上傳 / 下載.
public static class FlowGraphWaitStore
{
    private const string StateFile = "flowgraph-state.json";

    public static string Folder(string dataFolder) => Path.Combine(WaitMergeStore.Root(dataFolder), "_flowgraph");
    public static string WorkingPath(string dataFolder) => Path.Combine(Folder(dataFolder), "merged.json");

    public static bool IsWaiting(string dataFolder) =>
        !string.IsNullOrEmpty(dataFolder) && File.Exists(Path.Combine(Folder(dataFolder), StateFile));

    public static FlowGraphWaitState? State(string dataFolder)
    {
        try
        {
            return IsWaiting(dataFolder)
                ? JsonConvert.DeserializeObject<FlowGraphWaitState>(File.ReadAllText(Path.Combine(Folder(dataFolder), StateFile)))
                : null;
        }
        catch
        {
            return new FlowGraphWaitState();
        }
    }

    public static void Start(string dataFolder, FlowGraphSnapshot baseSnap, FlowGraphSnapshot mine, FlowGraphSnapshot theirs,
        FlowGraphMergeOutcome outcome, ProcessNodeSchemaDocument? schema)
    {
        var dir = Folder(dataFolder);
        Directory.CreateDirectory(dir);
        Write(dir, "base.json", baseSnap.ToJson(schema));
        WriteSides(dir, mine, theirs, outcome, schema);
        Write(dir, "README.md", BuildReadme(WorkingPath(dataFolder)));
        Write(dir, StateFile, JsonConvert.SerializeObject(
            new FlowGraphWaitState { CreatedAt = DateTime.Now, ConflictCount = outcome.ConflictCount }, Formatting.Indented));
    }

    // The cloud changed again during the wait and hit the merge result: the old theirs (already merged in)
    // becomes the base, the merge result so far mine, the latest cloud theirs.
    public static void Restack(string dataFolder, FlowGraphSnapshot newBase, FlowGraphSnapshot newMine, FlowGraphSnapshot newTheirs,
        FlowGraphMergeOutcome outcome, ProcessNodeSchemaDocument? schema)
    {
        var dir = Folder(dataFolder);
        Write(dir, "base.json", newBase.ToJson(schema));
        WriteSides(dir, newMine, newTheirs, outcome, schema);
        Write(dir, StateFile, JsonConvert.SerializeObject(
            new FlowGraphWaitState { CreatedAt = State(dataFolder)?.CreatedAt ?? DateTime.Now, ConflictCount = outcome.ConflictCount },
            Formatting.Indented));
    }

    public static FlowGraphSnapshot ReadBase(string dataFolder, ProcessNodeSchemaDocument? schema) => Read(dataFolder, "base.json", schema);
    public static FlowGraphSnapshot ReadMine(string dataFolder, ProcessNodeSchemaDocument? schema) => Read(dataFolder, "mine.json", schema);
    public static FlowGraphSnapshot ReadTheirs(string dataFolder, ProcessNodeSchemaDocument? schema) => Read(dataFolder, "theirs.json", schema);
    public static FlowGraphSnapshot ReadWorking(string dataFolder, ProcessNodeSchemaDocument? schema) => Read(dataFolder, "merged.json", schema);

    public static void Finish(string dataFolder)
    {
        var dir = Folder(dataFolder);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    private static void WriteSides(string dir, FlowGraphSnapshot mine, FlowGraphSnapshot theirs, FlowGraphMergeOutcome outcome,
        ProcessNodeSchemaDocument? schema)
    {
        Write(dir, "mine.json", mine.ToJson(schema));
        Write(dir, "theirs.json", theirs.ToJson(schema));
        Write(dir, "merged.json", outcome.Merged.ToJson(schema));
        Write(dir, "conflicts.md", FlowGraphMerge.DescribeConflicts(outcome, mine, theirs));
    }

    private static void Write(string dir, string file, string text) =>
        File.WriteAllText(Path.Combine(dir, file), text, new UTF8Encoding(false));

    private static FlowGraphSnapshot Read(string dataFolder, string file, ProcessNodeSchemaDocument? schema) =>
        FlowGraphSnapshot.Parse(File.ReadAllText(Path.Combine(Folder(dataFolder), file)), schema);

    private static string BuildReadme(string workingPath) => $$"""
        # 等待合併：流程圖

        流程圖在本機和雲端都改到了同一個地方，正在等待合併。

        - `base.json`：上次同步時的版本（共同祖先）
        - `mine.json`：本機的版本
        - `theirs.json`：雲端的版本
        - `conflicts.md`：衝突清單，每一處的基準／本機／雲端三個值
        - `merged.json`：**工作檔**。兩邊沒有衝突的修改都已經合併進去了，衝突的地方目前放的是本機的值。

        請照 `conflicts.md` 一處一處決定，把結果**直接改進工作檔**：

            {{workingPath}}

        工作檔的格式：`{ "graph": 流程圖, "memos": { 節點id: Memo }, "content": { 節點id: { conflict, change, characters, location } } }`。

        規則：
        - 只改 `conflicts.md` 列出的地方，其他已經合併好的內容不要動。
        - 節點的欄位在 `graph.nodes[]` 裡 `id` 相同的那個節點的 `data`；連線在 `graph.edges[]`；群組在 `graph.groups[]`。
        - 一邊刪掉、另一邊修改的節點：要留就讓它留在 `graph.nodes[]`（連到它的線也要留），要刪就連同它的線一起刪。
        - 同一處兩邊改得不一樣時，依劇情判斷；拿不定就停下來問使用者。
        - 不要改這個資料夾裡的其他檔案，也不要改 blueprint.json。

        改完後回到 StoryForge「雲端同步」分頁，按流程圖的「繼續合併」。
        """;
}
