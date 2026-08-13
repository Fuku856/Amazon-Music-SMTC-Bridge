using System.Diagnostics;

namespace AmazonMusicSmtc;

/// <summary>
/// Tracks whether Amazon Music is actually running. This is the gate that decides
/// whether the bridge may publish an SMTC session at all.
/// </summary>
/// <remarks>
/// Amazon Music's own SMTC session is not a death certificate: it can outlive the
/// process, and <c>SessionsChanged</c> is not guaranteed to reach us when it goes.
/// Watching the process is the one signal that cannot lie, which is why teardown
/// hangs off this rather than off <see cref="AmazonSessionWatcher"/> alone.
///
/// CEF's renderer and GPU children share the executable name "Amazon Music" and
/// come and go during normal use, so the anchor is the *oldest* match - the browser
/// process, which is their parent - and its Exited event is treated as "re-check
/// now" rather than as the verdict. The verdict always comes from a fresh
/// enumeration in <see cref="Poll"/>.
///
/// Everything here is UI-thread state. Exited fires on a threadpool thread and does
/// nothing but ask the UI thread to poll, so no field needs to be thread-safe.
/// </remarks>
internal sealed class AmazonProcessWatcher : IDisposable
{
    private readonly Action<string> _log;
    private readonly Action<Action> _post;

    private Process? _anchor;
    private bool _running;
    private bool _warnedNoExitEvent;
    private DateTime _suppressUntilUtc = DateTime.MinValue;
    private bool _disposed;

    /// <summary>
    /// Raised on transitions only, on whatever thread <c>post</c> marshals to.
    /// </summary>
    public event Action<bool>? RunningChanged;

    /// <param name="post">Marshals a callback onto the thread that owns this instance.</param>
    public AmazonProcessWatcher(Action<string> log, Action<Action> post)
    {
        _log = log;
        _post = post;
    }

    /// <summary>
    /// The last published verdict. Reads <c>true</c> for the duration of a
    /// suppression window - see <see cref="SuppressExitFor"/>.
    /// </summary>
    public bool IsRunning => _running;

    /// <summary>
    /// True while a relaunch the bridge asked for is still expected to complete.
    /// Amazon Music's own SMTC session disappears during one, so teardown driven by
    /// the session rather than the process has to check this too.
    /// </summary>
    public bool IsRelaunching => DateTime.UtcNow < _suppressUntilUtc;

    /// <summary>Re-evaluates presence and re-arms the exit hook. Owner thread only.</summary>
    public void Poll()
    {
        if (_disposed)
            return;

        // The common case: the anchor is still alive, so there is nothing to
        // enumerate. One handle check per tick.
        if (_anchor is not null && !HasExited(_anchor))
        {
            Publish(true);
            return;
        }

        ReleaseAnchor();

        var main = AmazonLauncher.FindMain();
        if (main is null)
        {
            Publish(false);
            return;
        }

        _anchor = main;
        WatchExit(main);
        Publish(true);
    }

    /// <summary>
    /// Holds back the "gone" verdict for a while, for a relaunch the bridge asked
    /// for itself. A deadline rather than a latch: if the relaunch never completes,
    /// the window expires and the next poll reports the truth.
    /// </summary>
    public void SuppressExitFor(TimeSpan window)
    {
        if (_disposed)
            return;

        _suppressUntilUtc = DateTime.UtcNow + window;
        _log($"holding the SMTC session for up to {window.TotalSeconds:0}s while Amazon Music restarts");
    }

    private void Publish(bool running)
    {
        if (running)
        {
            // A live process settles the question; nothing left to wait out.
            _suppressUntilUtc = DateTime.MinValue;
        }
        else if (DateTime.UtcNow < _suppressUntilUtc)
        {
            return;
        }

        if (running == _running)
            return;

        _running = running;
        _log(running ? "Amazon Music process detected" : "Amazon Music process gone");
        RunningChanged?.Invoke(running);
    }

    private void WatchExit(Process process)
    {
        try
        {
            process.Exited += OnAnchorExited;
            process.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            // Degrades to poll-only detection, which still closes the session -
            // just a tick later. Not worth failing over, and not worth repeating.
            if (!_warnedNoExitEvent)
            {
                _warnedNoExitEvent = true;
                _log($"cannot watch Amazon Music's exit ({ex.Message}); falling back to polling");
            }

            return;
        }

        // Closes the window between finding the process and subscribing to it.
        if (HasExited(process))
            _post(Poll);
    }

    private void OnAnchorExited(object? sender, EventArgs e) => _post(Poll);

    private void ReleaseAnchor()
    {
        var anchor = _anchor;
        _anchor = null;

        if (anchor is null)
            return;

        try
        {
            anchor.Exited -= OnAnchorExited;
            anchor.EnableRaisingEvents = false;
        }
        catch (Exception)
        {
            // Already gone; the Dispose below is what matters.
        }

        anchor.Dispose();
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception)
        {
            // No readable handle means we cannot vouch for it being alive.
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        RunningChanged = null;
        ReleaseAnchor();
    }
}
