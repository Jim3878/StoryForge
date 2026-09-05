namespace StoryForge.Core.Portrait;

public sealed class PortraitEntry
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string Category { get; init; }
    public required string Character { get; init; }
    public required string Variant { get; init; }
}

// Mirrors Unity's PortraitDiffBrowserWindow file-name parsing exactly (Category_Character_Variant, or
// Character_Variant, or a bare file name) so grouping/categorization here matches what the Editor tool
// would show for the same folder.
public static class PortraitAssetScanner
{
    public const string PortraitFolderRelativePath = "09.Sprites/RunTime/NoAtlas/Portrait";
    public const string PortraitCategory = "Portrait";
    public const string BattlePortraitCategory = "BattlePortrait";
    public const string OtherCategory = "Other";

    private static readonly string[] SupportedExtensions = { ".png", ".jpg", ".jpeg" };

    public static List<PortraitEntry> ScanFolder(string portraitFolder)
    {
        var result = new List<PortraitEntry>();
        if (!Directory.Exists(portraitFolder))
            return result;

        foreach (var file in Directory.EnumerateFiles(portraitFolder, "*.*", SearchOption.TopDirectoryOnly))
        {
            if (IsSupportedImageFile(file))
                result.Add(CreateEntry(file));
        }

        return result;
    }

    public static PortraitEntry CreateEntry(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var tokens = fileName.Split('_', StringSplitOptions.RemoveEmptyEntries);

        var category = OtherCategory;
        var character = fileName;
        var variant = "Default";

        if (tokens.Length >= 3 && IsKnownCategory(tokens[0]))
        {
            category = tokens[0];
            character = tokens[1];
            variant = string.Join("_", tokens.Skip(2));
        }
        else if (tokens.Length >= 2)
        {
            character = tokens[0];
            variant = string.Join("_", tokens.Skip(1));
        }

        if (string.IsNullOrWhiteSpace(character))
            character = fileName;
        if (string.IsNullOrWhiteSpace(variant))
            variant = "Default";

        return new PortraitEntry
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            Category = category,
            Character = character,
            Variant = variant,
        };
    }

    public static bool IsSupportedImageFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return SupportedExtensions.Any(ext => string.Equals(ext, extension, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsKnownCategory(string token)
    {
        return string.Equals(token, PortraitCategory, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, BattlePortraitCategory, StringComparison.OrdinalIgnoreCase);
    }

    public static int GetCategoryOrder(string category)
    {
        if (string.Equals(category, PortraitCategory, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (string.Equals(category, BattlePortraitCategory, StringComparison.OrdinalIgnoreCase))
            return 1;
        return 2;
    }
}
