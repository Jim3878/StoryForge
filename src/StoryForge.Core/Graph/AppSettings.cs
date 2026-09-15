using Newtonsoft.Json;

namespace StoryForge.Core.Graph;

// The app's one persisted settings bag — every user-configurable value lives here, not in separate files.
public sealed class AppSettings
{
    private const string SettingsFileName = "settings.json";

    // Defaults reverse-engineered from real PlayscriptProcessor group data: the left/right/bottom gap
    // GraphProcessor's own GroupView leaves around its member nodes, and the header/title bar height.
    public float Padding { get; set; } = 25.6f;
    public float TitleBarHeight { get; set; } = 60f;

    // Node identity text and group titles each stop shrinking once their normal (zoom-proportional) size
    // would drop below their own floor, holding at that fixed pixel size instead — until zoom drops below
    // the matching MinTextZoomBeforeShrinkResumes, at which point they resume shrinking normally (ending
    // up smaller than the floor again), so a fully-zoomed-out whole-graph view doesn't turn into
    // overlapping text. Kept as separate settings per element type since node identity text and group
    // titles read comfortably at different sizes.
    public float NodeFrozenTextSize { get; set; } = 9f;
    public float NodeMinTextZoomBeforeShrinkResumes { get; set; } = 0.05f;
    public float GroupFrozenTextSize { get; set; } = 9.5f;
    public float GroupMinTextZoomBeforeShrinkResumes { get; set; } = 0.05f;

    // The other end of the same range: caps how large node/group title text is allowed to render as the
    // canvas zooms in, so a user zoomed in close doesn't end up with oversized text dominating the view.
    // Applies to a node's title only when it's a real identity value (playscriptId/flagId) — a node with
    // no identity value yet (a type's generic display name, like "結束劇本"/"AND", or a just-created empty
    // node) stays at NodeFrozenTextSize always, never growing, per explicit user feedback that only an
    // actual playscript name should enlarge, not a generic node-type label.
    public float NodeMaxTextSize { get; set; } = 32f;
    public float GroupMaxTextSize { get; set; } = 40f;

    // Portrait browser thumbnail tuning — how much of each source image's height gets cropped away from
    // the top/bottom before the remaining middle band is shown, plus the on-screen thumbnail tile size.
    public float PortraitPreviewSize { get; set; } = 128f;
    public float PortraitTopCutPercent { get; set; } = 5f;
    public float PortraitBottomCutPercent { get; set; } = 60f;

    // Jumping to a search match never leaves zoom below this — a graph zoomed way out to see everything at
    // once would otherwise land on a match too tiny to actually read.
    public float SearchMinZoom { get; set; } = 0.5f;

    // While dragging a wire out of a port, a compatible port within this world-space radius snaps the
    // preview line to it as visual feedback that a connection would land there if released now.
    public float WireSnapDistance { get; set; } = 40f;

    // Root of the standalone external folder that IS the real save location for the flow graph
    // (session.json/connections.json), CharacterCardSettings, and StoryOutlineSettings — not a copy or an
    // export, the one place those are read from and written to. Independent of both the Unity project and
    // %LocalAppData%, specifically so an external AI tool (a Codex/Claude project pointed at this path) can
    // read and edit the same files directly, with nothing to import back afterward.
    public string ExternalDataFolder { get; set; } = @"E:\本地端\遊戲專案管理\臥底治安官\AI劇本\";

    // "定位到LDtk" launches this with [project.ldtk path, "--goto-level=<iid>", "--goto-entity=<iid>"] —
    // LDtk's own single-instance lock forwards those args to an already-running instance instead of opening
    // a second one. LdtkWorkingDirectory is only needed for the current dev-mode launch (running LDtk
    // straight from its Electron dev build via "electron.exe .", which needs its cwd set to the app folder
    // that "." resolves against) — leave it empty once pointing this at a real packaged LDtk.exe, which
    // needs neither a working directory nor the "." argument PipelineStatusState only adds when this is set.
    public string LdtkExecutablePath { get; set; } = @"E:\ldtk\app\node_modules\electron\dist\electron.exe";
    public string LdtkWorkingDirectory { get; set; } = @"E:\ldtk\app";

    // ProcessGraphPanel's inspector sidebar — remembered so a user-dragged size doesn't reset every time
    // the tool reopens or a different node is selected.
    public float InspectorPanelWidth { get; set; } = 260f;
    public float MemoFieldHeight { get; set; } = 80f;

    public static AppSettings LoadOrDefault()
    {
        var path = GetSettingsFilePath();
        DebugLog($"LoadOrDefault: resolved path = [{path}], LocalApplicationData = [{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}]");
        if (!File.Exists(path))
        {
            DebugLog($"LoadOrDefault: file does not exist at {path}");
            return new AppSettings();
        }

        DebugLog($"LoadOrDefault: file exists, LastWriteTime={File.GetLastWriteTime(path):O}, Length={new FileInfo(path).Length}");

        try
        {
            var json = File.ReadAllText(path);
            DebugLog($"LoadOrDefault: raw file content = {json}");
            var result = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            DebugLog($"LoadOrDefault: deserialized NodeFrozenTextSize = {result.NodeFrozenTextSize}");
            return result;
        }
        catch (Exception e)
        {
            DebugLog($"LoadOrDefault: EXCEPTION {e}");
            return new AppSettings();
        }
    }

    private static void DebugLog(string message)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StoryForge", "circuit-diagnostics.log");
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never be the thing that crashes the circuit.
        }
    }

    public void Save()
    {
        var path = GetSettingsFilePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
    }

    private static string GetSettingsFilePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StoryForge",
            SettingsFileName);
    }
}
