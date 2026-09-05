using StoryForge.Core;
using StoryForge.Core.CSharpSource;
using StoryForge.Core.GoogleSheet;
using StoryForge.Core.Ldtk;
using StoryForge.Core.Pipeline;
using StoryForge.Web.GraphEditor;

namespace StoryForge.Web.Pipeline;

public sealed class PipelineStatusRow
{
    public string Chapter { get; init; } = string.Empty;
    public string PlayscriptName { get; init; } = string.Empty;
    public bool IsOnLdtk { get; init; }
    public bool IsOnGraph { get; init; }

    // "Sheet目錄" — listed in the index sheet at all.
    public bool IsOnSheet { get; init; }

    // "Sheet檔案" — that listing already has its own dedicated GoogleSheet spreadsheet (a real link) AND
    // isn't marked "空" (still literally empty) in whichever empty-check column that chapter's tab has.
    public bool HasSheetFile { get; init; }
    public bool IsOnCSharp { get; init; }
}

// Scoped per circuit. LDtk/C# are plain local-file scans, re-run on every Refresh(). The Sheet source
// needs its own local xlsx (Assets/11.Other/Sheet/Playscript/PlaylistIndex.xlsx) — there's no such file
// until DownloadSheetAsync has been run at least once, mirroring the original tool's own 下載Sheet flow.
public sealed class PipelineStatusState
{
    private readonly GraphEditorState _graphState;
    private string? _projectRoot;
    private Dictionary<string, GooglePlayscriptSheetEntry> _sheetEntries = new();

    public PipelineStatusState(GraphEditorState graphState)
    {
        _graphState = graphState;
    }

    public List<PipelineStatusRow> Rows { get; private set; } = new();
    public string StatusMessage { get; private set; } = string.Empty;

    public void Refresh()
    {
        try
        {
            _projectRoot = ProjectPaths.ResolveUnityProjectRoot();
            var assetsRoot = Path.Combine(_projectRoot, "Assets");

            var ldtkPath = Path.Combine(assetsRoot, "12.Custom", "Company.ldtk");
            var ldtkNames = File.Exists(ldtkPath)
                ? LdtkPlayscriptScanner.ScanFile(ldtkPath)
                : new HashSet<string>();

            var factoryFolder = Path.Combine(assetsRoot, "12.Custom", "PlayscriptFactory");
            var cSharpNames = CSharpFactoryScanner.ScanFolder(factoryFolder);

            var sheetPath = Path.Combine(assetsRoot, GooglePlayscriptSheetClient.GooglePlayscriptFolder,
                GooglePlayscriptSheetClient.GooglePlayscriptFileName);
            var hasSheetFile = File.Exists(sheetPath);
            _sheetEntries = hasSheetFile ? GooglePlayscriptSheetClient.LoadEntries(sheetPath) : new();

            var graphNames = _graphState.GetPlayscriptIdentities().ToList();

            var infos = PlayscriptPipelineComparer.Compare(
                ldtkNames, graphNames, _sheetEntries.Keys, cSharpNames);

            Rows = infos
                .Select(info => new PipelineStatusRow
                {
                    Chapter = info.Chapter,
                    PlayscriptName = info.PlayscriptName,
                    IsOnLdtk = info.IsOnLdtk,
                    IsOnGraph = info.IsOnGraph,
                    IsOnSheet = info.IsOnSheet,
                    HasSheetFile = _sheetEntries.TryGetValue(info.PlayscriptName, out var entry) &&
                                   entry.SpreadsheetLink != null && !entry.IsMarkedEmpty,
                    IsOnCSharp = info.IsOnCSharp,
                })
                .ToList();

            var missing = new List<string>();
            if (!File.Exists(ldtkPath)) missing.Add("找不到LDtk檔案");
            if (!hasSheetFile) missing.Add("Sheet尚未下載");
            var suffix = missing.Count > 0 ? $"；{string.Join("，", missing)}" : string.Empty;

            StatusMessage = $"共 {Rows.Count} 個劇本名稱（LDtk={ldtkNames.Count}, 流程圖={graphNames.Count}, " +
                            $"Sheet={_sheetEntries.Count}, C#={cSharpNames.Count}）{suffix}";
        }
        catch (Exception e)
        {
            Rows = new();
            StatusMessage = $"讀取失敗：{e.Message}";
        }
    }

    public async Task DownloadSheetAsync()
    {
        if (_projectRoot == null)
        {
            StatusMessage = "請先按重新整理一次。";
            return;
        }

        try
        {
            StatusMessage = "正在下載 Google Sheet...";
            var assetsRoot = Path.Combine(_projectRoot, "Assets");
            await GooglePlayscriptSheetClient.DownloadAsync(assetsRoot);
            Refresh();
        }
        catch (Exception e)
        {
            StatusMessage = $"Sheet 下載失敗：{e.Message}";
        }
    }
}
