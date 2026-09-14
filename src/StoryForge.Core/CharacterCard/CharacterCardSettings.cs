using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StoryForge.Core;
using StoryForge.Core.Portrait;

namespace StoryForge.Core.CharacterCard;

// A bare JSON string here means data written before ChineseLabel existed (when this dictionary's values
// were plain per-file note strings) — CharacterCardExpressionNoteConverter reads that shape as a
// Note-only entry instead of throwing, so an already-published CharacterCards.json from before this
// schema change still loads instead of silently deserializing into an empty roster.
[JsonConverter(typeof(CharacterCardExpressionNoteConverter))]
public sealed class CharacterCardExpressionNote
{
    // The expression's Chinese label (e.g. "生氣"), resolved from the Unity project's Face enum export and
    // baked in here so the card is self-contained — an external tool reading CharacterCards.json doesn't
    // need to cross-reference the Unity project just to know what a file name's variant token means.
    // Kept in sync automatically (see CharacterCardState's label sync); not user-edited directly.
    public string ChineseLabel { get; set; } = string.Empty;

    // User-authored note on what this specific variant actually conveys beyond the label alone (e.g.
    // "假裝生氣" vs "真的暴怒").
    public string Note { get; set; } = string.Empty;
}

internal sealed class CharacterCardExpressionNoteConverter : JsonConverter<CharacterCardExpressionNote>
{
    public override CharacterCardExpressionNote ReadJson(JsonReader reader, Type objectType,
        CharacterCardExpressionNote? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.String)
            return new CharacterCardExpressionNote { Note = (string?)reader.Value ?? string.Empty };

        var obj = JObject.Load(reader);
        return new CharacterCardExpressionNote
        {
            ChineseLabel = obj[nameof(CharacterCardExpressionNote.ChineseLabel)]?.Value<string>() ?? string.Empty,
            Note = obj[nameof(CharacterCardExpressionNote.Note)]?.Value<string>() ?? string.Empty,
        };
    }

    public override void WriteJson(JsonWriter writer, CharacterCardExpressionNote? value, JsonSerializer serializer)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(nameof(CharacterCardExpressionNote.ChineseLabel));
        writer.WriteValue(value?.ChineseLabel ?? string.Empty);
        writer.WritePropertyName(nameof(CharacterCardExpressionNote.Note));
        writer.WriteValue(value?.Note ?? string.Empty);
        writer.WriteEndObject();
    }
}

public sealed class CharacterCardEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ChineseName { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;

    // Character token from PortraitAssetScanner (the 立繪差分 folder scan) this card is linked to — empty
    // means this character has no portrait art at all. Kept even if a rescan no longer finds it (renamed or
    // deleted source files) instead of being silently cleared; the panel just flags it as unresolved.
    public string LinkedPortraitCharacter { get; set; } = string.Empty;

    // Per-expression-variant data, keyed by portrait file name. Lives on the owning card, not a separate
    // global file, so it travels with the card (export/copy/inspect CharacterCards.json and it's right
    // there) instead of needing a live scan of the portrait folder + Face enum export to reconstruct.
    public Dictionary<string, CharacterCardExpressionNote> ExpressionNotesByFileName { get; set; } = new(StringComparer.Ordinal);
}

// User-curated character roster. Lives in the standalone external data folder (AppSettings.
// ExternalDataFolder) alongside StoryOutlineSettings and the flow graph — not inside the Unity project and
// not per-machine LocalAppData — so an external AI tool pointed at that folder can read/write it directly
// as the one real copy, no export/import round trip.
public sealed class CharacterCardSettings
{
    private const string FileName = "CharacterCards.json";

    public List<CharacterCardEntry> Cards { get; set; } = new();

