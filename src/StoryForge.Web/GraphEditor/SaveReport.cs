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
    IReadOnlyList<OrphanContentFiles>? Orphans = null)
{
    public IReadOnlyList<OrphanContentFiles> OrphanFiles { get; } =
        Orphans ?? Array.Empty<OrphanContentFiles>();

    public bool SaveSucceeded => Outcome != SaveOutcome.Failure;
}

// 一個真正孤兒 Guid 所擁有的內容檔。BasePath 是不含副檔名的絕對路徑，RelativePaths 僅供確認畫面顯示。
// 清除前 GraphEditorState 會重新掃描並比對整份清單，不會直接信任彈窗保留的舊路徑。
public sealed record OrphanContentFiles(
    string Guid,
    string BasePath,
    IReadOnlyList<string> RelativePaths);
