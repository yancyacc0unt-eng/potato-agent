// ---------- 六、思考模式（reasoning_content 回传） ----------
//
// DeepSeek 思考模式有一条硬要求（官方 thinking_mode 文档）：请求里带 tools 时，
// 【之前所有轮次】的 reasoning_content 必须完整回传，漏一个服务端就 400。
// 而思维链本身不是回答正文 —— 混进 content 的话界面会把思考过程当回答显示，历史也会被污染。
//
// 这一节用本地假服务端把两件事都钉死：
//   1) 流式解析：delta.reasoning_content 是【独立事件】（ChatReasoningDelta），不混进正文增量；
//   2) 会话层：这一轮的思维链写进 assistant 消息，之后每个请求原样带回；
//      并且反证 —— 模型没给（或只给了个空串）时，请求体里【不该出现】这个字段。
//
// 全部只打 127.0.0.1 上的本地假服务端，不发任何真实网络请求、不碰真实配置。

using System.Text.Json;
using PotatoAgent.Core.Agent;
using PotatoAgent.Core.Brain;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Core.SelfTest;

internal static class ReasoningTests
{
    /// <summary>第一片思维链。</summary>
    private const string Think1 = "用户问的是上海天气。";

    /// <summary>第二片思维链（故意和第一片分开发，验证按顺序拼回来）。</summary>
    private const string Think2 = "先调 get_weather，参数 city=上海。";

    /// <summary>第二轮（收尾）才出现的思维链，用来验证"所有之前轮次都回传"。</summary>
    private const string ThinkSecond = "上海问过了，这轮改问北京。";

    private const string Reply = "上海 22°C，晴。";

    private const string ToolOutput = "上海 22°C 晴";

    public static void Run()
    {
        Console.WriteLine("---- 六、思考模式（reasoning_content 回传）----");

        ProtocolField();
        StreamEvents();
        RoundTripWithTools();
        AllPreviousRounds();
        NoReasoningNoField();
    }

    // ==================== 6.1 协议字段本身 ====================

    private static void ProtocolField()
    {
        Console.WriteLine("  6.1 协议字段：有思维链就写 reasoning_content，没有（或空串）就别写");

        var json = JsonSerializer.Serialize(ChatMessage.Assistant("好的", null, "因为…所以…"));
        var back = JsonSerializer.Deserialize<ChatMessage>(json);

        Expect.Contains(json, "\"reasoning_content\"", "assistant 带思维链时，序列化里出现 reasoning_content（JSON 名与协议一致）");
        Expect.Equal("因为…所以…", back?.ReasoningContent, "反序列化（本地存档读回）思维链一字不差");
        Expect.Equal("好的", back?.Content, "读回来之后正文还是正文，没被思维链顶替");

        Expect.DoesNotContain(JsonSerializer.Serialize(ChatMessage.Assistant("好的")), "reasoning_content",
            "模型没给思维链时，请求里连这个字段都不写（不是写个 null）");
        Expect.DoesNotContain(JsonSerializer.Serialize(ChatMessage.Assistant("好的", null, string.Empty)), "reasoning_content",
            "模型只给了个空串时，同样不写这个字段");
        Expect.Equal(null, ChatMessage.Assistant("好的", null, string.Empty).ReasoningContent,
            "空串按『没有』处理，历史里不留一个空壳");

        Console.WriteLine();
    }

    // ==================== 6.2 流式解析：独立事件，不混正文 ====================

    private static void StreamEvents()
    {
        Console.WriteLine("  6.2 流式解析：delta.reasoning_content 是独立事件，不混进正文");

        using var server = MockChatServer.Start(MockResponse.Sse(
            Sse.Reasoning(Think1),
            Sse.Reasoning(Think2),
            Sse.Text("好的，"),
            Sse.Text(Reply),
            Sse.Finish("stop"),
            Sse.Done));

        using var provider = ProviderFor(server);
        var events = Drain(provider.StreamCompletionAsync(new[] { ChatMessage.User("上海天气？") }));

        var thoughts = events.OfType<ChatReasoningDelta>().Select(e => e.Text).ToList();
        var texts = events.OfType<ChatTextDelta>().Select(e => e.Text).ToList();

        Console.WriteLine($"    思维链 {thoughts.Count} 片 / 正文 {texts.Count} 片");

        Expect.Equal(2, thoughts.Count, "两片 reasoning_content 解析成 2 个独立的 ChatReasoningDelta");
        Expect.Equal(2, texts.Count, "两片 content 仍然是 2 个 ChatTextDelta（没被思维链顶掉）");
        Expect.Equal(Think1 + Think2, string.Join(string.Empty, thoughts), "思维链按顺序拼回来一字不差");
        Expect.Equal("好的，" + Reply, string.Join(string.Empty, texts), "正文拼出来只有 content，思维链一个字都没混进来");
        Expect.DoesNotContain(string.Join(string.Empty, texts), Think2, "正文里找不到思维链");
        Expect.True(events.Count > 0 && events[0] is ChatReasoningDelta, "思维链事件先到（官方顺序：先 reasoning 后 content）");

        Console.WriteLine();
    }

