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
    private readonly HashSet<string> _rejectedAumids = new(StringComparer.OrdinalIgnoreCase);
    private UserNotificationListener? _listener;
    private bool _warnedShape;

    public event Action<TrackInfo>? TrackDetected;

    /// <summary>When true, Amazon's notifications are removed from the Action Center.</summary>
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

            foreach (var notification in existing.OrderByDescending(n => n.CreationTime))
            {
                if (!IsAmazonTrackToast(notification, out var toast))
                    continue;

                // Stop at the newest Amazon toast whether or not it correlates.
                // Amazon overwrites a single artwork file per track change, so the
                // cover on disk belongs to this toast and to no older one.
                if (TryMatchSession(toast, out var track))
                {
                    _log($"catch-up track: {track}");
                    TrackDetected?.Invoke(track);
                }

                break;
            }
        }
        catch (Exception ex)
        {
            _log($"catch-up failed: {ex.Message}");
        }

        await SweepAsync();
    }

    /// <summary>
    /// Clears every Amazon track-change toast out of the Action Center.
    /// </summary>
    /// <remarks>
    /// <see cref="OnNotificationChanged"/> only ever sees toasts that arrive while
    /// the bridge is running with the setting already on. Anything that piled up
    /// before that is never revisited, so turning the setting on - or starting at
    /// all - has to deal with the backlog explicitly.
    /// </remarks>
    public async Task SweepAsync()
    {
        var listener = _listener;
        if (listener is null || !RemoveAfterProcessing)
            return;

        try
        {
            var existing = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            var removed = 0;

            foreach (var notification in existing)
            {
                if (!IsAmazonTrackToast(notification, out var toast))
                    continue;

                if (TryRemove(listener, notification.Id, toast))
                    removed++;
            }

            if (removed > 0)
                _log($"removed {removed} Amazon notification(s) from the Action Center");
        }
        catch (Exception ex)
        {
            _log($"sweep failed: {ex.Message}");
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

            if (!IsAmazonTrackToast(notification, out var toast))
                return;

            // Publishing and removal are decided separately on purpose. The session
            // correlation below races Amazon's own SMTC session and can legitimately
            // fail; when it does the toast is still Amazon's and still has to go.
            if (TryMatchSession(toast, out var track))
            {
                _log($"track: {track}");
                TrackDetected?.Invoke(track);
            }

            if (RemoveAfterProcessing)
                TryRemove(sender, args.UserNotificationId, toast);
        }
        catch (Exception ex)
        {
            _log($"notification handling failed: {ex.Message}");
        }
    }

    private bool TryRemove(UserNotificationListener listener, uint id, ToastText toast)
    {
        try
        {
            // Logged for every removal: identification below is partly shape-based,
            // so this line is the only record of what was actually taken away.
            listener.RemoveNotification(id);
            _log($"removed notification: {toast.Title}");
            return true;
        }
        catch (Exception ex)
        {
            _log($"could not remove notification \"{toast.Title}\": {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Whether this is one of Amazon Music's track-change toasts. Identification
    /// only - it deliberately says nothing about whether the metadata is usable,
    /// so that a failed correlation cannot also cancel removal.
    /// </summary>
    private bool IsAmazonTrackToast(UserNotification notification, out ToastText toast)
    {
        toast = default;

        // Amazon Music's toasts are published under an AUMID that resolves to no
        // installed application, so AppInfo throws and the publisher cannot be
        // named. Anything with a *readable* AppInfo naming someone else is
        // therefore definitely not Amazon Music.
        var identified = TryGetAumid(notification, out var aumid);
        if (identified && !aumid.Contains(AmazonPaths.SessionIdFragment, StringComparison.OrdinalIgnoreCase))
        {
            if (_rejectedAumids.Add(aumid))
                _log($"ignoring toasts from {aumid}");

            return false;
        }

        var binding = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        if (binding is null)
            return false;

        var texts = binding.GetTextElements();
        if (texts.Count != ExpectedTextElements)
        {
            // Only interesting for an unidentified publisher: that is the set Amazon
            // Music is in, so a change to its toast layout would surface here.
            if (!identified)
                WarnShape($"{texts.Count} text elements, expected {ExpectedTextElements}");

            return false;
        }

        var title = texts[0].Text?.Trim() ?? string.Empty;
        var artist = texts[1].Text?.Trim() ?? string.Empty;

        if (title.Length == 0 || artist.Length == 0)
        {
            if (!identified)
                WarnShape("title or artist was empty");

            return false;
        }

        toast = new ToastText(title, artist, texts[2].Text?.Trim() ?? string.Empty);
        return true;
    }

    /// <summary>
    /// Once per process. A sweep walks every toast on the machine, so an unbounded
    /// version of this would bury the rest of the log.
    /// </summary>
    private void WarnShape(string detail)
    {
        if (_warnedShape)
            return;

        _warnedShape = true;
        _log($"unidentified toast did not match Amazon's layout ({detail})");
    }

    /// <summary>
    /// Correlates a toast against the one field Amazon's own SMTC session fills in
    /// correctly, which is what establishes that the metadata is current. A failure
    /// means the session has not caught up with the toast yet, not that the toast
    /// belongs to someone else.
    /// </summary>
    private bool TryMatchSession(ToastText toast, out TrackInfo track)
    {
        track = null!;

        var sessionArtist = _currentAmazonArtist();
        if (sessionArtist is null)
        {
            _log($"not publishing \"{toast.Title}\": Amazon Music's session reported no artist");
            return false;
        }

        if (!string.Equals(sessionArtist.Trim(), toast.Artist, StringComparison.Ordinal))
        {
            _log($"not publishing \"{toast.Title}\" (artist \"{toast.Artist}\" != session artist \"{sessionArtist}\")");
            return false;
        }

        track = new TrackInfo(toast.Title, toast.Artist, toast.Album)
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

    private readonly record struct ToastText(string Title, string Artist, string Album);
}
