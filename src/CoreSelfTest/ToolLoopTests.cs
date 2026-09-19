// ---------- 二、工具调用循环 ----------
//
// 用本地假服务端（HttpListener，127.0.0.1）跑完整的"模型要工具 → 执行 → 回灌 → 再问"闭环。
// 关键点：tool_calls 的 arguments 故意【分两片】发过来，验证拼接之后参数完整；
// 第二轮请求里必须同时出现 assistant 带 tool_calls 的消息和 role:"tool" 的结果消息，
// 否则真实服务端会直接 400（协议要求 tool_call_id 必须对得上）。

using System.Text;
using System.Text.Json;
using PotatoAgent.Core.Brain;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Core.SelfTest;

internal static class ToolLoopTests
{
    /// <summary>arguments 的原文，故意切成两片发（切点落在中文属性名的中间）。</summary>
    private const string ArgsPart1 = "{\"city\":\"上";
    private const string ArgsPart2 = "海\",\"unit\":\"celsius\"}";

    private const string ToolOutput = "上海 22°C 晴";
    private const string Reply = "上海 22°C，晴。";

    public static void Run()
    {
        Console.WriteLine("---- 二、工具调用循环 ----");

        OneToolRound();
        ToolArgumentsSplit();
        BrokenArguments();
        NoIndexToolCall();
        RoundLimit();
        UrlAndGuards();
    }

    // ==================== 2.1 核心：一轮完整的工具调用 ====================

    private static void OneToolRound()
    {
        Console.WriteLine("  2.1 假服务端返回 tool_calls（arguments 分两片）→ 工具执行 → 第二轮带回灌");

        using var server = MockChatServer.Start((count, _) => count == 1
            ? ServerSse(
                Sse.Tool(0, "call_abc123", "get_weather", ArgsPart1),
                Sse.Tool(0, null, null, ArgsPart2),
                Sse.Finish("tool_calls"),
                Sse.Done)
            : ServerSse(Sse.Text(Reply), Sse.Finish("stop"), Sse.Done));

        var tool = new FakeWeatherTool();
        using var provider = ProviderFor(server);
        var session = new ChatSession(provider, Registry(tool));

        var turn = SendSync(session, "上海今天天气怎么样？", expectCalls: 1);

        Console.WriteLine($"    工具调用 : {tool.CallCount} 次，收到的参数原文 = {Program.Trim(tool.LastArgsJson, 80)}");
        Console.WriteLine($"    最终回答 : {Program.Trim(turn.FinalText, 60)}");
        Console.WriteLine($"    服务端   : 收到 {server.RequestCount} 个请求");

        Expect.Equal(1, tool.CallCount, "工具真的被执行了（恰好 1 次）");
        Expect.Equal("上海", tool.LastCity, "工具拿到的 city 参数完整（跨两片拼出来的）");
        Expect.Equal("celsius", tool.LastUnit, "工具拿到的 unit 参数完整");
        Expect.Equal(ArgsPart1 + ArgsPart2, tool.LastArgsJson, "工具收到的 arguments 与模型发出的逐字符一致");

        Expect.Equal(2, server.RequestCount, "一共发了 2 个请求（要工具的那轮 + 回灌后的那轮）");
        Expect.Equal(Reply, turn.FinalText, "最终回答一字不差");
        Expect.Equal(1, turn.ToolCallsExecuted, "统计的工具调用次数 = 1");
        Expect.Equal("stop", turn.FinishReason, "finish_reason 透传到结果里");

        CheckSecondRequest(server, "call_abc123");

        // 会话说自己历史里有什么，和真正发出去的请求对一下账。
        // 注意：一轮 = user → assistant(要工具) → tool → assistant(最终回答)，只在【开头发一次】用户消息。
        Expect.Equal(4, session.History.Count, "整轮结束后会话历史共 4 条");
        Expect.Equal("user,assistant,tool,assistant", string.Join(",", session.History.Select(m => m.Role)),
            "会话历史流水 = user,assistant,tool,assistant");

        Console.WriteLine();
    }

