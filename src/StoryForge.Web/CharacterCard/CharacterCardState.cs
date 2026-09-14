using System.Diagnostics;
using StoryForge.Core;
using StoryForge.Core.CharacterCard;
using StoryForge.Core.Graph;
using StoryForge.Core.Portrait;

namespace StoryForge.Web.CharacterCard;

// Scoped per circuit — mirrors GraphEditorState's own pattern, minus any JS/canvas involvement (this
// panel is plain server-rendered Blazor forms, no client-side state to keep in sync). Cards.Save() is
// called on every field edit rather than batched, matching CharacterCardPanel's own save-on-blur design
// in the original WinForms tool — there's no explicit 存檔 button here.
public sealed class CharacterCardState
{
    private readonly AppSettings _appSettings;

    // Still resolved and kept around for the portrait-folder scan below — that stays reading from the
    // Unity project (立繪差分 art lives in Assets, not in the external save folder). CharacterCardSettings
    // itself, though, now saves to _appSettings.ExternalDataFolder, not here.
    private string? _projectRoot;
    private CharacterCardSettings _settings = new();
    private string? _portraitFolder;

    public CharacterCardState(AppSettings appSettings)
    {
        _appSettings = appSettings;
    }

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

    public void Load()
    {
        try
        {
            _projectRoot = ProjectPaths.ResolveUnityProjectRoot();
            _settings = CharacterCardSettings.LoadOrDefault(_appSettings.ExternalDataFolder);

            _portraitFolder = Path.Combine(_projectRoot, "Assets",
                PortraitAssetScanner.PortraitFolderRelativePath.Replace('/', Path.DirectorySeparatorChar));

            // Character Card only wants the plain "Portrait_" line — "BattlePortrait_" variants are a
            // separate in-combat art set the user doesn't want cluttering the character-linking dropdown
            // or thumbnail gallery here.
            _portraitEntries = PortraitAssetScanner.ScanFolder(_portraitFolder)
                .Where(entry => !string.Equals(entry.Category, PortraitAssetScanner.BattlePortraitCategory,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            PortraitCharacters = _portraitEntries
                .Select(entry => entry.Character)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();

            _faceLabelMap = PortraitFaceLabelLoader.LoadFaceLabelMap(_projectRoot);
            SyncExpressionLabels();

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

    // Stamps every linked card's expression entries with the current Chinese label for each of its
    // portrait files, creating the entry if it doesn't exist yet — this is what makes the label show up in
    // CharacterCards.json even for variants the user has never typed a manual note for. Only ChineseLabel
    // is touched here; an existing Note is left alone.
    private void SyncExpressionLabels()
    {
        var changed = false;
        foreach (var card in _settings.Cards)
        {
            if (string.IsNullOrEmpty(card.LinkedPortraitCharacter))
                continue;

            foreach (var entry in GetPortraitEntries(card.LinkedPortraitCharacter))
            {
                var label = GetFaceLabel(entry.Variant);
                if (card.ExpressionNotesByFileName.TryGetValue(entry.FileName, out var note))
                {
                    if (note.ChineseLabel != label)
                    {
                        note.ChineseLabel = label;
                        changed = true;
                    }
                }
                else
                {
                    card.ExpressionNotesByFileName[entry.FileName] = new CharacterCardExpressionNote { ChineseLabel = label };
                    changed = true;
                }
            }
        }

        if (changed)
            Save();
    }

    // All characters' portraits live in one shared, flat folder (see PortraitAssetScanner), so there's no
    // per-character subfolder to open. When the given card is linked to a portrait character with at least
    // one scanned file, Explorer opens with that character's first file (same sort order as the thumbnail
    // gallery) pre-selected/highlighted so the user can actually find it; otherwise this just opens the
    // shared folder itself. Errors (no project-root override, folder missing on disk) are reported through
    // StatusMessage rather than thrown, matching GraphEditorState.OpenScriptTable.
    public void OpenPortraitFolder(CharacterCardEntry? selectedCard)
    {
        if (string.IsNullOrEmpty(_portraitFolder))
        {
            StatusMessage = "開啟立繪資料夾失敗：尚未載入立繪資料夾路徑";
            return;
        }

        try
        {
            var targetFile = string.IsNullOrEmpty(selectedCard?.LinkedPortraitCharacter)
                ? null
                : GetPortraitEntries(selectedCard.LinkedPortraitCharacter).FirstOrDefault()?.FilePath;

            if (targetFile != null)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{targetFile}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(_portraitFolder) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            StatusMessage = $"開啟立繪資料夾失敗：{e.Message}";
        }
    }

    public string GetPortraitDescription(CharacterCardEntry card, string fileName) =>
        card.ExpressionNotesByFileName.TryGetValue(fileName, out var note) ? note.Note : string.Empty;

    public void SetPortraitDescription(CharacterCardEntry card, string fileName, string text)
    {
        if (!card.ExpressionNotesByFileName.TryGetValue(fileName, out var note))
        {
            note = new CharacterCardExpressionNote { ChineseLabel = GetFaceLabel(GetVariant(fileName)) };
            card.ExpressionNotesByFileName[fileName] = note;
        }

        note.Note = text.Trim();
        Save();
    }

    private static string GetVariant(string fileName) => PortraitAssetScanner.CreateEntry(fileName).Variant;

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

    // Called after a drag-to-reorder in the 角色卡 list — orderedIds is the panel's own DOM read-back of
    // every row's data-card-id, in its final dropped order. Rebuilt from a dictionary lookup rather than an
    // in-place sort so the panel's JS side never needs to know anything about CharacterCardEntry itself.
    // Cards.Count must match exactly or this is a no-op — a mismatch means the id list is stale relative to
    // the live roster (e.g. a card was deleted from another circuit mid-drag), and silently dropping or
    // duplicating a card would be worse than just leaving the reorder unapplied.
    public void ReorderCards(IReadOnlyList<string> orderedIds)
    {
        var byId = _settings.Cards.ToDictionary(c => c.Id);
        var reordered = orderedIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        if (reordered.Count != _settings.Cards.Count)
            return;

        _settings.Cards = reordered;
        Save();
    }

    public void Save()
    {
        try
        {
            _settings.Save(_appSettings.ExternalDataFolder);
            StatusMessage = $"已儲存：{_settings.Cards.Count} 個角色卡";
        }
        catch (Exception e)
        {
            StatusMessage = $"儲存失敗：{e.Message}";
        }
    }
}
