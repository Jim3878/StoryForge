using StoryForge.Core;
using StoryForge.Core.Graph;
using StoryForge.Core.StoryOutline;
using StoryForge.Web.GraphEditor;

namespace StoryForge.Web.StoryOutline;

// Scoped per circuit — mirrors CharacterCardState's pattern. Save-on-blur, no explicit save button,
// matching the original tool's own convention for this panel.
public sealed class StoryOutlineState
{
    private readonly GraphEditorState _graphState;
    private readonly AppSettings _appSettings;
    private StoryOutlineSettings _settings = new();
    private string _agentsMdContent = string.Empty;

    public StoryOutlineState(GraphEditorState graphState, AppSettings appSettings)
    {
        _graphState = graphState;
        _appSettings = appSettings;
    }

    public string StatusMessage { get; private set; } = string.Empty;

    public string GlobalOutline
    {
        get => _settings.GlobalOutline;
        set => _settings.GlobalOutline = value;
    }

    // Backed by AGENTS.md directly (see AgentsMdStore), not StoryOutlineSettings' JSON — this is the same
    // file an external Codex/Claude agent auto-loads by filename convention, so the panel edits that one
    // real copy instead of keeping a second, easily-out-of-sync writing-rules text elsewhere.
    public string AgentsMdContent
    {
        get => _agentsMdContent;
        set => _agentsMdContent = value;
    }

    public void Load()
    {
        try
        {
            _settings = StoryOutlineSettings.LoadOrDefault(_appSettings.ExternalDataFolder);
            _agentsMdContent = AgentsMdStore.LoadOrDefault(_appSettings.ExternalDataFolder);
            StatusMessage = $"已載入：{_settings.ChapterOutlines.Count} 個章節大綱";
        }
        catch (Exception e)
        {
            StatusMessage = $"載入失敗：{e.Message}";
        }
    }

    public void SaveGlobalOutline() => Save();

    public void SaveAgentsMd()
    {
        try
        {
            AgentsMdStore.Save(_appSettings.ExternalDataFolder, _agentsMdContent);
            StatusMessage = "已儲存";
        }
        catch (Exception e)
        {
            StatusMessage = $"儲存失敗：{e.Message}";
        }
    }

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
        var set = new SortedSet<string>(NaturalStringComparer.Instance);
        foreach (var key in _settings.ChapterOutlines.Keys)
            set.Add(key);
        foreach (var title in _graphState.GetGroupTitles())
            set.Add(title);
        return set.ToList();
    }

    private void Save()
    {
        try
        {
            _settings.Save(_appSettings.ExternalDataFolder);
            StatusMessage = "已儲存";
        }
        catch (Exception e)
        {
            StatusMessage = $"儲存失敗：{e.Message}";
        }
    }
}
