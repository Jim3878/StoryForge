namespace StoryForge.Core.Pipeline;

public sealed class PlayscriptPipelineInfo
{
    public string PlayscriptName { get; }
    public string Chapter { get; }
    public bool IsOnLdtk { get; set; }
    public bool IsOnGraph { get; set; }
    public bool IsOnSheet { get; set; }
    public bool IsOnCSharp { get; set; }

    public PlayscriptPipelineInfo(string playscriptName)
    {
        PlayscriptName = playscriptName;
        Chapter = PlayscriptNaming.GetChapterName(playscriptName);
    }

    public bool IsComplete => IsOnLdtk && IsOnGraph && IsOnSheet && IsOnCSharp;
    public bool HasAnyMismatch => !IsComplete;
}
