// 本地假服务端：只监听 127.0.0.1 上系统分配的临时端口，按剧本返回一段 SSE。
//
// 用它取代真实网络请求 —— 自测绝不连外网，也就不会烧用户的 token、不需要任何真实密钥。
// 每次收到请求都把【原始请求体】和【Authorization 头】一起存下来，好在第二轮断言
// "assistant.tool_calls 和 role:\"tool\" 真的被发回去了"。

using System.Net;
using System.Text;
using System.Text.Json;

namespace PotatoAgent.Core.SelfTest;

/// <summary>假服务端对一次请求的响应剧本。</summary>
internal sealed class MockResponse
{
    public int Status { get; init; } = 200;

    public string ContentType { get; init; } = "text/event-stream";

    public byte[] Body { get; init; } = Array.Empty<byte>();

    /// <summary>自己往 body 流里写（用来复现"写到一半连接断了"），设了就忽略 <see cref="Body"/>。</summary>
    public Action<Stream>? StreamWriter { get; init; }

    /// <summary>写完之后直接掐断连接（不补 chunked 终止块）—— 复现中途断流。</summary>
    public bool AbortAfterWrite { get; init; }

    /// <summary>一个字都不回，把连接吊住（复现超时）。</summary>
    public bool HoldForever { get; init; }

    /// <summary>一段正常收尾的 SSE：每个参数各自是一个事件，事件之间必须有空行。</summary>
    public static MockResponse Sse(params string[] events)
    {
        var text = string.Join("\n\n", events) + "\n\n";
        return new MockResponse { Body = Encoding.UTF8.GetBytes(text) };
    }

    /// <summary>写几块合法的 SSE，然后掐断连接。</summary>
    public static MockResponse SseThenAbort(params string[] events)
    {
        var text = string.Join("\n\n", events) + "\n\n";
        return new MockResponse
        {
            StreamWriter = stream =>
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                Thread.Sleep(400);   // 让客户端先把这几块真读到手，再断
            },
            AbortAfterWrite = true,
        };
    }

    /// <summary>HTTP 非 2xx（400 / 401 / 429 …）。</summary>
    public static MockResponse HttpError(int status, string body)
    {
        return new MockResponse
        {
            Status = status,
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes(body),
        };
    }

    /// <summary>HTTP 200，但不是 SSE 流（例如服务端不理 stream:true 直接吐了一坨 JSON）。</summary>
    public static MockResponse Text(string body, string contentType = "application/json")
    {
        return new MockResponse { ContentType = contentType, Body = Encoding.UTF8.GetBytes(body) };
    }

    /// <summary>连接吊住不回。</summary>
    public static MockResponse Hang() => new() { HoldForever = true };
}

/// <summary>一次收工后能复查的请求。</summary>
internal sealed record MockRequest(string Method, string Path, string Body, string? Authorization, string? ContentType, int Count)
{
    public string BodyText => Body;
}

/// <summary>只跑在回环地址上的假 OpenAI 兼容服务端。用完必须 Dispose。</summary>
internal sealed class MockChatServer : IDisposable
{
    private static readonly TimeSpan RequestWait = TimeSpan.FromSeconds(15);

    private readonly HttpListener _listener;
    private readonly Func<int, MockRequest, MockResponse> _handler;
    private readonly List<MockRequest> _requests = new();
    private readonly SemaphoreSlim _arrived = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();

    private int _issues;

