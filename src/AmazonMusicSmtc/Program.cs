using Windows.Media;
using Windows.Media.Control;

namespace AmazonMusicSmtc;

internal static class Program
{
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
    private readonly TextBox _log;
    private readonly NotifyIcon _tray;
    private readonly Settings _settings;
    private readonly ToolStripMenuItem[] _sourceItems;

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

    private bool UsesCdp => _settings.MetadataSource is MetadataSource.Auto or MetadataSource.Cdp;

    private bool UsesNotifications => _settings.MetadataSource is MetadataSource.Auto or MetadataSource.Notification;

    public BridgeForm()
    {
        Text = "Amazon Music SMTC Bridge";
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
                "Amazon Music を再起動せず、デバッグポートも開きません。Amazon Music にフォーカスがある間は曲が更新されません。"),
        ];

        var sourceMenu = new ToolStripMenuItem("曲情報の取得方式");
        sourceMenu.DropDownItems.AddRange(_sourceItems);
        menu.Items.Add(sourceMenu);

        menu.Items.Add("デバッグポート付きで再起動", null, (_, _) => RestartAmazonMusic());

        var relaunchItem = Toggle(
            "常にデバッグポート付きで動かす",
            "デバッグポート無しで動いている Amazon Music を見つけたら起動し直します。自分で起動した場合も対象です。",
            _settings.AutoRelaunchAmazonMusic,
            value =>
            {
                _settings.AutoRelaunchAmazonMusic = value;
                ApplySourceSetting();
            });
        menu.Items.Add(relaunchItem);

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(Toggle(
            "読み取った通知を非表示",
            "Amazon Music の曲変更通知は、消さないと通知センターに溜まり続けます。",
            _settings.RemoveNotificationsAfterProcessing,
            value =>
            {
                _settings.RemoveNotificationsAfterProcessing = value;
                if (_notifications is not null)
                    _notifications.RemoveAfterProcessing = value;
            }));

        menu.Items.Add(Toggle(
            "ジャケットをオフライン用に保存",
            "取得したジャケットをディスクに残し、接続が無いときも表示できるようにします。",
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
            Text = "Amazon Music SMTC Bridge",
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
            await _notifications.StartAsync();
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
        if (!UsesCdp || _cdp is null)
            return;

        try
        {
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

        if (status is null || !_amazon.IsPresent)
        {
            _currentTrack = null;
            _publisher.Clear();
            return;
        }

        _publisher.PlaybackStatus = status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackStatus.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackStatus.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackStatus.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => _publisher.PlaybackStatus,
            _ => MediaPlaybackStatus.Closed,
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
