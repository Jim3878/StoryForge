using StoryForge.Core.Codegen;
using StoryForge.Core.Ldtk;
using StoryForge.Core.Schemas;

namespace StoryForge.Core.Cloud;

// Runs the real 匯出成C# validation (PlayscriptFactoryCodeGenerator, which throws on the first bad row)
// without writing anything — 繼續合併 check ③. Inputs are loaded once per validator.
public sealed class PlayscriptRowValidator
{
    private readonly EnumLabelMaps _maps;
    private readonly PlayscriptAudioClips _audio;
    private readonly LdtkTransportIndex? _transports;
    private readonly string _outputRoot;

    private PlayscriptRowValidator(EnumLabelMaps maps, PlayscriptAudioClips audio, LdtkTransportIndex? transports, string outputRoot)
    {
        _maps = maps;
        _audio = audio;
        _transports = transports;
        _outputRoot = outputRoot;
    }

    public static PlayscriptRowValidator Create(string projectRoot)
    {
        var document = EnumLabelMapSource.RefreshOrLoadExisting(projectRoot, out _)
                       ?? throw new InvalidOperationException("找不到 Enum 對照表，無法檢查劇本。");
        var assets = Path.Combine(projectRoot, "Assets");
        var ldtk = Path.Combine(assets, "12.Custom", "Company.ldtk");
        return new PlayscriptRowValidator(
            EnumLabelMaps.FromDocument(document),
            PlayscriptAudioClips.Scan(projectRoot),
            File.Exists(ldtk) ? LdtkTransportIndex.LoadFile(ldtk) : null,
            Path.Combine(assets, "12.Custom", "PlayscriptFactory"));
    }

    /// <returns>null when the rows would export, otherwise the generator's error message.</returns>
    public string? Validate(string playscriptName, IReadOnlyList<string[]> rows)
    {
        var content = rows.Where(r => !CloudRows.IsBlank(r)).ToList();
        if (content.Count == 0)
            return null;
        try
        {
            PlayscriptFactoryCodeGenerator.Generate(playscriptName, content, _maps, _audio, _transports, _outputRoot);
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }
}
