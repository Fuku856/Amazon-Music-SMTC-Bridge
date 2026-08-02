using AmazonMusicSmtc.Interop;
using Windows.Media;
using Windows.Storage.Streams;

namespace AmazonMusicSmtc;

/// <summary>
/// Publishes this app's own SMTC session. Amazon Music's session cannot be
/// corrected in place - SMTC metadata is owned by the publishing process - so the
/// bridge stands up a second, complete session alongside it.
/// </summary>
internal sealed class SmtcPublisher : IDisposable
{
    private readonly Action<string> _log;
    private readonly ArtworkProvider _artwork;
    private readonly SystemMediaTransportControls _controls;
    private string? _currentArtworkPath;

    private TimeSpan? _duration;
    private TimeSpan _elapsedBeforeResume;
    private DateTime? _playingSinceUtc;

    public event Action<SystemMediaTransportControlsButton>? ButtonPressed;

    public SmtcPublisher(IntPtr hwnd, ArtworkProvider artwork, Action<string> log)
    {
        _log = log;
        _artwork = artwork;
        _controls = SmtcInterop.GetForWindow(hwnd);

        _controls.IsPlayEnabled = true;
        _controls.IsPauseEnabled = true;
        _controls.IsNextEnabled = true;
        _controls.IsPreviousEnabled = true;
        _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
        _controls.IsEnabled = false;

        _controls.ButtonPressed += (_, args) => ButtonPressed?.Invoke(args.Button);
    }

    public bool IsEnabled
    {
        get => _controls.IsEnabled;
        set => _controls.IsEnabled = value;
    }

    public MediaPlaybackStatus PlaybackStatus
    {
        get => _controls.PlaybackStatus;
        set
        {
            _controls.PlaybackStatus = value;
            SetClockRunning(value == MediaPlaybackStatus.Playing);
        }
    }

    /// <summary>
    /// Starts the position clock for a newly detected track.
    /// </summary>
    /// <remarks>
    /// Amazon Music's own SMTC session reports position 0 forever. On the
    /// notification path there is nothing better, so position is estimated locally
    /// from the track-change time and a seek inside Amazon Music desyncs it until
    /// the next track. The CDP path re-anchors it every poll - see
    /// <see cref="ReportPosition"/>. Seek requests are not accepted either way.
    /// </remarks>
    public void BeginTrack(TimeSpan? duration)
    {
        _duration = duration;
        _elapsedBeforeResume = TimeSpan.Zero;
        _playingSinceUtc = _controls.PlaybackStatus == MediaPlaybackStatus.Playing ? DateTime.UtcNow : null;
        PublishTimeline();
    }

    /// <summary>
    /// Re-anchors the clock to a position read straight out of the player, so the
    /// estimate only has to cover the gap until the next reading.
    /// </summary>
    public void ReportPosition(TimeSpan position, TimeSpan? duration)
    {
        if (duration is { } known && known > TimeSpan.Zero)
            _duration = known;

        _elapsedBeforeResume = position;
        _playingSinceUtc = _controls.PlaybackStatus == MediaPlaybackStatus.Playing ? DateTime.UtcNow : null;
        PublishTimeline();
    }

    private void SetClockRunning(bool running)
    {
        var now = DateTime.UtcNow;

        if (running)
        {
            _playingSinceUtc ??= now;
            return;
        }

        if (_playingSinceUtc is { } since)
        {
            _elapsedBeforeResume += now - since;
            _playingSinceUtc = null;
        }
    }

    private TimeSpan CurrentPosition =>
        _playingSinceUtc is { } since
            ? _elapsedBeforeResume + (DateTime.UtcNow - since)
            : _elapsedBeforeResume;

    public void PublishTimeline()
    {
        if (_duration is not { } duration)
            return;

        var position = CurrentPosition;
        if (position > duration)
            position = duration;

        _controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            Position = position,
            MaxSeekTime = duration,
            EndTime = duration,
        });
    }

    public async Task UpdateAsync(TrackInfo track)
    {
        var updater = _controls.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;

        updater.MusicProperties.Title = track.Title;
        updater.MusicProperties.Artist = track.Artist;
        updater.MusicProperties.AlbumTitle = track.Album;
        updater.MusicProperties.AlbumArtist = track.Artist;

        var thumbnail = await LoadThumbnailAsync(track);
        updater.Thumbnail = thumbnail;

        updater.Update();
        _controls.IsEnabled = true;

        ReplaceArtwork(track.ArtworkPath);
        _log($"published: {track}{(thumbnail is null ? " (no artwork)" : string.Empty)}");
    }

    public void Clear()
    {
        _controls.DisplayUpdater.ClearAll();
        _controls.DisplayUpdater.Update();
        _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
        _controls.IsEnabled = false;

        _duration = null;
        _elapsedBeforeResume = TimeSpan.Zero;
        _playingSinceUtc = null;

        ReplaceArtwork(null);
    }

    /// <summary>
    /// Hands SMTC an in-memory stream rather than a file path or a URL, so the temp
    /// copy can be deleted as soon as the next track arrives and nothing is fetched
    /// lazily behind our back.
    /// </summary>
    private async Task<RandomAccessStreamReference?> LoadThumbnailAsync(TrackInfo track)
    {
        var bytes = await _artwork.LoadAsync(track);
        if (bytes is null || bytes.Length == 0)
            return null;

        try
        {
            var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream.GetOutputStreamAt(0));
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
            stream.Seek(0);

            return RandomAccessStreamReference.CreateFromStream(stream);
        }
        catch (Exception ex)
        {
            _log($"thumbnail load failed: {ex.Message}");
            return null;
        }
    }

    private void ReplaceArtwork(string? newPath)
    {
        var old = _currentArtworkPath;
        _currentArtworkPath = newPath;

        if (old is null || string.Equals(old, newPath, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            File.Delete(old);
        }
        catch (Exception)
        {
            // Temp file cleanup is best effort.
        }
    }

    public void Dispose()
    {
        try
        {
            Clear();
        }
        catch (Exception)
        {
            // Shutting down anyway.
        }
    }
}