    /// <summary>
    /// 断言第二轮请求体：assistant.tool_calls 和 role:"tool" 都在，且 id 对得上。
    /// 每条单独 Check，但整体也汇总成一条（自测报告里好读）。
    /// </summary>
    private static void CheckSecondRequest(MockChatServer server, string expectedCallId)
    {
        var request = server.Request(1);
        if (request is null)
        {
            Program.Fail("第二轮请求根本没到（回灌之后没有继续问）");
            return;
        }

        Expect.Equal("/v1/chat/completions", request.Path, "打的是 OpenAI 兼容端点 /v1/chat/completions");
        Expect.Equal("Bearer " + Program.FakeKey, request.Authorization, "Authorization 头带上了 Bearer 密钥");

        // 第二轮（回灌之后）的请求里应当是：最初那条 user + assistant(带 tool_calls) + tool(结果)。
        Expect.Equal("user,assistant,tool", RoleSequence(request.Body), "第二轮请求的角色流水 = user,assistant,tool");

        var assistant = FindMessage(request.Body, "assistant");
        var toolResult = FindMessage(request.Body, "tool");

        Expect.True(assistant is { } a && a.TryGetProperty("tool_calls", out _),
            "第二轮请求里的 assistant 消息带上了 tool_calls");
        Expect.True(toolResult is { } t && Expect.Str(t, "tool_call_id") == expectedCallId,
            $"role:\"tool\" 结果消息带上了 tool_call_id = {expectedCallId}");
        Expect.True(toolResult is { } t2 && (Expect.Str(t2, "content") ?? string.Empty).Contains(ToolOutput),
            "role:\"tool\" 的内容就是工具的真实输出");

        if (assistant is { } msg && msg.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0)
        {
            var call = calls[0];
            Expect.Equal(expectedCallId, Expect.Str(call, "id"), "assistant.tool_calls[0].id 与回灌的 tool_call_id 一致");
            Expect.Equal("get_weather", Expect.Str(call.GetProperty("function"), "name"), "assistant.tool_calls[0].function.name 原样带回");
            Expect.Equal(ArgsPart1 + ArgsPart2, Expect.Str(call.GetProperty("function"), "arguments"), "assistant.tool_calls[0].function.arguments 原样带回");
        }
    }

    // ==================== 2.2 参数分片拼接（协议细节） ====================

    private static void ToolArgumentsSplit()
    {
        Console.WriteLine("  2.2 arguments 分片拼接（含「多片 + 空片」的野路子服务端）");

        var accumulator = new ToolCallAccumulator();
        accumulator.Apply(new ChatToolCallDelta(0, "call_x", "get_weather", ArgsPart1));
        accumulator.Apply(new ChatToolCallDelta(0, null, null, string.Empty));
        accumulator.Apply(new ChatToolCallDelta(0, null, null, ArgsPart2));

        var calls = accumulator.Build();
        Expect.Equal(1, calls.Count, "同一个 index 的片归并成 1 个调用");
        Expect.Equal("call_x", calls.Count > 0 ? calls[0].Id : "<缺>", "id 只在第一片里也留住了");
        Expect.Equal("get_weather", calls.Count > 0 ? calls[0].Function.Name : "<缺>", "name 只在第一片里也留住了");
        Expect.Equal(ArgsPart1 + ArgsPart2, calls.Count > 0 ? calls[0].Function.Arguments : "<缺>", "参数按顺序首尾相接");

        // 无参数的调用：很多模型给空串，协议要 {}。
        var empty = new ToolCallAccumulator();
        empty.Apply(new ChatToolCallDelta(0, "call_y", "get_time", string.Empty));
        var emptyCalls = empty.Build();
        Expect.Equal("{}", emptyCalls.Count > 0 ? emptyCalls[0].Function.Arguments : "<缺>", "无参调用的 arguments 补成 {}");

        Console.WriteLine();
    }

    // ==================== 2.3 模型吐半截 JSON：不执行、回灌、自己改 ====================

