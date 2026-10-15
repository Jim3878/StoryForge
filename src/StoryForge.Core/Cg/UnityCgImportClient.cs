using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace StoryForge.Core.Cg;

// 對應 Unity 端 Assets/Editor/EditorWindow/CgImportPipeline/CgImportQueue.cs 的 JSON 格式。
public sealed class UnityImportRequest
{
    [JsonProperty("requestId")] public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    [JsonProperty("items")] public List<UnityImportItem> Items { get; set; } = new();
}

public enum UnityImportKind
{
    Sprite,
    Placeholder,
    Spine,
    Share
}

public sealed class UnityImportItem
{
    [JsonProperty("cgId")] public string CgId { get; set; } = string.Empty;
    [JsonProperty("kind"), JsonConverter(typeof(StringEnumConverter))] public UnityImportKind Kind { get; set; }
    [JsonProperty("pngFolder")] public string? PngFolder { get; set; }
    [JsonProperty("spineProjectPath")] public string? SpineProjectPath { get; set; }
    [JsonProperty("spineCliPath")] public string? SpineCliPath { get; set; }
    [JsonProperty("cliTimeoutSeconds")] public int CliTimeoutSeconds { get; set; } = 300;
    [JsonProperty("thumbnailPath")] public string? ThumbnailPath { get; set; }
    [JsonProperty("definitionDiffs")] public List<string>? DefinitionDiffs { get; set; }
    [JsonProperty("noDefinition")] public bool NoDefinition { get; set; }
    [JsonProperty("sharePrefabOf")] public string? SharePrefabOf { get; set; }
}

public sealed class UnityImportItemResult
{
    [JsonProperty("cgId")] public string CgId { get; set; } = string.Empty;
    [JsonProperty("ok")] public bool Ok { get; set; }
    [JsonProperty("error")] public string? Error { get; set; }
    [JsonProperty("log")] public List<string> Log { get; set; } = new();
}

public sealed class UnityImportResult
{
    [JsonProperty("requestId")] public string? RequestId { get; set; }
    [JsonProperty("items")] public List<UnityImportItemResult> Items { get; set; } = new();
}

public sealed class UnityImportProgress
{
    [JsonProperty("requestId")] public string? RequestId { get; set; }
    [JsonProperty("state")] public string? State { get; set; }
    [JsonProperty("index")] public int Index { get; set; }
    [JsonProperty("total")] public int Total { get; set; }
    [JsonProperty("cgId")] public string? CgId { get; set; }
    [JsonProperty("step")] public string? Step { get; set; }
}

// Unity 開著（Temp/UnityLockfile 被鎖住）→ 寫 pending.json 等 Unity 的 CgImportQueueHook 處理；
// 沒開 → 自己跑 Unity batchmode。兩條路都等 Unity 寫回 result.json 才算完成。
public static class UnityCgImportClient
{
    private const string QueueFolder = "Library/StoryForgeCgImport";

    public static string GetQueueFolder(string projectRoot) => Path.Combine(projectRoot, QueueFolder.Replace('/', Path.DirectorySeparatorChar));

    // 預設的 Unity.exe：依 ProjectSettings/ProjectVersion.txt 推 Unity Hub 的安裝路徑。
    public static string GuessUnityExePath(string projectRoot)
    {
        try
        {
            var versionLine = File.ReadLines(Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt"))
                .FirstOrDefault(x => x.StartsWith("m_EditorVersion:", StringComparison.Ordinal));
            var version = versionLine?.Split(':', 2)[1].Trim();
            if (!string.IsNullOrEmpty(version))
                return $@"C:\Program Files\Unity\Hub\Editor\{version}\Editor\Unity.exe";
        }
        catch
        {
            // 讀不到就回空字串，由設定頁讓使用者指定。
        }

        return string.Empty;
    }

    public static async Task<UnityImportResult> RunAsync(string projectRoot, string unityExePath, UnityImportRequest request,
        Action<CgToolProgress> onProgress, CancellationToken cancellationToken)
    {
        var folder = GetQueueFolder(projectRoot);
        Directory.CreateDirectory(folder);
        var resultPath = Path.Combine(folder, "result.json");
        var progressPath = Path.Combine(folder, "progress.json");
        if (File.Exists(resultPath))
            File.Delete(resultPath);
        if (File.Exists(progressPath))
            File.Delete(progressPath);

        var pendingPath = Path.Combine(folder, "pending.json");
        var temp = pendingPath + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(request, Formatting.Indented), new UTF8Encoding(false));
        File.Move(temp, pendingPath, true);

        var started = DateTime.UtcNow;
        Process? batch = null;
        var logPath = Path.Combine(folder, "batch.log");
        if (!CgUnityState.IsEditorRunning(projectRoot))
        {
            if (!File.Exists(unityExePath))
                throw new FileNotFoundException($"Unity 沒有開，也找不到 Unity.exe 來跑 batchmode：{unityExePath}（請在設定中指定）");

            var start = new ProcessStartInfo(unityExePath) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-batchmode", "-nographics", "-projectPath", projectRoot,
                         "-executeMethod", "VisualNovelCg.EditorTool.CgImportQueue.ProcessPendingBatch", "-quit", "-logFile", logPath })
                start.ArgumentList.Add(arg);
            batch = Process.Start(start);
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = TryRead<UnityImportResult>(resultPath);
                if (result != null && result.RequestId == request.RequestId)
                    return result;

                var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                var progress = TryRead<UnityImportProgress>(progressPath);
                if (progress != null && progress.RequestId == request.RequestId && progress.State == "running")
                {
                    onProgress(new CgToolProgress($"Unity 匯入 {progress.Index + 1}/{progress.Total} · {progress.CgId}", $"已 {elapsed:0} 秒"));
                }
                else if (batch != null)
                {
                    if (batch.HasExited)
                    {
                        await Task.Delay(500, CancellationToken.None);
                        result = TryRead<UnityImportResult>(resultPath);
                        if (result != null && result.RequestId == request.RequestId)
                            return result;
                        throw new InvalidOperationException($"Unity batchmode 結束了但沒有結果（exit {batch.ExitCode}）。最後的 log：{TailLine(logPath)}");
                    }

                    onProgress(new CgToolProgress("Unity 啟動中（batchmode）", $"已 {elapsed:0} 秒 · {TailLine(logPath)}"));
                }
                else
                {
                    onProgress(new CgToolProgress("等待 Unity 匯入（請切回 Unity）", $"已 {elapsed:0} 秒"));
                }

                await Task.Delay(500, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // 已經交給 Unity 的不能取消：batchmode 讓它跑完；Unity 開著時把還沒被接走的清單收回。
            if (batch == null && File.Exists(pendingPath))
                File.Delete(pendingPath);
            throw;
        }
    }

    private static T? TryRead<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonConvert.DeserializeObject<T>(File.ReadAllText(path, Encoding.UTF8)) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string TailLine(string path)
    {
        try
        {
            if (!File.Exists(path))
                return string.Empty;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 4096)
                stream.Seek(-4096, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
