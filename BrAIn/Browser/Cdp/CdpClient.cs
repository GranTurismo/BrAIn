using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace BrAIn.Browser.Cdp;

/// <summary>
/// Minimal Chrome DevTools Protocol client over WebSocket.
/// Supports flattened sessions (Target.attachToTarget flatten:true) so one
/// socket multiplexes every WebView2 page target.
/// </summary>
public sealed class CdpClient : IAsyncDisposable
{
    public sealed record TargetInfo(string TargetId, string Type, string Title, string Url);

    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _nextId;
    private Task? _reader;

    public event EventHandler<(string? SessionId, string Method, JsonElement Params)>? EventReceived;
    public string? BrowserWebSocketUrl { get; private set; }

    private CdpClient() { }

    public static async Task<CdpClient> ConnectAsync(int port, CancellationToken ct)
    {
        var client = new CdpClient();
        await client.ConnectInternalAsync(port, ct);
        return client;
    }

    private async Task ConnectInternalAsync(int port, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var baseUrl = $"http://127.0.0.1:{port}";

        // Wait for the debugging endpoint (browser process may still be starting).
        string? browserWs = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var json = await http.GetStringAsync($"{baseUrl}/json/version", ct);
                using var doc = JsonDocument.Parse(json);
                browserWs = doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString();
                if (!string.IsNullOrEmpty(browserWs)) break;
            }
            catch when (!ct.IsCancellationRequested)
            {
                await Task.Delay(250, ct);
            }
        }

        if (browserWs is null)
            throw new InvalidOperationException(
                $"CDP endpoint did not come up on 127.0.0.1:{port}. " +
                "WebView2 may have ignored --remote-debugging-port.");

        BrowserWebSocketUrl = browserWs;
        await _ws.ConnectAsync(new Uri(browserWs), ct);
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>List all DevTools targets (pages, workers, ...).</summary>
    public async Task<IReadOnlyList<TargetInfo>> GetTargetsAsync(CancellationToken ct)
    {
        var result = await SendAsync("Target.getTargets", null, null, ct);
        var list = new List<TargetInfo>();
        foreach (var t in result.GetProperty("targetInfos").EnumerateArray())
        {
            list.Add(new TargetInfo(
                t.GetProperty("targetId").GetString()!,
                t.GetProperty("type").GetString()!,
                t.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "",
                t.TryGetProperty("url", out var u) ? u.GetString() ?? "" : ""));
        }
        return list;
    }

    /// <summary>Attach to a target; returns a session to send page-level commands through.</summary>
    public async Task<CdpSession> AttachToTargetAsync(string targetId, CancellationToken ct)
    {
        var result = await SendAsync("Target.attachToTarget",
            new { targetId, flatten = true }, null, ct);
        var sessionId = result.GetProperty("sessionId").GetString()!;
        return new CdpSession(this, sessionId, targetId);
    }

    /// <summary>Auto-attach to future targets (pages created by new windows).</summary>
    public Task<JsonElement> SetAutoAttachAsync(CancellationToken ct) =>
        SendAsync("Target.setAutoAttach",
            new { autoAttach = true, waitForDebuggerOnStart = false, flatten = true }, null, ct);

    internal async Task<JsonElement> SendAsync(
        string method, object? parameters, string? sessionId, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var key = (sessionId ?? "") + ":" + id;
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        var msg = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null) msg["params"] = parameters;
        if (sessionId is not null) msg["sessionId"] = sessionId;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg);
        // ClientWebSocket allows exactly ONE outstanding SendAsync — sends can
        // arrive from the event-dispatch thread (LoadStarted → boot refresh)
        // concurrently with request-path sends; serialize them.
        await _sendLock.WaitAsync(ct);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        catch
        {
            _pending.TryRemove(key, out _);
            throw;
        }
        finally { _sendLock.Release(); }

        return await tcs.Task;
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var scratch = new byte[64 * 1024];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(scratch, _cts.Token);
                buffer.Write(scratch.AsSpan(0, result.Count));
                if (!result.EndOfMessage) continue;

                DispatchMessage(buffer.WrittenMemory);
                buffer.Clear();
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex) { BrowserController.Log("CDP reader error: " + ex.Message); }
        finally
        {
            foreach (var kv in _pending)
                kv.Value.TrySetException(new InvalidOperationException("CDP connection closed"));
            _pending.Clear();
        }
    }

    private void DispatchMessage(ReadOnlyMemory<byte> data)
    {
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            var sessionId = root.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null;

            if (root.TryGetProperty("id", out var idEl))
            {
                var key = (sessionId ?? "") + ":" + idEl.GetInt64();
                if (_pending.TryRemove(key, out var tcs))
                {
                    if (root.TryGetProperty("error", out var err))
                    {
                        var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "CDP error";
                        tcs.TrySetException(new CdpException(msg ?? "CDP error", err.GetRawText()));
                    }
                    else
                    {
                        var res = root.TryGetProperty("result", out var r) ? r.Clone() : default;
                        tcs.TrySetResult(res);
                    }
                }
            }
            else if (root.TryGetProperty("method", out var methodEl))
            {
                var method = methodEl.GetString()!;
                var prms = root.TryGetProperty("params", out var p) ? p.Clone() : default;
                EventReceived?.Invoke(this, (sessionId, method, prms));
            }
        }
        catch (JsonException) { }
        finally { doc?.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
        _ws.Dispose();
        _cts.Dispose();
    }
}

public sealed class CdpSession
{
    private readonly CdpClient _client;
    public string SessionId { get; }
    public string TargetId { get; }

    internal CdpSession(CdpClient client, string sessionId, string targetId)
    {
        _client = client;
        SessionId = sessionId;
        TargetId = targetId;
    }

    public Task<JsonElement> SendAsync(string method, object? parameters = null, CancellationToken ct = default)
        => _client.SendAsync(method, parameters, SessionId, ct);
}

public sealed class CdpException(string message, string? rawError = null) : Exception(message)
{
    public string? RawError { get; } = rawError;
}
