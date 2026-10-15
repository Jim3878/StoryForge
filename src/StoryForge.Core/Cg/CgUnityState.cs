namespace StoryForge.Core.Cg;

public sealed class CgUnityDefinition
{
    public string Id { get; set; } = string.Empty;
    public string PrefabAddress { get; set; } = string.Empty;
    public List<string> Diffs { get; } = new();
}

public enum CgPrefabKind
{
    None,
    Sprite,
    Spine
}

// Unity 專案裡 CG 的現況，全部直接讀檔，不需要開 Unity：
// definition（Assets/06.Definition/GalleryDefinition/{CG}.txt，base64 包著 protobuf-net 的 CgDefinition）、
// prefab（Assets/02.Prefabs/Gallery/{CG}.prefab）、暫代圖（Gallery/_Placeholder/{CG}_*.png）。
public sealed class CgUnityState
{
    public const string DefinitionFolder = "Assets/06.Definition/GalleryDefinition";
    public const string PrefabFolder = "Assets/02.Prefabs/Gallery";
    public const string GallerySpriteFolder = "Assets/09.Sprites/Gallery";
    public const string PlaceholderFolder = GallerySpriteFolder + "/_Placeholder";
    public const string SkeletonDataFolder = "Assets/11.Other/SkeletonData";

    private readonly string _projectRoot;
    private readonly Dictionary<string, CgUnityDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _prefabs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _placeholders = new(StringComparer.Ordinal);
    private readonly HashSet<string> _skeletonFolders = new(StringComparer.Ordinal);

    public List<string> Messages { get; } = new();

    public CgUnityState(string projectRoot)
    {
        _projectRoot = projectRoot;

        var definitionFolder = ToPath(DefinitionFolder);
        if (Directory.Exists(definitionFolder))
        {
            foreach (var file in Directory.GetFiles(definitionFolder, "*.txt"))
            {
                try
                {
                    var definition = CgDefinitionReader.Read(File.ReadAllText(file));
                    if (!string.IsNullOrEmpty(definition.Id))
                        _definitions[definition.Id] = definition;
                }
                catch (Exception e)
                {
                    Messages.Add($"無法讀取 definition {Path.GetFileName(file)}：{e.Message}");
                }
            }
        }

        AddNames(_prefabs, ToPath(PrefabFolder), "*.prefab", x => x);
        AddNames(_placeholders, ToPath(PlaceholderFolder), "*.png", x => x.Contains('_') ? x[..x.LastIndexOf('_')] : x);
        var skeletonFolder = ToPath(SkeletonDataFolder);
        if (Directory.Exists(skeletonFolder))
        {
            foreach (var folder in Directory.GetDirectories(skeletonFolder))
                _skeletonFolders.Add(Path.GetFileName(folder));
        }
    }

    public IEnumerable<string> PrefabNames => _prefabs.OrderBy(x => x, NaturalStringComparer.Instance);

    public CgUnityDefinition? GetDefinition(string cgId) => _definitions.GetValueOrDefault(cgId);

    public bool HasPrefab(string cgId) => _prefabs.Contains(CgNaming.SafeFileName(cgId));

    public bool IsPlaceholder(string cgId) => _placeholders.Contains(CgNaming.SafeFileName(cgId));

    public CgPrefabKind GetPrefabKind(string cgId)
    {
        if (!HasPrefab(cgId))
            return CgPrefabKind.None;
        return _skeletonFolders.Contains(CgNaming.SafeFileName(cgId)) ? CgPrefabKind.Spine : CgPrefabKind.Sprite;
    }

    // prefab 的差分：主 CG 的 definition 可能被共用表過濾過，所以聯集所有指向這個 prefab 的 definition。
    public List<string> GetPrefabDiffs(string prefabCgId)
    {
        var address = $"{PrefabFolder}/{CgNaming.SafeFileName(prefabCgId)}.prefab";
        return _definitions.Values
            .Where(x => x.Id == prefabCgId || string.Equals(x.PrefabAddress, address, StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.Diffs)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    // Editor 開著時會獨佔鎖住 Temp/UnityLockfile；Unity 當掉後檔案會留著，所以要用「能不能鎖住」判斷，不能只看存在。
    public static bool IsEditorRunning(string projectRoot)
    {
        var lockFile = Path.Combine(projectRoot, "Temp", "UnityLockfile");
        if (!File.Exists(lockFile))
            return false;

        try
        {
            using var stream = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private string ToPath(string assetPath) => Path.Combine(_projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));

    private static void AddNames(HashSet<string> set, string folder, string pattern, Func<string, string> map)
    {
        if (!Directory.Exists(folder))
            return;
        foreach (var file in Directory.GetFiles(folder, pattern, SearchOption.TopDirectoryOnly))
            set.Add(map(Path.GetFileNameWithoutExtension(file)));
    }
}

// 只解 CgDefinition 用到的欄位：id(10)、prefabAddress(40)、diffDefinitions(70) 內的 id(10)。
public static class CgDefinitionReader
{
    public static CgUnityDefinition Read(string base64)
    {
        var bytes = Convert.FromBase64String(base64.Trim());
        var definition = new CgUnityDefinition();
        var position = 0;
        while (position < bytes.Length)
        {
            var (field, wireType) = ReadTag(bytes, ref position);
            if (wireType != 2)
            {
                Skip(bytes, ref position, wireType);
                continue;
            }

            var length = (int)ReadVarint(bytes, ref position);
            var start = position;
            position += length;
            switch (field)
            {
                case 10:
                    definition.Id = System.Text.Encoding.UTF8.GetString(bytes, start, length);
                    break;
                case 40:
                    definition.PrefabAddress = System.Text.Encoding.UTF8.GetString(bytes, start, length);
                    break;
                case 70:
                    var diffId = ReadStringField(bytes, start, length, 10);
                    if (!string.IsNullOrEmpty(diffId))
                        definition.Diffs.Add(diffId);
                    break;
            }
        }

        return definition;
    }

    private static string? ReadStringField(byte[] bytes, int start, int length, int wantedField)
    {
        var position = start;
        var end = start + length;
        while (position < end)
        {
            var (field, wireType) = ReadTag(bytes, ref position);
            if (wireType != 2)
            {
                Skip(bytes, ref position, wireType);
                continue;
            }

            var fieldLength = (int)ReadVarint(bytes, ref position);
            if (field == wantedField)
                return System.Text.Encoding.UTF8.GetString(bytes, position, fieldLength);
            position += fieldLength;
        }

        return null;
    }

    private static (int Field, int WireType) ReadTag(byte[] bytes, ref int position)
    {
        var tag = ReadVarint(bytes, ref position);
        return ((int)(tag >> 3), (int)(tag & 7));
    }

    private static ulong ReadVarint(byte[] bytes, ref int position)
    {
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            var b = bytes[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
        }
    }

    private static void Skip(byte[] bytes, ref int position, int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarint(bytes, ref position); break;
            case 1: position += 8; break;
            case 5: position += 4; break;
            default: throw new InvalidDataException($"不支援的 protobuf wire type {wireType}");
        }
    }
}
