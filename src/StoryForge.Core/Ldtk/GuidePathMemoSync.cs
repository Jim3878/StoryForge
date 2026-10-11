using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StoryForge.Core.Ldtk;

public sealed class GuidePathPoint
{
    public required string Iid { get; init; }
    public required string Type { get; init; }
    public required string Level { get; init; }
    public string? Key { get; init; }

    // False when this entity type has no "Memo" field def in LDtk — such a point is exported without a memo
    // and can never be written back.
    public bool HasMemoField { get; init; }
    public string? Memo { get; init; }
    public required IReadOnlyList<string> Neighbors { get; init; }
}

public sealed class GuidePathMemoConflict
{
    public required string Iid { get; init; }
    public required string Level { get; init; }
    public string? Key { get; init; }
    public string? LdtkMemo { get; init; }
    public string? FileMemo { get; init; }
}

public sealed class GuidePathMemoSyncResult
{
    public int PendingCount { get; init; }
    public int WrittenCount { get; init; }
    public IReadOnlyList<GuidePathMemoConflict> Conflicts { get; init; } = Array.Empty<GuidePathMemoConflict>();
}

// Two-way sync of the guide-path points' LDtk "Memo" field with GuidePathPoints.json in
// AppSettings.ExternalDataFolder, so an AI can read the memos and fill them in without touching the 8 MB .ldtk.
// Company.ldtk stays the source of truth; the AI's edits only reach it through WriteBack (a manual button,
// since LDtk must be closed — it would overwrite the file from memory on its next save).
//
// Who changed what is decided three-way against a per-machine base (the value last seen identical on both
// sides, in %LocalAppData%\StoryForge\guide-path-memo-base.json): file differs from base -> the AI edited it;
// LDtk differs from base -> someone edited it in LDtk; both -> conflict, never silently resolved.
public static class GuidePathMemoSync
{
    public const string ExportFileName = "GuidePathPoints.json";
    private const string BaseFileName = "guide-path-memo-base.json";
    private const string MemoFieldName = "Memo";

    // Export/WriteBack/ResolveConflict all read-modify-write the same two files; two circuits refreshing at once
    // (e.g. Blazor's prerender + interactive pass, or a second browser tab) must not interleave.
    private static readonly object SyncLock = new();

    private static string BasePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StoryForge", BaseFileName);

    public static List<GuidePathPoint> ReadPoints(JObject ldtkRoot)
    {
        var memoTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var def in ldtkRoot["defs"]?["entities"] as JArray ?? new JArray())
        {
            var fieldDefs = def["fieldDefs"] as JArray ?? new JArray();
            if (fieldDefs.Any(f => f["identifier"]?.Value<string>() == MemoFieldName))
                memoTypes.Add(def["identifier"]?.Value<string>() ?? string.Empty);
        }

        var points = new List<GuidePathPoint>();
        foreach (var level in ldtkRoot["levels"] as JArray ?? new JArray())
        {
            var levelName = level["identifier"]?.Value<string>() ?? "?";
            foreach (var layer in level["layerInstances"] as JArray ?? new JArray())
            foreach (var entity in layer["entityInstances"] as JArray ?? new JArray())
            {
                var type = entity["__identifier"]?.Value<string>();
                var iid = entity["iid"]?.Value<string>();
                if (type is not ("PathPoint" or "TransportPathPoint") || iid == null)
                    continue;

                var fields = (entity["fieldInstances"] as JArray ?? new JArray())
                    .Where(f => f["__identifier"]?.Value<string>() != null)
                    .GroupBy(f => f["__identifier"]!.Value<string>()!)
                    .ToDictionary(g => g.Key, g => g.First()["__value"]);

                var key = fields.GetValueOrDefault("GuidePathNodeKey");
                var memo = fields.GetValueOrDefault(MemoFieldName);
                var neighbors = fields.GetValueOrDefault("Neighbors") as JArray ?? new JArray();
                points.Add(new GuidePathPoint
                {
                    Iid = iid,
                    Type = type,
                    Level = levelName,
                    Key = key is { Type: JTokenType.String } && key.Value<string>() != "None" ? key.Value<string>() : null,
                    HasMemoField = memoTypes.Contains(type),
                    Memo = NormalizeMemo(memo is { Type: JTokenType.String } ? memo.Value<string>() : null),
                    Neighbors = neighbors
                        .Select(n => n["entityIid"]?.Value<string>())
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Select(n => n!)
                        .ToList(),
                });
            }
        }

