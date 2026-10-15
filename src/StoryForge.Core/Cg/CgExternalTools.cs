using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StoryForge.Core.Cg;

public sealed class CspRender
{
    [JsonProperty("comp")] public string Comp { get; set; } = string.Empty;
    [JsonProperty("outputs")] public List<CspOutput> Outputs { get; set; } = new();
}

public sealed class CspOutput
{
    [JsonProperty("path")] public string Path { get; set; } = string.Empty;
    [JsonProperty("width", NullValueHandling = NullValueHandling.Ignore)] public int? Width { get; set; }
    [JsonProperty("height", NullValueHandling = NullValueHandling.Ignore)] public int? Height { get; set; }
    [JsonProperty("fit", NullValueHandling = NullValueHandling.Ignore)] public string? Fit { get; set; }

    public static CspOutput Full(string path) => new() { Path = path, Width = 1920 };
    public static CspOutput Preview(string path) => new() { Path = path, Width = CgCatalogConstants.PreviewWidth, Height = CgCatalogConstants.PreviewHeight, Fit = "letterbox" };
}

public sealed class CspClipJob
{
    [JsonProperty("clip")] public string Clip { get; set; } = string.Empty;

    [JsonProperty("renders", NullValueHandling = NullValueHandling.Ignore)] public List<CspRender>? Renders { get; set; }

    // 渲染全部構圖；輸出路徑裡的 "{comp}" 會換成構圖名稱。兩者都 null = 只列出構圖。
    [JsonProperty("render_all", NullValueHandling = NullValueHandling.Ignore)] public List<CspOutput>? RenderAll { get; set; }
}

public sealed class CspClipResult
{
    [JsonProperty("clip")] public string Clip { get; set; } = string.Empty;
    [JsonProperty("ok")] public bool Ok { get; set; }
    [JsonProperty("comps")] public List<string> Comps { get; set; } = new();
    [JsonProperty("outputs")] public List<string> Outputs { get; set; } = new();
    [JsonProperty("errors")] public List<string> Errors { get; set; } = new();
}

public static class CgCatalogConstants
{
    public const int PreviewWidth = 512;
    public const int PreviewHeight = 288;
}

// 一行進度：stage = 目前階段；detail = 補充（例如 Csp 的最後一行輸出）。
public sealed record CgToolProgress(string Stage, string Detail);

// 呼叫 clip_layer_comp_exporter.exe --batch job.json（Csp 專案的批次模式）。一個 clip 只載入一次，
// 進度從 progress_file（JSON lines）讀，因為 --windowed 的 exe 沒有可用的 stdout。
public static class CspBatchClient
{
    public static async Task<List<CspClipResult>> RunAsync(string exePath, List<CspClipJob> clips, Action<CgToolProgress> onProgress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"找不到 Csp 工具：{exePath}（請在設定中指定 clip_layer_comp_exporter.exe）");

        var workFolder = Path.Combine(Path.GetTempPath(), "StoryForgeCsp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workFolder);
        var jobPath = Path.Combine(workFolder, "job.json");
        var progressPath = Path.Combine(workFolder, "progress.jsonl");
        var resultPath = Path.Combine(workFolder, "result.json");
        File.WriteAllText(jobPath, JsonConvert.SerializeObject(new
        {
            progress_file = progressPath,
            result_file = resultPath,
            clips
        }), new UTF8Encoding(false));

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exePath)
            {
                ArgumentList = { "--batch", jobPath },
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath)!
            })!;

            var readLines = 0;
            var lastStage = "啟動 Csp 工具";
            while (!process.HasExited)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                await Task.Delay(250, CancellationToken.None);
                readLines = ReadProgress(progressPath, readLines, ref lastStage, onProgress);
            }

            ReadProgress(progressPath, readLines, ref lastStage, onProgress);
            if (!File.Exists(resultPath))
                throw new InvalidOperationException($"Csp 工具沒有輸出結果（exit {process.ExitCode}）。");

            var result = JObject.Parse(File.ReadAllText(resultPath, Encoding.UTF8));
            return result["clips"]?.ToObject<List<CspClipResult>>() ?? new List<CspClipResult>();
        }
        finally
        {
            try { Directory.Delete(workFolder, true); } catch { /* 暫存清不掉不影響結果 */ }
        }
    }

    private static int ReadProgress(string path, int alreadyRead, ref string lastStage, Action<CgToolProgress> onProgress)
    {
        if (!File.Exists(path))
            return alreadyRead;

        string[] lines;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException)
        {
            return alreadyRead;
        }

        for (var i = alreadyRead; i < lines.Length; i++)
        {
            JObject e;
            try { e = JObject.Parse(lines[i]); }
            catch { return i; } // 寫到一半的行，下次再讀。

            var type = (string?)e["type"];
            var elapsed = (double?)e["elapsed"] ?? 0;
            var clipName = Path.GetFileName((string?)e["clip"] ?? string.Empty);
            switch (type)
            {
                case "stage":
                    var comp = (string?)e["comp"];
                    lastStage = comp != null
                        ? $"{clipName} · 渲染構圖 {comp}（{e["comp_index"]}/{e["comp_total"]}）"
                        : $"{clipName} · {(string?)e["stage"]}（{e["index"]}/{e["total"]}）";
                    onProgress(new CgToolProgress(lastStage, $"已 {elapsed:0} 秒"));
                    break;
                case "heartbeat":
                    onProgress(new CgToolProgress(lastStage, $"已 {elapsed:0} 秒"));
                    break;
                case "detail":
                    onProgress(new CgToolProgress(lastStage, (string?)e["message"] ?? string.Empty));
                    break;
            }
        }

        return lines.Length;
    }

    internal static void TryKill(Process process)
    {
        try { process.Kill(true); } catch { /* 已結束 */ }
    }
}

