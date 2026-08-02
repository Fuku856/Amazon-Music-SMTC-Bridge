using System.Text.Json;

namespace AmazonMusicSmtc;

/// <summary>
/// Reads the now playing track straight out of Amazon Music's renderer over CDP.
/// </summary>
/// <remarks>
/// Unlike the notification path this keeps working while Amazon Music has focus,
/// which is the whole reason it exists: Amazon Music does not raise a track-change
/// toast at all when it is the foreground window.
///
/// The metadata comes from the app's Vuex store rather than the DOM. The store
/// separates the track from its playback context, which matters: the transport
/// bar's second line is <c>containerInfo.containerName</c> - the playlist name -
/// and reporting that as the album would be wrong. The store also carries exact
/// millisecond position and duration.
///
/// Deliberately does nothing but Runtime.evaluate on a timer. Enabling protocol
/// domains, registering bindings or installing a document-start script all wedge
/// Amazon Music on its splash screen, and polling costs nothing that matters here:
/// a two second delay on a track change is invisible to a scrobbler.
/// </remarks>
internal sealed class CdpWatcher : IDisposable
{
    private readonly Action<string> _log;
    private CdpConnection? _connection;
    private string? _lastKey;

    public event Action<TrackInfo>? TrackDetected;

    /// <summary>Live position and total length, both straight from the player.</summary>
    public event Action<TimeSpan, TimeSpan?>? PositionUpdated;

    public CdpWatcher(Action<string> log) => _log = log;

    public bool IsConnected => _connection?.IsOpen == true;

    /// <summary>The port currently connected to, for logging and monitor decisions.</summary>
    public int Port { get; private set; }

    /// <summary>
    /// False when the Vuex store could not be found and the DOM fallback is in use,
    /// in which case the album is not available.
    /// </summary>
    public bool UsingStore { get; private set; }

    public async Task<bool> TryConnectAsync(int port)
    {
        if (IsConnected)
            return true;

        // Drop whatever closed on us before replacing it.
        Disconnect();

        if (!await CdpEndpoint.IsAliveAsync(port))
            return false;

        // Returns nothing until the app has finished starting up - attaching before
        // then stops it from ever mounting its UI.
        var url = await CdpEndpoint.FindReadyPageWebSocketUrlAsync(port);
        if (url is null)
            return false;

        var connection = await CdpConnection.ConnectAsync(url, _log);
        if (connection is null)
            return false;

        _connection = connection;
        Port = port;
        _lastKey = null;

        if (!await InstallAsync())
        {
            Disconnect();
            return false;
        }

        _log($"CDP connected on port {port}");
        await PollAsync();
        return true;
    }

    /// <summary>Evaluates the reader into the page. Idempotent.</summary>
    private async Task<bool> InstallAsync()
    {
        var connection = _connection;
        if (connection is null)
            return false;

        return await connection.EvaluateStringAsync(ReaderScript) is not null;
    }

    /// <summary>
    /// Pulls a fresh snapshot: this is both the track-change detector and the
    /// timeline source.
    /// </summary>
    public async Task PollAsync()
    {
        var connection = _connection;
        if (connection is null || !connection.IsOpen)
            return;

        var json = await connection.EvaluateStringAsync(
            $"window.{BridgeObject} ? window.{BridgeObject}.read() : ''");

        if (string.IsNullOrEmpty(json))
        {
            // A navigation threw the reader away; put it back and pick up next tick.
            await InstallAsync();
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // The app restores its last route before the player store is populated,
            // so a connection made right after a restart starts out on the DOM
            // fallback and switches over a poll or two later.
            var ready = root.TryGetProperty("ready", out var flag) && flag.GetBoolean();
            if (ready != UsingStore)
            {
                UsingStore = ready;
                _log(ready
                    ? "CDP: reading the player store"
                    : "CDP: player store unavailable, falling back to the DOM (album will be empty)");
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return;

            Handle(Snapshot.From(data));
        }
        catch (JsonException ex)
        {
            _log($"CDP snapshot parse failed: {ex.Message}");
        }
    }

    private void Handle(Snapshot? snapshot)
    {
        if (snapshot is null || snapshot.Title.Length == 0)
            return;

        if (snapshot.PositionMs is { } position)
            PositionUpdated?.Invoke(TimeSpan.FromMilliseconds(position), snapshot.DurationTimeSpan);

        if (string.Equals(snapshot.Key, _lastKey, StringComparison.Ordinal))
            return;

        _lastKey = snapshot.Key;

        TrackDetected?.Invoke(new TrackInfo(snapshot.Title, snapshot.Artist, snapshot.Album)
        {
            ArtworkUrl = snapshot.ArtworkUrl,
            Duration = snapshot.DurationTimeSpan,
        });
    }

    public void Disconnect()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null)
            return;

