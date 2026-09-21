// ---------- 八、历史装回 ----------
//
// AgentSession.RestoreHistory 是"切回旧会话 / 重启后接着上次"的唯一入口：
// 先清空上下文和"总是允许"名单，再按顺序装入。这节覆盖四件事：
//   1. 装入后的条数 / 顺序 / 实例（装的就是同一批消息，不做拷贝）；
//   2. null、空表、null 条目这三种退化输入 —— 只清空、不抛、不往上下文里塞 null；
//   3. Reset() 之后确实空了，而且再装还能装回来；
//   4. 装完之后还能接着 SendAsync：假服务端收到的 messages 里必须真的有那段历史，顺序也要对。
//
// ⚠ 网络范围与其它用例一致：只打 127.0.0.1 上本进程起的假服务端（HttpListener），不发外部请求；
//    本用例不碰磁盘、不读也不写 %APPDATA%\PotatoAgent。

using System.Text.Json;
using PotatoAgent.Core.Agent;
using PotatoAgent.Core.Brain;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Core.SelfTest;

internal static class HistoryRestoreTests
{
    /// <summary>假服务端这一轮的最终回答。</summary>
    private const string Reply = "接着聊的回答。";

    public static void Run()
    {
        Console.WriteLine("---- 八、历史装回（AgentSession.RestoreHistory） ----");

        RestoreKeepsOrderAndInstances();
        NullAndEmptyRestore();
        ResetThenRestoreAgain();
        ContinueAfterRestore();

        Console.WriteLine();
    }

    // ==================== 8.1 装入的条数 / 顺序 / 实例 ====================

    private static void RestoreKeepsOrderAndInstances()
    {
        Console.WriteLine("  8.1 装回后的条数与顺序（同一批实例，不复制）");

        using var server = MockChatServer.Start(ReplyScript());
        using var provider = ProviderFor(server);
        var session = NewSession(provider);

        var history = SampleHistory();
        session.RestoreHistory(history);

        var roles = string.Join(",", session.History.Select(m => m.Role));
        Console.WriteLine($"    装回后流水 = {roles}");

        Expect.Equal(5, session.History.Count, "5 条历史一条不多一条不少");
        Expect.Equal("system,user,assistant,user,assistant", roles, "顺序与装入的完全一致");
        Expect.Equal("第一句提问", session.History[1].Content, "第二条就是那句用户消息");
        Expect.Equal("第二句回答", session.History[^1].Content, "最后一条是最后那句回答");
        Expect.True(ReferenceEquals(history[0], session.History[0]), "装进去的是同一批实例（消息不做拷贝）");

        Console.WriteLine();
    }

    // ==================== 8.2 退化输入：null / 空表 / null 条目 ====================

    private static void NullAndEmptyRestore()
    {
        Console.WriteLine("  8.2 null / 空表 = 只是清空；null 条目被跳过");

        using var server = MockChatServer.Start(ReplyScript());
        using var provider = ProviderFor(server);
        var session = NewSession(provider);

        session.RestoreHistory(SampleHistory());
        Expect.Equal(5, session.History.Count, "先装回 5 条");

        session.RestoreHistory(null);
        Expect.Equal(0, session.History.Count, "RestoreHistory(null) 只清空，不抛");

        session.RestoreHistory(SampleHistory());
        session.RestoreHistory(Array.Empty<ChatMessage>());
        Expect.Equal(0, session.History.Count, "空表同样只是清空（不会把空表当成'别动历史'）");

        var withNullEntry = new List<ChatMessage> { ChatMessage.User("甲"), null!, ChatMessage.Assistant("乙") };
        session.RestoreHistory(withNullEntry);
        Expect.Equal(2, session.History.Count, "null 条目被跳过（上下文里不会出现 null 消息）");
        Expect.Equal("甲,乙", string.Join(",", session.History.Select(m => m.Content)), "非 null 的按顺序保留");

        Console.WriteLine();
    }

    // ==================== 8.3 Reset() 之后还能再装 ====================

