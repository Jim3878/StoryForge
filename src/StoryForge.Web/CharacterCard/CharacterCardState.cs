using StoryForge.Core;
using StoryForge.Core.CharacterCard;
using StoryForge.Core.Portrait;

namespace StoryForge.Web.CharacterCard;

// Scoped per circuit — mirrors GraphEditorState's own pattern, minus any JS/canvas involvement (this
// panel is plain server-rendered Blazor forms, no client-side state to keep in sync). Cards.Save() is
// called on every field edit rather than batched, matching CharacterCardPanel's own save-on-blur design
// in the original WinForms tool — there's no explicit 存檔 button here.
public sealed class CharacterCardState
{
    private string? _projectRoot;
    private CharacterCardSettings _settings = new();

    public string StatusMessage { get; private set; } = string.Empty;
    public List<CharacterCardEntry> Cards => _settings.Cards;

    // Distinct Character tokens from PortraitAssetScanner (the 立繪差分 folder scan), for the linked-group
    // dropdown. _portraitEntries keeps every scanned file so GetPortraitEntries can list a character's
    // individual variants for the thumbnail gallery.
    public List<string> PortraitCharacters { get; private set; } = new();
    private List<PortraitEntry> _portraitEntries = new();

    // Face enum member name -> Chinese label (e.g. "Angry" -> "生氣"), read from the same exported enum
    // map the Unity project's own Face enum's [LabelText] attributes produce. entry.Variant is the raw
    // enum member name parsed out of the file name, so this is a straight lookup, falling back to the raw
    // variant token itself when a file's variant doesn't match any known Face member.
    private Dictionary<string, string> _faceLabelMap = new(StringComparer.OrdinalIgnoreCase);

    // User-authored per-file notes on what a specific expression variant actually conveys — a Face label
    // like "生氣" doesn't capture nuance (e.g. "假裝生氣" vs "真的暴怒"), so this lets the user annotate each
    // file individually. Shared with the original tool's 立繪差分 panel (same portrait-descriptions.json).
    private PortraitDescriptionSettings _descriptionSettings = new();

    public void Load()
    {
        try
        {
            _projectRoot = ProjectPaths.ResolveUnityProjectRoot();
            _settings = CharacterCardSettings.LoadOrDefault(_projectRoot);

            var portraitFolder = Path.Combine(_projectRoot, "Assets",
                PortraitAssetScanner.PortraitFolderRelativePath.Replace('/', Path.DirectorySeparatorChar));
            _portraitEntries = PortraitAssetScanner.ScanFolder(portraitFolder);
            PortraitCharacters = _portraitEntries
                .Select(entry => entry.Character)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();

            _faceLabelMap = PortraitFaceLabelLoader.LoadFaceLabelMap(_projectRoot);
            _descriptionSettings = PortraitDescriptionSettings.LoadOrDefault();

            StatusMessage = $"已載入：{_settings.Cards.Count} 個角色卡（立繪角色 {PortraitCharacters.Count} 個）";
        }
        catch (Exception e)
        {
            StatusMessage = $"載入失敗：{e.Message}";
        }
    }

    public List<PortraitEntry> GetPortraitEntries(string character) =>
        _portraitEntries
            .Where(entry => string.Equals(entry.Character, character, StringComparison.Ordinal))
            .OrderBy(entry => PortraitAssetScanner.GetCategoryOrder(entry.Category))
            .ThenBy(entry => entry.Variant, StringComparer.Ordinal)
            .ToList();

    public string GetFaceLabel(string variant) =>
        _faceLabelMap.TryGetValue(variant, out var label) ? label : variant;

    public string GetPortraitDescription(string fileName) =>
        _descriptionSettings.DescriptionsByFileName.GetValueOrDefault(fileName, string.Empty);

    public void SetPortraitDescription(string fileName, string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed))
            _descriptionSettings.DescriptionsByFileName.Remove(fileName);
        else
            _descriptionSettings.DescriptionsByFileName[fileName] = trimmed;

        _descriptionSettings.Save();
    }

    public CharacterCardEntry AddCard()
    {
        var card = new CharacterCardEntry();
        _settings.Cards.Add(card);
        Save();
        return card;
    }

    public void DeleteCard(string id)
    {
        _settings.Cards.RemoveAll(c => c.Id == id);
        Save();
    }

    public void Save()
    {
        if (_projectRoot == null)
            return;

        try
        {
            _settings.Save(_projectRoot);
            StatusMessage = $"已儲存：{_settings.Cards.Count} 個角色卡";
        }
        catch (Exception e)
        {
            StatusMessage = $"儲存失敗：{e.Message}";
        }
    }
}
