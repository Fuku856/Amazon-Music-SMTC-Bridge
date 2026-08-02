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

    public event Action<GlobalSystemMediaTransportControlsSessionPlaybackStatus?>? PlaybackStatusChanged;

    public AmazonSessionWatcher(Action<string> log) => _log = log;

    public bool IsPresent => _session is not null;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += (_, _) => Rebind();
        Rebind();
    }

    private void Rebind()
    {
        var previous = _session;

        var found = _manager?
            .GetSessions()
            .FirstOrDefault(s =>
                s.SourceAppUserModelId.Contains(AmazonPaths.SessionIdFragment, StringComparison.OrdinalIgnoreCase) ||
                s.SourceAppUserModelId.Contains(AmazonPaths.SessionIdExe, StringComparison.OrdinalIgnoreCase));

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
            _log("Amazon Music session gone");
            PlaybackStatusChanged?.Invoke(null);
            return;
        }

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
