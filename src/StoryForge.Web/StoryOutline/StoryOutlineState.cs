using StoryForge.Core;
using StoryForge.Core.StoryOutline;
using StoryForge.Web.GraphEditor;

namespace StoryForge.Web.StoryOutline;

// Scoped per circuit — mirrors CharacterCardState's pattern. Save-on-blur, no explicit save button,
// matching the original tool's own convention for this panel.
public sealed class StoryOutlineState
{
    private readonly GraphEditorState _graphState;
    private string? _projectRoot;
    private StoryOutlineSettings _settings = new();

    public StoryOutlineState(GraphEditorState graphState)
    {
        _graphState = graphState;
    }

    public string StatusMessage { get; private set; } = string.Empty;

    public string GlobalOutline
    {
        get => _settings.GlobalOutline;
        set => _settings.GlobalOutline = value;
    }

    public string AiWritingGuidelines
    {
        get => _settings.AiWritingGuidelines;
        set => _settings.AiWritingGuidelines = value;
    }

    public void Load()
    {
        try
        {
            _projectRoot = ProjectPaths.ResolveUnityProjectRoot();
            _settings = StoryOutlineSettings.LoadOrDefault(_projectRoot);
            StatusMessage = $"已載入：{_settings.ChapterOutlines.Count} 個章節大綱";
        }
        catch (Exception e)
        {
            StatusMessage = $"載入失敗：{e.Message}";
        }
    }

    public void SaveGlobalOutline() => Save();

    public void SaveAiWritingGuidelines() => Save();

    public string GetChapterOutline(string chapter) =>
        _settings.ChapterOutlines.TryGetValue(chapter, out var text) ? text : string.Empty;

    // Blank text removes the dictionary key entirely rather than storing an empty string — keeps the
    // JSON file free of empty-string cruft, matching the original's own SaveChapterOutline behavior.
    public void SetChapterOutline(string chapter, string text)
    {
        if (string.IsNullOrEmpty(chapter))
            return;

        if (string.IsNullOrWhiteSpace(text))
            _settings.ChapterOutlines.Remove(chapter);
        else
            _settings.ChapterOutlines[chapter] = text;

        Save();
    }

    // Suggestions merge every chapter key already saved with every live flow-graph Group title — not a
    // hard dependency (the chapter field stays free-typed), just a convenience list so a chapter that
    // already has a Group but zero outline text yet still shows up to pick from.
    public List<string> GetChapterSuggestions()
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in _settings.ChapterOutlines.Keys)
            set.Add(key);
        foreach (var title in _graphState.GetGroupTitles())
            set.Add(title);
        return set.ToList();
    }

    private void Save()
    {
        if (_projectRoot == null)
            return;

        try
        {
            _settings.Save(_projectRoot);
            StatusMessage = "已儲存";
        }
        catch (Exception e)
        {
            StatusMessage = $"儲存失敗：{e.Message}";
        }
    }
}
