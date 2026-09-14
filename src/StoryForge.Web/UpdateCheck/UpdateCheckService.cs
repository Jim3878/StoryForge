using System.Reflection;
using Newtonsoft.Json;

namespace StoryForge.Web.UpdateCheck;

// Singleton (registered in Program.cs) — deliberately not AddScoped like every other service in this app:
// a GitHub release check is process-wide, not per-circuit/per-browser-tab, and per the "once per app launch,
// no background polling" decision there's no reason for every browser tab to re-fetch it. First circuit to
// call CheckOnceAsync() triggers the one HTTP call for the process's whole lifetime; everyone after gets the
// cached result immediately.
public sealed class UpdateCheckService
{
    private const string ReleasesLatestUrl = "https://api.github.com/repos/Jim3878/StoryForge/releases/latest";
    // Fixed name, not versioned per-release — the release workflow always attaches exactly one asset under
    // this name, so the client never has to guess/parse a version into a filename to find it.
    private const string ReleaseAssetName = "StoryForge-Release.zip";

    private Task<UpdateInfo?>? _cachedCheck;
    private readonly object _lock = new();

    public Task<UpdateInfo?> CheckOnceAsync()
    {
        lock (_lock)
        {
            return _cachedCheck ??= RunCheckAsync();
        }
    }

    private static async Task<UpdateInfo?> RunCheckAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            // GitHub's REST API rejects requests with no User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("StoryForge-UpdateCheck");

            var json = await client.GetStringAsync(ReleasesLatestUrl);
            var release = JsonConvert.DeserializeObject<GitHubReleaseInfo>(json);
            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
                return null;

            if (!IsNewer(release.TagName, GetRunningVersion()))
                return null;

            var asset = release.Assets.FirstOrDefault(a => a.Name == ReleaseAssetName);
            if (asset == null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                return null;

            return new UpdateInfo
            {
                Tag = release.TagName,
                NotesMarkdown = release.Body,
                ZipDownloadUrl = asset.BrowserDownloadUrl,
                ReleaseHtmlUrl = release.HtmlUrl,
            };
        }
        catch
        {
            // Offline, GitHub unreachable, rate-limited, malformed response, etc. — the check is a nicety,
            // not a requirement to use the app, so failure just means no update button this session.
            return null;
        }
    }

    // Same source MainLayout.razor's BuildVersion already reads — set at build time from the `<Version>`
    // MSBuild property (StoryForge.Web.csproj), overridden by the release workflow via -p:Version=X.Y.Z.
    private static string GetRunningVersion() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0-dev";

    // Both sides get their build-metadata suffix (the `+build-yyyyMMdd-HHmmss` SourceRevisionId stamp)
    // stripped before comparing — that suffix identifies a specific build, not a release version, and would
    // make every local dev build compare as "newer" than any tagged release.
    private static bool IsNewer(string latestTag, string runningVersion)
    {
        var latest = ParseVersion(latestTag);
        var running = ParseVersion(runningVersion);
        return latest != null && (running == null || latest > running);
    }

    private static Version? ParseVersion(string raw)
    {
        var trimmed = raw.Trim().TrimStart('v', 'V');
        var metadataIndex = trimmed.IndexOfAny(new[] { '+', '-' });
        if (metadataIndex >= 0)
            trimmed = trimmed[..metadataIndex];

        return Version.TryParse(trimmed, out var version) ? version : null;
    }
}
