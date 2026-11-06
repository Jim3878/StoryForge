using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;

namespace StoryForge.Core.Cloud;

public sealed class CloudPlayscript
{
    public string Guid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Chapter { get; set; } = string.Empty;
    public bool Orphan { get; set; }
    public List<string[]> Rows { get; set; } = new();
    public string Sv { get; set; } = string.Empty;
}

public sealed class CloudPullResult
{
    public int SchemaVersion { get; set; }
    public bool Exists { get; set; }
    public List<CloudPlayscript> Playscripts { get; set; } = new();
}

public sealed class CloudGraphPull
{
    public bool Exists { get; set; }
    public bool Enabled { get; set; }
    public Newtonsoft.Json.Linq.JObject? Graph { get; set; }
    public Dictionary<string, string> Memos { get; set; } = new();
    public string Sv { get; set; } = string.Empty;
}

public sealed class CloudPushItem
{
    public string Guid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Chapter { get; set; } = string.Empty;
    public List<string[]> Rows { get; set; } = new();
    // null = this playscript has never been synced from this machine.
    public List<string[]>? BaseRows { get; set; }
}

public sealed class CloudPushResult
{
    public string Guid { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // ok | unchanged | rejected
    public string? Message { get; set; }
    public List<string[]>? Rows { get; set; }
    public string? Sv { get; set; }
}

// Talks to the cloud through the affine-mcp fork's StoryForge extension (`affine-mcp sf pull|push`), which
// owns the AFFiNE login and the Yjs/WebSocket work; this side only exchanges JSON files with it.
public sealed class SfCloudClient
{
    public const int SupportedSchemaVersion = 1;

    private readonly string _command;
    private readonly string _workspaceId;

    public SfCloudClient(string command, string workspaceId)
    {
        _command = string.IsNullOrWhiteSpace(command) ? "affine-mcp.cmd" : command.Trim();
        _workspaceId = workspaceId.Trim();
    }

    public async Task<CloudPullResult> PullAsync(CancellationToken ct = default)
    {
        var outPath = TempFile("pull");
        try
        {
            await RunAsync(new[] { "sf", "pull", "--workspace", _workspaceId, "--out", outPath }, ct);
            var result = JsonConvert.DeserializeObject<CloudPullResult>(await File.ReadAllTextAsync(outPath, ct))
                         ?? throw new InvalidOperationException("sf pull 沒有回傳資料");
            if (result.SchemaVersion > SupportedSchemaVersion)
                throw new InvalidOperationException(
                    $"雲端資料的 schemaVersion 是 {result.SchemaVersion}，這個 StoryForge 只支援到 {SupportedSchemaVersion}。請先更新 StoryForge。");
            return result;
        }
        finally
        {
            TryDelete(outPath);
        }
    }

    public async Task<List<CloudPushResult>> PushAsync(IReadOnlyList<CloudPushItem> items, (string Json, string Hash)? rules,
        CancellationToken ct = default)
    {
        var inPath = TempFile("push-in");
        var outPath = TempFile("push-out");
        try
        {
            var body = new
            {
                playscripts = items.Select(i => new
                {
                    guid = i.Guid, name = i.Name, chapter = i.Chapter,
                    rows = CloudRows.Normalize(i.Rows),
                    baseRows = i.BaseRows == null ? null : CloudRows.Normalize(i.BaseRows),
                }),
                rules = rules == null ? null : new { json = rules.Value.Json, hash = rules.Value.Hash },
            };
            await File.WriteAllTextAsync(inPath, JsonConvert.SerializeObject(body), new UTF8Encoding(false), ct);
            await RunAsync(new[] { "sf", "push", "--workspace", _workspaceId, "--in", inPath, "--out", outPath }, ct);
            var parsed = JsonConvert.DeserializeAnonymousType(await File.ReadAllTextAsync(outPath, ct),
                new { results = new List<CloudPushResult>() });
            return parsed?.results ?? new List<CloudPushResult>();
        }
        finally
        {
            TryDelete(inPath);
            TryDelete(outPath);
        }
    }

    public async Task<CloudGraphPull> PullGraphAsync(CancellationToken ct = default)
    {
        var outPath = TempFile("graph-pull");
        try
        {
            await RunAsync(new[] { "sf", "graph-pull", "--workspace", _workspaceId, "--out", outPath }, ct);
            return JsonConvert.DeserializeObject<CloudGraphPull>(await File.ReadAllTextAsync(outPath, ct))
                   ?? throw new InvalidOperationException("sf graph-pull 沒有回傳資料");
        }
        finally
        {
            TryDelete(outPath);
        }
    }

    /// <param name="graphJson">canonical graph JSON (the blueprint's `graph`)</param>
    /// <param name="mode">"seed" (only into an empty cloud graph) or "overwrite"</param>
    /// <returns>null on success, otherwise the reason it was refused</returns>
    public async Task<string?> PushGraphAsync(string graphJson, IReadOnlyDictionary<string, string> memos, string? nodeSchemaJson,
        string mode, CancellationToken ct = default)
    {
        var inPath = TempFile("graph-in");
        var outPath = TempFile("graph-out");
        try
        {
            var body = new Newtonsoft.Json.Linq.JObject
            {
                ["graph"] = Newtonsoft.Json.Linq.JObject.Parse(graphJson),
                ["memos"] = Newtonsoft.Json.Linq.JObject.FromObject(memos),
                ["mode"] = mode,
            };
            if (nodeSchemaJson != null)
                body["nodeSchema"] = nodeSchemaJson;
            await File.WriteAllTextAsync(inPath, body.ToString(Formatting.None), new UTF8Encoding(false), ct);
            await RunAsync(new[] { "sf", "graph-push", "--workspace", _workspaceId, "--in", inPath, "--out", outPath }, ct);
            var result = Newtonsoft.Json.Linq.JObject.Parse(await File.ReadAllTextAsync(outPath, ct));
            return result.Value<string>("status") == "ok" ? null : result.Value<string>("message") ?? "雲端拒絕了上傳";
        }
        finally
        {
            TryDelete(inPath);
            TryDelete(outPath);
        }
    }

    private async Task RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_workspaceId))
            throw new InvalidOperationException("還沒設定雲端 workspace id（設定 → 雲端）。");

        var psi = new ProcessStartInfo(_command)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        // Drain both pipes — an undrained pipe deadlocks the child once its buffer fills (see the Launcher).
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        try
        {
            process.Start();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"無法執行「{_command}」：{e.Message}。請確認已安裝 nineday 版 affine-mcp。", e);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException("雲端同步超過 5 分鐘沒有完成，已中止。");
        }

        if (process.ExitCode != 0)
        {
            string text;
            lock (stderr) text = stderr.ToString();
            var lastLine = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? $"exit code {process.ExitCode}";
            throw new InvalidOperationException(lastLine.TrimStart('✗', ' '));
        }
    }

    private static string TempFile(string tag) =>
        Path.Combine(Path.GetTempPath(), $"storyforge-sf-{tag}-{System.Guid.NewGuid():N}.json");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* temp file */ }
    }
}
