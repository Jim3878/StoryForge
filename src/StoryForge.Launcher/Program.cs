using System.Diagnostics;

namespace StoryForge.Launcher;

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

        chrome?.WaitForExit();
        KillIfRunning(server);
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