        return points;
    }

    // Runs on every 流程狀態 refresh: brings LDtk-side memo edits into the file, keeps the AI's not-yet-written
    // edits, and reports what is pending / conflicting. Only rewrites the file when its content changed (the
    // external data folder is a git repo).
    public static GuidePathMemoSyncResult Export(JObject ldtkRoot, string folder)
    {
        lock (SyncLock)
            return ExportLocked(ldtkRoot, folder);
    }

    private static GuidePathMemoSyncResult ExportLocked(JObject ldtkRoot, string folder)
    {
        var points = ReadPoints(ldtkRoot);
        var fileMemos = ReadFileMemos(folder);
        var baseMemos = LoadBase();
        var newBase = new Dictionary<string, string?>(StringComparer.Ordinal);
        var output = new Dictionary<string, string?>(StringComparer.Ordinal);
        var conflicts = new List<GuidePathMemoConflict>();
        var pending = 0;

        foreach (var point in points.Where(p => p.HasMemoField))
        {
            var ldtk = point.Memo;
            var file = fileMemos.TryGetValue(point.Iid, out var f) ? f : ldtk;
            var hasBase = baseMemos.TryGetValue(point.Iid, out var b);

            if (file == ldtk)
            {
                output[point.Iid] = ldtk;
                newBase[point.Iid] = ldtk;
            }
            else if (hasBase && file == b)
            {
                output[point.Iid] = ldtk;
                newBase[point.Iid] = ldtk;
            }
            else if (hasBase && ldtk == b)
            {
                output[point.Iid] = file;
                newBase[point.Iid] = b;
                pending++;
            }
            else
            {
                output[point.Iid] = file;
                if (hasBase)
                    newBase[point.Iid] = b;
                conflicts.Add(ToConflict(point, file));
            }
        }

        WriteFileIfChanged(folder, BuildExportJson(points, output));
        SaveBase(newBase);
        return new GuidePathMemoSyncResult { PendingCount = pending, Conflicts = conflicts };
    }

    // The manual "寫回 LDtk" button: writes every memo the AI changed (file != LDtk, LDtk still == base) into
    // Company.ldtk by replacing only that field's "__value"/"realEditorValues" text, so the rest of the file —
    // and its git diff — stays untouched. Conflicts are reported and left alone.
    public static GuidePathMemoSyncResult WriteBack(string ldtkPath, string folder)
    {
        lock (SyncLock)
            return WriteBackLocked(ldtkPath, folder);
    }

    private static GuidePathMemoSyncResult WriteBackLocked(string ldtkPath, string folder)
    {
        var text = File.ReadAllText(ldtkPath);
        var points = ReadPoints(JObject.Parse(text));
        var fileMemos = ReadFileMemos(folder);
        var baseMemos = LoadBase();
        var spans = new LdtkMemoSpanScanner(text).FindMemoFields(MemoFieldName);

        var writes = new List<(GuidePathPoint Point, string? Memo)>();
        var conflicts = new List<GuidePathMemoConflict>();
        foreach (var point in points.Where(p => p.HasMemoField))
        {
            if (!fileMemos.TryGetValue(point.Iid, out var file) || file == point.Memo)
                continue;

            var hasBase = baseMemos.TryGetValue(point.Iid, out var b);
            if (hasBase && point.Memo == b)
                writes.Add((point, file));
            else if (!(hasBase && file == b))
                conflicts.Add(ToConflict(point, file));
            // else: only the LDtk side changed — the next refresh pulls it into the file.
        }

        var missing = writes.Where(w => !spans.ContainsKey(w.Point.Iid)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"{missing.Count} 個引導點在 LDtk 裡沒有 Memo 欄位的資料（例如 {missing[0].Point.Iid}），請先用 LDtk 開啟並存檔一次再寫回。");

        if (writes.Count > 0)
        {
            var replacements = new List<(int Start, int End, string Text)>();
            foreach (var (point, memo) in writes)
            {
                var span = spans[point.Iid];
                replacements.Add((span.ValueStart, span.ValueEnd, memo == null ? "null" : JsonConvert.ToString(memo)));
                replacements.Add((span.EditorValuesStart, span.EditorValuesEnd, memo == null
                    ? "[]"
                    : $"[{{ \"id\": \"V_String\", \"params\": [{JsonConvert.ToString(memo)}] }}]"));
            }

            var patched = text;
            foreach (var r in replacements.OrderByDescending(r => r.Start))
                patched = patched[..r.Start] + r.Text + patched[r.End..];

            // Never write a file that no longer parses or doesn't hold exactly the intended values.
            var check = ReadPoints(JObject.Parse(patched)).ToDictionary(p => p.Iid, p => p.Memo, StringComparer.Ordinal);
            if (writes.Any(w => check.GetValueOrDefault(w.Point.Iid) != w.Memo))
                throw new InvalidOperationException("寫回後的 LDtk 內容驗證失敗，已取消，LDtk 檔案沒有被修改。");

            File.WriteAllText(ldtkPath, patched, new System.Text.UTF8Encoding(false));

            foreach (var (point, memo) in writes)
                baseMemos[point.Iid] = memo;
            SaveBase(baseMemos);
        }

        return new GuidePathMemoSyncResult { WrittenCount = writes.Count, Conflicts = conflicts };
    }

    // Resolving a conflict = making one side win by moving the base: keeping the file's value makes it a plain
    // pending AI edit (written by the next WriteBack); keeping LDtk's value rewrites the file entry to match.
    public static void ResolveConflict(GuidePathMemoConflict conflict, bool keepFile, string folder)
    {
        lock (SyncLock)
            ResolveConflictLocked(conflict, keepFile, folder);
    }

    private static void ResolveConflictLocked(GuidePathMemoConflict conflict, bool keepFile, string folder)
    {
        var baseMemos = LoadBase();
        baseMemos[conflict.Iid] = conflict.LdtkMemo;
        SaveBase(baseMemos);

        if (keepFile)
            return;

        var path = Path.Combine(folder, ExportFileName);
        var root = JObject.Parse(File.ReadAllText(path));
        foreach (var entry in EnumerateFileEntries(root))
        {
            if (entry["iid"]?.Value<string>() == conflict.Iid)
                entry["memo"] = conflict.LdtkMemo;
        }

        WriteFileIfChanged(folder, root.ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n");
    }

    private static GuidePathMemoConflict ToConflict(GuidePathPoint point, string? file) => new()
    {
        Iid = point.Iid,
        Level = point.Level,
        Key = point.Key,
        LdtkMemo = point.Memo,
        FileMemo = file,
    };

    private static string? NormalizeMemo(string? memo) => string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();

    private static string BuildExportJson(List<GuidePathPoint> points, Dictionary<string, string?> memos)
    {
        var levels = new JObject();
        foreach (var group in points.GroupBy(p => p.Level))
        {
            levels[group.Key] = new JArray(group.Select(p =>
            {
                var json = new JObject
                {
                    ["iid"] = p.Iid,
                    ["type"] = p.Type,
                };
                if (p.Key != null)
                    json["key"] = p.Key;
                if (p.HasMemoField)
                    json["memo"] = memos.GetValueOrDefault(p.Iid);
                json["neighbors"] = new JArray(p.Neighbors);
                return json;
            }));
        }

        return new JObject { ["Levels"] = levels }.ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n";
    }

    private static Dictionary<string, string?> ReadFileMemos(string folder)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        var path = Path.Combine(folder, ExportFileName);
        if (!File.Exists(path))
            return result;

        JObject root;
        try
        {
            root = JObject.Parse(File.ReadAllText(path));
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"{ExportFileName} 不是合法的 JSON：{e.Message}");
        }

        foreach (var entry in EnumerateFileEntries(root))
        {
            var iid = entry["iid"]?.Value<string>();
            if (iid != null && entry.TryGetValue("memo", out var memo))
                result[iid] = NormalizeMemo(memo.Type == JTokenType.String ? memo.Value<string>() : null);
        }

        return result;
    }

    private static IEnumerable<JObject> EnumerateFileEntries(JObject root) =>
        (root["Levels"] as JObject ?? new JObject()).Properties()
            .SelectMany(level => level.Value as JArray ?? new JArray())
            .OfType<JObject>();

    private static void WriteFileIfChanged(string folder, string json)
    {
        var path = Path.Combine(folder, ExportFileName);
        if (File.Exists(path) && File.ReadAllText(path) == json)
            return;

        File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
    }

    private static Dictionary<string, string?> LoadBase()
    {
        if (!File.Exists(BasePath))
            return new Dictionary<string, string?>(StringComparer.Ordinal);

        var loaded = JsonConvert.DeserializeObject<Dictionary<string, string?>>(File.ReadAllText(BasePath));
        return new Dictionary<string, string?>(loaded ?? new(), StringComparer.Ordinal);
    }

    private static void SaveBase(Dictionary<string, string?> baseMemos)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BasePath)!);
        File.WriteAllText(BasePath, JsonConvert.SerializeObject(baseMemos, Formatting.Indented));
    }
}

