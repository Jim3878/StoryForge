using Newtonsoft.Json;
using StoryForge.Core.Graph;

namespace StoryForge.Core.Cloud;

public sealed class WaitMergeState
{
    public string Guid { get; set; } = string.Empty;
    public string PlayscriptName { get; set; } = string.Empty;
    // The working file Claude edits in place (the playscript's normal Nodes/<章節>/<名稱>.md).
    public string WorkingPath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

// Wait merge: a both-sides-changed playscript parked for an external (AI) merge, as files an AI can read:
//   <ExternalDataFolder>\Conflicts\<guid>\base.md     last synced version (empty table if never synced)
//                                        \mine.md     the local version when the wait started
//                                        \theirs.md   the cloud version (refreshed if the cloud changes again)
//                                        \state.json  WaitMergeState
//                                        \README.md   what to do, for the AI
// The folder is keyed by node guid so a rename can't orphan it. While a folder exists the playscript is
// read-only inside StoryForge (no upload, no Sheet download over it), and 匯出成C# is blocked as a whole.
public static class WaitMergeStore
{
    private const string FolderName = "Conflicts";

    public static string Root(string dataFolder) => Path.Combine(dataFolder, FolderName);

    public static string FolderFor(string dataFolder, string guid) => Path.Combine(Root(dataFolder), guid);

    public static IReadOnlyList<WaitMergeState> All(string dataFolder)
    {
        var root = Root(dataFolder);
        if (!Directory.Exists(root))
            return Array.Empty<WaitMergeState>();

        var list = new List<WaitMergeState>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            var statePath = Path.Combine(dir, "state.json");
            if (!File.Exists(statePath))
                continue;
            try
            {
                var state = JsonConvert.DeserializeObject<WaitMergeState>(File.ReadAllText(statePath));
                if (state != null)
                    list.Add(state);
            }
            catch
            {
                // A broken state file still blocks nothing; the folder stays for the user to look at.
            }
        }

        return list.OrderBy(s => s.PlayscriptName, StringComparer.Ordinal).ToList();
    }

    public static bool IsWaiting(string dataFolder, string? guid) =>
        !string.IsNullOrEmpty(guid) && File.Exists(Path.Combine(FolderFor(dataFolder, guid), "state.json"));

    public static void Start(string dataFolder, string guid, string playscriptName, string workingPath,
        IReadOnlyList<string[]> baseRows, IReadOnlyList<string[]> mine, IReadOnlyList<string[]> theirs)
    {
        var dir = FolderFor(dataFolder, guid);
        Directory.CreateDirectory(dir);
        NodeScriptTableStore.WriteContentAt(Path.Combine(dir, "base.md"), playscriptName, CloudRows.Normalize(baseRows));
        NodeScriptTableStore.WriteContentAt(Path.Combine(dir, "mine.md"), playscriptName, CloudRows.Normalize(mine));
        NodeScriptTableStore.WriteContentAt(Path.Combine(dir, "theirs.md"), playscriptName, CloudRows.Normalize(theirs));
        File.WriteAllText(Path.Combine(dir, "README.md"), BuildReadme(playscriptName, workingPath));
        var state = new WaitMergeState { Guid = guid, PlayscriptName = playscriptName, WorkingPath = workingPath, CreatedAt = DateTime.Now };
        File.WriteAllText(Path.Combine(dir, "state.json"), JsonConvert.SerializeObject(state, Formatting.Indented));
    }

    public static List<string[]> ReadBase(string dataFolder, string guid) => Read(dataFolder, guid, "base.md");
    public static List<string[]> ReadMine(string dataFolder, string guid) => Read(dataFolder, guid, "mine.md");
    public static List<string[]> ReadTheirs(string dataFolder, string guid) => Read(dataFolder, guid, "theirs.md");

    // The cloud changed again during the wait: latest cloud becomes theirs, the current merge result becomes mine.
    public static void Restack(string dataFolder, string guid, string playscriptName,
        IReadOnlyList<string[]> newMine, IReadOnlyList<string[]> newTheirs)
    {
        var dir = FolderFor(dataFolder, guid);
        NodeScriptTableStore.WriteContentAt(Path.Combine(dir, "mine.md"), playscriptName, CloudRows.Normalize(newMine));
        NodeScriptTableStore.WriteContentAt(Path.Combine(dir, "theirs.md"), playscriptName, CloudRows.Normalize(newTheirs));
    }

    public static void Finish(string dataFolder, string guid)
    {
        var dir = FolderFor(dataFolder, guid);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    private static List<string[]> Read(string dataFolder, string guid, string file) =>
        CloudRows.Normalize(NodeScriptTableStore.ReadRowsFromFile(Path.Combine(FolderFor(dataFolder, guid), file), keepBlankRows: true));

    private static string BuildReadme(string playscriptName, string workingPath) => $"""
        # 等待合併：{playscriptName}

        這份劇本在本機和雲端都被改過，正在等待合併。

        - `base.md`：上次同步時的版本（共同祖先）
        - `mine.md`：本機的版本
        - `theirs.md`：雲端的版本

        請把合併結果**直接寫進工作檔**：

            {workingPath}

        規則：
        - 保留兩邊各自的修改；同一段兩邊改得不一樣時，依劇情判斷，拿不定就停下來問使用者。
        - 格式照 AGENTS.md：七欄表格，最上與最下各留一個全空白列，功能／參數照規則填。
        - 不要改這個資料夾裡的檔案。

        改完後回到 StoryForge「雲端同步」分頁，按這份劇本的「繼續合併」。
        """;
}
