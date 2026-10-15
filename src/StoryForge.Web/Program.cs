using Microsoft.AspNetCore.SignalR;
using StoryForge.Core;
using StoryForge.Core.Graph;
using StoryForge.Web.Components;
using StoryForge.Web.GraphEditor;
using StoryForge.Web.UpdateCheck;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
// Scoped (one load per circuit/tab), not a singleton: re-reads settings.json fresh each time a tab opens,
// same freshness guarantee GraphEditorState already gives the graph itself. Shared with the Settings page
// via DI so an edit there is immediately visible to GraphEditorState within the same circuit, with no
// separate reload/refresh plumbing needed.
builder.Services.AddScoped(_ => AppSettings.LoadOrDefault());
builder.Services.AddScoped<GraphEditorState>();
builder.Services.AddScoped<StoryForge.Web.CharacterCard.CharacterCardState>();
builder.Services.AddScoped<StoryForge.Web.StoryOutline.StoryOutlineState>();
builder.Services.AddScoped<StoryForge.Web.Pipeline.PipelineStatusState>();
builder.Services.AddScoped<StoryForge.Web.Cg.CgManagerState>();
builder.Services.AddScoped<StoryForge.Web.ReloadCoordinator>();
builder.Services.AddScoped<StoryForge.Web.FlowGraphFocusCoordinator>();
// Singleton, not scoped — see UpdateCheckService's own comment: one GitHub check result shared by the
// whole process, not one per browser tab/circuit.
builder.Services.AddSingleton<UpdateCheckService>();
// Singleton for the same reason — one per-machine cache file shared by every circuit (see its own comment).
builder.Services.AddSingleton<StoryForge.Web.Audio.AudioWaveformCacheService>();

// DetailedErrors surfaces the real reason in the browser console/reconnect UI instead of a bare
// "connection closed", and CircuitDiagnosticsHandler below logs every open/close/error to a plain file so
// it survives regardless of what the terminal happens to be showing at the time.
builder.Services.AddServerSideBlazor(options =>
{
    options.DetailedErrors = true;
});
builder.Services.Configure<HubOptions>(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);

    // The 存檔 round trip sends the whole graph's node positions + edges back from JS in one interop
    // call — for a few hundred nodes that easily clears SignalR's 32KB default MaximumReceiveMessageSize,
    // which silently kills the circuit (looks like a random disconnect, not an error) right as the export
    // payload comes back. This tool is a single local user, not a public multi-tenant hub, so there's no
    // reason to keep a small cap here.
    options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
});
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, CircuitDiagnosticsHandler>();

// 由 Launcher 啟動時：所有瀏覽器分頁都關閉一段時間後自行結束（見 IdleShutdownService）
builder.Services.AddSingleton<StoryForge.Web.ClientConnectionTracker>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler,
    StoryForge.Web.ClientConnectionCircuitHandler>();
builder.Services.AddHostedService<StoryForge.Web.IdleShutdownService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();

// Streams a cropped PNG for one portrait file — used by CharacterCardPanel's thumbnail gallery. The
// filename comes straight from the client, so it's re-derived through Path.GetFileName before touching
// disk to strip any path segments a request could try to smuggle in.
app.MapGet("/api/portraits/{fileName}", (string fileName, AppSettings settings) =>
{
    var projectRoot = StoryForge.Core.ProjectPaths.ResolveUnityProjectRoot();
    var portraitFolder = Path.Combine(projectRoot, "Assets",
        StoryForge.Core.Portrait.PortraitAssetScanner.PortraitFolderRelativePath.Replace('/', Path.DirectorySeparatorChar));
    var safeFileName = Path.GetFileName(fileName);
    var filePath = Path.Combine(portraitFolder, safeFileName);

    if (!File.Exists(filePath) || !StoryForge.Core.Portrait.PortraitAssetScanner.IsSupportedImageFile(filePath))
        return Results.NotFound();

    var png = StoryForge.Core.Portrait.PortraitThumbnailRenderer.RenderCroppedPng(
        filePath, settings.PortraitTopCutPercent, settings.PortraitBottomCutPercent);
    return Results.Bytes(png, "image/png");
});

