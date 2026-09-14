using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace StoryForge.Launcher;

// Written by StoryForge.Web's UpdateDialog.razor to %LocalAppData%\StoryForge\update-request.json when the
// user confirms the "請更新" button — see this file's own ApplyUpdateAndRestart for why the actual
// download/swap/restart has to happen here rather than in the (about to be killed) web process.
internal sealed record UpdateRequest(string Tag, string ZipUrl);

// One-click daily-use launcher: builds/starts StoryForge.Web (Release, not the Debug build `dotnet watch`
// uses during active development), waits for it to come up, opens it in a dedicated app-mode Chrome window
// (its own isolated profile — no tabs/address bar, doesn't touch the user's normal Chrome windows/profile),
// then kills the server once that Chrome window is closed. WinExe output type means this runs with no
// console window — double-click and it just works, or fails silently into launcher-error.log.
internal static class Program
{
    private const string ServerUrl = "http://localhost:5194";

    // Windows taskbar/Start grouping and pin identity go by AUMID, not by exe path — Chrome's own AUMID
    // is generic, so without tagging our app window with a distinct one, "Pin to taskbar" on StoryForge
    // silently merges into (or no-ops against) an already-pinned regular Chrome icon. See AppUserModelId.cs.
    private const string AppId = "StoryForge.Launcher";

    private static void Main()
    {
        try
        {
            Run();
        }
        catch (Exception e)
        {
            LogError(e);
        }
    }

    private static void Run()
    {
        // Reaching this line at all means the last self-update (if any) already started successfully, so a
        // leftover backup from it is safe to discard — see ApplyUpdateAndRestart/SpawnLauncherSelfUpdateHelper.
        CleanupStaleLauncherBackup();

        var webExePath = ResolveOrBuildWebExePath();
        if (webExePath == null)
        {
            LogError(new FileNotFoundException("找不到 StoryForge.Web 且自動建置失敗，請先手動執行一次 dotnet build -c Release。"));
            return;
        }

        EnsureShortcutsTagged();

        using var server = StartServer(webExePath);
        if (!WaitForServerReady(TimeSpan.FromSeconds(30)))
        {
            LogError(new TimeoutException("等待伺服器啟動逾時（30 秒）。"));
            KillIfRunning(server);
            return;
        }

        var chromePath = ResolveChromeExecutablePath() ?? "chrome.exe";
        var profileDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StoryForge", "ChromeProfile");
        Directory.CreateDirectory(profileDir);

        using var chrome = Process.Start(new ProcessStartInfo(chromePath)
        {
            Arguments = $"--app={ServerUrl} --user-data-dir=\"{profileDir}\" --window-size=1400,900",
            UseShellExecute = true,
        });

        if (chrome != null)
            TagWindowWhenReady(chrome, TimeSpan.FromSeconds(5));

        var updateRequest = WaitForChromeExitOrUpdateRequest(chrome);
        KillIfRunning(server);

        if (updateRequest != null)
            ApplyUpdateAndRestart(updateRequest);
    }

    // Polls instead of blocking on chrome.WaitForExit() so a "請更新" click (which writes update-request.json
    // from the still-open browser tab) can interrupt the normal "wait for the window to close" flow — same
    // polling style as WaitForServerReady/TagWindowWhenReady above, not async/await, to match this file's
    // existing fully-synchronous shape.
    private static UpdateRequest? WaitForChromeExitOrUpdateRequest(Process? chrome)
    {
        while (true)
        {
            if (chrome == null || chrome.HasExited)
                return null;

            if (TryReadAndConsumeUpdateRequest(out var request))
            {
                KillIfRunning(chrome);
                return request;
            }

            chrome.WaitForExit(1000);
        }
    }

    private static bool TryReadAndConsumeUpdateRequest(out UpdateRequest? request)
    {
        request = null;
        var path = UpdateRequestPath;
        if (!File.Exists(path))
            return false;

        try
        {
            var json = File.ReadAllText(path);
            request = JsonSerializer.Deserialize<UpdateRequest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception e)
        {
            LogError(e);
        }
        finally
        {
            // Consume it either way — a malformed request should be discarded, not re-read forever.
            try { File.Delete(path); } catch { /* best-effort */ }
        }

        return request != null;
    }

    private static string UpdateRequestPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StoryForge", "update-request.json");

    // Downloads the release zip, applies the StoryForge.Web half directly (its process is already dead, so
    // its files aren't locked), then hands the StoryForge.Launcher half off to a detached helper script —
    // this process's own currently-executing files can't be overwritten by itself while it's still running.
    private static void ApplyUpdateAndRestart(UpdateRequest request)
    {
        try
        {
            var storyForgeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StoryForge");
            var downloadDir = Path.Combine(storyForgeDir, "update-download");
            var stagingDir = Path.Combine(storyForgeDir, "update-staging", SanitizeForPath(request.Tag));
            Directory.CreateDirectory(downloadDir);
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);

            var zipPath = Path.Combine(downloadDir, $"{SanitizeForPath(request.Tag)}.zip");
            DownloadFile(request.ZipUrl, zipPath);
            ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);

