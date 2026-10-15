using System.Text;
using Newtonsoft.Json;

namespace StoryForge.Core.Cg;

// Unity 專案內 Assets/Editor/CgImportPipeline/CgSpecialCases.json：「哪張 CG 讀哪個 prefab 的哪些差分」。
// Unity 的 CG 共用表視窗（CgShareTableWindow）編輯同一份檔，所以存檔一律以一列為單位：重新讀檔 → 只換這一列 → 寫回。
public sealed class CgSpecialCasesFile
{
    public const string RelativePath = "Assets/Editor/CgImportPipeline/CgSpecialCases.json";

    [JsonProperty("shares")] public List<CgShare> Shares { get; set; } = new();

    public static string GetPath(string unityProjectRoot) =>
        Path.Combine(unityProjectRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar));

    public static CgSpecialCasesFile Load(string unityProjectRoot)
    {
        var path = GetPath(unityProjectRoot);
        if (!File.Exists(path))
            return new CgSpecialCasesFile();

        return JsonConvert.DeserializeObject<CgSpecialCasesFile>(File.ReadAllText(path, Encoding.UTF8)) ?? new CgSpecialCasesFile();
    }

    // previousCgId：這一列原本的 CG 名稱（改名或刪除時用）；share 為 null 表示刪除。
    public static void SaveRow(string unityProjectRoot, string? previousCgId, CgShare? share)
    {
        var file = Load(unityProjectRoot);
        file.Shares.RemoveAll(x => x.CgId == previousCgId || (share != null && x.CgId == share.CgId));
        if (share != null)
            file.Shares.Add(share);
        file.Shares.Sort((a, b) => NaturalStringComparer.Instance.Compare(a.CgId, b.CgId));

        var path = GetPath(unityProjectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonConvert.SerializeObject(file, Formatting.Indented) + "\n", new UTF8Encoding(false));
    }
}

public sealed class CgShare
{
    [JsonProperty("cgId")] public string CgId { get; set; } = string.Empty;
    [JsonProperty("prefab")] public string Prefab { get; set; } = string.Empty;
    [JsonProperty("diffs")] public List<string> Diffs { get; set; } = new();
}

// AI劇本\CgTypes.json：只記被設為靜態圖的 CG（預設動態圖）。Unity 不讀，只看 prefab 類型。
public sealed class CgTypeSettings
{
    public const string FileName = "CgTypes.json";

    [JsonProperty("static")] public List<string> Static { get; set; } = new();

    public static CgTypeSettings Load(string externalDataFolder)
    {
        var path = Path.Combine(externalDataFolder, FileName);
        if (!File.Exists(path))
            return new CgTypeSettings();

        try
        {
            return JsonConvert.DeserializeObject<CgTypeSettings>(File.ReadAllText(path, Encoding.UTF8)) ?? new CgTypeSettings();
        }
        catch
        {
            return new CgTypeSettings();
        }
    }

    // 與 CgSpecialCases 同理，重讀再改，避免 AI 或另一個分頁同時改過。
    public static void SetStatic(string externalDataFolder, string cgId, bool isStatic)
    {
        var settings = Load(externalDataFolder);
        settings.Static.Remove(cgId);
        if (isStatic)
            settings.Static.Add(cgId);
        settings.Static.Sort(NaturalStringComparer.Instance);
        File.WriteAllText(Path.Combine(externalDataFolder, FileName),
            JsonConvert.SerializeObject(settings, Formatting.Indented) + "\n", new UTF8Encoding(false));
    }
}

// %LocalAppData%\StoryForge 下的 CG 分頁本機資料：章節篩選，以及 clip 構圖 / spine 動畫清單快取
// （依檔案大小＋修改時間失效）和每張 CG 上次輸出至 Unity 時的來源時間（判斷「來源較新」）。
public sealed class CgLocalState
{
    private const string FileName = "cg-manager.json";

    public List<string>? SelectedChapters { get; set; }

    // 上次存篩選時看過的章節；之後才出現的新章節預設勾選。
    public List<string>? KnownChapters { get; set; }
    public float PreviewSize { get; set; } = 320f;
    public Dictionary<string, CgDiffListCache> DiffLists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> ExportedSourceTimes { get; set; } = new(StringComparer.Ordinal);

    public static CgLocalState Load()
    {
        var path = GetPath();
        if (!File.Exists(path))
            return new CgLocalState();

        try
        {
            return JsonConvert.DeserializeObject<CgLocalState>(File.ReadAllText(path)) ?? new CgLocalState();
        }
        catch
        {
            return new CgLocalState();
        }
    }

    public void Save()
    {
        var path = GetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
    }

    public List<string>? GetCachedDiffs(string sourcePath)
    {
        if (!DiffLists.TryGetValue(sourcePath, out var cache) || !File.Exists(sourcePath))
            return null;

        var info = new FileInfo(sourcePath);
        return cache.Length == info.Length && cache.LastWriteUtc == info.LastWriteTimeUtc ? cache.Diffs : null;
    }

    public void SetCachedDiffs(string sourcePath, List<string> diffs)
    {
        var info = new FileInfo(sourcePath);
        DiffLists[sourcePath] = new CgDiffListCache { Length = info.Length, LastWriteUtc = info.LastWriteTimeUtc, Diffs = diffs };
    }

    private static string GetPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StoryForge", FileName);
}

public sealed class CgDiffListCache
{
    public long Length { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public List<string> Diffs { get; set; } = new();
}
