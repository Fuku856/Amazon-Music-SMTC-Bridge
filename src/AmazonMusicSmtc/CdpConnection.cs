using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmazonMusicSmtc;

/// <summary>
/// Discovery over Amazon Music's CEF DevTools HTTP endpoint.
/// </summary>
/// <remarks>
/// Always addressed as 127.0.0.1: the DevTools HTTP server validates the Host
/// header and an IP literal is accepted where a hostname may not be. The bridge
/// runs full trust (runFullTrust, so not in an AppContainer), which is why plain
/// loopback works without a network isolation exemption.
/// </remarks>
internal static class CdpEndpoint
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private static string Root(int port) => $"http://127.0.0.1:{port}";

    /// <summary>Confirms a DevTools server is actually answering on the port.</summary>
    public static async Task<bool> IsAliveAsync(int port, CancellationToken ct = default)
    {
        if (port is <= 0 or > 65535)
            return false;

        try
        {
            using var response = await Http.GetAsync($"{Root(port)}/json/version", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The WebSocket URL of Amazon Music's page target, but only once the app has
    /// finished starting up.
    /// </summary>
    /// <remarks>
    /// Attaching a DevTools client while the app is still booting leaves it stuck
    /// on its splash screen with Vue never mounted - measured repeatedly, and it
    /// recovers the moment nothing attaches during startup. So readiness is decided
    /// from plain HTTP, which attaches nothing: the target URL is bare
    /// "index.html" until the SPA routes, after which it carries a "#/" fragment.
    /// </remarks>
    public static async Task<string?> FindReadyPageWebSocketUrlAsync(int port, CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync($"{Root(port)}/json/list", ct);
            using var document = JsonDocument.Parse(json);

            foreach (var target in document.RootElement.EnumerateArray())
            {
                if (!target.TryGetProperty("type", out var type) || type.GetString() != "page")
                    continue;

                if (!target.TryGetProperty("url", out var pageUrl) ||
                    pageUrl.GetString() is not { } address ||
                    !address.Contains("#/", StringComparison.Ordinal))
                {
                    continue;
                }

                if (target.TryGetProperty("webSocketDebuggerUrl", out var url))
                    return url.GetString();
            }
        }
        catch (Exception)
        {
            // Treated as "no target yet"; the caller retries.
        }

        return null;
    }
}

/// <summary>
/// Minimal Chrome DevTools Protocol client: id-matched request/response over a
/// WebSocket, plus a stream of protocol events.
/// </summary>
internal sealed class CdpConnection : IDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly ClientWebSocket _socket;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();

    private int _nextId;
    private int _disposed;

    /// <summary>Protocol event: method name and its params object.</summary>
    public event Action<string, JsonElement>? EventReceived;

    /// <summary>Raised once when the socket goes away for any reason.</summary>
    public event Action? Closed;

    private CdpConnection(ClientWebSocket socket, Action<string> log)
    {
        _socket = socket;
        _log = log;
    }

    public static async Task<CdpConnection?> ConnectAsync(string webSocketUrl, Action<string> log)
    {
        var socket = new ClientWebSocket();

        // No keepalive pings. .NET sends one every 30s by default, and CEF 79's
        // DevTools server drops the connection on receiving them - the symptom was
        // a disconnect/reconnect cycle exactly 30 seconds apart. Liveness is
        // covered by the bridge's own two-second poll.
        socket.Options.KeepAliveInterval = TimeSpan.Zero;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(new Uri(webSocketUrl), timeout.Token);
        }
        catch (Exception ex)
        {
            log($"CDP connect failed: {ex.Message}");
            socket.Dispose();
            return null;
        }

        var connection = new CdpConnection(socket, log);
        _ = Task.Run(connection.ReceiveLoopAsync);
        return connection;
    }

    public bool IsOpen => _disposed == 0 && _socket.State == WebSocketState.Open;

    /// <summary>
    /// Sends a command and waits for its reply. Returns null on protocol errors,
    /// timeouts and disconnects - none of them should take the bridge down.
    /// </summary>
    public async Task<JsonElement?> CallAsync(string method, object? parameters = null)
    {
        if (!IsOpen)
            return null;

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new CdpRequest { Id = id, Method = method, Params = parameters }, RequestOptions);

            await _sendLock.WaitAsync(_cts.Token);
            try
            {
                await _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, _cts.Token);
            }
            finally
            {
                _sendLock.Release();
            }

            using var timeout = new CancellationTokenSource(CallTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _cts.Token);

            return await completion.Task.WaitAsync(linked.Token);
        }
        catch (Exception ex)
        {
            if (_disposed == 0)
                _log($"CDP {method} failed: {ex.Message}");

            return null;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Evaluates an expression in the page and returns its value, which the
    /// injected scripts always produce as a JSON string.
    /// </summary>
    public async Task<string?> EvaluateStringAsync(string expression)
    {
        var result = await CallAsync("Runtime.evaluate", new
        {
            expression,
            returnByValue = true,
            awaitPromise = true,
        });

        if (result is not { } value)
            return null;

        if (value.TryGetProperty("exceptionDetails", out var exception))
        {
            _log($"CDP evaluate threw: {Describe(exception)}");
            return null;
        }

        if (value.TryGetProperty("result", out var inner) &&
            inner.TryGetProperty("value", out var payload) &&
            payload.ValueKind == JsonValueKind.String)
        {
            return payload.GetString();
        }

        return null;
    }

    private static string Describe(JsonElement exceptionDetails)
    {
        if (exceptionDetails.TryGetProperty("exception", out var exception) &&
            exception.TryGetProperty("description", out var description))
        {
            return description.GetString() ?? "unknown";
        }

        return exceptionDetails.TryGetProperty("text", out var text)
            ? text.GetString() ?? "unknown"
            : "unknown";
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer, _cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                Dispatch(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                message.SetLength(0);
            }
        }
        catch (Exception ex)
        {
            if (_disposed == 0)
                _log($"CDP receive loop ended: {ex.Message}");
        }
        finally
        {
            FailPending();
            Closed?.Invoke();
        }
    }

    private void Dispatch(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
            {
                if (!_pending.TryRemove(id, out var completion))
                    return;

                if (root.TryGetProperty("error", out var error))
                    completion.TrySetException(new InvalidOperationException(error.ToString()));
                else if (root.TryGetProperty("result", out var result))
                    completion.TrySetResult(result.Clone());
                else
                    completion.TrySetResult(default);

                return;
            }

            if (root.TryGetProperty("method", out var method))
            {
                var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
                EventReceived?.Invoke(method.GetString() ?? string.Empty, parameters);
            }
        }
        catch (Exception ex)
        {
            _log($"CDP message parse failed: {ex.Message}");
        }
    }

    private void FailPending()
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var completion))
                completion.TrySetCanceled();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _cts.Cancel();
        }
        catch (Exception)
        {
            // Nothing useful to do while tearing down.
        }

        FailPending();
        _socket.Dispose();
        _cts.Dispose();
        _sendLock.Dispose();
    }

    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class CdpRequest
    {
        [JsonPropertyName("id")] public int Id { get; init; }

        [JsonPropertyName("method")] public string Method { get; init; } = string.Empty;

        [JsonPropertyName("params")] public object? Params { get; init; }
    }
}