    // ==================== 6.3 一轮工具调用：第二轮请求必须带上思维链 ====================

    private static void RoundTripWithTools()
    {
        Console.WriteLine("  6.3 一轮工具调用：第二轮请求必须带上上一轮 assistant 的 reasoning_content");

        using var server = MockChatServer.Start((count, _) => count == 1
            ? MockResponse.Sse(
                Sse.Reasoning(Think1),
                Sse.Reasoning(Think2),
                Sse.Tool(0, "call_r1", "get_weather", "{\"city\":\"上海\"}"),
                Sse.Finish("tool_calls"),
                Sse.Done)
            : MockResponse.Sse(Sse.Text(Reply), Sse.Finish("stop"), Sse.Done));

        var tool = new FakeWeatherTool();
        using var provider = ProviderFor(server);
        var session = new AgentSession(provider, Registry(tool));

        var result = SendSync(session, "上海今天天气怎么样？");
        Console.WriteLine($"    工具调用 {tool.CallCount} 次 / 服务端收到 {server.RequestCount} 个请求");
        Console.WriteLine($"    最终回答 : {Program.Trim(result.FinalText, 60)}");

        Expect.Equal(1, tool.CallCount, "工具真的被执行了");
        Expect.Equal(2, server.RequestCount, "一共 2 个请求（要工具那轮 + 回灌后那轮）");
        Expect.Equal(Reply, result.FinalText, "最终回答 = 正文，思维链没混进去");

        var assistant = session.History.FirstOrDefault(m => m.Role == "assistant");
        Expect.Equal(Think1 + Think2, assistant?.ReasoningContent, "assistant 消息里存下了整轮思维链（两片拼完整）");
        Expect.Equal(null, assistant?.Content, "只调工具那轮没有正文 —— content 还是 null，没被思维链顶替");

        var second = server.Request(1);
        if (second is null)
        {
            Program.Fail("第二个请求根本没到（回灌之后没有继续问）");
            return;
        }

        Expect.Equal("user,assistant,tool", RoleSequence(second.Body), "角色流水没被破坏：user,assistant,tool");
        var sent = FindAssistant(second.Body);
        Expect.Equal(Think1 + Think2, Expect.Str(sent, "reasoning_content"),
            "第二轮请求体里 assistant.reasoning_content = 上一轮思维链全文（逐字符一致）");
        Expect.True(sent.ValueKind == JsonValueKind.Object && !sent.TryGetProperty("content", out _),
            "思维链没有被塞进 content 字段（两者是两回事）");

        Console.WriteLine();
    }

    // ==================== 6.4 所有之前轮次都要回传 ====================

    private static void AllPreviousRounds()
    {
        Console.WriteLine("  6.4 多轮：之前【所有】轮次的思维链都要在（第 3 个请求里能看到两条 assistant）");

        using var server = MockChatServer.Start((count, _) => count switch
        {
            1 => MockResponse.Sse(
                Sse.Reasoning(Think1),
                Sse.Tool(0, "call_a", "get_weather", "{\"city\":\"上海\"}"),
                Sse.Finish("tool_calls"),
                Sse.Done),
            2 => MockResponse.Sse(
                Sse.Reasoning(ThinkSecond),
                Sse.Tool(0, "call_b", "get_weather", "{\"city\":\"北京\"}"),
                Sse.Finish("tool_calls"),
                Sse.Done),
            _ => MockResponse.Sse(Sse.Text(Reply), Sse.Finish("stop"), Sse.Done),
        });

        var tool = new FakeWeatherTool();
        using var provider = ProviderFor(server);
        var session = new AgentSession(provider, Registry(tool));

        var result = SendSync(session, "上海和北京的天气都看一下");

        Expect.Equal(3, server.RequestCount, "一共 3 个请求（两次要工具 + 最后收尾）");
        Expect.Equal(2, tool.CallCount, "两次工具调用都真的执行了（换了目标，没被重复护栏误拦）");

        var third = server.Request(2);
        if (third is null)
        {
            Program.Fail("第三个请求根本没到");
            return;
        }

        var assistants = Assistants(third.Body);
        Expect.Equal(2, assistants.Count, "第三个请求里有 2 条 assistant 消息");
        Expect.Equal(Think1, assistants.Count > 0 ? Expect.Str(assistants[0], "reasoning_content") : "<缺>",
            "第 1 轮的思维链回传了");
        Expect.Equal(ThinkSecond, assistants.Count > 1 ? Expect.Str(assistants[1], "reasoning_content") : "<缺>",
            "第 2 轮的思维链也回传了（『所有之前轮次』都算，不是只带最近一条）");
        Expect.Equal("user,assistant,tool,assistant,tool", RoleSequence(third.Body),
            "角色流水 = user,assistant,tool,assistant,tool");
        Expect.Equal(Reply, result.FinalText, "最终回答不受影响");

        Console.WriteLine();
    }

    // ==================== 6.5 反证：没给 / 给了空串时不该出现这个字段 ====================

