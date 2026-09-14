using Newtonsoft.Json;

namespace StoryForge.Web.UpdateCheck;

// Subset of GitHub's `GET /repos/{owner}/{repo}/releases/latest` response actually needed here — see
// https://docs.github.com/en/rest/releases/releases#get-the-latest-release.
internal sealed class GitHubReleaseInfo
{
    [JsonProperty("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonProperty("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    // Markdown, written by hand when the release is created — rendered for display, not consumed raw.
    [JsonProperty("body")]
    public string Body { get; set; } = string.Empty;

    [JsonProperty("assets")]
    public List<GitHubReleaseAsset> Assets { get; set; } = new();
}

internal sealed class GitHubReleaseAsset
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;
}
