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

    // Once the flow graph has been uploaded (or a download found it enabled in the cloud), the cloud graph is
    // the master: local StoryForge only downloads it and its flow graph tab is read-only.
    private string FlowGraphMarker => Path.Combine(_folder, "flowgraph-managed.json");

    public bool IsFlowGraphManaged => File.Exists(FlowGraphMarker);

    public void SetFlowGraphManaged()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FlowGraphMarker, JsonConvert.SerializeObject(new { since = DateTime.Now }));
    }

    // Is the flow graph cloud-managed for the workspace these settings point at?
    public static bool FlowGraphManagedFor(Graph.AppSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.CloudWorkspaceId) && new CloudBaseStore(settings.CloudWorkspaceId.Trim()).IsFlowGraphManaged;

    private string PathFor(string guid) => Path.Combine(_folder, Sanitize(guid) + ".json");

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
