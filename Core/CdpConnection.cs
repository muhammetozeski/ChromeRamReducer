using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace ChromeRamReducer.Core;

/// <summary>
/// Minimal Chrome DevTools Protocol client.
/// One WebSocket is opened against the browser-level endpoint; every page, worker and extension
/// is then driven through a flattened session over that same socket.
/// </summary>
public sealed class CdpConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private Task? _pump;
    private int _nextId;

    public string BrowserVersion { get; private set; } = "unknown";

    public static async Task<CdpConnection> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(4) };

        string json = await http.GetStringAsync(
            $"http://127.0.0.1:{port}/json/version", cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        string wsUrl = root.TryGetProperty("webSocketDebuggerUrl", out JsonElement urlElement)
            ? urlElement.GetString() ?? throw new InvalidOperationException("Browser endpoint returned no WebSocket URL.")
            : throw new InvalidOperationException("Browser endpoint returned no WebSocket URL.");

        CdpConnection connection = new();
        connection.BrowserVersion = root.TryGetProperty("Browser", out JsonElement browser)
            ? browser.GetString() ?? "unknown"
            : "unknown";

        await connection._socket.ConnectAsync(new Uri(wsUrl), cancellationToken).ConfigureAwait(false);
        connection._pump = Task.Run(connection.PumpAsync);

        return connection;
    }

    /// <summary>Lists every attachable target, including extension pages and service workers.</summary>
    public async Task<IReadOnlyList<CdpTarget>> GetTargetsAsync(CancellationToken cancellationToken)
    {
        JsonElement result = await SendAsync("Target.getTargets", null, null, cancellationToken).ConfigureAwait(false);

        if (!result.TryGetProperty("targetInfos", out JsonElement infos))
        {
            return [];
        }

        List<CdpTarget> targets = [];

        foreach (JsonElement info in infos.EnumerateArray())
        {
            string id = info.GetProperty("targetId").GetString() ?? string.Empty;
            string type = info.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;
            string title = info.TryGetProperty("title", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
            string url = info.TryGetProperty("url", out JsonElement u) ? u.GetString() ?? string.Empty : string.Empty;

            if (id.Length > 0)
            {
                targets.Add(new CdpTarget(id, type, title, url));
            }
        }

        return targets;
    }

    /// <summary>Attaches to a target and returns its flattened session id, or null when it refuses.</summary>
    public async Task<string?> AttachAsync(string targetId, CancellationToken cancellationToken)
    {
        try
        {
            JsonElement result = await SendAsync(
                "Target.attachToTarget",
                new { targetId, flatten = true },
                null,
                cancellationToken).ConfigureAwait(false);

            return result.TryGetProperty("sessionId", out JsonElement sessionId) ? sessionId.GetString() : null;
        }
        catch (CdpException)
        {
            return null;
        }
    }

    /// <summary>Runs a full V8 major garbage collection inside the attached target.</summary>
    public Task CollectGarbageAsync(string sessionId, CancellationToken cancellationToken) =>
        SendAsync("HeapProfiler.collectGarbage", null, sessionId, cancellationToken);

    /// <summary>
    /// Asks the renderer to drop V8 memory the way Chrome does under an out-of-memory intervention.
    /// Caches are rebuilt on demand, so page state and scripts keep working.
    /// </summary>
    public Task PurgeJavaScriptMemoryAsync(string sessionId, CancellationToken cancellationToken) =>
        SendAsync("Memory.forciblyPurgeJavaScriptMemory", null, sessionId, cancellationToken);

    public Task DetachAsync(string sessionId, CancellationToken cancellationToken) =>
        SendAsync("Target.detachFromTarget", new { sessionId }, null, cancellationToken);

    private async Task<JsonElement> SendAsync(
        string method,
        object? parameters,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        int id = Interlocked.Increment(ref _nextId);
        TaskCompletionSource<JsonElement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        Dictionary<string, object> message = new()
        {
            ["id"] = id,
            ["method"] = method,
        };

        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        if (sessionId is not null)
        {
            message["sessionId"] = sessionId;
        }

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<JsonElement>)state!).TrySetCanceled(), completion);

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task PumpAsync()
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

        try
        {
            while (!_pumpCts.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                StringBuilder frame = new();
                WebSocketReceiveResult result;

                do
                {
                    result = await _socket.ReceiveAsync(buffer, _pumpCts.Token).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        FailAllPending(new CdpException("Chrome closed the DevTools connection."));
                        return;
                    }

                    frame.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                Dispatch(frame.ToString());
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            FailAllPending(new CdpException("DevTools connection dropped.", ex));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Dispatch(string frame)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(frame);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("id", out JsonElement idElement) || !idElement.TryGetInt32(out int id))
            {
                // An event, not a reply to one of our calls.
                return;
            }

            if (!_pending.TryRemove(id, out TaskCompletionSource<JsonElement>? completion))
            {
                return;
            }

            if (root.TryGetProperty("error", out JsonElement error))
            {
                string text = error.TryGetProperty("message", out JsonElement m)
                    ? m.GetString() ?? "unknown DevTools error"
                    : "unknown DevTools error";

                completion.TrySetException(new CdpException(text));
                return;
            }

            JsonElement payload = root.TryGetProperty("result", out JsonElement r)
                ? r.Clone()
                : default;

            completion.TrySetResult(payload);
        }
    }

    private void FailAllPending(Exception exception)
    {
        foreach (int key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out TaskCompletionSource<JsonElement>? completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _pumpCts.CancelAsync().ConfigureAwait(false);

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using CancellationTokenSource closeTimeout = new(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeTimeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Closing is best effort.
        }

        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch
            {
                // Already reported through pending calls.
            }
        }

        _socket.Dispose();
        _pumpCts.Dispose();
        _sendLock.Dispose();
    }
}

public sealed record CdpTarget(string TargetId, string Type, string Title, string Url);

public sealed class CdpException : Exception
{
    public CdpException(string message) : base(message)
    {
    }

    public CdpException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
