namespace AmazonMusicSmtc;

/// <summary>
/// Keeps Amazon Music running with a debug port so the CDP path stays available,
/// including when the user starts Amazon Music normally from the Start menu.
/// </summary>
/// <remarks>
/// Chromium cannot start its DevTools server after the fact, so the only way to
/// pick up an instance launched without the flag is to replace it. That makes the
/// guards below the important part of this class: without them a relaunch that
/// never brings the port up turns into an endless kill/start loop.
/// </remarks>
internal sealed class AmazonMusicMonitor
{
    /// <summary>How long a newly seen process is left alone before replacing it.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Grace after a relaunch. Doubles as the verdict window: the port takes a few
    /// seconds to open, and if it has not by the end of this, the attempt failed.
    /// </summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    private const int MaxConsecutiveFailures = 3;

    private readonly Action<string> _log;

    private DateTime? _firstSeenRunningUtc;
    private DateTime _nextAttemptUtc = DateTime.MinValue;
    private bool _awaitingVerdict;
    private int _failures;

    public AmazonMusicMonitor(Action<string> log) => _log = log;

    /// <summary>Set from the metadata source and the auto-relaunch setting.</summary>
    public bool Enabled { get; set; }

    /// <summary>Port to request. 0 asks Chromium for an ephemeral one.</summary>
    public int Port { get; set; }

    /// <summary>True once the bridge has stopped trying, until something resets it.</summary>
    public bool HasGivenUp { get; private set; }

    /// <summary>
    /// Drives one step of the state machine. Returns true when Amazon Music was
    /// just relaunched, so the caller knows a reconnect attempt is worth queuing.
    /// </summary>
    public bool Tick(bool cdpConnected)
    {
        if (cdpConnected)
        {
            Reset();
            return false;
        }

        if (!Enabled || HasGivenUp)
            return false;

        var now = DateTime.UtcNow;
        if (now < _nextAttemptUtc)
            return false;

        if (_awaitingVerdict)
        {
            _awaitingVerdict = false;
            Fail("Amazon Music restarted but its debug port never answered");

            if (HasGivenUp)
                return false;
        }

        if (!AmazonLauncher.IsRunning())
        {
            // Nothing to attach to. The bridge deliberately does not start Amazon
            // Music by itself - the user is not asking to listen to anything.
            _firstSeenRunningUtc = null;
            return false;
        }

        _firstSeenRunningUtc ??= now;
        if (now - _firstSeenRunningUtc.Value < Settle)
            return false;

        _firstSeenRunningUtc = null;
        _nextAttemptUtc = now + Cooldown;

        _log("Amazon Music is running without a debug port; restarting it");

        if (AmazonLauncher.Relaunch(Port, _log))
        {
            _awaitingVerdict = true;
            return true;
        }

        Fail("relaunch failed");
        return false;
    }

    /// <summary>Clears the give-up latch, for a manual retry from the tray menu.</summary>
    public void Reset()
    {
        _failures = 0;
        _awaitingVerdict = false;
        _firstSeenRunningUtc = null;
        _nextAttemptUtc = DateTime.MinValue;
        HasGivenUp = false;
    }

    private void Fail(string reason)
    {
        if (++_failures < MaxConsecutiveFailures)
        {
            _log($"{reason} ({_failures}/{MaxConsecutiveFailures})");
            return;
        }

        HasGivenUp = true;
        _log($"!! {reason}. Giving up after {MaxConsecutiveFailures} attempts - " +
             "use the tray menu to restart Amazon Music manually and try again.");
    }
}