// Finds, without re-serializing anything, the exact text spans of each entity's Memo field "__value" and
// "realEditorValues" inside a .ldtk file, keyed by entity iid — walking only
// levels[].layerInstances[].entityInstances[].fieldInstances[] and skipping every other value.
internal sealed class LdtkMemoSpanScanner
{
    public readonly record struct MemoSpan(int ValueStart, int ValueEnd, int EditorValuesStart, int EditorValuesEnd);

    private readonly string _t;

    public LdtkMemoSpanScanner(string text) => _t = text;

    public Dictionary<string, MemoSpan> FindMemoFields(string fieldName)
    {
        var result = new Dictionary<string, MemoSpan>(StringComparer.Ordinal);
        foreach (var levels in Members(0).Where(m => m.Key == "levels"))
        foreach (var level in Items(levels.Start))
        foreach (var layers in Members(level.Start).Where(m => m.Key == "layerInstances" && _t[m.Start] == '['))
        foreach (var layer in Items(layers.Start))
        foreach (var entities in Members(layer.Start).Where(m => m.Key == "entityInstances"))
        foreach (var entity in Items(entities.Start))
        {
            string? iid = null;
            MemoSpan? span = null;
            foreach (var member in Members(entity.Start))
            {
                if (member.Key == "iid")
                    iid = JsonConvert.DeserializeObject<string>(_t[member.Start..member.End]);
                else if (member.Key == "fieldInstances")
                    span = FindField(member.Start, fieldName) ?? span;
            }

            if (iid != null && span != null)
                result[iid] = span.Value;
        }

        return result;
    }

