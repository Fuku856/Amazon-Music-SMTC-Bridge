using Windows.Media;
using Windows.Media.Control;

namespace AmazonMusicSmtc;

internal static class Program
{
    /// <summary>
    /// The app name as users see it. Must match DisplayName in pkg\AppxManifest.xml.
    /// Release assets use the same name without spaces - see tools\pack-release.ps1.
    /// "AmazonMusic" is one word on purpose so that media tools filtering by app-name
    /// substring do not match an "Amazon Music" rule against this bridge.
    /// </summary>
    internal const string AppName = "AmazonMusic SMTC Bridge";

    [STAThread]
    private static void Main()
    {
        using var single = new Mutex(true, @"Local\AmazonMusicSmtc.SingleInstance", out var isOwner);
        if (!isOwner)
        {
            Log.Write("another instance already owns the single-instance mutex; exiting");
            return;
        }

        Log.Write("=== process start ===");

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new BridgeForm());
        }
        catch (Exception ex)
        {
            Log.Write($"!! fatal: {ex}");
            throw;
        }
    }
}

/// <summary>
/// Hidden host window. SMTC is acquired per-HWND, so the bridge needs a real
/// window even though it never shows one during normal operation.
/// </summary>
internal sealed class BridgeForm : Form
{
    /// <summary>
    /// How long a manual restart is given before the process watcher is allowed to
    /// call Amazon Music gone. Covers the measured ~13s replacement with room to
    /// spare, while bounding how long stale metadata can survive a failed attempt.
    /// </summary>
    private static readonly TimeSpan ManualRelaunchGrace = TimeSpan.FromSeconds(45);

    private readonly TextBox _log;
    private readonly NotifyIcon _tray;
    private readonly Settings _settings;
    private readonly ToolStripMenuItem[] _sourceItems;
    private readonly AmazonProcessWatcher _processes;

    private AmazonSessionWatcher _amazon = null!;
    private ArtworkProvider _artwork = null!;
    private AmazonMusicMonitor _monitor = null!;
    private CdpWatcher? _cdp;
    private NotificationWatcher? _notifications;
    private SmtcPublisher? _publisher;
    private System.Windows.Forms.Timer? _timelineTimer;
    private System.Windows.Forms.Timer? _monitorTimer;

    private TrackInfo? _currentTrack;
    private bool _initialized;
    private bool _exiting;
    private bool _polling;
    private bool _connecting;

    /// <summary>
    /// Whether the notification listener actually has permission. Distinct from
    /// <c>_notifications is not null</c>, which only means construction was
    /// attempted - access can still be denied. Gates
    /// <see cref="ApplyBannerSuppression"/> so the banner is never turned off
    /// without the removal side that is supposed to keep the Action Center clean.
    /// </summary>
    private bool _notificationsAllowed;

    private bool UsesCdp => _settings.MetadataSource is MetadataSource.Auto or MetadataSource.Cdp;

    private bool UsesNotifications => _settings.MetadataSource is MetadataSource.Auto or MetadataSource.Notification;

