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
        if (_anchor is not null && !IsGone(_anchor))
        {
            Publish(true);
            return;
        }

        ReleaseAnchor();

        // Bounded on purpose. A process can die between being enumerated and being
        // subscribed to, and one more round settles that race - but re-enumerating
        // until it settles would spin forever against anything that keeps handing
        // the same unusable process back.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var main = AmazonLauncher.FindMain();
            if (main is null)
                break;

            _anchor = main;
            WatchExit(main);

            if (!IsGone(main))
            {
                Publish(true);
                return;
            }

            ReleaseAnchor();
        }

        Publish(false);
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
        // Seeing a live process must not cancel the window. It is opened while the
        // outgoing process is still up, and the replacement comes up before its own
        // SMTC session registers - cancelling on either sighting would reopen the
        // exact gap the window exists to cover. Only the deadline closes it.
        if (!running && DateTime.UtcNow < _suppressUntilUtc)
            return;

        if (running == _running)
            return;

        _running = running;
        _log(running ? "Amazon Music process detected" : "Amazon Music process gone");
        RunningChanged?.Invoke(running);
    }

    /// <summary>
    /// Subscribes to the anchor's exit. Deliberately does not re-check the process
    /// afterwards: <see cref="Poll"/> does that inline, and posting another Poll
    /// from here would be a call back into the method that just called us.
    /// </summary>
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
        }
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

    /// <summary>
    /// True only when the process is known to have ended.
    /// </summary>
    /// <remarks>
    /// The handle is held from enumeration onwards and stays readable across the
    /// exit, so a read that throws means something denied us the handle rather than
    /// that the process is gone. Reading it as "gone" would put this at odds with
    /// <see cref="AmazonLauncher.FindMain"/>, which hands the same live process
    /// straight back, and the two would trade turns for as long as it ran. Holding
    /// the session open instead degrades to the session-driven teardown in
    /// <c>ApplyPlaybackStatus</c>, which does not need a handle.
    /// </remarks>
    private static bool IsGone(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception)
        {
            return false;
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
