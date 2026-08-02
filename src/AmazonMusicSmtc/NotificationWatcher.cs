using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace AmazonMusicSmtc;

/// <summary>
/// Primary metadata source. Amazon Music's SMTC session only ever populates
/// Artist, but its track-change toast carries title, artist and album.
/// </summary>
internal sealed class NotificationWatcher
{
    /// <summary>
    /// Amazon Music's toast uses the legacy ToastImageAndText04 template, which
    /// Windows surfaces as a ToastGeneric binding with exactly three text lines:
    /// title, artist, album.
    /// </summary>
    private const int ExpectedTextElements = 3;

    private readonly Action<string> _log;
    private readonly Func<string?> _currentAmazonArtist;
    private UserNotificationListener? _listener;

    public event Action<TrackInfo>? TrackDetected;

    /// <summary>When true, processed Amazon notifications are removed from the Action Center.</summary>
    public bool RemoveAfterProcessing { get; set; }

    public NotificationWatcher(Action<string> log, Func<string?> currentAmazonArtist)
    {
        _log = log;
        _currentAmazonArtist = currentAmazonArtist;
    }

    public async Task<bool> StartAsync()
    {
        _listener = UserNotificationListener.Current;

        var status = await _listener.RequestAccessAsync();
        if (status != UserNotificationListenerAccessStatus.Allowed)
        {
            _log($"notification access denied ({status}); metadata cannot be read");
            return false;
        }

        _listener.NotificationChanged += OnNotificationChanged;
        _log("listening for Amazon Music track-change notifications");

        await CatchUpAsync();
        return true;
    }

    /// <summary>
    /// Amazon's notifications are not transient - they accumulate in the Action
    /// Center. Replaying the newest one means a bridge started mid-playback shows
    /// the current track immediately instead of staying blank until the next skip.
    /// </summary>
    private async Task CatchUpAsync()
    {
        if (_listener is null)
            return;

        try
        {
            var existing = await _listener.GetNotificationsAsync(NotificationKinds.Toast);

            // TryReadTrack copies artwork as a side effect, so it must run at most
            // once per candidate - hence the explicit loop rather than a predicate.
            foreach (var notification in existing.OrderByDescending(n => n.CreationTime))
            {
                if (!TryReadTrack(notification, out var track))
                    continue;

                _log($"catch-up track: {track}");
                TrackDetected?.Invoke(track);
                return;
            }
        }
        catch (Exception ex)
        {
            _log($"catch-up failed: {ex.Message}");
        }
    }

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind != UserNotificationChangedKind.Added)
            return;

        try
        {
            var notification = sender.GetNotification(args.UserNotificationId);
            if (notification is null)
                return;

            if (TryReadTrack(notification, out var track))
            {
                _log($"track: {track}");
                TrackDetected?.Invoke(track);

                if (RemoveAfterProcessing)
                    sender.RemoveNotification(args.UserNotificationId);
            }
        }
        catch (Exception ex)
        {
            _log($"notification handling failed: {ex.Message}");
        }
    }

    private bool TryReadTrack(UserNotification notification, out TrackInfo track)
    {
        track = null!;

        // Amazon Music's notifications throw "not implemented" from AppInfo, so the
        // publisher cannot be identified by AUMID. Anything with a *readable* AppInfo
        // is therefore definitely not Amazon Music and can be discarded.
        if (TryGetAumid(notification, out var aumid))
        {
            if (!aumid.Contains(AmazonPaths.SessionIdFragment, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        var binding = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        if (binding is null)
            return false;

        var texts = binding.GetTextElements();
        if (texts.Count != ExpectedTextElements)
            return false;

        var title = texts[0].Text?.Trim() ?? string.Empty;
        var artist = texts[1].Text?.Trim() ?? string.Empty;
        var album = texts[2].Text?.Trim() ?? string.Empty;

        if (title.Length == 0 || artist.Length == 0)
            return false;

        // Correlate against the one field Amazon's own SMTC session fills in
        // correctly. This is what actually establishes that the toast is Amazon's.
        var sessionArtist = _currentAmazonArtist();
        if (sessionArtist is null)
            return false;

        if (!string.Equals(sessionArtist.Trim(), artist, StringComparison.Ordinal))
        {
            _log($"ignoring toast (artist \"{artist}\" != session artist \"{sessionArtist}\")");
            return false;
        }

        track = new TrackInfo(title, artist, album)
        {
            ArtworkPath = CaptureArtwork(),
        };
        return true;
    }

    private static bool TryGetAumid(UserNotification notification, out string aumid)
    {
        try
        {
            aumid = notification.AppInfo?.AppUserModelId ?? string.Empty;
            return aumid.Length > 0;
        }
        catch (Exception)
        {
            aumid = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Copies the cover art out of Amazon's cache immediately, because the source
    /// file is a single fixed name that the next track change overwrites.
    /// </summary>
    private string? CaptureArtwork()
    {
        var source = AmazonPaths.FindNotificationArtwork();
        if (source is null)
            return null;

        try
        {
            var destination = Path.Combine(
                Path.GetTempPath(),
                $"amazonmusicsmtc-art-{Guid.NewGuid():N}.jpg");

            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var output = File.Create(destination))
            {
                input.CopyTo(output);
            }

            return destination;
        }
        catch (Exception ex)
        {
            _log($"artwork capture failed: {ex.Message}");
            return null;
        }
    }
}