        UsingStore = false;
        _lastKey = null;
        connection.Dispose();
    }

    public void Dispose() => Disconnect();

    private sealed record Snapshot(
        string Key,
        string Title,
        string Artist,
        string Album,
        string? ArtworkUrl,
        double? DurationMs,
        double? PositionMs)
    {
        public TimeSpan? DurationTimeSpan =>
            DurationMs is > 0 ? TimeSpan.FromMilliseconds(DurationMs.Value) : null;

        public static Snapshot? From(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            var title = Text(element, "title");
            if (title.Length == 0)
                return null;

            return new Snapshot(
                Text(element, "key"),
                title,
                Text(element, "artist"),
                Text(element, "album"),
                Optional(element, "artworkUrl"),
                Number(element, "durationMs"),
                Number(element, "positionMs"));
        }

        private static string Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? string.Empty
                : string.Empty;

        private static string? Optional(JsonElement element, string name)
        {
            var text = Text(element, name);
            return text.Length == 0 ? null : text;
        }

        private static double? Number(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : null;
    }

    private const string BridgeObject = "__smtcBridge";

    /// <summary>
    /// Evaluated into the renderer once per connection. Pure reader: it installs no
    /// hooks, starts no timers and touches nothing the app owns.
    /// </summary>
    private const string ReaderScript = """
        (function () {
          var B = window.__smtcBridge;
          if (B && B.read) return 'reused';

          B = window.__smtcBridge = { store: null };

          function findStore() {
            var all = document.querySelectorAll('*');
            for (var i = 0; i < all.length; i++) {
              var vm = all[i].__vue__;
              if (vm && vm.$store && vm.$store.state && vm.$store.state.player) return vm.$store;
            }
            return null;
          }

          function num(v) { return (typeof v === 'number' && isFinite(v)) ? v : null; }

          function fromStore(store) {
            var p = store.state.player;
            if (!p || !p.model) return null;
            var cp = p.model.currentPlayable;
            var t = cp && cp.track;
            if (!t || !t.title) return null;
            var album = t.album || {};
            var artist = t.artist || {};
            var durationMs = num(p.model.duration);
            if (durationMs === null && num(t.duration) !== null) durationMs = t.duration * 1000;
            /* containerInfo is deliberately not read: it holds the playback context
               (playlist) name, which must never be reported as the album. */
            return {
              key: t.asin || (t.title + '|' + (artist.name || '')),
              title: t.title || '',
              artist: artist.name || '',
              album: album.name || '',
              artworkUrl: album.image || null,
              durationMs: durationMs,
              positionMs: p.progress ? num(p.progress.currentTime) : null
            };
          }

          function text(el) { return el ? (el.textContent || '').trim() : ''; }

          function seconds(s) {
            if (!s) return null;
            var parts = s.replace('-', '').split(':');
            var total = 0;
            for (var i = 0; i < parts.length; i++) {
              var n = Number(parts[i]);
              if (isNaN(n)) return null;
              total = total * 60 + n;
            }
            return total;
          }

          function fromDom() {
            var root = document.getElementById('transport');
            if (!root) return null;
            var meta = root.querySelector('.trackMetadata');
            var primary = meta && meta.querySelector('.primaryContainer');
            var title = primary ? (primary.getAttribute('title') || text(primary)) : '';
            if (!title) return null;
            var inner = meta ? meta.querySelectorAll('.secondaryText .secondaryInnerText') : [];
            var img = root.querySelector('.albumArt img.artImage');
            var pos = seconds(text(root.querySelector('.currentPlaybackPosition')));
            var rem = seconds(text(root.querySelector('.currentRemainingPosition')));
            var artist = inner.length > 0 ? text(inner[0]) : '';
            /* The second link here is the playback context, not the album. Leaving
               album empty beats reporting a playlist name as one. */
            return {
              key: title + '|' + artist,
              title: title,
              artist: artist,
              album: '',
              artworkUrl: img ? img.getAttribute('src') : null,
              durationMs: (pos !== null && rem !== null) ? (pos + rem) * 1000 : null,
              positionMs: pos !== null ? pos * 1000 : null
            };
          }

          B.read = function () {
            try {
              if (!B.store) B.store = findStore();
              var data = B.store ? fromStore(B.store) : null;
              if (!data) data = fromDom();
              return JSON.stringify({ ready: !!B.store, data: data });
            } catch (e) {
              return JSON.stringify({ ready: false, data: null });
            }
          };

          return 'installed';
        })()
        """;
}