    private static void BrokenArguments()
    {
        Console.WriteLine("  2.3 模型吐半截非法 JSON → 工具不执行、错误回灌、模型自己改对");

        const string HalfJson = "{\"city\":\"上";   // 就断在这儿，没有收尾
        const string Fixed = "{\"city\":\"上海\",\"unit\":\"celsius\"}";

        var tool = new FakeWeatherTool();
        using var server = MockChatServer.Start((count, _) => count switch
        {
            1 => ServerSse(Sse.Tool(0, "call_bad", "get_weather", HalfJson), Sse.Finish("tool_calls"), Sse.Done),
            2 => ServerSse(Sse.Tool(0, "call_fixed", "get_weather", Fixed), Sse.Finish("tool_calls"), Sse.Done),
            _ => ServerSse(Sse.Text(Reply), Sse.Finish("stop"), Sse.Done),
        });

        using var provider = ProviderFor(server);
        var session = new ChatSession(provider, Registry(tool));
        var turn = SendSync(session, "上海天气（先让模型吐半截 JSON）", expectCalls: 2);

        Console.WriteLine($"    工具真实执行次数 = {tool.CallCount}（半截 JSON 那轮不该执行）");
        Console.WriteLine($"    最终回答: {Program.Trim(turn.FinalText, 60)}");

        Expect.Equal(1, tool.CallCount, "半截 JSON 的那次没有执行工具，只有改正后的那次执行了");
        Expect.Equal("上海", tool.LastCity, "改正后工具拿到了正确参数");
        Expect.Equal(3, server.RequestCount, "坏参数也回灌给了模型，所以一共问了 3 轮");

        // 坏参数那一轮也被回灌了，所以第三轮请求里既能找到那段坏 JSON 原文，也能找到 ERROR 结果。
        // 注意：坏 JSON 是嵌在 tool 消息的 content 里的，在请求体里会被再转义一层
        // （{\"city\"…），所以要在【json 解码之后】的文本里找，不能拿请求体原文直接 Contains。
        var third = server.Request(2);
        var thirdContents = DecodedContents(third);
        Expect.Contains(thirdContents, HalfJson,
            "第三轮请求的 tool 结果里还带着那段坏 JSON 原文（说明坏参数那轮结果确实进历史了）");
        Expect.Contains(thirdContents, "ERROR: Tool arguments are not valid JSON",
            "第三轮请求里带着回灌给模型的 ERROR 结果");
        Expect.Equal(5, RoleSequenceAll(third), "第三轮请求共 5 条消息（user,assistant,tool,assistant,tool）");

        // 直接从第三轮请求里把 arguments 抠出来（最后一条 assistant 才是改正后的那次）。
        var raw = RawArgumentsOf(third);
        Expect.Equal(Fixed, raw, "改正后的 arguments 与模型发的原文逐字符一致（JSON 转义没被改坏）");

        Console.WriteLine();
    }

    /// <summary>把所有消息的 content 解码后接起来（找"回灌了什么给模型"用）。</summary>
    private static string DecodedContents(MockRequest? request)
    {
        if (request is null)
        {
            Program.Fail("请求没到，取不出 content");
            return "<缺>";
        }

        using var doc = JsonDocument.Parse(request.Body);
        var text = new System.Text.StringBuilder();
        if (doc.RootElement.TryGetProperty("messages", out var messages))
        {
            foreach (var message in messages.EnumerateArray())
            {
                text.AppendLine(Expect.Str(message, "content") ?? string.Empty);
            }
        }

        return text.ToString();
    }

    /// <summary>请求体里 messages 的条数（请求没到就返回 -1）。</summary>
    private static int RoleSequenceAll(MockRequest? request)
    {
        if (request is null)
        {
            return -1;
        }

        using var doc = JsonDocument.Parse(request.Body);
        return doc.RootElement.TryGetProperty("messages", out var messages) ? messages.GetArrayLength() : -1;
    }

