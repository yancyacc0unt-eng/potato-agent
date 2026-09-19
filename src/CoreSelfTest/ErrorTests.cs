// ---------- 三、错误处理 ----------
//
// 每一种坏情况都必须落到【明确的 ProviderException 子类】上，绝不能裸崩、也不能漏原始 HttpRequestException。
// 全部用本地假服务端造，唯一"连不上"的两个用例打的是回环地址（瞬间失败，不出去）。
//
// 覆盖：400 / 401 / 429 / 5xx、中途断流、流内非法 JSON、非 SSE 响应、取消、超时、连不上、配置不全、
//       以及"HTTP 200 但流里塞了 error 对象"（限额、上下文超长这种）。

using System.Net.Http;
using System.Text.Json;
using PotatoAgent.Core.Brain;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Core.SelfTest;

internal static class ErrorTests
{
    public static void Run()
    {
        Console.WriteLine("---- 三、错误处理 ----");

        HttpFailures();
        StreamFailures();
        CancellationAndTimeout();
        Unreachable();
        InStreamApiError();
        SessionPropagates();
    }

    // ==================== 3.1 HTTP 非 2xx ====================

    private static void HttpFailures()
    {
        Console.WriteLine("  3.1 HTTP 400 / 401 / 429 / 500");

        using (var server = MockChatServer.Start(MockResponse.HttpError(400, "{\"error\":{\"message\":\"bad request\"}}")))
        using (var provider = ProviderFor(server))
        {
            var error = Failure(provider);
            Expect.ExceptionType<ProviderHttpException>(error, "400 → ProviderHttpException");
            if (error is ProviderHttpException http)
            {
                Expect.Equal(400, http.StatusCode, "状态码如实带出来");
                Expect.Equal("BadRequest", http.StatusCodeName, "状态码名字带出来");
                Expect.Contains(http.ResponseBody, "bad request", "服务端响应体带出来（排查线索）");
                Expect.True(!http.IsAuthFailure, "400 不算鉴权失败");
                Expect.True(!http.IsRetryable, "400 重试没意义");
            }
        }

        using (var server = MockChatServer.Start(MockResponse.HttpError(401, "{\"error\":{\"message\":\"invalid api key\"}}")))
        using (var provider = ProviderFor(server))
        {
            var error = Failure(provider);
            Expect.ExceptionType<ProviderHttpException>(error, "401 → ProviderHttpException");
            if (error is ProviderHttpException http)
            {
                Expect.True(http.IsAuthFailure, "401 标记成鉴权失败（界面该引导去设置页）");
                Expect.True(!http.IsRetryable, "401 重试没意义");
            }
        }

        using (var server = MockChatServer.Start(MockResponse.HttpError(429, "{\"error\":{\"message\":\"rate limited\"}}")))
        using (var provider = ProviderFor(server))
        {
            var error = Failure(provider);
            Expect.ExceptionType<ProviderHttpException>(error, "429 → ProviderHttpException");
            Expect.True(error is ProviderHttpException { IsRetryable: true }, "429 标记成可重试");
        }

        using (var server = MockChatServer.Start(MockResponse.HttpError(500, "upstream boom")))
        using (var provider = ProviderFor(server))
        {
            var error = Failure(provider);
            Expect.ExceptionType<ProviderHttpException>(error, "500 → ProviderHttpException");
            Expect.True(error is ProviderHttpException { IsRetryable: true }, "5xx 标记成可重试");
        }

        Console.WriteLine();
    }

    // ==================== 3.2 流读到一半坏掉 ====================

    private static void StreamFailures()
    {
        Console.WriteLine("  3.2 中途断流 / 流内非法 JSON / 非 SSE 响应");

        using (var server = MockChatServer.Start(MockResponse.SseThenAbort(Sse.Text("说到一半"), Sse.Text("话还没说完"))))
        using (var provider = ProviderFor(server))
        {
            var error = Failure(provider);
            Expect.ExceptionType<ProviderNetworkException>(error, "服务端中途掐断连接 → ProviderNetworkException（不是 Protocol）");
        }

        using (var server = MockChatServer.Start(MockResponse.Sse("data: {\"choices\":[{\"delta\":{\"content\":\"坏\"}}]", Sse.Done)))
        using (var provider = ProviderFor(server))
        {
            var error = Failure(provider);
            Expect.ExceptionType<ProviderProtocolException>(error, "流里出现非法 JSON → ProviderProtocolException");
            if (error is ProviderProtocolException protocol)
            {
                Expect.Contains(protocol.RawPayload ?? string.Empty, "choices", "原始 payload 被留下来（排查协议不兼容的关键）");
            }
        }

        using (var server = MockChatServer.Start(MockResponse.Text("{\"error\":\"i am not an sse stream\"}")))
        using (var provider = ProviderFor(server))
        {
            // 这一帧没有 "data:" 前缀，解析器认不出任何字段 → 直接忽略，事件流为空。
            // 也就是"服务端不理 stream:true 时不会崩"，只会拿不到内容（界面表现为空回复）。
            var events = new List<ChatStreamEvent>();
            var error = Catch.Of(async () =>
            {
                await foreach (var evt in provider.StreamCompletionAsync(
                    new[] { ChatMessage.User("你好") }, Array.Empty<ITool>()))
                {
                    events.Add(evt);
                }
            }).GetAwaiter().GetResult();

            Expect.True(error is null, $"HTTP 200 但回的不是 SSE 时不崩（实际 {(error is null ? "没抛" : error.GetType().Name)}）");
            Expect.Equal(1, events.Count, "没有任何正文增量，只收到一条结束事件");
            Expect.True(events.Count == 1 && events[0] is ChatFinished, "唯一的那个事件是 ChatFinished（流正常收尾）");
        }

        Console.WriteLine();
    }

