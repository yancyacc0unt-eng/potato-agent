// 实测 1 与实测 2：离线全链路、权限门。

using PotatoAgent.Core.Agent;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Agent.SelfTest;

internal static class ChainTests
{
    /// <summary>实测 1：发消息 → 流式文本 → 模型要求调工具 → 工具执行 → 结果回灌 → 第二轮拿到最终回答。</summary>
    public static async Task OfflineToolLoopAsync()
    {
        var echo = new FakeEchoTool();
        var tools = new ToolRegistry();
        tools.Register(echo);

        using var server = MockServer.Start((index, _) => index switch
        {
            1 => Sse.Script(
                Sse.Text("Let me check. "),
                Sse.ToolCall(0, "call_1", "fake_echo", "{\"text\":\"ping\"}"),
                Sse.Finish("tool_calls"),
                Sse.Done),
            2 => Sse.Script(
                Sse.Text("Tool said: pong"),
                Sse.Finish("stop"),
                Sse.Done),
            _ => Sse.Script(Sse.Text("(意外的第三次请求)"), Sse.Finish("stop"), Sse.Done),
        });

        using var provider = Provider.For(server);
        var session = new AgentSession(provider, tools);

        var events = await Runner.RunAsync(session, "please echo ping");
        var result = Runner.Result(events);

        Program.Info("事件序列: " + string.Join(" → ", events.Select(Describe)));

        Program.Check(server.RequestCount == 2, $"假服务端一共收到 2 次请求（实际 {server.RequestCount}）");

        var first = server.Request(0)!;
        Program.Check(
            first.Body.Contains("\"tools\"") && first.Body.Contains("fake_echo"),
            "第一次请求带上了 tools 数组（模型能看到有哪些工具）");

        Program.Check(events.Count(e => e is AgentTextDelta) == 2, "流式文本增量收了 2 次（两轮各一次）");
        Program.Check(events.Count(e => e is AgentToolStarting) == 1, "恰好发出 1 条 AgentToolStarting");
        Program.Check(events.Count(e => e is AgentToolFinished) == 1, "恰好发出 1 条 AgentToolFinished");
        Program.Check(events.Count(e => e is AgentToolDenied) == 0, "没有任何工具被拒绝");
        Program.Check(events.Count(e => e is AgentError) == 0, "没有出错事件");
        Program.Check(events[^1] is AgentTurnCompleted, "最后一条事件是 AgentTurnCompleted");

        var order = events.Select(Describe).ToList();
        var firstText = order.FindIndex(x => x.StartsWith("text(", StringComparison.Ordinal));
        var start = order.FindIndex(x => x.StartsWith("tool-start(", StringComparison.Ordinal));
        var finish = order.FindIndex(x => x.StartsWith("tool-end(", StringComparison.Ordinal));
        var lastText = order.FindLastIndex(x => x.StartsWith("text(", StringComparison.Ordinal));
        Program.Check(
            firstText >= 0 && firstText < start && start < finish && finish < lastText,
            "顺序正确：文本 → 工具开始 → 工具结束 → 第二轮文本");

        Program.Check(echo.Executions == 1 && echo.LastText == "ping", "假工具真的被执行了 1 次，拿到参数 text=ping");
        Program.Check(result.FinalText == "Tool said: pong", $"最终回答是第二轮的文本（实际 \"{result.FinalText}\"）");
        Program.Check(result.ToolCallsExecuted == 1 && result.ToolCallsDenied == 0, "统计：执行 1 次、拒绝 0 次");
        Program.Check(result.FinishReason == "stop", $"结束原因 stop（实际 {result.FinishReason ?? "<null>"}）");
        Program.Check(!result.StoppedAtRoundLimit, "没有撞上轮次上限");

        var second = server.Request(1)!;
        Program.Check(
            second.Body.Contains("\"role\":\"tool\"") && second.Body.Contains("call_1") && second.Body.Contains("pong"),
            "第二次请求体里带着 role=tool 的结果（tool_call_id 对得上，内容就是工具返回的 pong）");
        Program.Check(
            second.Body.Contains("\"tool_calls\""),
            "第二次请求体里带着上一轮 assistant 的 tool_calls（模型知道自己在等什么）");

        Program.Check(session.History.Count == 4, $"历史 4 条：user / assistant / tool / assistant（实际 {session.History.Count}）");
    }

