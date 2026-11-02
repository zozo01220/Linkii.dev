using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Linkii.Pi;

/// <summary>Client minimal du protocole de débogage de Chromium (DevTools) : commandes avec réponse, et événements.</summary>
internal sealed class Cdp : IDisposable
{
    private readonly ClientWebSocket ws = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private int nextId;

    public event Action<string, JsonElement>? Event;

    /// <summary>Se termine quand la connexion se ferme (Chromium arrêté ou planté).</summary>
    public Task Closed { get; private set; } = Task.CompletedTask;

    public static async Task<Cdp> Connect(string wsUrl, CancellationToken ct)
    {
        var c = new Cdp();
        await c.ws.ConnectAsync(new Uri(wsUrl), ct);
        c.Closed = Task.Run(c.ReadLoop, CancellationToken.None);
        return c;
    }

    public async Task<JsonElement> Send(string method, object? args = null, int timeoutMs = 15_000)
    {
        var id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = args ?? new { } });
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            using var cts = new CancellationTokenSource(timeoutMs);
            await using var _ = cts.Token.Register(() => tcs.TrySetException(new TimeoutException(method)));
            return await tcs.Task;
        }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task ReadLoop()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);

                using var doc = JsonDocument.Parse(ms.ToArray());
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idEl) && pending.TryGetValue(idEl.GetInt32(), out var tcs))
                {
                    if (root.TryGetProperty("error", out var err)) tcs.TrySetException(new InvalidOperationException(err.GetRawText()));
                    else tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res.Clone() : default);
                }
                else if (root.TryGetProperty("method", out var m))
                {
                    var p = root.TryGetProperty("params", out var pp) ? pp.Clone() : default;
                    Event?.Invoke(m.GetString() ?? "", p);
                }
            }
        }
        catch { /* connexion perdue : Closed se termine */ }
        finally { foreach (var t in pending.Values) t.TrySetCanceled(); }
    }

    public void Dispose() => ws.Dispose();
}
