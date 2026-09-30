using System.Diagnostics;

namespace StoryForge.Core.Audio;

// One ffmpeg invocation with both output streams drained — shared by 轉檔成 OGG (AudioOggConverter) and
// the 音量 features (AudioGainProcessor, which also reads the ebur128 loudness summary off stderr).
internal static class FfmpegProcess
{
    public static bool Run(string ffmpegPath, IEnumerable<string> args, int timeoutMs, out string stderr, out string error)
    {
        stderr = string.Empty;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null)
            {
                error = "無法啟動 ffmpeg";
                return false;
            }

            // Both streams drained concurrently — ffmpeg writes its whole progress log to stderr, and an
            // undrained redirected pipe blocks the child once the OS buffer fills (the same deadlock
            // StoryForge.Launcher already hit once).
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); } catch { }
                error = $"ffmpeg 逾時（{timeoutMs / 1000} 秒）";
                return false;
            }

            stderr = stderrTask.Result;
            if (process.ExitCode != 0)
            {
                error = $"ExitCode: {process.ExitCode}\n{stderr}\n{stdoutTask.Result}".Trim();
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }
}
