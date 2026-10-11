using StoryForge.Core.Audio;

namespace StoryForge.Core.Codegen;

// The clip names a playscript's PlayBgm/PushBgm/PlaySfx rows may reference — the same flat BGM/SFX folders
// the BGM/音效 tabs browse. Unity resolves a clip with an exact Dictionary lookup on this name
// (AudioDirectorSystem.GetAudioClip) and throws at runtime on a miss, so the generator checks it up front.
public sealed class PlayscriptAudioClips
{
    public PlayscriptAudioClips(IEnumerable<string> bgmNames, IEnumerable<string> sfxNames)
    {
        Bgm = new HashSet<string>(bgmNames, StringComparer.Ordinal);
        Sfx = new HashSet<string>(sfxNames, StringComparer.Ordinal);
    }

    public HashSet<string> Bgm { get; }
    public HashSet<string> Sfx { get; }

    public static PlayscriptAudioClips Scan(string projectRoot)
    {
        return new PlayscriptAudioClips(
            AudioAssetScanner.ScanFolder(AudioAssetScanner.GetFolderPath(projectRoot, AudioCategory.Bgm)).Select(e => e.Name),
            AudioAssetScanner.ScanFolder(AudioAssetScanner.GetFolderPath(projectRoot, AudioCategory.Sfx)).Select(e => e.Name));
    }
}