    private MemoSpan? FindField(int arrayStart, string fieldName)
    {
        foreach (var field in Items(arrayStart))
        {
            var members = Members(field.Start).ToList();
            var id = members.FirstOrDefault(m => m.Key == "__identifier");
            if (id.Key == null || JsonConvert.DeserializeObject<string>(_t[id.Start..id.End]) != fieldName)
                continue;

            var value = members.FirstOrDefault(m => m.Key == "__value");
            var editor = members.FirstOrDefault(m => m.Key == "realEditorValues");
            if (value.Key == null || editor.Key == null)
                return null;

            return new MemoSpan(value.Start, value.End, editor.Start, editor.End);
        }

        return null;
    }

    private IEnumerable<(string Key, int Start, int End)> Members(int objectStart)
    {
        var i = SkipWs(objectStart) + 1;
        while (true)
        {
            i = SkipWs(i);
            if (_t[i] == '}')
                yield break;

            var keyStart = i;
            i = SkipString(i);
            var key = JsonConvert.DeserializeObject<string>(_t[keyStart..i])!;
            i = SkipWs(i) + 1; // ':'
            var valueStart = SkipWs(i);
            var valueEnd = SkipValue(valueStart);
            yield return (key, valueStart, valueEnd);

            i = SkipWs(valueEnd);
            if (_t[i] == ',')
                i++;
        }
    }

    private IEnumerable<(int Start, int End)> Items(int arrayStart)
    {
        var i = SkipWs(arrayStart) + 1;
        while (true)
        {
            i = SkipWs(i);
            if (_t[i] == ']')
                yield break;

            var end = SkipValue(i);
            yield return (i, end);

            i = SkipWs(end);
            if (_t[i] == ',')
                i++;
        }
    }

    private int SkipWs(int i)
    {
        while (i < _t.Length && char.IsWhiteSpace(_t[i]))
            i++;
        return i;
    }

    private int SkipString(int i)
    {
        i++;
        while (true)
        {
            var c = _t[i];
            if (c == '\\')
                i += 2;
            else if (c == '"')
                return i + 1;
            else
                i++;
        }
    }

    private int SkipValue(int i)
    {
        var c = _t[i];
        if (c == '"')
            return SkipString(i);

        if (c is '{' or '[')
        {
            var depth = 0;
            while (true)
            {
                c = _t[i];
                if (c == '"')
                {
                    i = SkipString(i);
                    continue;
                }

                if (c is '{' or '[')
                    depth++;
                else if (c is '}' or ']' && --depth == 0)
                    return i + 1;
                i++;
            }
        }

        while (i < _t.Length && _t[i] is not (',' or '}' or ']') && !char.IsWhiteSpace(_t[i]))
            i++;
        return i;
    }
}
