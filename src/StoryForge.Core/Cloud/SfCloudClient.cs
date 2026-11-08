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
    // node id -> { conflict, change, characters, location }; only meaningful once ContentSeeded.
    public Dictionary<string, Dictionary<string, string>> Content { get; set; } = new();
    // False for a graph uploaded before node content was synced: its empty Content means "never sent", not
    // "cleared", so a download must leave local 衝突/變化/... alone.
    public bool ContentSeeded { get; set; }
    public string Sv { get; set; } = string.Empty;
}

public sealed class CloudGraphPushResult
{
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
}

/// <summary>The local flow graph sent with an upload, used only if the cloud has none yet.</summary>
public sealed record CloudGraphSeed(string GraphJson, IReadOnlyDictionary<string, string> Memos,
    IReadOnlyDictionary<string, Dictionary<string, string>> Content, string? NodeSchemaJson);

/// <summary>A reference file uploaded for MCP-only AIs (AGENTS.md, the world book, ...).</summary>
public sealed record CloudReference(string Name, string Text, string Hash);

public sealed class CloudPushOutcome
{
    public List<CloudPushResult> Results { get; set; } = new();
    // Set when a flow graph seed was sent: status "ok" or "rejected" (the cloud already has one) + message.
    public CloudGraphPushResult? Graph { get; set; }
    // Set when contentSeed was sent: true once the cloud graph has node content (now or already before).
    public bool? ContentSeeded { get; set; }
}

public sealed class CloudSyncPull
{
    public int SchemaVersion { get; set; }
    public bool Exists { get; set; }
    // False when the extension read everything (no --since, or a host without doc timestamps).
    public bool Incremental { get; set; }
    // Server time (ms) to pass as `since` next time.
    public long ServerTime { get; set; }
    public List<CloudPlayscript> Index { get; set; } = new();
    // Null when the flow graph did not change since `since`.
    public CloudGraphPull? Graph { get; set; }
    // Only the tables that changed since `since` (all of them without it).
    public List<CloudPlayscript> Playscripts { get; set; } = new();
}

/// <summary>One progress report from the extension: a phase and, when countable, how far along it is.</summary>
public sealed record CloudProgress(string Phase, int? Done, int? Total);

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

    /// <summary>Called (on a thread-pool thread) for each `@sf-progress` line the extension prints.</summary>
    public Action<CloudProgress>? Progress { get; set; }

    public SfCloudClient(string command, string workspaceId)
    {
        _command = string.IsNullOrWhiteSpace(command) ? "affine-mcp.cmd" : command.Trim();
        _workspaceId = workspaceId.Trim();
    }

    /// <summary>
    /// One connection: index, and the tables changed after <paramref name="since"/> / the flow graph changed after
    /// <paramref name="graphSince"/> (null = read it all), each only when asked for.
    /// </summary>
    public async Task<CloudSyncPull> SyncPullAsync(long? since, long? graphSince, bool tables = true, bool graph = true,
        IReadOnlyCollection<string>? only = null, CancellationToken ct = default)
    {
        var outPath = TempFile("sync-pull");
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var args = new List<string> { "sf", "sync-pull", "--workspace", _workspaceId, "--out", outPath };
            if (since is { } s)
                args.AddRange(new[] { "--since", s.ToString(inv) });
            args.Add("--graph-since");
            args.Add(graphSince is { } g ? g.ToString(inv) : "");
            if (!tables) args.Add("--no-tables");
            if (!graph) args.Add("--no-graph");
            if (only is { Count: > 0 })
                args.AddRange(new[] { "--only", string.Join(",", only) });
            await RunAsync(args, ct);
            var result = JsonConvert.DeserializeObject<CloudSyncPull>(await File.ReadAllTextAsync(outPath, ct))
                         ?? throw new InvalidOperationException("sf sync-pull 沒有回傳資料");
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

    public async Task<CloudPushOutcome> PushAsync(IReadOnlyList<CloudPushItem> items, (string Json, string Hash)? rules,
        IReadOnlyList<CloudReference>? references = null,
        IReadOnlyDictionary<string, Dictionary<string, string>>? contentSeed = null,
        CloudGraphSeed? graphSeed = null,
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
                references = references is { Count: > 0 } ? references.Select(r => new { name = r.Name, text = r.Text, hash = r.Hash }) : null,
                contentSeed,
                graph = graphSeed == null ? null : new Newtonsoft.Json.Linq.JObject
                {
                    ["graph"] = Newtonsoft.Json.Linq.JObject.Parse(graphSeed.GraphJson),
                    ["memos"] = Newtonsoft.Json.Linq.JObject.FromObject(graphSeed.Memos),
                    ["content"] = Newtonsoft.Json.Linq.JObject.FromObject(graphSeed.Content),
                    ["nodeSchema"] = graphSeed.NodeSchemaJson,
                },
            };
            await File.WriteAllTextAsync(inPath, JsonConvert.SerializeObject(body), new UTF8Encoding(false), ct);
            await RunAsync(new[] { "sf", "push", "--workspace", _workspaceId, "--in", inPath, "--out", outPath }, ct);
            return JsonConvert.DeserializeObject<CloudPushOutcome>(await File.ReadAllTextAsync(outPath, ct)) ?? new CloudPushOutcome();
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

    private async Task RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_workspaceId))
            throw new InvalidOperationException("還沒設定雲端 workspace id（設定 → 雲端）。");

        var (exe, prefix) = ResolveLauncher(_command);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in prefix)
            psi.ArgumentList.Add(a);
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        // Drain both pipes — an undrained pipe deadlocks the child once its buffer fills (see the Launcher).
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (e.Data.StartsWith(ProgressPrefix, StringComparison.Ordinal))
            {
                ReportProgress(e.Data[ProgressPrefix.Length..]);
                return;
            }
            lock (stderr) stderr.AppendLine(e.Data);
        };
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

    private const string ProgressPrefix = "@sf-progress ";

    private void ReportProgress(string json)
    {
        if (Progress == null) return;
        try
        {
            var o = Newtonsoft.Json.Linq.JObject.Parse(json);
            Progress(new CloudProgress(o.Value<string>("phase") ?? string.Empty, o.Value<int?>("done"), o.Value<int?>("total")));
        }
        catch
        {
            // A malformed progress line only costs the progress bar an update.
        }
    }

    // The default command goes straight to node + affine-mcp's lean extension entry (dist/ext-cli.js, which
    // also reuses its sign-in between runs) instead of the .cmd shim and the full MCP server entry: about
    // two seconds less per call. Anything else configured is run as given.
    private static (string Exe, string[] Prefix) ResolveLauncher(string command)
    {
        var name = Path.GetFileName(command).ToLowerInvariant();
        if (name is "affine-mcp.cmd" or "affine-mcp" or "affine-mcp-ext.cmd" or "affine-mcp-ext")
        {
            var dir = Path.GetDirectoryName(command);
            if (string.IsNullOrEmpty(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
            var entry = Path.Combine(dir, "node_modules", "affine-mcp-server", "dist", "ext-cli.js");
            if (File.Exists(entry))
                return ("node", new[] { entry });
            var ext = Path.Combine(dir, "affine-mcp-ext.cmd");
            if (File.Exists(ext))
                return (ext, Array.Empty<string>());
        }
        return (command, Array.Empty<string>());
    }

    private static string TempFile(string tag) =>
        Path.Combine(Path.GetTempPath(), $"storyforge-sf-{tag}-{System.Guid.NewGuid():N}.json");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* temp file */ }
    }
}
