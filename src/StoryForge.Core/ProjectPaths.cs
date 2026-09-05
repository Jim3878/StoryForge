namespace StoryForge.Core;

public static class ProjectPaths
{
    private const string OverrideFileName = "project-root-override.txt";

    // StoryForge lives in its own repo, separate from the Unity project it reads/writes Assets data
    // for — unlike the old PlayscriptOfflineTool (nested inside the Unity project, so it could find the
    // project root by walking up parent directories), there's no directory relationship to walk up from
    // here. The override file is the only mechanism now, not a fallback.
    public static string ResolveUnityProjectRoot()
    {
        var overridePath = TryReadOverride();
        if (overridePath != null && Directory.Exists(overridePath))
            return overridePath;

        var overrideFilePath = GetOverrideFilePath();
        throw new DirectoryNotFoundException(
            $"Unity project root isn't configured. Create '{overrideFilePath}' containing the Unity " +
            "project's root folder path (the one with 'Assets' directly inside it).");
    }

    private static string? TryReadOverride()
    {
        var path = GetOverrideFilePath();
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private static string GetOverrideFilePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StoryForge",
            OverrideFileName);
    }
}
