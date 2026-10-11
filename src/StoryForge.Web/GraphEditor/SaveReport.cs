namespace StoryForge.Web.GraphEditor;

public enum SaveOutcome
{
    Success,
    Warning,
    Failure,
}

// 一次存檔的結果：顯示在存檔完成的彈窗（SaveResultDialog）並決定提示音。
// Details 是給人看的紀錄，每一項一行，例如節點數量、存檔位置、孤兒內容檔、改名失敗的項目。
public sealed record SaveReport(
    SaveOutcome Outcome,
    string Title,
    IReadOnlyList<string> Details,
    IReadOnlyList<OrphanContentFiles>? Orphans = null,
    IReadOnlyList<RenameCollision>? Collisions = null)
{
    public IReadOnlyList<OrphanContentFiles> OrphanFiles { get; } =
        Orphans ?? Array.Empty<OrphanContentFiles>();

    public IReadOnlyList<RenameCollision> RenameCollisions { get; } =
        Collisions ?? Array.Empty<RenameCollision>();

    public bool SaveSucceeded => Outcome != SaveOutcome.Failure;
}

// 一個真正孤兒 Guid 所擁有的內容檔。BasePath 是不含副檔名的絕對路徑，RelativePaths 僅供確認畫面顯示。
// 清除前 GraphEditorState 會重新掃描並比對整份清單，不會直接信任彈窗保留的舊路徑。
public sealed record OrphanContentFiles(
    string Guid,
    string BasePath,
    IReadOnlyList<string> RelativePaths);

// 存檔時節點改名、要把內容檔搬到新名稱，但新名稱的位置已經有檔案時的一筆衝突。
// 不會自動覆蓋；存檔結果彈窗逐項讓使用者選擇處理方式（RenameCollisionChoice），套用前 GraphEditorState 會重新掃描比對。
public enum RenameCollisionKind
{
    // 新名稱位置只有 .md 劇本檔、沒有 .json（例如先從 Sheet 下載或 AI 工具直接產生）
    TargetScriptOnly,
    // 新名稱位置是孤兒內容檔（流程圖上已沒有它的節點）
    TargetOrphan,
    // 新名稱位置屬於流程圖上另一個仍存在的節點：不能覆蓋，只能先改成不同的名稱
    TargetLiveNode,
}

public enum RenameCollisionChoice
{
    Skip,
    // 保留新名稱位置的 .md 劇本，把節點的 .json 接過去；節點原本的 .md（與孤兒 .json）移到資源回收筒
    KeepTarget,
    // 用節點目前的內容檔覆蓋；新名稱位置原本的檔案移到資源回收筒
    Overwrite,
}

// 一個內容檔的摘要，給使用者比較兩邊哪一份才是要留下的內容
public sealed record ContentFileInfo(string RelativePath, long Size, DateTime Modified, string Summary);

public sealed record RenameCollision(
    string Guid,
    string CurrentBase,
    string TargetBase,
    string CurrentRelativeBase,
    string TargetRelativeBase,
    string NewName,
    RenameCollisionKind Kind,
    IReadOnlyList<ContentFileInfo> CurrentFiles,
    IReadOnlyList<ContentFileInfo> TargetFiles)
{
    public bool CanResolve => Kind != RenameCollisionKind.TargetLiveNode;

    public string Title => $"{CurrentRelativeBase} → {NewName}";

    public string Reason => Kind switch
    {
        RenameCollisionKind.TargetScriptOnly => "新名稱的位置已經有劇本檔（.md），但沒有對應的節點資料。",
        RenameCollisionKind.TargetOrphan => "新名稱的位置是孤兒內容檔（流程圖上已經沒有它的節點）。",
        _ => "新名稱的位置屬於流程圖上另一個節點，請先在流程圖上改成不同的名稱。",
    };

    // 確認畫面上列出的實際檔案操作
    public IEnumerable<string> PlannedActions(RenameCollisionChoice choice)
    {
        var targetFiles = TargetFiles.Select(f => f.RelativePath);
        var currentMd = CurrentFiles.Select(f => f.RelativePath).Where(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
        var targetJson = targetFiles.Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

        var recycled = choice == RenameCollisionChoice.Overwrite ? targetFiles : currentMd.Concat(targetJson);
        foreach (var path in recycled)
            yield return $"移到資源回收筒：{path}";

        yield return $"搬移：{CurrentRelativeBase}.json → {TargetRelativeBase}.json";
        if (choice == RenameCollisionChoice.Overwrite && currentMd.Any())
            yield return $"搬移：{CurrentRelativeBase}.md → {TargetRelativeBase}.md";
    }

    public bool SameFilesAs(RenameCollision other) =>
        Guid == other.Guid && Kind == other.Kind &&
        string.Equals(CurrentBase, other.CurrentBase, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(TargetBase, other.TargetBase, StringComparison.OrdinalIgnoreCase) &&
        CurrentFiles.SequenceEqual(other.CurrentFiles) && TargetFiles.SequenceEqual(other.TargetFiles);
}

public sealed record RenameCollisionDecision(RenameCollision Collision, RenameCollisionChoice Choice);
