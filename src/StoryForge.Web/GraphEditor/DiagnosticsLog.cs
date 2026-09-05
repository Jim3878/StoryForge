namespace StoryForge.Web.GraphEditor;

// Temporary while chasing an unexplained circuit disconnect pattern — see CircuitDiagnosticsHandler. Plain
// file so it survives regardless of what the terminal happens to be showing at the time.
public static class DiagnosticsLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StoryForge", "circuit-diagnostics.log");

    public static void Write(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never be the thing that crashes the circuit.
        }
    }
}
