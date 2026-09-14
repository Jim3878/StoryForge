using Newtonsoft.Json;

namespace StoryForge.Core.Graph;

// One JSON file per 劇本 (PlayscriptProcessNode) story node — memo plus future narrative-detail fields
// (conflict/change/characters/location) an external AI tool fills in directly, living under
// AppSettings.ExternalDataFolder\Nodes\<chapter>\<name>.json. Deliberately separate from
// GraphSessionDocument/GraphConnectionsDocument: those are pure canvas/logic structure the app itself
// owns and edits through the canvas; this is authored content meant to be opened and edited directly by
// an external AI tool, one small file per story beat instead of one field buried inside a huge graph file.
public sealed class NodeContentEntry
{
    public string Guid { get; set; } = string.Empty;
    public string PlayscriptName { get; set; } = string.Empty;
    public string Memo { get; set; } = string.Empty;
    public string Conflict { get; set; } = string.Empty;
    public string Change { get; set; } = string.Empty;
    public string Characters { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
}

public static class NodeContentStore
{
    // Only a 劇本 node represents a story beat — 劇本條件/公用開關/公用變數/AND/OR/... logic nodes are
    // condition sources, never narrative content, so they never get a NodeContentStore file. Shared with
    // GraphDocumentModel (deletion cleanup) and GraphEditorState (Web layer) so there's one source of truth.
    public const string PlayscriptProcessNodeTypeName = "Definition.ProcessNode.PlayscriptProcessNode, MainAssemble";

    private const string FolderName = "Nodes";

    // Regenerates every story node's content file from the live graph in one pass — called whenever the
    // graph is loaded or saved. A file that already exists keeps whatever's already written into it
    // (Memo/Conflict/Change/Characters/Location); only Guid/PlayscriptName get refreshed, so a rename or
    // one of the graph's real duplicate-identity nodes never silently overwrites someone else's content.
    // currentMemo seeds a brand-new file only — it's the one-time migration off the old
    // GraphNodeVm.Fields["memo"] convention (the caller strips that key out of Fields once read).
    public static void SyncAll(string dataFolder,
        IEnumerable<(string Guid, string PlayscriptName, string CurrentMemo)> nodes)
    {
        foreach (var (guid, playscriptName, currentMemo) in nodes)
        {
            var path = ResolvePath(dataFolder, playscriptName, guid);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var entry = TryReadRaw(path) ?? new NodeContentEntry { Memo = currentMemo };
            entry.Guid = guid;
            entry.PlayscriptName = playscriptName;
            File.WriteAllText(path, JsonConvert.SerializeObject(entry, Formatting.Indented));
        }
    }

    public static NodeContentEntry? Read(string dataFolder, string playscriptName, string guid)
    {
        return TryReadRaw(ResolvePath(dataFolder, playscriptName, guid));
    }

    // Reads whatever .json content file currently sits at a playscript name's PLAIN (non-collision-suffixed)
    // path, regardless of which guid it actually belongs to — used by GraphEditorState.SyncRenamedScriptFiles
    // to check whether a rename's target name is already claimed by a DIFFERENT node's file before ever
    // attempting the move. ResolveBasePath's own automatic suffixing exists to keep two nodes' files from
    // colliding, but silently routing a rename through that same fallback would just be a different flavor
    // of "自己生一個名字" — the caller needs the plain-path truth to decide whether to warn instead.
    public static NodeContentEntry? ReadAtPlainPath(string dataFolder, string playscriptName) =>
        TryReadRaw(ResolvePlainBasePath(dataFolder, playscriptName) + ".json");

    public static void Write(string dataFolder, NodeContentEntry entry)
    {
        var path = ResolvePath(dataFolder, entry.PlayscriptName, entry.Guid);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonConvert.SerializeObject(entry, Formatting.Indented));
    }

    // Unconditionally writes at the PLAIN (non-collision-suffixed) path, overwriting whatever is already
    // there — used by PipelineStatusState's 建立劇本檔 overwrite-confirmation flow once the user has
    // explicitly agreed to take over a name a stale/orphaned file was still occupying. Never routes through
    // ResolveBasePath's own automatic suffixing, since the whole point is to reclaim the plain name.
    public static void WriteAtPlainPath(string dataFolder, NodeContentEntry entry)
    {
        var basePath = ResolvePlainBasePath(dataFolder, entry.PlayscriptName);
        var directory = Path.GetDirectoryName(basePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(basePath + ".json", JsonConvert.SerializeObject(entry, Formatting.Indented));
    }

    // Deletes this node's content file, if any — called when the node itself is actually being deleted
    // (GraphDocumentModel.MarkNodeForDeletion), so its old identity name never lingers on disk as an
    // orphaned file that a later, unrelated node could collide with.
    public static void DeleteFile(string dataFolder, string playscriptName, string guid)
    {
        var path = ResolveBasePath(dataFolder, playscriptName, guid) + ".json";
        if (File.Exists(path))
            File.Delete(path);
    }

    // A node's file lives at Nodes\<chapter>\<remainder>.json (e.g. "A1/1_釣魚執法" -> Nodes\A1\1_釣魚執法.json).
    // The real graph does have a couple of genuine duplicate playscriptId values, so a plain-name collision
    // with a file belonging to a DIFFERENT guid falls back to a short guid-suffixed name instead of two
    // nodes fighting over the same file — whichever node's file lands there first keeps the plain name.
    //
    // Exposed (extension stripped) so NodeScriptTableStore's sibling .md file for the same node always
    // resolves to the same base name — this JSON file's own recorded Guid is the source of truth for the
    // duplicate-playscriptId collision case, so routing through here keeps both stores in agreement without
    // a second copy of this check.
    public static string ResolveBasePath(string dataFolder, string playscriptName, string guid)
    {
        var plainBase = ResolvePlainBasePath(dataFolder, playscriptName);

        var existing = TryReadRaw(plainBase + ".json");
        if (existing == null || existing.Guid == guid)
            return plainBase;

        var suffix = guid.Replace("-", "");
        suffix = suffix.Length > 8 ? suffix[..8] : suffix;
        return $"{plainBase}-{suffix}";
    }

    // The name-only half of ResolveBasePath above, with no guid-collision suffixing — used where there is
    // no live node/guid to resolve against at all (e.g. PipelineStatusState checking whether a playscript
    // name that no longer has a graph node still has a leftover file on disk).
    public static string ResolvePlainBasePath(string dataFolder, string playscriptName)
    {
        var slashIndex = playscriptName.IndexOf('/');
        var chapter = slashIndex > 0 ? playscriptName[..slashIndex] : "_其他";
        var remainder = slashIndex >= 0 ? playscriptName[(slashIndex + 1)..] : playscriptName;

        var root = Path.Combine(dataFolder, FolderName, SanitizeSegment(chapter));
        return Path.Combine(root, SanitizeSegment(remainder));
    }

    private static string ResolvePath(string dataFolder, string playscriptName, string guid) =>
        ResolveBasePath(dataFolder, playscriptName, guid) + ".json";

    private static NodeContentEntry? TryReadRaw(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonConvert.DeserializeObject<NodeContentEntry>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        return string.IsNullOrEmpty(result) ? "_" : result;
    }
}
