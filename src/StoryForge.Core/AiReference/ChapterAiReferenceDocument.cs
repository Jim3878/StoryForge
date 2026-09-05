namespace StoryForge.Core.AiReference;

public sealed class ChapterAiReferenceDocument
{
    public string ChapterKey { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; }

    // User-authored instructions (tone, format expectations, house rules) carried along verbatim from
    // Settings so the AI sees them alongside the structural data below.
    public string Prompt { get; set; } = string.Empty;

    // Whole-work and per-chapter story background, from the 劇情大綱 panel (StoryOutlineSettings) — free
    // text rather than structured fields, since that's more flexible for an AI to read.
    public string GlobalOutline { get; set; } = string.Empty;
    public string ChapterOutline { get; set; } = string.Empty;

    public List<ChapterAiReferenceNode> Nodes { get; set; } = new();
    public List<ChapterAiReferenceCharacter> Characters { get; set; } = new();
}

public sealed class ChapterAiReferenceNode
{
    public string PlayscriptName { get; set; } = string.Empty;

    // Null means unconditional (no incoming edges at all) — worth distinguishing from "條件不明", which
    // means there WAS an edge but it couldn't be resolved to a readable description.
    public string? EntryCondition { get; set; }

    // Only set when this playscript already has its own dedicated GoogleSheet document — most nodes in a
    // chapter export are exactly the ones that don't (they're the target of the outline generation).
    public string? ExistingContentLink { get; set; }

    // User-authored note from the node's 備忘 field in the offline graph editor — null when never written.
    public string? Memo { get; set; }
}

public sealed class ChapterAiReferenceCharacter
{
    public string ChineseName { get; set; } = string.Empty;

    // User-authored note from the 角色卡 panel — personality/tone/background etc., independent of whether
    // this character has any portrait art. Null when never written.
    public string? Note { get; set; }

    // Empty when this character has no portrait art linked in its 角色卡 entry.
    public List<ChapterAiReferenceVariant> Variants { get; set; } = new();
}

public sealed class ChapterAiReferenceVariant
{
    public string Category { get; set; } = string.Empty;
    public string Variant { get; set; } = string.Empty;
    public string FaceLabel { get; set; } = string.Empty;

    // User-authored 差分描述 from the 立繪差分 panel — null when never annotated.
    public string? Description { get; set; }
}
