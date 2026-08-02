namespace AmazonMusicSmtc;

/// <summary>
/// File logger. Deliberately independent of the UI so that startup problems are
/// still recorded when no window is ever shown.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string Path =
        System.IO.Path.Combine(AppContext.BaseDirectory, "bridge.log");

    public static event Action<string>? LineWritten;

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";

        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics must never take the app down.
        }

        LineWritten?.Invoke(line);
    }
}