    private MockChatServer(int port, Func<int, MockRequest, MockResponse> handler)
    {
        Port = port;
        _handler = handler;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");   // 前缀必须以 / 结尾

        // 先把监听器拉起来：Start() 之前投递的请求不会被处理。
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>起一个假服务端。<paramref name="handler"/> 收 (第几次请求(从 1 开始), 请求内容) 返回剧本。</summary>
    public static MockChatServer Start(Func<int, MockRequest, MockResponse> handler)
    {
        return new MockChatServer(FindFreePort(), handler);
    }

    /// <summary>最简单的用法：每次都回同一段脚本。</summary>
    public static MockChatServer Start(MockResponse response)
    {
        return Start((_, _) => response);
    }

    public int Port { get; }

    /// <summary>喂给 OpenAiProvider 的 base URL（不带 /v1，让被测代码自己去补）。</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>收到的请求数。</summary>
    public int RequestCount
    {
        get { lock (_gate) return _requests.Count; }
    }

    /// <summary>按序号取请求（<paramref name="index"/> 从 0 开始），越界返回 null。</summary>
    public MockRequest? Request(int index)
    {
        lock (_gate)
        {
            return index >= 0 && index < _requests.Count ? _requests[index] : null;
        }
    }

    /// <summary>等第 <paramref name="count"/> 个请求到达；超时或服务端出过错都返回 false。</summary>
    public async Task<bool> WaitForRequestAsync(int count)
    {
        var deadline = DateTime.UtcNow + RequestWait;
        while (true)
        {
            lock (_gate)
            {
                if (_requests.Count >= count) return true;
                if (_issues > 0) return false;
            }

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return false;
            try
            {
                await _arrived.WaitAsync(left).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    /// <summary>服务端自己抽风了几次（用来区分"被测代码坏了"和"假服务端坏了"）。</summary>
    public int Issues
    {
        get { lock (_gate) return _issues; }
    }

    public void Dispose()
    {
        try { _stop.Cancel(); } catch { /* 已经停了 */ }
        try { _listener.Stop(); } catch { /* 已经停了 */ }
        try { _listener.Close(); } catch { /* 已经关了 */ }
        _stop.Dispose();
        _arrived.Dispose();
    }

    // ==================== 内部：收请求 / 回剧本 ====================

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Note($"listener failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        MockRequest request;
        MockResponse response;

        try
        {
            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            int count;
            lock (_gate)
            {
                count = _requests.Count + 1;
            }

            request = new MockRequest(
                context.Request.HttpMethod,
                context.Request.Url?.AbsolutePath ?? string.Empty,
                body,
                context.Request.Headers["Authorization"],
                context.Request.ContentType,
                count);

            lock (_gate)
            {
                _requests.Add(request);
            }

            _arrived.Release();

            response = _handler(count, request);
        }
        catch (Exception ex)
        {
            Note($"failed to read the request: {ex.GetType().Name}: {ex.Message}");
            try { context.Response.Abort(); } catch { /* 已经断了 */ }
            return;
        }

        try
        {
            context.Response.StatusCode = response.Status;
            context.Response.ContentType = response.ContentType;
            context.Response.Headers["Connection"] = "close";

            if (response.HoldForever)
            {
                // 把连接吊住：一个字都不回，让客户端的超时生效。
                // 结束后 Dispose 会 Stop listener 把这条连接一起收掉。
                await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false);
                return;
            }

            if (response.StreamWriter is { } writer)
            {
                context.Response.SendChunked = true;
                await using var stream = context.Response.OutputStream;
                writer(stream);
                if (response.AbortAfterWrite)
                {
                    context.Response.Abort();   // 不补终止块 = 服务端把连接掐了
                    return;
                }

                return;
            }

            context.Response.ContentLength64 = response.Body.Length;
            await context.Response.OutputStream.WriteAsync(response.Body).ConfigureAwait(false);
            context.Response.OutputStream.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException or InvalidOperationException)
        {
            // 客户端提前跑了（取消、超时），正常现象。
        }
        catch (OperationCanceledException)
        {
            // Dispose 期间被叫停，忽略。
        }
    }

    private void Note(string message)
    {
        lock (_gate)
        {
            _issues++;
        }

        Program.Fail($"假服务端自身出错: {message}");
    }

    /// <summary>向系统要一个当前空闲的回环端口（bind 到 0 再问真实端口）。</summary>
    private static int FindFreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

/// <summary>在一段 SSE 脚本里拼出一个 OpenAI 风格的数据块。</summary>
internal static class Sse
{
    /// <summary>
    /// 不转义非 ASCII：这样脚本里带的就是<b>原样的中文</b>，
    /// 于是"把汉字的多字节序列切开"才是真的在切 UTF-8，而不是在切 <c>\uXXXX</c> 六个 ASCII 字符。
    /// </summary>
    private static readonly JsonSerializerOptions Wire = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>正文增量。</summary>
    public static string Text(string text)
    {
        return "data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { index = 0, delta = new { content = text }, finish_reason = (string?)null } },
        }, Wire);
    }

    /// <summary>
    /// 思考模式的思维链增量（<c>delta.reasoning_content</c>）。
    /// 和 <see cref="Text"/> 是<b>两个不同的字段</b>，被测定代码必须把它们当两种事件，不许混。
    /// </summary>
    public static string Reasoning(string text)
    {
        return "data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { index = 0, delta = new { reasoning_content = text }, finish_reason = (string?)null } },
        }, Wire);
    }

    /// <summary>工具调用增量（<paramref name="index"/> 归并，<paramref name="argumentsFragment"/> 是要拼接的碎片）。</summary>
    public static string Tool(int index, string? id, string? name, string argumentsFragment)
    {
        return "data: " + JsonSerializer.Serialize(new
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
                            new
                            {
                                index,
                                id,
                                type = "function",
                                function = new { name, arguments = argumentsFragment },
                            },
                        },
                    },
                    finish_reason = (string?)null,
                },
            },
        }, Wire);
    }

    /// <summary>结束帧（finish_reason）。</summary>
    public static string Finish(string reason)
    {
        return "data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { index = 0, delta = new { }, finish_reason = reason } },
        }, Wire);
    }

    /// <summary>流结束标记。</summary>
    public const string Done = "data: [DONE]";

    /// <summary>注释心跳（服务端保活用，解析器必须忽略）。</summary>
    public const string Comment = ": keep-alive";
}
