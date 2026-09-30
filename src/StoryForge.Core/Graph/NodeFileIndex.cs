using Newtonsoft.Json;

namespace StoryForge.Core.Graph;

// GUID → 內容檔基底路徑（不含副檔名）的索引。NodeContentStore 的 .json 內容檔以劇本名稱命名方便人和外部 AI
// 直接開啟，但真正的配對依據是檔案內記錄的 Guid——劇本在 Unity、切換分支或其他工具中改名時，
// 檔名會暫時對不上，靠這份索引仍然能找到同一個節點的內容，不會變成「找不到、顯示空白」。
//
// 建立方式：掃描 Nodes\**\*.json 讀出每個檔案的 Guid。StoryForge 自己寫檔、搬檔時會同步更新；
// 查詢時若索引記錄的檔案已不存在或 Guid 對不上（外部改動），就整批重掃一次。
// 同一個 Guid 出現在多個檔案時（例如手動複製檔案），優先採用檔名符合劇本名稱的那個，其餘記在 Duplicates。
public static class NodeFileIndex
{
    private const string FolderName = "Nodes";
    private static readonly TimeSpan MissRescanInterval = TimeSpan.FromSeconds(2);

    private static readonly object Gate = new();
    private static string? _dataFolder;
    private static Dictionary<string, string> _basePathByGuid = new(StringComparer.Ordinal);
    private static List<string> _duplicates = new();
    private static HashSet<string> _duplicateGuids = new(StringComparer.Ordinal);
    private static DateTime _lastScanUtc = DateTime.MinValue;

    // 同一個 Guid 被多個檔案宣告時，沒被採用的那些檔案路徑
    public static IReadOnlyList<string> Duplicates
    {
        get
        {
            lock (Gate)
                return _duplicates.ToList();
        }
    }

    // 有兩個以上內容檔宣告同一個 Guid 時，該 Guid 的所有檔案都必須由使用者人工判斷；
    // 孤兒清除流程不得自動處理其中任何一個。
    public static IReadOnlySet<string> DuplicateGuids
    {
        get
        {
            lock (Gate)
                return new HashSet<string>(_duplicateGuids, StringComparer.Ordinal);
        }
    }

    public static string? TryGetBasePath(string dataFolder, string guid)
    {
        if (string.IsNullOrEmpty(guid))
            return null;

        lock (Gate)
        {
            EnsureFolder(dataFolder);

            if (_basePathByGuid.TryGetValue(guid, out var basePath))
            {
                if (IsStillOwnedBy(basePath, guid))
                    return basePath;

                // 檔案被外部移動、刪除或改寫了 Guid：索引過期，整批重掃
                Rescan(dataFolder);
                return _basePathByGuid.GetValueOrDefault(guid);
            }

            // 找不到時可能是外部剛新增的檔案；但大多數節點本來就沒有內容檔，所以限制重掃頻率
            if (DateTime.UtcNow - _lastScanUtc < MissRescanInterval)
                return null;

            Rescan(dataFolder);
            return _basePathByGuid.GetValueOrDefault(guid);
        }
    }

    // 索引中所有 Guid 與其檔案，用來找出流程圖上已不存在的孤兒內容檔
    public static IReadOnlyDictionary<string, string> GetAll(string dataFolder)
    {
        lock (Gate)
        {
            EnsureFolder(dataFolder);
            return new Dictionary<string, string>(_basePathByGuid, StringComparer.Ordinal);
        }
    }

    public static void Set(string dataFolder, string guid, string basePath)
    {
        if (string.IsNullOrEmpty(guid))
            return;

        lock (Gate)
        {
            EnsureFolder(dataFolder);
            _basePathByGuid[guid] = basePath;
        }
    }

    public static void Rebuild(string dataFolder)
    {
        lock (Gate)
        {
            _dataFolder = dataFolder;
            Rescan(dataFolder);
        }
    }

    private static void EnsureFolder(string dataFolder)
    {
        if (string.Equals(_dataFolder, dataFolder, StringComparison.OrdinalIgnoreCase))
            return;

        _dataFolder = dataFolder;
        Rescan(dataFolder);
    }

    private static void Rescan(string dataFolder)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var duplicates = new List<string>();
        var duplicateGuids = new HashSet<string>(StringComparer.Ordinal);
        var root = Path.Combine(dataFolder, FolderName);

        if (Directory.Exists(root))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var entry = TryRead(path);
                if (entry == null || string.IsNullOrEmpty(entry.Guid))
                    continue;

                var basePath = path[..^".json".Length];
                if (!result.TryGetValue(entry.Guid, out var existing))
                {
                    result[entry.Guid] = basePath;
                    continue;
                }

                // 同一個 Guid 有兩個檔案：檔名符合劇本名稱的優先
                var plain = NodeContentStore.ResolvePlainBasePath(dataFolder, entry.PlayscriptName);
                if (string.Equals(basePath, plain, StringComparison.OrdinalIgnoreCase))
                {
                    duplicates.Add(existing + ".json");
                    result[entry.Guid] = basePath;
                }
                else
                {
                    duplicates.Add(path);
                }

                duplicateGuids.Add(entry.Guid);
            }
        }

        _basePathByGuid = result;
        _duplicates = duplicates;
        _duplicateGuids = duplicateGuids;
        _lastScanUtc = DateTime.UtcNow;
    }

    private static bool IsStillOwnedBy(string basePath, string guid)
    {
        var entry = TryRead(basePath + ".json");
        return entry != null && string.Equals(entry.Guid, guid, StringComparison.Ordinal);
    }

    private static NodeContentEntry? TryRead(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonConvert.DeserializeObject<NodeContentEntry>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }
}