    private static void NoReasoningNoField()
    {
        Console.WriteLine("  6.5 反证：模型没给思维链（或只给空串）时，请求体里不该出现这个字段");

        // (a) 整段剧本里完全没有 reasoning_content 帧。
        using (var server = MockChatServer.Start((count, _) => count == 1
            ? MockResponse.Sse(
                Sse.Tool(0, "call_plain", "get_weather", "{\"city\":\"上海\"}"),
                Sse.Finish("tool_calls"),
                Sse.Done)
            : MockResponse.Sse(Sse.Text(Reply), Sse.Finish("stop"), Sse.Done)))
        {
            using var provider = ProviderFor(server);
            var session = new AgentSession(provider, Registry(new FakeWeatherTool()));

            SendSync(session, "上海天气？");

            var second = server.Request(1);
            Expect.True(second is not null, "第二个请求到了");
            Expect.DoesNotContain(second?.Body ?? string.Empty, "reasoning_content",
                "模型没给思维链 → 第二轮请求体里一个 reasoning_content 都没有（不写 null、不写空串）");
            Expect.Equal(null, session.History.FirstOrDefault(m => m.Role == "assistant")?.ReasoningContent,
                "会话历史里的 assistant 也没有思维链");
        }

        // (b) 服务端先发一个空的 reasoning_content 占位帧（野路子服务端会这么干）。
        using (var server = MockChatServer.Start((count, _) => count == 1
            ? MockResponse.Sse(
                Sse.Reasoning(string.Empty),
                Sse.Tool(0, "call_empty", "get_weather", "{\"city\":\"上海\"}"),
                Sse.Finish("tool_calls"),
                Sse.Done)
            : MockResponse.Sse(Sse.Text(Reply), Sse.Finish("stop"), Sse.Done)))
        {
            using var provider = ProviderFor(server);
            var session = new AgentSession(provider, Registry(new FakeWeatherTool()));

            SendSync(session, "上海天气？");

            var second = server.Request(1);
            Expect.DoesNotContain(second?.Body ?? string.Empty, "reasoning_content",
                "空的占位帧不算思维链 → 请求体里同样没有这个字段");
        }

        Console.WriteLine();
    }

    // ==================== 内部工具 ====================

    /// <summary>把一条用户消息整轮跑完（同步等待）。</summary>
    private static AgentTurnResult SendSync(AgentSession session, string userText)
    {
        var task = session.SendAndCollectAsync(userText);
        if (!task.Wait(TimeSpan.FromSeconds(20)))
        {
            Program.Fail("整轮没有在 20 秒内结束");
            return new AgentTurnResult(string.Empty, 0, 0, null, false, Array.Empty<ChatMessage>());
        }

        return task.Result;
    }

    /// <summary>把一次流式请求的事件全部收下来（同步等待）。</summary>
    private static List<ChatStreamEvent> Drain(IAsyncEnumerable<ChatStreamEvent> source)
    {
        var task = Catch.DrainAsync(source);
        if (!task.Wait(TimeSpan.FromSeconds(20)))
        {
            Program.Fail("流式请求没有在 20 秒内跑完");
            return new List<ChatStreamEvent>();
        }

        return task.Result;
    }

    private static OpenAiProvider ProviderFor(MockChatServer server) =>
        new(new ProviderProfile
        {
            Name = "selftest",
            BaseUrl = server.BaseUrl,
            Model = "thinking-model",   // 思考模式的模型名在这里只是个字符串，服务端是假的
            ApiKey = Program.FakeKey,
            Temperature = 0,
            MaxTokens = 128,
        });

    private static ToolRegistry Registry(params ITool[] tools)
    {
        var registry = new ToolRegistry();
        foreach (var tool in tools)
        {
            registry.Register(tool);
        }

        return registry;
    }

    /// <summary>把请求体里的角色按顺序串起来。</summary>
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

    /// <summary>请求体里第一条 assistant 消息（没有就返回 default，取字段会得到 null）。</summary>
    private static JsonElement FindAssistant(string body)
    {
        var assistants = Assistants(body);
        return assistants.Count > 0 ? assistants[0] : default;
    }

    /// <summary>请求体里所有 assistant 消息（按顺序，用来验证"每轮都带上了"）。</summary>
    private static List<JsonElement> Assistants(string body)
    {
        var found = new List<JsonElement>();

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("messages", out var messages))
        {
            foreach (var message in messages.EnumerateArray())
            {
                if (Expect.Str(message, "role") == "assistant")
                {
                    found.Add(message.Clone());
                }
            }
        }

        return found;
    }

    /// <summary>自测用的假工具：只记调用次数，不碰任何真实资源（Risk = Safe，不需要权限门）。</summary>
    private sealed class FakeWeatherTool : ITool
    {
        public int CallCount { get; private set; }

        public string Name => "get_weather";

        public string Description => "Get the current weather for a city. (self test)";

        public string ParametersJsonSchema =>
            "{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"}},\"required\":[\"city\"]}";

        public ToolRisk Risk => ToolRisk.Safe;

        public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(ToolResult.Ok(ToolOutput));
        }
    }
}