// Streams one file from the game's own BGM/SFX folder for the BGM/音效 tabs' playback and browser-side
// waveform decoding. Same Path.GetFileName re-derivation as the portrait endpoint above, plus the category
// segment is mapped to a fixed folder (never used as a path itself), so only those two folders are
// reachable. Range processing on so <audio> can seek/loop without re-downloading the whole file.
app.MapGet("/api/audio/{category}/{fileName}", (string category, string fileName) =>
{
    StoryForge.Core.Audio.AudioCategory audioCategory;
    if (category == "bgm")
        audioCategory = StoryForge.Core.Audio.AudioCategory.Bgm;
    else if (category == "sfx")
        audioCategory = StoryForge.Core.Audio.AudioCategory.Sfx;
    else
        return Results.NotFound();

    var folder = StoryForge.Core.Audio.AudioAssetScanner.GetFolderPath(
        StoryForge.Core.ProjectPaths.ResolveUnityProjectRoot(), audioCategory);
    var filePath = Path.Combine(folder, Path.GetFileName(fileName));
    if (!File.Exists(filePath) || !StoryForge.Core.Audio.AudioAssetScanner.IsSupportedAudioFile(filePath))
        return Results.NotFound();

    var contentType = Path.GetExtension(filePath).ToLowerInvariant() switch
    {
        ".ogg" => "audio/ogg",
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".m4a" or ".aac" => "audio/aac",
        _ => "application/octet-stream",
    };
    return Results.File(filePath, contentType, enableRangeProcessing: true);
});

// The backed-up original of an adjusted clip (AudioOriginals/{BGM|SFX}/<guid>.<ext>), which the 調整音量
// dialog previews from. Only a 32-hex GUID is accepted and it's only ever used as a file-name prefix
// inside that one folder.
app.MapGet("/api/audio-original/{category}/{guid}", (string category, string guid) =>
{
    StoryForge.Core.Audio.AudioCategory audioCategory;
    if (category == "bgm")
        audioCategory = StoryForge.Core.Audio.AudioCategory.Bgm;
    else if (category == "sfx")
        audioCategory = StoryForge.Core.Audio.AudioCategory.Sfx;
    else
        return Results.NotFound();

    if (guid.Length != 32 || !guid.All(Uri.IsHexDigit))
        return Results.NotFound();

    var filePath = StoryForge.Core.Audio.AudioGainProcessor.FindOriginal(
        StoryForge.Core.ProjectPaths.ResolveUnityProjectRoot(), audioCategory, guid);
    if (filePath == null)
        return Results.NotFound();

    var contentType = Path.GetExtension(filePath).ToLowerInvariant() switch
    {
        ".ogg" => "audio/ogg",
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".m4a" or ".aac" => "audio/aac",
        _ => "application/octet-stream",
    };
    return Results.File(filePath, contentType, enableRangeProcessing: true);
});

// CG 管理分頁的預覽圖：只放行 CG 根目錄底下的 .png（預覽圖 / 美術 png），路徑來自 client 所以要確認落在根目錄內。
app.MapGet("/api/cg/image", (string path, AppSettings settings) =>
{
    var root = Path.GetFullPath(settings.CgRootFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var full = Path.GetFullPath(path);
    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
        !full.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        return Results.NotFound();

    // 預覽圖本來就是 512x288，直接送；美術的 1920 png 縮成預覽尺寸再送，卡片多的時候才不會一次載幾十 MB。
    if (StoryForge.Core.Cg.CgNaming.IsPreviewFile(full))
        return Results.File(full, "image/png");

    var key = $"{full}|{File.GetLastWriteTimeUtc(full).Ticks}";
    var png = CgImageCache.GetOrAdd(key, _ => StoryForge.Core.Cg.CgImageUtility.RenderLetterboxed(full,
        StoryForge.Core.Cg.CgCatalogConstants.PreviewWidth, StoryForge.Core.Cg.CgCatalogConstants.PreviewHeight));
    return Results.Bytes(png, "image/png");
});

// 沒有美術檔的差分：當場畫一張 512x288 暫代圖（與輸出至 Unity 的暫代圖同一套畫法）。
app.MapGet("/api/cg/placeholder", (string cg, string diff, int i, int n, int hue, string? memo) =>
{
    var png = StoryForge.Core.Cg.CgPlaceholderRenderer.RenderPng(cg, diff, i, n,
        StoryForge.Core.Cg.CgPlaceholderRenderer.GetHue(hue), memo ?? string.Empty,
        StoryForge.Core.Cg.CgCatalogConstants.PreviewWidth, StoryForge.Core.Cg.CgCatalogConstants.PreviewHeight);
    return Results.Bytes(png, "image/png");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// CG 管理分頁縮過的美術 png（key = 路徑|修改時間），檔案改了 key 就不同，不會拿到舊圖。
static partial class Program
{
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> CgImageCache = new();
}