    /// <summary>实测 2：没有 approver 一律拒绝；approver 说拒绝就不执行；说允许才执行；AllowAlways 记住不再问。</summary>
    public static async Task ApprovalGateAsync()
    {
        // (a) 没有 approver
        {
            var danger = new FakeDangerTool();
            var tools = new ToolRegistry();
            tools.Register(danger);

            using var server = OneDangerCallServer();
            using var provider = Provider.For(server);
            var session = new AgentSession(provider, tools, approver: null);

            var events = await Runner.RunAsync(session, "delete everything");
            var result = Runner.Result(events);

            Program.Check(danger.Executions == 0, "反证 A：没有 approver 时 Confirm 工具【一次都没执行】(Executions=0)");
            Program.Check(events.Count(e => e is AgentToolDenied) == 1, "A：发出了 AgentToolDenied");
            Program.Check(events.Count(e => e is AgentToolFinished) == 0, "A：没有 AgentToolFinished（它压根没跑）");
            Program.Check(result.ToolCallsExecuted == 0 && result.ToolCallsDenied == 1, "A：统计为 执行 0 / 拒绝 1");
            Program.Check(
                server.Request(1)!.Body.Contains("no approver"),
                "A：模型收到的拒绝理由是『没有配置 approver』");
        }

        // (b) approver 返回拒绝
        {
            var danger = new FakeDangerTool();
            var tools = new ToolRegistry();
            tools.Register(danger);

            using var server = OneDangerCallServer();
            using var provider = Provider.For(server);
            var approver = new RecordingApprover(ToolApprovalDecision.Deny);
            var session = new AgentSession(provider, tools, approver);

            var events = await Runner.RunAsync(session, "delete everything");
            var result = Runner.Result(events);

            Program.Check(approver.Calls == 1, "B：approver 被 await 了 1 次（执行前确实问了）");
            Program.Check(
                approver.LastToolName == "fake_danger" && approver.LastRisk == ToolRisk.Confirm &&
                (approver.LastArgumentsJson ?? string.Empty).Contains("delete"),
                "B：approver 拿到了工具名 / 风险等级 / 参数原文");
            Program.Check(danger.Executions == 0, "反证 B：approver 说拒绝 → 工具【一次都没执行】(Executions=0)");
            Program.Check(events.Count(e => e is AgentToolDenied) == 1, "B：发出了 AgentToolDenied");
            Program.Check(result.ToolCallsExecuted == 0 && result.ToolCallsDenied == 1, "B：统计为 执行 0 / 拒绝 1");
        }

        // (c) approver 说"这次允许"
        {
            var danger = new FakeDangerTool();
            var tools = new ToolRegistry();
            tools.Register(danger);

            using var server = OneDangerCallServer();
            using var provider = Provider.For(server);
            var approver = new RecordingApprover(ToolApprovalDecision.AllowOnce);
            var session = new AgentSession(provider, tools, approver);

            var events = await Runner.RunAsync(session, "delete everything");
            var result = Runner.Result(events);

            Program.Check(approver.Calls == 1, "C：approver 被 await 了 1 次");
            Program.Check(danger.Executions == 1, "正证 C：approver 说允许 → 工具【真的执行了 1 次】(Executions=1)");
            Program.Check(events.Count(e => e is AgentToolFinished) == 1, "C：发出了 AgentToolFinished");
            Program.Check(events.Count(e => e is AgentToolDenied) == 0, "C：没有拒绝事件");
            Program.Check(result.ToolCallsExecuted == 1 && result.ToolCallsDenied == 0, "C：统计为 执行 1 / 拒绝 0");
            Program.Check(
                server.Request(1)!.Body.Contains("dangerous thing"),
                "C：工具的真实输出被回灌给了模型");
            Program.Check(
                !session.AlwaysAllowedTools.Contains("fake_danger"),
                "C：AllowOnce 不会被记住（下次还要问）");
        }

        // (d) AllowAlways：一轮里两次调用只问一次
        {
            var danger = new FakeDangerTool();
            var tools = new ToolRegistry();
            tools.Register(danger);

            using var server = MockServer.Start((index, _) => index switch
            {
                1 => Sse.Script(
                    Sse.ToolCall(0, "call_a", "fake_danger", "{\"what\":\"first\"}"),
                    Sse.ToolCall(1, "call_b", "fake_danger", "{\"what\":\"second\"}"),
                    Sse.Finish("tool_calls"),
                    Sse.Done),
                _ => Sse.Script(Sse.Text("both done"), Sse.Finish("stop"), Sse.Done),
            });

            using var provider = Provider.For(server);
            var approver = new RecordingApprover(ToolApprovalDecision.AllowAlways);
            var session = new AgentSession(provider, tools, approver);

            var events = await Runner.RunAsync(session, "do two dangerous things");
            var result = Runner.Result(events);

            Program.Check(danger.Executions == 2, "D：两次调用都执行了（Executions=2）");
            Program.Check(approver.Calls == 1, "D：只问了用户一次（AllowAlways 记住了）");
            Program.Check(session.AlwaysAllowedTools.Contains("fake_danger"), "D：会话记下了已授权的工具");
            Program.Check(result.ToolCallsExecuted == 2 && result.ToolCallsDenied == 0, "D：统计为 执行 2 / 拒绝 0");

            session.ClearRememberedApprovals();
            Program.Check(session.AlwaysAllowedTools.Count == 0, "D：ClearRememberedApprovals() 能清掉（下一轮重新问）");
        }
    }

    private static MockServer OneDangerCallServer() => MockServer.Start((index, _) => index switch
    {
        1 => Sse.Script(
            Sse.ToolCall(0, "call_1", "fake_danger", "{\"what\":\"delete everything\"}"),
            Sse.Finish("tool_calls"),
            Sse.Done),
        _ => Sse.Script(Sse.Text("ok, I will not do that"), Sse.Finish("stop"), Sse.Done),
    });

    private static string Describe(AgentEvent evt) => evt switch
    {
        AgentTextDelta delta => $"text(\"{delta.Text}\")",
        AgentToolStarting start => $"tool-start({start.Name}, risk={start.Risk}, needsApproval={start.NeedsApproval})",
        AgentToolFinished finish => $"tool-end({finish.Name}, ok={finish.Success}, images={finish.ImageCount})",
        AgentToolDenied denied => $"tool-denied({denied.Name})",
        AgentError error => $"error({Program.Trim(error.Message, 60)})",
        AgentTurnCompleted => "completed",
        _ => evt.GetType().Name,
    };
}