    public static CharacterCardSettings LoadOrDefault(string dataFolder)
    {
        var path = GetFilePath(dataFolder);
        CharacterCardSettings settings;
        if (!File.Exists(path))
        {
            settings = MigrateFromLegacyLocations(dataFolder);
        }
        else
        {
            // Deliberately NOT caught here: a real, existing CharacterCards.json that fails to parse must
            // surface as a visible load error, not silently look like a legitimate empty roster — an empty
            // roster the caller believes is real is one accidental edit away from being saved over the
            // actual data still sitting in the file.
            var json = File.ReadAllText(path);
            settings = JsonConvert.DeserializeObject<CharacterCardSettings>(json) ?? new CharacterCardSettings();
        }

        if (MigrateExpressionNotesFromLegacyFile(settings))
            settings.Save(dataFolder);

        return settings;
    }

    // One-time per-machine migration of expression notes that used to live in their own file
    // (%LocalAppData%\StoryForge\portrait-descriptions.json, keyed only by file name with no link back to
    // a specific character) into the CharacterCardEntry that owns that portrait's Character token. Gated
    // by a marker file next to the legacy one so it runs exactly once per machine and never re-adds a note
    // the user has since deleted from a card; only fills notes a card doesn't already have, never
    // overwrites one that's already there (so re-running this on a teammate's already-migrated
    // CharacterCards.json can only add, never clobber).
    private static bool MigrateExpressionNotesFromLegacyFile(CharacterCardSettings settings)
    {
        try
        {
            var legacyFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StoryForge");
            var legacyPath = Path.Combine(legacyFolder, "portrait-descriptions.json");
            var markerPath = Path.Combine(legacyFolder, "expression-notes-migrated.txt");

            if (File.Exists(markerPath) || !File.Exists(legacyPath))
                return false;

            var descriptions = JObject.Parse(File.ReadAllText(legacyPath))["DescriptionsByFileName"] as JObject;
            var changed = false;

            if (descriptions != null)
            {
                foreach (var prop in descriptions.Properties())
                {
                    var text = prop.Value?.Value<string>();
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    var character = PortraitAssetScanner.CreateEntry(prop.Name).Character;
                    var card = settings.Cards.FirstOrDefault(c =>
                        string.Equals(c.LinkedPortraitCharacter, character, StringComparison.Ordinal));
                    if (card == null || card.ExpressionNotesByFileName.ContainsKey(prop.Name))
                        continue;

                    card.ExpressionNotesByFileName[prop.Name] = new CharacterCardExpressionNote { Note = text };
                    changed = true;
                }
            }

            Directory.CreateDirectory(legacyFolder);
            File.WriteAllText(markerPath,
                $"Migrated expression notes from {legacyPath} into CharacterCards.json at {DateTime.Now:O}. " +
                "Delete this file to allow the migration to run again.");

            return changed;
        }
        catch
        {
            // Best-effort — worst case the user re-enters expression notes by hand in the 角色卡 panel.
            return false;
        }
    }

    public void Save(string dataFolder)
    {
        var path = GetFilePath(dataFolder);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
    }

    // One-time migration for a machine that already had this file at its old home — either the Unity
    // project (where every version of this tool up to the node/save-location redesign kept it, tracked in
    // git) or, further back still, the per-machine AppSettings-based roster from before CharacterCardSettings
    // existed at all. Tried in that order; whichever hits first wins and is copied into the new folder.
    private static CharacterCardSettings MigrateFromLegacyLocations(string dataFolder)
    {
        try
        {
            var projectRoot = ProjectPaths.ResolveUnityProjectRoot();
            var oldProjectPath = Path.Combine(projectRoot, "Assets", "06.Definition",
                "PlayscriptOfflineToolData", "CharacterCards.json");
            if (File.Exists(oldProjectPath))
            {
                var migrated = JsonConvert.DeserializeObject<CharacterCardSettings>(File.ReadAllText(oldProjectPath))
                                ?? new CharacterCardSettings();
                if (migrated.Cards.Count > 0)
                    migrated.Save(dataFolder);
                return migrated;
            }
        }
        catch
        {
            // Best-effort — fall through to the older AppSettings-based migration below.
        }

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
                result.Save(dataFolder);
        }
        catch
        {
            // Best-effort migration — worst case the user rebuilds the roster by hand in the 角色卡 panel.
        }

        return result;
    }

    private static string GetFilePath(string dataFolder)
    {
        return Path.Combine(dataFolder, FileName);
    }
}