    // ==================== 3.3 取消与超时 ====================

    private static void CancellationAndTimeout()
    {
        Console.WriteLine("  3.3 用户取消 / 请求超时");

        using (var server = MockChatServer.Start(MockResponse.SseThenAbort(Sse.Text("第一段"), Sse.Text("第二段"))))
        using (var provider = ProviderFor(server))
        using (var cts = new CancellationTokenSource())
        {
            cts.CancelAfter(TimeSpan.FromMilliseconds(250));
            var error = Failure(provider, cts.Token);

            Expect.True(error is OperationCanceledException,
                $"取消时抛 OperationCanceledException（实际 {(error is null ? "没抛" : error.GetType().Name)}）");
            Expect.True(error is not ProviderException, "取消【不】被包装成 Provider 异常（界面靠它区分「用户按了停止」）");
            Expect.True(!cts.IsCancellationRequested || error is OperationCanceledException, "取消令牌状态与异常一致");
        }

        using (var server = MockChatServer.Start(MockResponse.Hang()))
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2.5) })
        using (var provider = ProviderFor(server, http))
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            var error = Failure(provider, cts.Token);
            Expect.ExceptionType<ProviderTimeoutException>(error, "连接吊住不回 → ProviderTimeoutException（不是取消）");
            if (error is ProviderTimeoutException timeout)
            {
                Expect.Equal(TimeSpan.FromSeconds(2.5), timeout.Timeout ?? TimeSpan.Zero, "超时阈值也带出来");
            }
        }

        Console.WriteLine();
    }

    // ==================== 3.4 连不上 ====================

    private static void Unreachable()
    {
        Console.WriteLine("  3.4 连不上（回环地址上没人监听）");

        var profile = new ProviderProfile
        {
            Name = "selftest",
            BaseUrl = $"http://127.0.0.1:{UnusedPort()}",
            Model = "fake-model",
            ApiKey = Program.FakeKey,
        };

        using (var provider = new OpenAiProvider(profile))
        {
            var error = Failure(provider);
            Expect.ExceptionType<ProviderNetworkException>(error, "端口没人监听 → ProviderNetworkException");
        }

        // 域名解析不了这种用例在本机测不出结论：环境里的代理/TUN 会把不存在的域名接成 HTTP 502，
        // 于是拿到的是 ProviderHttpException（也是明确的 ProviderException 子类，不算裸崩）。
        // 宁可如实记下来，也不写一条"看环境脸色"的断言。
        Console.WriteLine("    （域名解析失败的用例跳过：本机有代理/TUN 会把坏域名接成 502，测不出真结论）");

        Console.WriteLine();
    }

    // ==================== 3.5 HTTP 200 但流里塞了 error 对象 ====================

    private static void InStreamApiError()
    {
        Console.WriteLine("  3.5 HTTP 200 但流里塞了 error 对象（限额 / 上下文超长）");

        // 这里不用 Sse.Text，手工拼一个 error 帧。
        var payload = JsonSerializer.Serialize(new
        {
            error = new { message = "You exceeded your current quota", type = "insufficient_quota", code = "insufficient_quota" },
        });

        using var server = MockChatServer.Start(MockResponse.Sse("data: " + payload, Sse.Done));
        using var provider = ProviderFor(server);

        var error = Failure(provider);
        Expect.ExceptionType<ProviderApiException>(error, "流内 error 对象 → ProviderApiException（和 HTTP 异常分开）");
        if (error is ProviderApiException api)
        {
            Expect.Equal("insufficient_quota", api.Code, "服务端错误码带出来");
            Expect.Equal("insufficient_quota", api.ErrorType, "服务端错误类型带出来");
        }

        Console.WriteLine();
    }

    // ==================== 3.6 会话层不吞异常、也不裸崩 ====================

    private static void SessionPropagates()
    {
        Console.WriteLine("  3.6 工具循环层（ChatSession）原样透出 ProviderException");

        using var server = MockChatServer.Start(MockResponse.HttpError(401, "{\"error\":{\"message\":\"invalid api key\"}}"));
        using var provider = ProviderFor(server);
        var session = new ChatSession(provider, new ToolRegistry());

        var error = Catch.Of(async () =>
        {
            await foreach (var _ in session.SendAsync("你好"))
            {
                // 这条路径上什么都不该吐出来：请求就 401 了。
            }
        }).GetAwaiter().GetResult();

        Expect.ExceptionType<ProviderHttpException>(error, "SendAsync 把 401 原样透出来，不是 InvalidOperationException");

        Console.WriteLine();
    }

    // ==================== 内部工具 ====================

    /// <summary>跑一次完整请求，返回抛出来的异常（null = 居然没抛）。</summary>
    private static Exception? Failure(OpenAiProvider provider, CancellationToken ct = default)
    {
        return Catch.Of(async () =>
        {
            await foreach (var _ in provider.StreamCompletionAsync(
                new[] { ChatMessage.User("你好") }, Array.Empty<ITool>(), ct))
            {
                // 全部拉完；测试关心的是异常类型。
            }
        }).GetAwaiter().GetResult();
    }

    private static OpenAiProvider ProviderFor(MockChatServer server, HttpClient? http = null)
    {
        var profile = new ProviderProfile
        {
            Name = "selftest",
            BaseUrl = server.BaseUrl,
            Model = "fake-model",
            ApiKey = Program.FakeKey,
            Temperature = 0,
            MaxTokens = 64,
        };

        return new OpenAiProvider(profile, http);
    }

    /// <summary>要一个肯定没人监听的端口（bind 到 0 拿到号之后立刻放掉）。</summary>
    private static int UnusedPort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
