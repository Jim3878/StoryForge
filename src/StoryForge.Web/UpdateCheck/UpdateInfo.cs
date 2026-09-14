namespace StoryForge.Web.UpdateCheck;

// What MainLayout/UpdateDialog need once a newer release has actually been found — null out of
// UpdateCheckService.CheckOnceAsync() means "already on the latest tag" (or the check failed; either way,
// no button).
public sealed class UpdateInfo
{
    public required string Tag { get; init; }
    public required string NotesMarkdown { get; init; }
    public required string ZipDownloadUrl { get; init; }
    public required string ReleaseHtmlUrl { get; init; }
}
