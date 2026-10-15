namespace StoryForge.Core.Cg;

public sealed class CgSourceFiles
{
    public List<string> Clips { get; } = new();
    public List<string> Spines { get; } = new();

    // 差分 → 找到的 png（可能多個 = 重複）。
    public Dictionary<string, List<string>> Pngs { get; } = new(StringComparer.Ordinal);
}

// 在 CG 根目錄遞迴找 {CG}.clip / {CG}.spine / {CG}_{差分}.png，只依檔名（見 CgNaming）。
// 一次走完整個目錄，之後每張 CG 查表。
public sealed class CgSourceIndex
{
    private readonly Dictionary<string, List<string>> _clips = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _spines = new(StringComparer.Ordinal);
    private readonly List<(string Path, string Name)> _pngs = new();

    public string RootFolder { get; }
    public bool RootExists { get; }

    public CgSourceIndex(string rootFolder)
    {
        RootFolder = rootFolder;
        RootExists = Directory.Exists(rootFolder);
        if (!RootExists)
            return;

        foreach (var file in Directory.EnumerateFiles(rootFolder, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            var name = Path.GetFileNameWithoutExtension(file);
            switch (extension)
            {
                case ".clip":
                    Add(_clips, CgNaming.Normalize(name), file);
                    break;
                case ".spine":
                    Add(_spines, CgNaming.Normalize(name), file);
                    break;
                case ".png" when !CgNaming.IsPreviewFile(file):
                    _pngs.Add((file, PrefixedName(name)));
                    break;
            }
        }
    }

    // cgIds：所有已知 CG（劇本 + 共用表），png 依最長的 CG 名稱前綴配對。
    public Dictionary<string, CgSourceFiles> Resolve(IEnumerable<string> cgIds)
    {
        var result = new Dictionary<string, CgSourceFiles>(StringComparer.Ordinal);
        var keys = new List<(string CgId, string Key)>();
        foreach (var cgId in cgIds.Distinct(StringComparer.Ordinal))
        {
            var key = CgNaming.Normalize(cgId);
            var files = new CgSourceFiles();
            if (_clips.TryGetValue(key, out var clips))
                files.Clips.AddRange(clips);
            if (_spines.TryGetValue(key, out var spines))
                files.Spines.AddRange(spines);
            result[cgId] = files;
            keys.Add((cgId, key));
        }

        keys.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
        foreach (var (path, name) in _pngs)
        {
            foreach (var (cgId, key) in keys)
            {
                if (name.Length <= key.Length + 1 ||
                    !name.StartsWith(key + "_", StringComparison.OrdinalIgnoreCase))
                    continue;

                var diff = name[(key.Length + 1)..];
                if (diff.Equals("thumbnail", StringComparison.OrdinalIgnoreCase))
                    break;

                var pngs = result[cgId].Pngs;
                if (!pngs.TryGetValue(diff, out var list))
                    pngs[diff] = list = new List<string>();
                list.Add(path);
                break;
            }
        }

        return result;
    }

    // 保留原本大小寫（差分名稱區分大小寫），但套用零寬字元 / '-' / CG_ 前綴的容錯。
    private static string PrefixedName(string name)
    {
        var cleaned = CgNaming.StripZeroWidth(name).Trim().Replace('-', '_');
        return cleaned.StartsWith("CG_", StringComparison.OrdinalIgnoreCase) ? cleaned : "CG_" + cleaned;
    }

    private static void Add(Dictionary<string, List<string>> index, string key, string path)
    {
        if (!index.TryGetValue(key, out var list))
            index[key] = list = new List<string>();
        list.Add(path);
    }

    public IEnumerable<string> AllClipPaths => _clips.Values.SelectMany(x => x);
    public IEnumerable<string> AllSpinePaths => _spines.Values.SelectMany(x => x);
}