    public BridgeForm()
    {
        Text = Program.AppName;
        Width = 1000;
        Height = 560;
        // A normal taskbar window: the log can be minimised and restored like
        // anything else. It simply starts hidden - see SetVisibleCore.
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;

        _log = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9f),
        };
        Controls.Add(_log);

        _settings = Settings.Load();

        // Built here rather than in InitializeAsync so PublishTrack can never see a
        // null gate. It reports "not running" until the first poll, which is the
        // safe direction to be wrong in.
        _processes = new AmazonProcessWatcher(Write, PostToUi);

        var menu = new ContextMenuStrip();
        menu.Items.Add("ログを表示", null, (_, _) => ShowLog());
        menu.Items.Add(new ToolStripSeparator());

        _sourceItems =
        [
            SourceItem(MetadataSource.Auto, "自動",
                "デバッグポートが使えるときは CDP、使えないときは通知から取得します。"),
            SourceItem(MetadataSource.Cdp, "デバッグポートのみ",
                "通知を一切使いません。通知へのアクセス許可も要求しません。"),
            SourceItem(MetadataSource.Notification, "通知のみ",
                "Amazon Music を再起動せず、デバッグポートも開きません。Amazon Music にフォーカス中は曲情報が更新されません。"),
        ];

        var sourceMenu = new ToolStripMenuItem("曲情報の取得方式");
        sourceMenu.DropDownItems.AddRange(_sourceItems);
        menu.Items.Add(sourceMenu);

        menu.Items.Add("デバッグポート付きで再起動", null, (_, _) => RestartAmazonMusic());

        var relaunchItem = Toggle(
            "常にデバッグポート付きで動かす",
            "デバッグポート無しで動いている Amazon Music を見つけたら起動し直します。手動起動した場合も対象です。",
            _settings.AutoRelaunchAmazonMusic,
            value =>
            {
                _settings.AutoRelaunchAmazonMusic = value;
                ApplySourceSetting();
            });
        menu.Items.Add(relaunchItem);

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(Toggle(
            "Amazon Music の曲変更通知を通知センターから消す",
            "読み取った曲変更通知を通知センターから自動で削除します。ON にした時点で溜まっている分も消します。",
            _settings.RemoveNotificationsAfterProcessing,
            value =>
            {
                _settings.RemoveNotificationsAfterProcessing = value;
                if (_notifications is not null)
                {
                    _notifications.RemoveAfterProcessing = value;
                    if (value)
                        _ = _notifications.SweepAsync();
                }

                ApplyBannerSuppression();
            }));

        menu.Items.Add(Toggle(
            "曲変更通知のバナーも出さない",
            "Windows の通知設定で Amazon Music のバナー表示を OFF にします。上の設定と併用したときだけ効きます。"
            + "OFF に戻すと元の設定に戻ります。設定 > システム > 通知 からも確認できます。",
            _settings.SuppressNotificationBanner,
            value =>
            {
                _settings.SuppressNotificationBanner = value;
                ApplyBannerSuppression();
            }));

        menu.Items.Add(Toggle(
            "ジャケットをオフライン用に保存",
            "取得したジャケットを保存し、接続が無いときも表示できるようにします。",
            _settings.KeepArtworkCache,
            value =>
            {
                _settings.KeepArtworkCache = value;
                if (_artwork is not null)
                    _artwork.KeepCache = value;
            }));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitApplication());

        UpdateSourceChecks();

        var icon = LoadAppIcon();
        Icon = icon;

        _tray = new NotifyIcon
        {
            Icon = icon,
            Text = Program.AppName,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowLog();

        Log.LineWritten += AppendToView;
        FormClosing += OnFormClosing;
    }

    private ToolStripMenuItem SourceItem(MetadataSource source, string text, string tooltip)
    {
        var item = new ToolStripMenuItem(text) { Tag = source, ToolTipText = tooltip };
        item.Click += (_, _) =>
        {
            if (_settings.MetadataSource == source)
                return;

            _settings.MetadataSource = source;
            _settings.Save();
            UpdateSourceChecks();
            ApplySourceSetting();
            Write($"metadata source: {source}");
        };

        return item;
    }

    private void UpdateSourceChecks()
    {
        foreach (var item in _sourceItems)
            item.Checked = (MetadataSource)item.Tag! == _settings.MetadataSource;
    }

    private ToolStripMenuItem Toggle(string text, string tooltip, bool initial, Action<bool> onChanged)
    {
        var item = new ToolStripMenuItem(text)
        {
            CheckOnClick = true,
            Checked = initial,
            ToolTipText = tooltip,
        };

        item.CheckedChanged += (_, _) =>
        {
            onChanged(item.Checked);
            _settings.Save();
        };

        return item;
    }

    /// <summary>
    /// Initialization is driven from handle creation, not Load. Load only fires
    /// when a form is actually shown, and this one deliberately never is.
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        if (_initialized)
            return;

        _initialized = true;
        BeginInvoke(new Func<Task>(InitializeAsync));

        // With CDP the position is read back from the player; without it, it is
        // estimated locally. Either way it has to be pushed on a cadence.
        _timelineTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timelineTimer.Tick += (_, _) => _ = OnTimelineTickAsync();
        _timelineTimer.Start();

        _monitorTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _monitorTimer.Tick += (_, _) => _ = OnMonitorTickAsync();
        _monitorTimer.Start();
    }

    protected override void SetVisibleCore(bool value)
    {
        // Start hidden, but still force handle creation so SMTC has an HWND.
        if (!IsHandleCreated)
        {
            CreateHandle();
            value = false;
        }

        base.SetVisibleCore(value);
    }

    /// <summary>
    /// Loads the shipped icon, degrading to the stock one rather than failing to
    /// start if it is missing or unreadable.
    /// </summary>
    private static Icon LoadAppIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

        try
        {
            if (File.Exists(path))
                return new Icon(path);

            Log.Write($"app icon not found at {path}; using the system default");
        }
        catch (Exception ex)
        {
            Log.Write($"app icon could not be loaded ({ex.Message}); using the system default");
        }

        return SystemIcons.Application;
    }

    private void ShowLog()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private async Task InitializeAsync()
    {
        try
        {
            _artwork = new ArtworkProvider(Write) { KeepCache = _settings.KeepArtworkCache };

            _publisher = new SmtcPublisher(Handle, _artwork, Write);
            _publisher.ButtonPressed += OnButtonPressed;
            Write("SMTC session created");

            // Armed before any metadata source starts. The notification listener
            // replays the newest toast from the Action Center on start-up, and that
            // toast can belong to an Amazon Music run that has already ended.
            _processes.RunningChanged += OnAmazonRunningChanged;
            _processes.Poll();

            _amazon = new AmazonSessionWatcher(Write);
            _amazon.PlaybackStatusChanged += OnAmazonPlaybackStatusChanged;
            await _amazon.StartAsync();

            _cdp = new CdpWatcher(Write);
            _cdp.TrackDetected += track => BeginInvoke(() => PublishTrack(track, fromCdp: true));
            _cdp.PositionUpdated += (position, duration) =>
                BeginInvoke(() => _publisher?.ReportPosition(position, duration));

            if (_settings.RemoteDebuggingPort <= 0)
            {
                _settings.RemoteDebuggingPort = AmazonLauncher.PickFreePort();
                _settings.Save();
            }

            _monitor = new AmazonMusicMonitor(Write) { Port = _settings.RemoteDebuggingPort };
            _monitor.Relaunching += window => _processes.SuppressExitFor(window);

            Write($"metadata source: {_settings.MetadataSource}, debug port {_settings.RemoteDebuggingPort}");
            ApplySourceSetting();

            if (UsesCdp)
                await ConnectCdpAsync();
        }
        catch (Exception ex)
        {
            Write($"!! startup failed: {ex}");
        }
    }

    /// <summary>
    /// Brings the watchers in line with the current settings. Safe to call at any
    /// time - the source can be switched from the tray while the bridge is running.
    /// </summary>
    private void ApplySourceSetting()
    {
        _monitor.Enabled = UsesCdp && _settings.AutoRelaunchAmazonMusic;
        _monitor.Reset();

        if (!UsesCdp)
            _cdp?.Disconnect();

        if (UsesNotifications)
            _ = StartNotificationsAsync();

        // Covers Cdp-only mode, where StartNotificationsAsync never runs and so
        // never gets a chance to (re)apply this. Under Auto/Notification this is an
        // early pass against whatever _notificationsAllowed currently holds;
        // StartNotificationsAsync corrects it once real access is known.
        ApplyBannerSuppression();
    }

    /// <summary>
    /// Brings Windows' per-app banner setting in line with the notification
    /// toggles. Suppressing needs all three: both toggles on, and - whenever
    /// notifications are actually in use - permission actually granted. Without
    /// that last check, a denied prompt would leave banners off with nothing left
    /// to ever clear the Action Center, which is worse than either setting alone.
    /// </summary>
    private void ApplyBannerSuppression()
    {
        var canSuppress = _settings is { RemoveNotificationsAfterProcessing: true, SuppressNotificationBanner: true }
            && (!UsesNotifications || _notificationsAllowed);

        if (canSuppress)
            NotificationBannerSuppressor.Apply(_settings, Write);
        else
            NotificationBannerSuppressor.Restore(_settings, Write);

        // Apply/Restore record what they replaced, and that has to outlive the run
        // that made the change or the user's own setting cannot be put back.
        _settings.Save();
    }

    /// <summary>
    /// Starts the notification listener once. Never called under
    /// <see cref="MetadataSource.Cdp"/>, which is the point of that mode: the
    /// listener can read every app's toasts, so its permission is not requested
    /// unless the bridge might actually use it.
    /// </summary>
    private async Task StartNotificationsAsync()
    {
        if (_notifications is not null)
            return;

        try
        {
            _notifications = new NotificationWatcher(Write, () => _amazon.GetArtistNow())
            {
                RemoveAfterProcessing = _settings.RemoveNotificationsAfterProcessing,
            };
            _notifications.TrackDetected += track => BeginInvoke(() => PublishTrack(track, fromCdp: false));

            _notificationsAllowed = await _notifications.StartAsync();

            // Re-evaluated now that access is actually known - the pass in
            // ApplySourceSetting ran before this await and could not have known it.
            ApplyBannerSuppression();
        }
        catch (Exception ex)
        {
            Write($"!! notification listener failed to start: {ex.Message}");
        }
    }

    private async Task OnTimelineTickAsync()
    {
        if (_polling)
            return;

        _polling = true;
        try
        {
            if (_cdp is { IsConnected: true } cdp)
                await cdp.PollAsync();
            else
                _publisher?.PublishTimeline();
        }
        catch (Exception ex)
        {
            Write($"timeline tick failed: {ex.Message}");
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task OnMonitorTickAsync()
    {
        try
        {
            // The process gate applies in every metadata source mode, so it runs
            // ahead of the CDP-only work below. This only detects Amazon Music
            // starting; an exit arrives from Process.Exited within a moment.
            _processes.Poll();

            if (!UsesCdp || _cdp is null)
                return;

            _monitor.Tick(_cdp.IsConnected);

            if (!_cdp.IsConnected)
                await ConnectCdpAsync();
        }
        catch (Exception ex)
        {
            Write($"monitor tick failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Marshals a watcher callback onto the UI thread, dropping it if the form has
    /// gone away underneath it.
    /// </summary>
    private void PostToUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        try
        {
            BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
        catch (InvalidOperationException)
        {
            // The handle went away between the check and the call.
        }
    }

    private void OnAmazonRunningChanged(bool running)
    {
        if (running)
            return;

        // Whatever the session is showing belongs to a process that no longer
        // exists. The CDP socket died with it, so drop that too rather than waiting
        // for the next poll to notice.
        _cdp?.Disconnect();
        ClearSession("Amazon Music is not running");
    }

    /// <summary>
    /// The one way the bridge's SMTC session comes down. Idempotent, and silent when
    /// it is already down.
    /// </summary>
    private void ClearSession(string reason)
    {
        // Dropped either way, so that whatever comes back is republished in full and
        // the position clock is re-anchored rather than resumed mid-track.
        _currentTrack = null;

        // Amazon Music's own session goes away during a relaunch as surely as it
        // does when the user quits, and this path is reached for both. Holding here
        // is what keeps media widgets from blinking through a restart the bridge
        // asked for itself.
        if (_processes.IsRelaunching)
            return;

        if (_publisher is not { IsEnabled: true })
            return;

        _publisher.Clear();
        Write($"SMTC session cleared ({reason})");
    }

    /// <summary>
    /// Tries the configured port, then whatever Chromium last wrote to
    /// DevToolsActivePort - which is how an ephemeral port is discovered.
    /// </summary>
    private async Task ConnectCdpAsync()
    {
        if (_cdp is null || _cdp.IsConnected || _connecting)
            return;

        _connecting = true;
        try
        {
            var ports = new List<int>();
            if (_settings.RemoteDebuggingPort > 0)
                ports.Add(_settings.RemoteDebuggingPort);

            if (AmazonPaths.FindDevToolsPort() is { } discovered && !ports.Contains(discovered))
                ports.Add(discovered);

            foreach (var port in ports)
            {
                if (await _cdp.TryConnectAsync(port))
                    return;
            }
        }
        finally
        {
            _connecting = false;
        }
    }

    private void RestartAmazonMusic()
    {
        _monitor.Reset();
        _cdp?.Disconnect();

        // Opened here, on the UI thread, before the kill is dispatched: this is the
        // bridge's own doing, not the user quitting Amazon Music.
        _processes.SuppressExitFor(ManualRelaunchGrace);

        Task.Run(() => AmazonLauncher.Relaunch(_settings.RemoteDebuggingPort, Write));
    }

    /// <summary>
    /// Publishes a detected track. CDP wins whenever it is connected: it is the
    /// only source that keeps working while Amazon Music has focus, and unlike the
    /// toast it carries the real album rather than the playback context.
    /// </summary>
    private void PublishTrack(TrackInfo track, bool fromCdp)
    {
        if (_publisher is null)
            return;

        // A CDP poll still in flight, or a toast replayed out of the Action Center,
        // must not stand the session back up after Amazon Music has gone. Silent on
        // purpose: in notification mode a stale toast can arrive repeatedly.
        if (!_processes.IsRunning)
            return;

        if (!fromCdp && _cdp is { IsConnected: true })
            return;

        if (track.SameTrackAs(_currentTrack))
            return;

        _currentTrack = track;

        _ = PublishTrackAsync(track);
    }

    private async Task PublishTrackAsync(TrackInfo track)
    {
        if (_publisher is null)
            return;

        try
        {
            await _publisher.UpdateAsync(track);

            // UpdateAsync fetches artwork, so it can take seconds, and its last act
            // is IsEnabled = true. If Amazon Music died in that window the session
            // has just come back up behind an earlier Clear().
            if (!_processes.IsRunning)
            {
                ClearSession("Amazon Music exited while the track was being published");
                return;
            }

            ApplyPlaybackStatus(_amazon.GetPlaybackStatus());

            // CDP reports the exact length; the notification path has to go looking.
            var duration = track.Duration ?? DurationLookup.Find(track);
            _publisher.BeginTrack(duration);

            Write(duration is null
                ? "duration unknown (not in Amazon's catalog cache)"
                : $"duration: {duration:mm\\:ss}");
        }
        catch (Exception ex)
        {
            Write($"!! publish failed: {ex.Message}");
        }
    }

    private void OnAmazonPlaybackStatusChanged(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status) =>
        BeginInvoke(() => ApplyPlaybackStatus(status));

    private void ApplyPlaybackStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status)
    {
        if (_publisher is null)
            return;

        if (status is null || !_amazon.IsPresent || !_processes.IsRunning)
        {
            ClearSession("Amazon Music has no media session");
            return;
        }

        _publisher.PlaybackStatus = status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackStatus.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackStatus.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackStatus.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaPlaybackStatus.Closed,
            // Changing and Opening are both transient while a track loads. Holding
            // the current status keeps anything mirroring this session from
            // blinking on every skip.
            _ => _publisher.PlaybackStatus,
        };
    }

    private void OnButtonPressed(SystemMediaTransportControlsButton button)
    {
        // Our session is a facade; the real transport lives in Amazon Music.
        _ = button switch
        {
            SystemMediaTransportControlsButton.Play => _amazon.TryPlayAsync(),
            SystemMediaTransportControlsButton.Pause => _amazon.TryPauseAsync(),
            SystemMediaTransportControlsButton.Next => _amazon.TrySkipNextAsync(),
            SystemMediaTransportControlsButton.Previous => _amazon.TrySkipPreviousAsync(),
            _ => Task.FromResult(false),
        };
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // This form is the Application.Run main form, so closing it would end the
        // process. Closing the log means "put it away", not "stop bridging" -
        // only the tray's Exit item really quits.
        //
        // The check is a denylist rather than `== UserClosing`: the X button
        // arrives as WM_SYSCOMMAND/SC_CLOSE, but Alt+F4 and a bare WM_CLOSE come
        // through with CloseReason.None and would otherwise still kill the app.
        // Windows shutdown and Task Manager must always be allowed through.
        var forced = e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing;

        if (!_exiting && !forced)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _tray.Visible = false;
        _timelineTimer?.Stop();
        _monitorTimer?.Stop();
        // First, so no late exit callback can reach a disposed publisher.
        _processes.Dispose();
        _cdp?.Dispose();
        _publisher?.Dispose();
    }

    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    private static void Write(string message) => Log.Write(message);

    private void AppendToView(string line)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        try
        {
            if (_log.InvokeRequired)
                _log.BeginInvoke(() => _log.AppendText(line + Environment.NewLine));
            else
                _log.AppendText(line + Environment.NewLine);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }
}
