using StoryForge.Core.CharacterCard;
using StoryForge.Core.Graph;
using StoryForge.Core.GoogleSheet;
using StoryForge.Core.Pipeline;
using StoryForge.Core.Portrait;
using StoryForge.Core.StoryOutline;

namespace StoryForge.Core.AiReference;

public static class ChapterAiReferenceBuilder
{
    // Only 劇本 (PlayscriptProcessNode) counts as a node needing an outline — 劇本條件/公用開關/公用變數 etc.
    // always represent condition sources, not story beats, regardless of what chapter their own identity
    // value happens to fall in.
    private const string PlayscriptProcessTypeName = "Definition.ProcessNode.PlayscriptProcessNode, MainAssemble";

    public static ChapterAiReferenceDocument Build(
        GraphDocumentModel model,
        string chapterKey,
        IReadOnlyDictionary<string, GooglePlayscriptSheetEntry> sheetEntries,
        IReadOnlyList<PortraitEntry> portraitEntries,
        IReadOnlyDictionary<string, string> faceLabelMap,
        IReadOnlyDictionary<string, string> portraitDescriptionsByFileName,
        IReadOnlyList<CharacterCardEntry> characterCards,
        string prompt,
        bool includeContentLinks,
        StoryOutlineSettings storyOutline)
    {
        var chapterNodes = model.Nodes
            .Where(n => !n.IsMarkedForDeletion && n.TypeName == PlayscriptProcessTypeName)
            .Where(n => !string.IsNullOrEmpty(n.IdentityValue) &&
                        PlayscriptNaming.GetChapterName(n.IdentityValue!) == chapterKey)
            .ToList();
        var chapterNodeGuids = new HashSet<string>(chapterNodes.Select(n => n.Guid), StringComparer.Ordinal);

        var document = new ChapterAiReferenceDocument
        {
            ChapterKey = chapterKey,
            GeneratedAtUtc = DateTime.UtcNow,
            Prompt = prompt,
            GlobalOutline = storyOutline.GlobalOutline,
            ChapterOutline = storyOutline.ChapterOutlines.GetValueOrDefault(chapterKey, string.Empty),
        };

        foreach (var node in chapterNodes.OrderBy(n => n.IdentityValue, StringComparer.Ordinal))
        {
            var playscriptName = node.IdentityValue!;
            var link = includeContentLinks && sheetEntries.TryGetValue(playscriptName, out var entry)
                ? entry.SpreadsheetLink
                : null;

            var memo = node.Fields.TryGetValue("memo", out var memoValue) ? memoValue?.ToString() : null;

            document.Nodes.Add(new ChapterAiReferenceNode
            {
                PlayscriptName = playscriptName,
                EntryCondition = ChapterConditionDescriber.DescribeEntryCondition(model, node, chapterNodeGuids),
                ExistingContentLink = link,
                Memo = string.IsNullOrWhiteSpace(memo) ? null : memo,
            });
        }

        foreach (var card in characterCards)
        {
            // A card's 連結立繪分組 (LinkedPortraitCharacter) is chosen explicitly in the 角色卡 panel rather
            // than inferred from matching names, so a blank link correctly means "no portrait art" instead
            // of falling back to any coincidental name match.
            var variants = string.IsNullOrEmpty(card.LinkedPortraitCharacter)
                ? new List<ChapterAiReferenceVariant>()
                : portraitEntries
                    .Where(p => string.Equals(p.Character, card.LinkedPortraitCharacter, StringComparison.OrdinalIgnoreCase))
                    .Select(p => new ChapterAiReferenceVariant
                    {
                        Category = p.Category,
                        Variant = p.Variant,
                        FaceLabel = faceLabelMap.TryGetValue(p.Variant, out var label) ? label : p.Variant,
                        Description = portraitDescriptionsByFileName.TryGetValue(p.FileName, out var desc) ? desc : null,
                    })
                    .ToList();

            document.Characters.Add(new ChapterAiReferenceCharacter
            {
                ChineseName = card.ChineseName,
                Note = string.IsNullOrWhiteSpace(card.Note) ? null : card.Note,
                Variants = variants,
            });
        }

        return document;
    }
}
