namespace StoryForge.Core.StoryOutline;

// AGENTS.md, at the root of AppSettings.ExternalDataFolder, is the one place all writing rules and
// technical playscript conventions live — both what an external Codex/Claude agent auto-loads by that
// filename convention, and what the App's own 世界書 panel edits (see StoryOutlineState). Plain text, not
// part of StoryOutlineSettings' JSON — a separate file so the agent-side auto-load convention keeps working.
public static class AgentsMdStore
{
    private const string FileName = "AGENTS.md";

    public static string LoadOrDefault(string dataFolder)
    {
        var path = GetFilePath(dataFolder);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    public static void Save(string dataFolder, string content)
    {
        var path = GetFilePath(dataFolder);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, content);
    }

    private static string GetFilePath(string dataFolder)
    {
        return Path.Combine(dataFolder, FileName);
    }
}