// Spine 預覽：Spine CLI 先匯出骨架 JSON 取得動畫清單與 bg 範圍（遊戲鏡頭框），再一次啟動串接每個動畫的第 0 幀 PNG。
// 每次啟動 Spine 約 14 秒，所以全部動畫合併成同一次啟動。
public static class SpinePreviewExporter
{
    public static async Task<List<string>> ExportAsync(string spineCliPath, string spinePath, string ownerId,
        Action<CgToolProgress> onProgress, CancellationToken cancellationToken)
    {
        if (!File.Exists(spineCliPath))
            throw new FileNotFoundException($"找不到 Spine CLI：{spineCliPath}");

        var work = Path.Combine(Path.GetTempPath(), "StoryForgeSpine", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var jsonOut = Path.Combine(work, "json");
            var jsonSettings = Path.Combine(work, "json.export.json");
            File.WriteAllText(jsonSettings, JsonConvert.SerializeObject(new
            {
                @class = "export-json", extension = ".json", format = "JSON", prettyPrint = false, nonessential = false,
                cleanUp = true, packAtlas = (object?)null, packSource = "attachments", packTarget = "perskeleton", warnings = false
            }));
            await RunAsync(spineCliPath, new[] { "-i", spinePath, "-o", jsonOut, "-e", jsonSettings },
                $"{Path.GetFileName(spinePath)} · 讀取動畫清單", onProgress, cancellationToken);

            var skeletonJson = Directory.GetFiles(jsonOut, "*.json").FirstOrDefault()
                               ?? throw new InvalidOperationException("Spine 沒有輸出骨架 JSON。");
            var skeleton = JObject.Parse(File.ReadAllText(skeletonJson, Encoding.UTF8));
            var animations = (skeleton["animations"] as JObject)?.Properties().Select(x => x.Name).ToList() ?? new List<string>();
            if (animations.Count == 0)
                throw new InvalidOperationException("spine 沒有任何動畫。");

            var crop = FindBackgroundRect(skeleton);
            var args = new List<string>();
            for (var i = 0; i < animations.Count; i++)
            {
                var settingsPath = Path.Combine(work, $"png{i}.export.json");
                var scale = crop != null ? Math.Min(100.0, 100.0 * CgCatalogConstants.PreviewWidth * 2 / crop.Value.Width) : 25.0;
                File.WriteAllText(settingsPath, JsonConvert.SerializeObject(new
                {
                    @class = "export-png", exportType = "animation", skeletonType = "single", skeleton = (string?)null,
                    animationType = "single", animation = animations[i], skinType = "current", skinNone = false, skin = (string?)null,
                    maxBounds = false, renderImages = true, renderBones = false, renderOthers = false, scale,
                    fitWidth = 0, fitHeight = 0, enlarge = false, background = (string?)null, fps = 30, lastFrame = false,
                    cropX = crop?.X ?? 0, cropY = crop?.Y ?? 0, cropWidth = crop?.Width ?? 0, cropHeight = crop?.Height ?? 0,
                    rangeStart = 0, rangeEnd = 0, packAtlas = (object?)null, compression = 6, pad = false, msaa = 4, smoothing = 8,
                    renderSelection = false, cropToWindow = false
                }));
                args.AddRange(new[] { "-i", spinePath, "-o", Path.Combine(work, "png" + i), "-e", settingsPath });
            }

            await RunAsync(spineCliPath, args, $"{Path.GetFileName(spinePath)} · 輸出 {animations.Count} 個動畫預覽", onProgress, cancellationToken);

            var spineFolder = Path.GetDirectoryName(spinePath)!;
            for (var i = 0; i < animations.Count; i++)
            {
                var png = Directory.Exists(Path.Combine(work, "png" + i))
                    ? Directory.GetFiles(Path.Combine(work, "png" + i), "*.png").FirstOrDefault()
                    : null;
                if (png != null)
                    CgImageUtility.SaveLetterboxed(png, Path.Combine(spineFolder, CgNaming.PreviewFileName(ownerId, animations[i])),
                        CgCatalogConstants.PreviewWidth, CgCatalogConstants.PreviewHeight);
            }

            return animations;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* 暫存清不掉不影響結果 */ }
        }
    }

    private readonly record struct Rect(double X, double Y, double Width, double Height);

    // 遊戲鏡頭框 = 名為 bg 的 slot 上的圖（region 或 mesh）在世界座標的範圍。骨骼只加總位移（CG 的 bg 都掛在 root 附近）。
    private static Rect? FindBackgroundRect(JObject skeleton)
    {
        var bones = (skeleton["bones"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
        var byName = bones.ToDictionary(x => (string)x["name"]!, x => x);
        (double X, double Y) BoneOffset(string? name)
        {
            double x = 0, y = 0;
            while (name != null && byName.TryGetValue(name, out var bone))
            {
                x += (double?)bone["x"] ?? 0;
                y += (double?)bone["y"] ?? 0;
                name = (string?)bone["parent"];
            }
            return (x, y);
        }

        var slot = (skeleton["slots"] as JArray)?.OfType<JObject>()
            .FirstOrDefault(x => string.Equals((string?)x["name"], "bg", StringComparison.OrdinalIgnoreCase));
        if (slot == null)
            return null;

        var slotName = (string)slot["name"]!;
        var attachments = (skeleton["skins"] as JArray)?.OfType<JObject>()
            .Select(s => s["attachments"]?[slotName] as JObject)
            .FirstOrDefault(x => x != null);
        var attachment = attachments?.Properties().Select(p => p.Value as JObject).FirstOrDefault(x => x != null);
        if (attachment == null)
            return null;

        var type = (string?)attachment["type"] ?? "region";
        if (type == "region")
        {
            var (bx, by) = BoneOffset((string?)slot["bone"]);
            var w = ((double?)attachment["width"] ?? 0) * ((double?)attachment["scaleX"] ?? 1);
            var h = ((double?)attachment["height"] ?? 0) * ((double?)attachment["scaleY"] ?? 1);
            var cx = bx + ((double?)attachment["x"] ?? 0);
            var cy = by + ((double?)attachment["y"] ?? 0);
            return w > 0 && h > 0 ? new Rect(cx - w / 2, cy - h / 2, w, h) : null;
        }

        if (type != "mesh" || attachment["vertices"] is not JArray vertexArray)
            return null;

        var vertices = vertexArray.Select(x => (double)x).ToList();
        var uvCount = (attachment["uvs"] as JArray)?.Count ?? 0;
        var points = new List<(double X, double Y)>();
        if (vertices.Count == uvCount)
        {
            var (bx, by) = BoneOffset((string?)slot["bone"]);
            for (var i = 0; i + 1 < vertices.Count; i += 2)
                points.Add((bx + vertices[i], by + vertices[i + 1]));
        }
        else
        {
            // 加權網格：boneCount, (boneIndex, x, y, weight) * boneCount
            var index = 0;
            while (index < vertices.Count)
            {
                var count = (int)vertices[index++];
                double x = 0, y = 0;
                for (var b = 0; b < count; b++)
                {
                    var boneIndex = (int)vertices[index];
                    var (bx, by) = boneIndex < bones.Count ? BoneOffset((string?)bones[boneIndex]["name"]) : (0, 0);
                    var weight = vertices[index + 3];
                    x += (bx + vertices[index + 1]) * weight;
                    y += (by + vertices[index + 2]) * weight;
                    index += 4;
                }
                points.Add((x, y));
            }
        }

        if (points.Count == 0)
            return null;
        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        return new Rect(minX, minY, points.Max(p => p.X) - minX, points.Max(p => p.Y) - minY);
    }

    private static async Task RunAsync(string exe, IEnumerable<string> args, string stage, Action<CgToolProgress> onProgress,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        var output = new StringBuilder();
        var lastLine = "Spine 啟動中";
        var started = DateTime.UtcNow;
        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;
            lock (output)
                output.AppendLine(line);
            lastLine = line.Trim();
        }
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        while (!process.HasExited)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                CspBatchClient.TryKill(process);
                cancellationToken.ThrowIfCancellationRequested();
            }

            await Task.Delay(500, CancellationToken.None);
            onProgress(new CgToolProgress(stage, $"已 {(DateTime.UtcNow - started).TotalSeconds:0} 秒 · {lastLine}"));
        }

        process.WaitForExit();
        string log;
        lock (output)
            log = output.ToString();
        if (process.ExitCode != 0 || log.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Spine CLI 失敗（exit {process.ExitCode}）：{string.Join("\n", log.Split('\n').TakeLast(6))}");
    }
}