    /// <summary>
    /// 取请求体里<b>最后一条</b>带 tool_calls 的 assistant 消息的 arguments 原文。
    /// 取最后一条是因为"模型吐坏 JSON 再自己改对"那种用例里会有两次工具调用，
    /// 要看的显然是改正后的那次。
    /// </summary>
    private static string RawArgumentsOf(MockRequest? request)
    {
        if (request is null)
        {
            Program.Fail("请求没到，取不出 arguments");
            return "<缺>";
        }

        using var doc = JsonDocument.Parse(request.Body);
        if (doc.RootElement.TryGetProperty("messages", out var messages))
        {
            string? found = null;
            foreach (var message in messages.EnumerateArray())
            {
                if (Expect.Str(message, "role") != "assistant" ||
                    !message.TryGetProperty("tool_calls", out var calls) ||
                    calls.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var call in calls.EnumerateArray())
                {
                    if (call.TryGetProperty("function", out var function))
                    {
                        found = Expect.Str(function, "arguments");
                    }
                }
            }

            if (found is not null)
            {
                return found;
            }
        }

        Program.Fail("请求体里找不到带 tool_calls 的 assistant 消息");
        return "<缺>";
    }

    // ==================== 2.4 服务端不给 index 的野路子 ====================

    private static void NoIndexToolCall()
    {
        Console.WriteLine("  2.4 服务端不给 index 的野路子（靠 id 归并，别把参数粘错）");

        var accumulator = new ToolCallAccumulator();
        accumulator.Apply(new ChatToolCallDelta(null, "call_1", "alpha", "{\"a\":"));
        accumulator.Apply(new ChatToolCallDelta(null, null, null, "1}"));
        accumulator.Apply(new ChatToolCallDelta(null, "call_2", "beta", "{\"b\":2}"));

        var calls = accumulator.Build();
        Expect.Equal(2, calls.Count, "两个不同的 id 分成两个调用");
        Expect.Equal("{\"a\":1}", calls.Count > 0 ? calls[0].Function.Arguments : "<缺>", "第一个调用的参数没被粘上第二个的");
        Expect.Equal("{\"b\":2}", calls.Count > 1 ? calls[1].Function.Arguments : "<缺>", "第二个调用独立成条");

        Console.WriteLine();
    }

    // ==================== 2.5 轮次上限护栏 ====================

    private static void RoundLimit()
    {
        Console.WriteLine("  2.5 单轮工具调用轮次上限（防模型来回抽风烧钱）");

        using var server = MockChatServer.Start((_, _) => ServerSse(
            Sse.Tool(0, "call_loop", "get_time", "{}"),
            Sse.Finish("tool_calls"),
            Sse.Done));

        using var provider = ProviderFor(server);
        var session = new ChatSession(provider, Registry(new FakeWeatherTool())) { MaxToolRounds = 3 };

        // SendAndCollectAsync 的返回值里没有"是否撞上限"这个字段，所以走事件流把它读出来。
        var stopped = false;
        var task = Task.Run(async () =>
        {
            await foreach (var evt in session.SendAsync("一直调工具试试"))
            {
                if (evt is TurnCompleted completed)
                {
                    stopped = completed.StoppedAtRoundLimit;
                }
            }
        });

        if (!task.Wait(TimeSpan.FromSeconds(20)))
        {
            Program.Fail("轮次上限用例没有在 20 秒内跑完");
        }

        Expect.True(stopped, "到轮次上限就停下，并如实标记 StoppedAtRoundLimit");
        Expect.Equal(3, server.RequestCount, "最多只发了 3 个请求（MaxToolRounds = 3）");

        Console.WriteLine();
    }

    // ==================== 2.6 端点补全 + 配置护栏 ====================

