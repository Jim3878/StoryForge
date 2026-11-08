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

    // Server time (ms) of the last 下載 of the tables / of the flow graph; the next one only reads docs changed
    // after it. Two times because a download can leave either part out. (Older files have only serverTime,
    // which then counts for both.)
    private string PullStatePath => Path.Combine(_folder, "pull-state.json");

    private Newtonsoft.Json.Linq.JObject ReadPullState()
    {
        try
        {
            return File.Exists(PullStatePath) ? Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(PullStatePath)) : new();
        }
        catch
        {
            return new();
        }
    }

    private void WritePullState(string key, long? value)
    {
        Directory.CreateDirectory(_folder);
        var o = ReadPullState();
        if (o["graphServerTime"] == null && o["serverTime"] != null)
            o["graphServerTime"] = o["serverTime"]; // keep the old shared time for the part not written now
        if (value == null) o.Remove(key);
        else o[key] = value;
        o["at"] = DateTime.Now;
        File.WriteAllText(PullStatePath, o.ToString(Formatting.Indented));
    }

    public long? LastPullServerTime
    {
        get => ReadPullState().Value<long?>("serverTime");
        set => WritePullState("serverTime", value);
    }

    public long? LastGraphPullServerTime
    {
        get
        {
            var o = ReadPullState();
            return o["graphServerTime"] != null ? o.Value<long?>("graphServerTime") : o.Value<long?>("serverTime");
        }
        set => WritePullState("graphServerTime", value);
    }

    // Hash of the rules JSON last uploaded from here; unchanged rules are not sent again.
    private string RulesHashPath => Path.Combine(_folder, "rules-hash.txt");

    public string? LastRulesHash
    {
        get => File.Exists(RulesHashPath) ? File.ReadAllText(RulesHashPath).Trim() : null;
        set
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(RulesHashPath, value ?? string.Empty);
        }
    }

    // Hash of each reference file (AGENTS.md, StoryOutline, ...) last uploaded from here; unchanged ones are
    // not sent again.
    private string ReferenceHashesPath => Path.Combine(_folder, "references-hash.json");

    public Dictionary<string, string> ReferenceHashes
    {
        get
        {
            try
            {
                return File.Exists(ReferenceHashesPath)
                    ? JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(ReferenceHashesPath)) ?? new()
                    : new();
            }
            catch
            {
                return new();
            }
        }
        set
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(ReferenceHashesPath, JsonConvert.SerializeObject(value, Formatting.Indented));
        }
    }

    // Set once the cloud flow graph is known to hold the nodes' 衝突/變化/角色/地點 (seeded by an upload).
    private string ContentSeededMarker => Path.Combine(_folder, "flowgraph-content-seeded.json");

    public bool NodeContentSeeded
    {
        get => File.Exists(ContentSeededMarker);
        set
        {
            Directory.CreateDirectory(_folder);
            if (value) File.WriteAllText(ContentSeededMarker, JsonConvert.SerializeObject(new { since = DateTime.Now }));
            else if (File.Exists(ContentSeededMarker)) File.Delete(ContentSeededMarker);
        }
    }

    private string PathFor(string guid) => Path.Combine(_folder, Sanitize(guid) + ".json");

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
