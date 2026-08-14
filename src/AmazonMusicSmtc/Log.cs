namespace AmazonMusicSmtc;

/// <summary>
/// File logger. Deliberately independent of the UI so that startup problems are
/// still recorded when no window is ever shown.
/// </summary>
internal static class Log
{
    /// <summary>
    /// Rotated at this size, keeping one generation - so the log costs at most
    /// twice this on disk and a long-running bridge cannot fill the drive.
    /// </summary>
    private const long MaxBytes = 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _path;
    private static bool _reportedFailure;

    public static event Action<string>? LineWritten;

    /// <summary>
    /// Resolved on first use rather than in a static initialiser, because
    /// <see cref="Settings.LocalFolder"/> belongs to a type whose own failure path
    /// logs back into here.
    /// </summary>
    /// <remarks>
    /// Must not be AppContext.BaseDirectory: for the packaged build that is the
    /// read-only package root under WindowsApps, so every write failed silently and
    /// the installed bridge produced no diagnostics at all.
    /// </remarks>
    private static string Path => _path ??= System.IO.Path.Combine(Settings.LocalFolder, "bridge.log");

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        string? failure = null;

        lock (Gate)
        {
            try
            {
                var path = Path;
                Rotate(path);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                // Diagnostics must never take the app down, but they must not vanish
                // either. Reported once per process: with no file, the log window is
                // the only record left and flooding it would defeat the point.
                if (!_reportedFailure)
                {
                    _reportedFailure = true;
                    failure = $"[{DateTime.Now:HH:mm:ss}] log file unavailable ({ex.Message}); " +
                              "this window is the only record for this run";
                }
            }
        }

        if (failure is not null)
            LineWritten?.Invoke(failure);

        LineWritten?.Invoke(line);
    }

    private static void Rotate(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxBytes)
            return;

        var previous = path + ".1";
        File.Delete(previous);
        File.Move(path, previous);
    }
}
