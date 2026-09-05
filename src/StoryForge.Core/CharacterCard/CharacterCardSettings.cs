using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StoryForge.Core.CharacterCard;

public sealed class CharacterCardEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ChineseName { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;

    // Character token from PortraitAssetScanner (the 立繪差分 folder scan) this card is linked to — empty
    // means this character has no portrait art at all. Kept even if a rescan no longer finds it (renamed or
    // deleted source files) instead of being silently cleared; the panel just flags it as unresolved.
    public string LinkedPortraitCharacter { get; set; } = string.Empty;
}

// User-curated character roster for the AI reference export. Replaces the old scan-derived checklist
// (AppSettings.AiReferenceSelectedCharacters/AiReferenceOtherCharacters) — a portrait "Character" grouping
// token isn't reliably a real character (some are props/effects), and a real character doesn't always have
// portrait art at all, so the roster has to be hand-curated rather than inferred from the portrait folder.
// Stored inside the Unity project (not per-machine LocalAppData like the other tool settings) and tracked in
// git, so the roster is shared across whoever uses this tool rather than living on one machine only.
public sealed class CharacterCardSettings
{
    private const string RelativeFilePath = "Assets/06.Definition/PlayscriptOfflineToolData/CharacterCards.json";

    public List<CharacterCardEntry> Cards { get; set; } = new();

    public static CharacterCardSettings LoadOrDefault(string projectRoot)
    {
        var path = GetFilePath(projectRoot);
        if (!File.Exists(path))
            return MigrateFromLegacyAppSettings(projectRoot);

        try
        {
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<CharacterCardSettings>(json) ?? new CharacterCardSettings();
        }
        catch
        {
            return new CharacterCardSettings();
        }
    }

    public void Save(string projectRoot)
    {
        var path = GetFilePath(projectRoot);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
    }

    // One-time migration from the old AppSettings-based roster into this project-tracked file. Reads the
    // legacy fields directly out of the raw settings JSON (rather than typed AppSettings properties) since
    // those two fields were removed from AppSettings once this migration existed — nothing else needs them
    // afterward, so there is no reason to keep dead properties around just for this one-time read.
    private static CharacterCardSettings MigrateFromLegacyAppSettings(string projectRoot)
    {
        var result = new CharacterCardSettings();

        try
        {
            var legacySettingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StoryForge", "settings.json");
            if (!File.Exists(legacySettingsPath))
                return result;

            var root = JObject.Parse(File.ReadAllText(legacySettingsPath));

            var selectedCharacters = (root["AiReferenceSelectedCharacters"] as JArray)?
                .Select(t => t.Value<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                ?? Enumerable.Empty<string>();

            foreach (var name in selectedCharacters)
                result.Cards.Add(new CharacterCardEntry { LinkedPortraitCharacter = name });

            // Split on any whitespace, not just newlines — despite the old field's "一行一個" label, at
            // least one real settings.json on this project has all the names on a single space-separated
            // line instead, and splitting on '\n' alone would swallow that whole line as one bogus name.
            var otherCharactersText = root["AiReferenceOtherCharacters"]?.Value<string>() ?? string.Empty;
            var otherCharacters = otherCharactersText
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var name in otherCharacters)
                result.Cards.Add(new CharacterCardEntry { ChineseName = name });

            if (result.Cards.Count > 0)
                result.Save(projectRoot);
        }
        catch
        {
            // Best-effort migration — worst case the user rebuilds the roster by hand in the 角色卡 panel.
        }

        return result;
    }

    private static string GetFilePath(string projectRoot)
    {
        return Path.Combine(projectRoot, RelativeFilePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