            // AppContext.BaseDirectory is .../src/StoryForge.Launcher/bin/Release/net8.0/ — the release zip
            // (built by .github/workflows/release.yml) mirrors this same relative layout under src/, so both
            // halves land exactly where ResolveOrBuildWebExePath already expects them, with no path changes.
            var launcherDir = AppContext.BaseDirectory;
            var webProjectDir = Path.GetFullPath(Path.Combine(launcherDir, "..", "..", "..", "..", "StoryForge.Web"));
            var webPublishDir = Path.Combine(webProjectDir, "bin", "Release", "net8.0", "publish");

            var stagedWebDir = Path.Combine(stagingDir, "src", "StoryForge.Web", "bin", "Release", "net8.0", "publish");
            var stagedLauncherDir = Path.Combine(stagingDir, "src", "StoryForge.Launcher", "bin", "Release", "net8.0");

            CopyDirectoryOverwrite(stagedWebDir, webPublishDir);
            SpawnLauncherSelfUpdateHelper(launcherDir, stagedLauncherDir);
        }
        catch (Exception e)
        {
            LogError(e);
        }
    }

    private static string SanitizeForPath(string value) =>
        string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static void DownloadFile(string url, string destinationPath)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StoryForge-Launcher");

        using var response = client.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        using var fileStream = File.Create(destinationPath);
        response.Content.CopyToAsync(fileStream).GetAwaiter().GetResult();
    }

    private static void CopyDirectoryOverwrite(string sourceDir, string destinationDir)
    {
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"更新包裡找不到預期的資料夾：{sourceDir}");

        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(destinationDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // The Launcher can't overwrite its own currently-loaded exe/dll files, so this writes a small detached
    // batch script that: waits for this exact process to actually exit (polling `tasklist`, not just firing
    // immediately — Process.Kill/exit isn't instantaneous), renames the current Launcher folder to `-old`
    // (the one-generation rollback safety net — see CleanupStaleLauncherBackup, which clears it on the next
    // successful normal startup), copies the staged new build into place, and relaunches. A plain detached
    // child process already survives this one exiting on Windows, so no Job Object plumbing is needed.
    private static void SpawnLauncherSelfUpdateHelper(string currentLauncherDir, string stagedLauncherDir)
    {
        var storyForgeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StoryForge");
        Directory.CreateDirectory(storyForgeDir);
        var helperPath = Path.Combine(storyForgeDir, "apply-launcher-update.bat");

        var normalizedLauncherDir = currentLauncherDir.TrimEnd(Path.DirectorySeparatorChar);
        var backupDir = Path.Combine(Path.GetDirectoryName(normalizedLauncherDir)!, "net8.0-old");
        var pid = Environment.ProcessId;
        var launcherExe = Path.Combine(currentLauncherDir, "StoryForge.Launcher.exe");

        var script = $"""
            @echo off
            :waitloop
            tasklist /fi "PID eq {pid}" 2>NUL | find /I "{pid}" >NUL
            if not errorlevel 1 (
                timeout /t 1 /nobreak >nul
                goto waitloop
            )

            if exist "{backupDir}" rmdir /s /q "{backupDir}"
            mkdir "{backupDir}"
            xcopy "{currentLauncherDir}*" "{backupDir}\" /e /i /y >nul
            xcopy "{stagedLauncherDir}\*" "{currentLauncherDir}" /e /i /y >nul
            start "" "{launcherExe}"
            del "%~f0"

            """;
        File.WriteAllText(helperPath, script);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{helperPath}\"")
        {
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });

        Environment.Exit(0);
    }

    private static void CleanupStaleLauncherBackup()
    {
        try
        {
            var launcherDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var backupDir = Path.Combine(Path.GetDirectoryName(launcherDir)!, "net8.0-old");
            if (Directory.Exists(backupDir))
                Directory.Delete(backupDir, recursive: true);
        }
        catch (Exception e)
        {
            LogError(e);
        }
    }

    // Re-stamps both shortcuts every launch (cheap — just overwrites two small .lnk files) so a pin made
    // today keeps working after a future rebuild moves/recreates the Launcher's own exe path.
    private static void EnsureShortcutsTagged()
    {
        var exePath = Path.Combine(AppContext.BaseDirectory, "StoryForge.Launcher.exe");
        var workingDir = AppContext.BaseDirectory;

        var startMenuShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "StoryForge.lnk");
        var desktopShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "StoryForge.lnk");

        foreach (var shortcutPath in new[] { startMenuShortcut, desktopShortcut })
        {
            try
            {
                AppUserModelId.EnsureShortcut(shortcutPath, exePath, workingDir, AppId, "StoryForge");
            }
            catch (Exception e)
            {
                // Best-effort — a stale/unpinnable shortcut isn't fatal to launching the app itself, but
                // log it so a pinning problem is actually diagnosable instead of silently swallowed.
                LogError(e);
            }
        }
    }

    // MainWindowHandle isn't populated the instant Process.Start returns — the window is created
    // asynchronously by Chrome's own startup, so poll briefly rather than tagging a zero handle.
    private static void TagWindowWhenReady(Process chrome, TimeSpan timeout)
    {
        var launcherExePath = Path.Combine(AppContext.BaseDirectory, "StoryForge.Launcher.exe");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            chrome.Refresh();
            if (chrome.HasExited)
                return;

            if (chrome.MainWindowHandle != IntPtr.Zero)
            {
                try
                {
                    AppUserModelId.SetForWindow(chrome.MainWindowHandle, AppId, launcherExePath, "StoryForge");
                    LogError(new Exception($"AUMID tag applied OK to hwnd=0x{chrome.MainWindowHandle:X} pid={chrome.Id}"));
                }
                catch (Exception e)
                {
                    // Best-effort — worst case the window just groups with regular Chrome. Logged (not just
                    // swallowed) since a pinning problem is otherwise undiagnosable from outside.
                    LogError(e);
                }

                return;
            }

            Thread.Sleep(100);
        }
    }

    // Prefers an already-published Release exe (fast path for everyday use) — only falls back to
    // publishing if one doesn't exist yet, so a normal launch doesn't pay a compile every time. Must be a
    // `dotnet publish` output specifically, not a plain `dotnet build` one: a build's own bin/<config>/net8.0
    // folder does NOT contain a physical wwwroot (static web assets there are only resolved via an
    // obj/staticwebassets manifest pointing back at the source tree, for `dotnet run`'s benefit) — every
    // page render that touches IWebHostEnvironment.WebRootPath (e.g. FlowGraphPanel.JsLastModified) would
    // null-ref and 500. Only `dotnet publish` actually copies wwwroot into the output directory.
    private static string? ResolveOrBuildWebExePath()
    {
        var launcherDir = AppContext.BaseDirectory;
        var webProjectDir = Path.GetFullPath(Path.Combine(launcherDir, "..", "..", "..", "..", "StoryForge.Web"));
        var publishDir = Path.Combine(webProjectDir, "bin", "Release", "net8.0", "publish");
        var releaseExe = Path.Combine(publishDir, "StoryForge.Web.exe");

        if (File.Exists(releaseExe))
            return releaseExe;

        var csprojPath = Path.Combine(webProjectDir, "StoryForge.Web.csproj");
        if (!File.Exists(csprojPath))
            return null;

        var publish = StartRedirected(
            new ProcessStartInfo("dotnet", $"publish \"{csprojPath}\" -c Release -o \"{publishDir}\""),
            "build-output.log");
        publish?.WaitForExit();

        return publish is { ExitCode: 0 } && File.Exists(releaseExe) ? releaseExe : null;
    }

    private static Process StartServer(string webExePath)
    {
        var startInfo = new ProcessStartInfo(webExePath)
        {
            WorkingDirectory = Path.GetDirectoryName(webExePath)!,
        };
        startInfo.EnvironmentVariables["ASPNETCORE_URLS"] = ServerUrl;

        return StartRedirected(startInfo, "server-output.log")!;
    }

    // Redirecting stdout/stderr without ever reading them deadlocks the child the moment it writes enough
    // to fill the OS pipe buffer (Kestrel's own startup logging is easily enough on its own) — the process
    // just sits there forever, looking to WaitForServerReady like it never started. BeginOutputReadLine /
    // BeginErrorReadLine drain both streams asynchronously as they arrive, so nothing can back up, while
    // still capturing the output to a log file for troubleshooting instead of silently discarding it.
    private static Process? StartRedirected(ProcessStartInfo startInfo, string logFileName)
    {
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StoryForge", logFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, $"=== {DateTime.Now:O} ==={Environment.NewLine}");

        var process = Process.Start(startInfo);
        if (process == null)
            return null;

        process.OutputDataReceived += (_, e) => AppendLogLine(logPath, e.Data);
        process.ErrorDataReceived += (_, e) => AppendLogLine(logPath, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private static void AppendLogLine(string logPath, string? line)
    {
        if (line == null)
            return;

        try
        {
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch
        {
            // Best-effort logging only.
        }
    }

    private static bool WaitForServerReady(TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = client.GetAsync(ServerUrl).GetAwaiter().GetResult();
                if ((int)response.StatusCode < 500)
                    return true;
            }
            catch
            {
                // Not up yet — keep polling.
            }

            Thread.Sleep(300);
        }

        return false;
    }

    private static string? ResolveChromeExecutablePath()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "Google", "Chrome", "Application", "chrome.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         "Google", "Chrome", "Application", "chrome.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Google", "Chrome", "Application", "chrome.exe"),
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort shutdown.
        }
    }

    private static void LogError(Exception e)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StoryForge", "launcher-error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"{DateTime.Now:O} {e}{Environment.NewLine}");
        }
        catch
        {
            // Nothing more we can do.
        }
    }
}