    private static void UrlAndGuards()
    {
        Console.WriteLine("  2.6 端点补全 + 配置不全时的护栏");

        Expect.Equal("https://api.example.com/v1/chat/completions",
            OpenAiProvider.BuildChatCompletionsUri("https://api.example.com").ToString(), "裸 base URL 补 /v1/chat/completions");
        Expect.Equal("https://api.example.com/v1/chat/completions",
            OpenAiProvider.BuildChatCompletionsUri("https://api.example.com/").ToString(), "末尾带斜杠也认");
        Expect.Equal("https://api.example.com/v1/chat/completions",
            OpenAiProvider.BuildChatCompletionsUri("https://api.example.com/v1").ToString(), "已经带 /v1 就不重复补");
        Expect.Equal("https://api.example.com/v1/chat/completions",
            OpenAiProvider.BuildChatCompletionsUri("https://api.example.com/v1/chat/completions").ToString(), "完整端点原样使用");

        var missing = Catch.Of(() =>
        {
            OpenAiProvider.BuildChatCompletionsUri("");
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
        Expect.ExceptionType<ProviderConfigurationException>(missing, "base URL 为空时抛 ProviderConfigurationException");

        var malformed = Catch.Of(() =>
        {
            OpenAiProvider.BuildChatCompletionsUri("这个不是网址");
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
        Expect.ExceptionType<ProviderConfigurationException>(malformed, "base URL 格式不对时抛 ProviderConfigurationException");

        var store = new ConfigStore(Path.Combine(Path.GetTempPath(), "pa-never-used", "config.json"), new FakeSecretProtector());
        store.Load();
        var fromEmpty = Catch.Of(() => Task.FromResult(OpenAiProvider.FromActiveProfile(store))).GetAwaiter().GetResult();
        Expect.ExceptionType<ProviderConfigurationException>(fromEmpty, "没有可用档案时 FromActiveProfile 抛 ProviderConfigurationException");

        Console.WriteLine();
    }

    // ==================== 内部工具 ====================

    /// <summary>把一条用户消息整轮跑完（同步等待）。</summary>
    private static ChatTurnResult SendSync(ChatSession session, string userText, int expectCalls)
    {
        var task = session.SendAndCollectAsync(userText);
        if (!task.Wait(TimeSpan.FromSeconds(20)))
        {
            Program.Fail($"整轮没有在 20 秒内结束（期望 {expectCalls} 次工具调用）");
            return new ChatTurnResult(string.Empty, 0, null, Array.Empty<ChatMessage>());
        }

        return task.Result;
    }

    /// <summary>SSE 剧本：每条 data 一行，事件之间空行，末尾补一个 [DONE]。</summary>
    private static MockResponse ServerSse(params string[] lines) => MockResponse.Sse(lines);

    private static OpenAiProvider ProviderFor(MockChatServer server, string? model = "fake-model")
    {
        var profile = new ProviderProfile
        {
            Name = "selftest",
            BaseUrl = server.BaseUrl,
            Model = model ?? string.Empty,
            ApiKey = Program.FakeKey,
            Temperature = 0,
            MaxTokens = 128,
        };

        return new OpenAiProvider(profile);
    }

    private static ToolRegistry Registry(params ITool[] tools)
    {
        var registry = new ToolRegistry();
        foreach (var tool in tools)
        {
            registry.Register(tool);
        }

        return registry;
    }

    /// <summary>把请求体里的角色按顺序串起来（u/a/t 简写太长，直接用全名）。</summary>
    private static string RoleSequence(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var roles = new List<string>();
        if (doc.RootElement.TryGetProperty("messages", out var messages))
        {
            foreach (var message in messages.EnumerateArray())
            {
                roles.Add(Expect.Str(message, "role") ?? "?");
            }
        }

        return string.Join(",", roles);
    }

    private static JsonElement? FindMessage(string body, string role)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("messages", out var messages))
        {
            foreach (var message in messages.EnumerateArray())
            {
                if (Expect.Str(message, "role") == role)
                {
                    return message.Clone();
                }
            }
        }

        return null;
    }

    /// <summary>自测用的假工具：只记参数，不碰任何真实资源。</summary>
    private sealed class FakeWeatherTool : ITool
    {
        public int CallCount { get; private set; }

        public string LastCity { get; private set; } = string.Empty;

        public string LastUnit { get; private set; } = string.Empty;

        public string LastArgsJson { get; private set; } = string.Empty;

        public string Name => "get_weather";

        public string Description => "Get the current weather for a city. (self test)";

        public string ParametersJsonSchema =>
            "{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"},\"unit\":{\"type\":\"string\"}},\"required\":[\"city\"]}";

        public ToolRisk Risk => ToolRisk.Safe;

        public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
        {
            CallCount++;
            LastArgsJson = args.GetRawText();
            LastCity = args.TryGetProperty("city", out var city) ? city.GetString() ?? string.Empty : string.Empty;
            LastUnit = args.TryGetProperty("unit", out var unit) ? unit.GetString() ?? string.Empty : string.Empty;
            return Task.FromResult(ToolResult.Ok(ToolOutput));
        }
    }
}