    private static void ResetThenRestoreAgain()
    {
        Console.WriteLine("  8.3 Reset() 之后为空，再装照常装回来");

        using var server = MockChatServer.Start(ReplyScript());
        using var provider = ProviderFor(server);
        var session = NewSession(provider);

        session.RestoreHistory(SampleHistory());
        session.Reset();
        Expect.Equal(0, session.History.Count, "Reset() 之后上下文为空");

        session.RestoreHistory(SampleHistory());
        Expect.Equal(5, session.History.Count, "Reset() 之后再 RestoreHistory 还能装回来");

        Console.WriteLine();
    }

    // ==================== 8.4 装完接着聊一轮 ====================

    private static void ContinueAfterRestore()
    {
        Console.WriteLine("  8.4 装完历史接着 SendAsync（那段历史真的发给了服务端）");

        using var server = MockChatServer.Start(ReplyScript());
        using var provider = ProviderFor(server);
        var session = NewSession(provider);

        session.RestoreHistory(SampleHistory());
        var turn = SendSync(session, "接着聊");

        var request = server.Request(0);
        var roles = RoleSequence(request);
        Console.WriteLine($"    服务端收到的流水 = {roles}");
        Console.WriteLine($"    最终回答 = {Program.Trim(turn.FinalText, 40)}");

        Expect.Equal(1, server.RequestCount, "整轮只问了 1 次（假服务端直接给最终回答）");
        Expect.Equal("system,user,assistant,user,assistant,user", roles,
            "请求里的 messages = 装回的历史 + 这次的新问题（顺序不变）");
        Expect.Contains(DecodedContents(request), "第一句提问", "那段历史真的发给了服务端，不是只留在本地");
        Expect.Equal(Reply, turn.FinalText, "回答原样透传");
        Expect.Equal(7, session.History.Count, "跑完一轮：装回的 5 条 + 新问题 + 新回答 = 7 条");
        Expect.Equal(2, turn.NewMessages.Count, "NewMessages 只含这一轮新增的 2 条（user + assistant）");

        Console.WriteLine();
    }

    // ==================== 内部工具（照 ToolLoopTests 的写法） ====================

    /// <summary>假历史：system + 两问两答。</summary>
    private static List<ChatMessage> SampleHistory() => new()
    {
        ChatMessage.System("你是一个自测用的假人设。"),
        ChatMessage.User("第一句提问"),
        ChatMessage.Assistant("第一句回答"),
        ChatMessage.User("第二句提问"),
        ChatMessage.Assistant("第二句回答"),
    };

    /// <summary>一个没有任何工具的会话（本用例只关心历史，不关心工具）。</summary>
    private static AgentSession NewSession(OpenAiProvider provider) => new(provider, new ToolRegistry());

    /// <summary>假服务端的剧本：直接吐一句最终回答，不再要工具。</summary>
    private static MockResponse ReplyScript() =>
        MockResponse.Sse(Sse.Text(Reply), Sse.Finish("stop"), Sse.Done);

    /// <summary>指向假服务端的 Provider（密钥是自测用的假串）。</summary>
    private static OpenAiProvider ProviderFor(MockChatServer server)
    {
        var profile = new ProviderProfile
        {
            Name = "selftest",
            BaseUrl = server.BaseUrl,
            Model = "fake-model",
            ApiKey = Program.FakeKey,
            Temperature = 0,
            MaxTokens = 128,
        };

        return new OpenAiProvider(profile);
    }

    /// <summary>把一轮跑完（同步等；超时就算失败，不让自测挂死）。</summary>
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

    /// <summary>请求体里 messages 的角色流水（请求没到就返回 "&lt;缺&gt;"）。</summary>
    private static string RoleSequence(MockRequest? request)
    {
        if (request is null)
        {
            Program.Fail("请求没到，取不出角色流水");
            return "<缺>";
        }

        using var doc = JsonDocument.Parse(request.Body);
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

    /// <summary>把所有消息的 content 解码后接起来（找"历史有没有真的发出去"用）。</summary>
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
}
