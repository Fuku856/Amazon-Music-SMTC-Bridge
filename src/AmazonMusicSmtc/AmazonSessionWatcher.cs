using Windows.Foundation;
using Windows.Media.Control;

namespace AmazonMusicSmtc;

/// <summary>
/// Reads Amazon Music's own (incomplete) SMTC session. It supplies the two things
/// the notifications cannot: live playback state, and the Artist string used to
/// confirm that an incoming toast really belongs to Amazon Music.
/// </summary>
internal sealed class AmazonSessionWatcher
{
    private static readonly TimeSpan MediaPropertiesTimeout = TimeSpan.FromSeconds(2);

    private readonly Action<string> _log;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _boundSessionId;
    private string? _cachedArtist;
    private bool _reportedGone;

    public event Action<GlobalSystemMediaTransportControlsSessionPlaybackStatus?>? PlaybackStatusChanged;

    /// <summary>
    /// Raised when Windows picks a different current session. The current session is
    /// the one the media keys and most SMTC consumers target.
    /// </summary>
    public event Action? CurrentSessionChanged;

    public AmazonSessionWatcher(Action<string> log) => _log = log;

    public bool IsPresent => _session is not null;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += (_, _) => Rebind();
        _manager.CurrentSessionChanged += (_, _) => CurrentSessionChanged?.Invoke();
        Rebind();
    }

    /// <summary>
    /// Whether Amazon Music itself, rather than the bridge, currently owns the
    /// system's "most likely to control" session.
    /// </summary>
    public bool IsAmazonMusicCurrent()
    {
        try
        {
            var current = _manager?.GetCurrentSession();
            return current is not null && IsAmazonSession(current.SourceAppUserModelId);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsAmazonSession(string sourceAppUserModelId) =>
        sourceAppUserModelId.Contains(AmazonPaths.SessionIdFragment, StringComparison.OrdinalIgnoreCase) ||
        sourceAppUserModelId.Contains(AmazonPaths.SessionIdExe, StringComparison.OrdinalIgnoreCase);

    private void Rebind()
    {
        var previous = _session;

        var found = _manager?
            .GetSessions()
            .FirstOrDefault(s => IsAmazonSession(s.SourceAppUserModelId));

        // GetSessions() hands back a fresh projection wrapper every call, so
        // reference equality always fails. Comparing ids instead keeps SessionsChanged
        // from re-subscribing (and stacking event handlers) on every fire.
        if (previous is not null && found is not null &&
            string.Equals(_boundSessionId, found.SourceAppUserModelId, StringComparison.Ordinal))
        {
            return;
        }

        if (previous is not null)
        {
            previous.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            previous.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        }

        _session = found;
        _boundSessionId = found?.SourceAppUserModelId;

        if (found is null)
        {
            _cachedArtist = null;

            // SessionsChanged fires for every media app on the machine, and with no
            // Amazon session bound this branch is reached on every one of them.
            // Once the absence has been reported there is nothing new to say.
            if (previous is null && _reportedGone)
                return;

            _reportedGone = true;
            _log("Amazon Music session gone");
            PlaybackStatusChanged?.Invoke(null);
            return;
        }

        _reportedGone = false;
        found.PlaybackInfoChanged += OnPlaybackInfoChanged;
        found.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _log($"bound to Amazon Music session: {found.SourceAppUserModelId}");

        _ = RefreshArtistAsync();
        PlaybackStatusChanged?.Invoke(GetPlaybackStatus());
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, object args) =>
        PlaybackStatusChanged?.Invoke(GetPlaybackStatus());

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args) =>
        _ = RefreshArtistAsync();

    public GlobalSystemMediaTransportControlsSessionPlaybackStatus? GetPlaybackStatus()
    {
        try
        {
            return _session?.GetPlaybackInfo()?.PlaybackStatus;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Blocking artist read for use from the notification callback (which already
    /// runs on a threadpool thread). Falls back to the cached value, because
    /// TryGetMediaPropertiesAsync throws outright while playback is stopped.
    /// </summary>
    public string? GetArtistNow()
    {
        try
        {
            var fresh = RefreshArtistAsync().Wait(MediaPropertiesTimeout);
            if (fresh)
                return _cachedArtist;
        }
        catch (Exception)
        {
            // fall through to cache
        }

        return _cachedArtist;
    }

    private async Task RefreshArtistAsync()
    {
        var session = _session;
        if (session is null)
        {
            _cachedArtist = null;
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            _cachedArtist = props?.Artist;
        }
        catch (Exception)
        {
            // Expected while stopped; keep the last known artist.
        }
    }

    public async Task<bool> TryPlayAsync() => await Invoke(s => s.TryPlayAsync());
    public async Task<bool> TryPauseAsync() => await Invoke(s => s.TryPauseAsync());
    public async Task<bool> TrySkipNextAsync() => await Invoke(s => s.TrySkipNextAsync());
    public async Task<bool> TrySkipPreviousAsync() => await Invoke(s => s.TrySkipPreviousAsync());

    private async Task<bool> Invoke(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> action)
    {
        var session = _session;
        if (session is null)
            return false;

        try
        {
            return await action(session);
        }
        catch (Exception ex)
        {
            _log($"transport command failed: {ex.Message}");
            return false;
        }
    }
}
