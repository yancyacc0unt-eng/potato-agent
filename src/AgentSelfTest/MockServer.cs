// 本地假服务端：只监听 127.0.0.1 上系统分配的临时端口，按剧本返回 SSE。
//
// 自测绝不连外网：不烧 token、不需要真实密钥，也不受网络波动影响。
// 每次请求的【原始请求体】都留档，好断言"工具结果 / 图片真的被发回去了"。

using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace PotatoAgent.Agent.SelfTest;

/// <summary>一次收工后能复查的请求。</summary>
internal sealed record RecordedRequest(int Index, string Body)
{
    /// <summary>把请求体解析成 JsonDocument（用完要 Dispose）。</summary>
    public JsonDocument Parse() => JsonDocument.Parse(Body);

    /// <summary>messages 数组里最后一条 role=... 的消息的 content 文本；没有返回 null。</summary>
    public string? LastContentOfRole(string role) => LastContentOfRole(Body, role);

    /// <summary>不建对象也能用：从一段请求体里取最后一条某角色的 content 文本。</summary>
    public static string? LastContentOfRole(string body, string role)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? found = null;
        foreach (var message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("role", out var roleElement) || roleElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (!string.Equals(roleElement.GetString(), role, StringComparison.Ordinal))
            {
                continue;
            }

            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                found = content.GetString();
            }
        }

        return found;
    }
}

/// <summary>只跑在回环地址上的假 OpenAI 兼容服务端。用完必须 Dispose。</summary>
internal sealed class MockServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Func<int, string, string> _handler;
    private readonly List<RecordedRequest> _requests = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();

    private MockServer(int port, Func<int, string, string> handler)
    {
        Port = port;
        _handler = handler;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>起一个假服务端。<paramref name="handler"/> 收 (第几次请求(从 1 开始), 请求体) 返回一段 SSE 文本。</summary>
    public static MockServer Start(Func<int, string, string> handler) => new(FindFreePort(), handler);

    /// <summary>每次都回同一段 SSE。</summary>
    public static MockServer Start(string sse) => Start((_, _) => sse);

    public int Port { get; }

    /// <summary>喂给 OpenAiProvider 的 base URL。</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>收到的请求数。</summary>
    public int RequestCount
    {
        get { lock (_gate) return _requests.Count; }
    }

    /// <summary>第 <paramref name="index"/> 个请求（0 起）；越界返回 null。</summary>
    public RecordedRequest? Request(int index)
    {
        lock (_gate)
        {
            return index >= 0 && index < _requests.Count ? _requests[index] : null;
        }
    }

    public void Dispose()
    {
        try { _stop.Cancel(); } catch { /* 已经停了 */ }
        try { _listener.Stop(); } catch { /* 已经停了 */ }
        try { _listener.Close(); } catch { /* 已经关了 */ }
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            int index;
            lock (_gate)
            {
                index = _requests.Count + 1;
                _requests.Add(new RecordedRequest(index, body));
            }

            var sse = _handler(index, body);
            var bytes = Encoding.UTF8.GetBytes(sse);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.Headers["Connection"] = "close";
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.OutputStream.Close();
        }
        catch (Exception)
        {
            try { context.Response.Abort(); } catch { /* 客户端提前跑了 */ }
        }
    }

    private static int FindFreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

/// <summary>拼 OpenAI 风格的 SSE 数据块。</summary>
internal static class Sse
{
    private static readonly JsonSerializerOptions Wire = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>正文增量。</summary>
    public static string Text(string text) => "data: " + JsonSerializer.Serialize(new
    {
        choices = new[] { new { index = 0, delta = new { content = text }, finish_reason = (string?)null } },
    }, Wire);

    /// <summary>一次工具调用（一片就发完，不拆碎片 —— 拆碎片是 CoreSelfTest 的事）。</summary>
    public static string ToolCall(int index, string id, string name, string argumentsJson) =>
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        tool_calls = new[]
                        {
                            new { index, id, type = "function", function = new { name, arguments = argumentsJson } },
                        },
                    },
                    finish_reason = (string?)null,
                },
            },
        }, Wire);

    /// <summary>结束帧。</summary>
    public static string Finish(string reason) => "data: " + JsonSerializer.Serialize(new
    {
        choices = new[] { new { index = 0, delta = new { }, finish_reason = reason } },
    }, Wire);

    /// <summary>流结束标记。</summary>
    public const string Done = "data: [DONE]";

    /// <summary>把若干帧拼成一段完整的 SSE。</summary>
    public static string Script(params string[] frames) => string.Join("\n\n", frames) + "\n\n";
}
