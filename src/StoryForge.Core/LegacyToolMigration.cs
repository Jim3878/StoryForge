using Newtonsoft.Json.Linq;

namespace StoryForge.Core;

// One-time best-effort copy of the old WinForms PlayscriptOfflineTool's per-machine LocalAppData files
// into StoryForge's own — the two tools use different app-name subfolders under %LocalAppData%, so nothing
// here is shared automatically the way the Unity-project-tracked files (StoryOutline.json,
// CharacterCards.json) already are.
//
// This MUST run inside the real app process (called once from Program.cs at startup), never performed by
// an external tool reaching into %LocalAppData% from outside — that folder is subject to Windows'
// per-process file virtualization for sandboxed tools, which silently redirects reads/writes to a
// container-private overlay invisible to this app's own real process. A migration "performed" that way
// looks like it succeeded (a follow-up read from the same sandboxed tool sees its own overlay write) but
// never actually reaches the file this app itself opens.
//
// Gated by a marker file so it only ever runs once — without that, every app restart would re-overwrite
// whatever the user has since tuned via the Settings dialog with the old tool's frozen values.
public static class LegacyToolMigration
{
    private const string LegacyFolderName = "PlayscriptOfflineTool";
    private const string CurrentFolderName = "StoryForge";
    private const string MarkerFileName = "legacy-migration-done.txt";

    public static void MigrateIfNeeded()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var legacyFolder = Path.Combine(localAppData, LegacyFolderName);
            var currentFolder = Path.Combine(localAppData, CurrentFolderName);
            var markerPath = Path.Combine(currentFolder, MarkerFileName);

            if (File.Exists(markerPath) || !Directory.Exists(legacyFolder))
                return;

            Directory.CreateDirectory(currentFolder);

            CopyIfMissing(Path.Combine(legacyFolder, "portrait-descriptions.json"), Path.Combine(currentFolder, "portrait-descriptions.json"));
            CopyIfMissing(Path.Combine(legacyFolder, "portrait-filter.json"), Path.Combine(currentFolder, "portrait-filter.json"));
            CopyIfMissing(Path.Combine(legacyFolder, "pipeline-filter.json"), Path.Combine(currentFolder, "pipeline-filter.json"));
            MergeSettingsJson(legacyFolder, currentFolder);

            File.WriteAllText(markerPath,
                $"Migrated from {legacyFolder} at {DateTime.Now:O}. Delete this file to allow the migration to run again.");
        }
        catch
        {
            // Best-effort — worst case the user re-enters things by hand via the Settings dialog.
        }
    }

    private static void CopyIfMissing(string sourcePath, string destPath)
    {
        if (File.Exists(sourcePath) && !File.Exists(destPath))
            File.Copy(sourcePath, destPath);
    }

    // settings.json needs field-level merging rather than a straight copy: AppSettings' schema has moved on
    // since the WinForms tool (NodeMaxTextSize/GroupMaxTextSize/InspectorPanelWidth/MemoFieldHeight are new
    // here and don't exist there; AiReferencePrompt/AiReferenceSelectedCharacters/AiReferenceOtherCharacters
    // existed there but were removed here — the first no longer has an equivalent (writing guidelines now
    // live only in AGENTS.md, see AgentsMdStore), the other two moved into CharacterCardSettings' own
    // separate migration). Legacy values win for every overlapping field since they reflect real day-to-day
    // tuning, not this tool's stock defaults.
    private static void MergeSettingsJson(string legacyFolder, string currentFolder)
    {
        var legacyPath = Path.Combine(legacyFolder, "settings.json");
        if (!File.Exists(legacyPath))
            return;

        var legacyRoot = JObject.Parse(File.ReadAllText(legacyPath));

        var currentPath = Path.Combine(currentFolder, "settings.json");
        var current = File.Exists(currentPath)
            ? JObject.Parse(File.ReadAllText(currentPath))
            : new JObject();

        foreach (var key in new[]
                 {
                     "Padding", "TitleBarHeight", "NodeFrozenTextSize", "NodeMinTextZoomBeforeShrinkResumes",
                     "GroupFrozenTextSize", "GroupMinTextZoomBeforeShrinkResumes", "PortraitPreviewSize",
                     "PortraitTopCutPercent", "PortraitBottomCutPercent", "SearchMinZoom", "WireSnapDistance",
                 })
        {
            if (legacyRoot[key] != null)
                current[key] = legacyRoot[key];
        }

        File.WriteAllText(currentPath, current.ToString(Newtonsoft.Json.Formatting.Indented));
    }
}
