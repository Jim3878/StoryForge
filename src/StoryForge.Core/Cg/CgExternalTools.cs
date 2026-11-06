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

    // Unity CG 鏡頭在 Spine 骨架座標（像素）的可見範圍，以骨架原點為中心，不含出血。
    // 由 Unity 端換算：鏡頭 orthographicSize 5（高 10 單位）、SkeletonDataAsset scale 0.01、Spine 子物件縮放 0.5
    // → 10 / (0.01 * 0.5) = 2000；寬依 1920:1080。
    public const double SpineCameraHeight = 2000;
    public const double SpineCameraWidth = SpineCameraHeight * 1920 / 1080;
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

// Spine 預覽：Spine CLI 先匯出骨架 JSON 取得動畫清單，再一次啟動串接每個動畫的第 0 幀 PNG（裁成遊戲鏡頭範圍）。
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
                @class = "export-json", extension = ".json", format = "JSON", prettyPrint = false, nonessential = true,
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

            // PNG 從改過的 JSON 輸出（不是 .spine）：遮擋用的 censor_* slot 不進預覽。
            // images 是相對 .spine 的路徑，JSON 換了位置要改成絕對路徑，否則圖全部變成 MISSING。
            var spineFolder = Path.GetDirectoryName(spinePath)!;
            if (skeleton["skeleton"] is JObject info)
                info["images"] = Path.GetFullPath(Path.Combine(spineFolder, (string?)info["images"] ?? "./images/"));
            HideCensorSlots(skeleton);
            var previewJson = Path.Combine(work, Path.GetFileNameWithoutExtension(spinePath) + ".json");
            File.WriteAllText(previewJson, skeleton.ToString(Formatting.None), new UTF8Encoding(false));

            // 只裁遊戲鏡頭看得到的範圍；bg 圖本身含出血，不能拿來當裁切框
            const double cropWidth = CgCatalogConstants.SpineCameraWidth;
            const double cropHeight = CgCatalogConstants.SpineCameraHeight;
            var scale = Math.Min(100.0, 100.0 * CgCatalogConstants.PreviewWidth * 2 / cropWidth);

            var args = new List<string>();
            for (var i = 0; i < animations.Count; i++)
            {
                var settingsPath = Path.Combine(work, $"png{i}.export.json");
                File.WriteAllText(settingsPath, JsonConvert.SerializeObject(new
                {
                    @class = "export-png", exportType = "animation", skeletonType = "single", skeleton = (string?)null,
                    animationType = "single", animation = animations[i], skinType = "current", skinNone = false, skin = (string?)null,
                    maxBounds = false, renderImages = true, renderBones = false, renderOthers = false, scale,
                    fitWidth = 0, fitHeight = 0, enlarge = false, background = (string?)null, fps = 30, lastFrame = false,
                    cropX = -cropWidth / 2, cropY = -cropHeight / 2, cropWidth, cropHeight,
                    rangeStart = 0, rangeEnd = 0, packAtlas = (object?)null, compression = 6, pad = false, msaa = 4, smoothing = 8,
                    renderSelection = false, cropToWindow = false
                }));
                // 輸出資料夾要先存在：不存在時 Spine 會把 -o 當成檔名前綴，寫成上一層的 png{i}_0.png。
                var pngOut = Path.Combine(work, "png" + i);
                Directory.CreateDirectory(pngOut);
                args.AddRange(new[] { "-i", previewJson, "-o", pngOut, "-e", settingsPath });
            }

            await RunAsync(spineCliPath, args, $"{Path.GetFileName(spinePath)} · 輸出 {animations.Count} 個動畫預覽", onProgress, cancellationToken);

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

    // 名稱 censor_ 開頭的 slot：顏色 alpha 設 0，並拿掉動畫裡的顏色時間軸（否則動畫會把 alpha 打回來）。
    // 不刪 slot 本身，避免動畫 / 繪製順序 / 剪裁參照到不存在的 slot。
    private static void HideCensorSlots(JObject skeleton)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in (skeleton["slots"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            var name = (string?)slot["name"];
            if (name == null || !name.StartsWith("censor_", StringComparison.OrdinalIgnoreCase))
                continue;
            names.Add(name);
            var color = (string?)slot["color"] ?? "ffffffff";
            slot["color"] = color[..6] + "00";
        }

        foreach (var animation in (skeleton["animations"] as JObject)?.Properties().Select(p => p.Value).OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            foreach (var name in names)
            {
                if (animation["slots"]?[name] is not JObject timelines)
                    continue;
                foreach (var key in new[] { "rgba", "rgb", "alpha", "rgba2", "rgb2" })
                    timelines.Remove(key);
            }
        }
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
