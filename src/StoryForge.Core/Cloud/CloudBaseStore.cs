using Newtonsoft.Json;

namespace StoryForge.Core.Cloud;

public sealed class CloudBaseEntry
{
    public string Guid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string[]> Rows { get; set; } = new();
    public string Sv { get; set; } = string.Empty;
    public DateTime SyncedAt { get; set; }
}

// The sync base: each playscript's table as it was the last time this machine and the cloud agreed on it
// (after a download, an upload, or a resolved conflict). Per machine, per workspace, keyed by node guid so a
// rename never loses it: %LocalAppData%\StoryForge\cloud-base\<workspace>\<guid>.json.
// Only ever written by the app process itself (see CLAUDE.md's sandbox gotcha).
public sealed class CloudBaseStore
{
    private readonly string _folder;

    public CloudBaseStore(string workspaceId)
    {
        _folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StoryForge", "cloud-base", Sanitize(workspaceId));
    }

    public string Folder => _folder;

    public CloudBaseEntry? Get(string guid)
    {
        var path = PathFor(guid);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonConvert.DeserializeObject<CloudBaseEntry>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    public void Set(string guid, string name, IEnumerable<string[]> rows, string? sv)
    {
        Directory.CreateDirectory(_folder);
        var entry = new CloudBaseEntry
        {
            Guid = guid, Name = name, Rows = CloudRows.Normalize(rows), Sv = sv ?? string.Empty, SyncedAt = DateTime.Now,
        };
        var tmp = PathFor(guid) + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(entry, Formatting.Indented));
        File.Move(tmp, PathFor(guid), overwrite: true);
    }

    private string PathFor(string guid) => Path.Combine(_folder, Sanitize(guid) + ".json");

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
