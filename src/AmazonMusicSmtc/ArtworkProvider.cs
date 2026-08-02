using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace AmazonMusicSmtc;

/// <summary>
/// Turns whatever cover art reference a track carries into bytes for SMTC.
/// </summary>
/// <remarks>
/// The notification path hands over a local file; the CDP path hands over a URL on
/// Amazon's public image CDN, which needs no cookies. Caching is opt-in, so by
/// default nothing is written to disk and a cover that cannot be fetched simply
/// means no thumbnail for that track.
/// </remarks>
internal sealed partial class ArtworkProvider
{
    /// <summary>
    /// Amazon's image URLs take size modifiers. The full-size original is around
    /// 1400x1400 / 100 KB, which is far more than a transport thumbnail needs;
    /// the 500 px variant is about 27 KB.
    /// </summary>
    private const string SizeModifier = "_SX500_";

    private const int MaxCacheEntries = 100;

    /// <summary>Per-address connect budget before moving on to the next one.</summary>
    private static readonly TimeSpan ConnectAttempt = TimeSpan.FromSeconds(3);

    private static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// Builds a client that walks the resolved addresses IPv4 first.
    /// </summary>
    /// <remarks>
    /// Networks that advertise IPv6 without actually carrying it are common, and
    /// HttpClient has no Happy Eyeballs: it sits in SYN_SENT on the first address
    /// until the whole request times out. Chromium papers over the same broken path
    /// in a few hundred milliseconds, so Amazon Music shows the cover while the
    /// bridge saw nothing but timeouts. Falling through rather than forcing IPv4
    /// keeps IPv6-only networks working.
    /// </remarks>
    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, token) =>
            {
                var host = context.DnsEndPoint.Host;
                var port = context.DnsEndPoint.Port;

                var addresses = await Dns.GetHostAddressesAsync(host, token);

                foreach (var address in addresses.OrderBy(
                             a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true,
                    };

                    try
                    {
                        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                        attempt.CancelAfter(ConnectAttempt);

                        await socket.ConnectAsync(address, port, attempt.Token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception)
                    {
                        socket.Dispose();
                        token.ThrowIfCancellationRequested();
                    }
                }

                throw new IOException($"no reachable address for {host}:{port}");
            },
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private readonly Action<string> _log;

    public ArtworkProvider(Action<string> log) => _log = log;

    /// <summary>When true, fetched covers are kept on disk and reused offline.</summary>
    public bool KeepCache { get; set; }

    [GeneratedRegex(@"^(?<root>https://[^/]*media-amazon\.com/images/I/)(?<id>[^./]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AmazonImage();

    public async Task<byte[]?> LoadAsync(TrackInfo track)
    {
        // The notification path already copied the cover somewhere private.
        if (track.ArtworkPath is { } path)
            return await ReadFileAsync(path);

        if (track.ArtworkUrl is not { } url)
            return null;

        var match = AmazonImage().Match(url);
        var id = match.Success ? match.Groups["id"].Value : null;

        if (KeepCache && id is not null && CachePath(id) is { } cached && File.Exists(cached))
            return await ReadFileAsync(cached);

        var bytes = await DownloadAsync(match.Success ? Resize(match) : url);

        if (bytes is not null)
        {
            if (KeepCache && id is not null)
                await StoreAsync(id, bytes);

            return bytes;
        }

        if (!KeepCache)
            return null;

        // Offline: Amazon's own notification cover is the only local copy left.
        // It is only reached with caching on, because that file belongs to the last
        // toast Amazon raised - which, while its window has focus, may be a
        // different track entirely.
        return AmazonPaths.FindNotificationArtwork() is { } fallback
            ? await ReadFileAsync(fallback)
            : null;
    }

    private static string Resize(Match match) =>
        $"{match.Groups["root"].Value}{match.Groups["id"].Value}.{SizeModifier}.jpg";

    private async Task<byte[]?> DownloadAsync(string url)
    {
        try
        {
            return await Http.GetByteArrayAsync(url);
        }
        catch (Exception ex)
        {
            _log($"artwork download failed: {ex.Message}");
            return null;
        }
    }

    private async Task<byte[]?> ReadFileAsync(string path)
    {
        try
        {
            return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
        }
        catch (Exception ex)
        {
            _log($"artwork read failed: {ex.Message}");
            return null;
        }
    }

    private static string? CacheDirectory
    {
        get
        {
            try
            {
                return Path.Combine(Settings.LocalFolder, "artwork");
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private static string? CachePath(string id) =>
        CacheDirectory is { } directory ? Path.Combine(directory, id + ".jpg") : null;

    private async Task StoreAsync(string id, byte[] bytes)
    {
        try
        {
            if (CacheDirectory is not { } directory || CachePath(id) is not { } path)
                return;

            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(path, bytes);
            Evict(directory);
        }
        catch (Exception ex)
        {
            _log($"artwork cache write failed: {ex.Message}");
        }
    }

    /// <summary>Keeps the cache bounded, dropping the least recently written first.</summary>
    private static void Evict(string directory)
    {
        try
        {
            var files = new DirectoryInfo(directory).GetFiles("*.jpg");
            if (files.Length <= MaxCacheEntries)
                return;

            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - MaxCacheEntries))
                file.Delete();
        }
        catch (Exception)
        {
            // Cache hygiene is best effort.
        }
    }
}
