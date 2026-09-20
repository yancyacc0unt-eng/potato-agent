// 实测 6：AgentSession 的"同一回合重复调用"护栏。
//
// 护栏的由来是真实事故：模型想开记事本，一时没拿到窗口句柄就反复调 pc_launch，
// 用户点过 Allow always 之后连确认框都不弹 —— 屏幕上静默堆出一堆记事本窗口。
//
// 这里同时跑正证和四条反证：
//   正证：同一回合里同名 + 参数（规范化后）完全相同的调用，第二次起不再执行，只回一条带第一次结果的提示；
//   反证 1：换个参数 → 正常执行；
//   反证 2：换个工具（参数一模一样）→ 正常执行；
//   反证 3：换一个回合（第二次 SendAsync）→ 正常执行；
//   反证 4：把 DeduplicateToolCalls 关掉 → 重复调用照常执行。
// 外加一条"事故复现"：Confirm 工具 + AllowAlways，同一个回合里连调两次 —— 只能真的执行一次。

using PotatoAgent.Core.Agent;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Agent.SelfTest;

internal static class GuardTests
{
    /// <summary>一个回合里：完全相同、只差空白、换个参数、换个工具 —— 四种情况一起上。</summary>
    private const string FiveCalls =
        """
        同一个回合里发五次调用：
          c1 fake_echo  {"text":"ping"}          ← 第一次，正常执行
          c2 fake_echo  { "text" : "ping" }      ← 规范化后与 c1 逐字节相等 → 必须被拦
          c3 fake_echo  {"text":"ping"}          ← 一模一样 → 必须被拦
          c4 fake_echo  {"text":"pong"}          ← 参数不同 → 必须执行
          c5 fake_other {"text":"ping"}          ← 工具不同（参数与 c1 相同）→ 必须执行
        """;

    public static async Task DuplicateCallsAsync()
    {
        await SameTurnAsync();
        await NewTurnAsync();
        await ConfirmToolAccidentAsync();
        await GuardDisabledAsync();
    }

    /// <summary>正证 + 反证 1/2：同一回合内，只有"同名 + 同参数"被拦。</summary>
    private static async Task SameTurnAsync()
    {
        Program.Info(FiveCalls);

        var echo = new FakeEchoTool();
        var other = new FakeOtherTool();
        var tools = new ToolRegistry();
        tools.Register(echo);
        tools.Register(other);

        using var server = MockServer.Start((index, _) => index == 1
            ? Sse.Script(
                Sse.ToolCall(0, "c1", "fake_echo", "{\"text\":\"ping\"}"),
                Sse.ToolCall(1, "c2", "fake_echo", "{ \"text\" : \"ping\" }"),
                Sse.ToolCall(2, "c3", "fake_echo", "{\"text\":\"ping\"}"),
                Sse.ToolCall(3, "c4", "fake_echo", "{\"text\":\"pong\"}"),
                Sse.ToolCall(4, "c5", "fake_other", "{\"text\":\"ping\"}"),
                Sse.Finish("tool_calls"),
                Sse.Done)
            : Sse.Script(Sse.Text("all done"), Sse.Finish("stop"), Sse.Done));

        using var provider = Provider.For(server);
        var session = new AgentSession(provider, tools);

        var events = await Runner.RunAsync(session, "call the tools");
        var result = Runner.Result(events);

        Program.Info("事件序列: " + string.Join(" → ", events.Select(Describe)));

        Program.Check(echo.Executions == 2, $"反证 1：换参数的 c4 照常执行 —— fake_echo 一共跑了 2 次（实际 {echo.Executions}）");
        Program.Check(other.Executions == 1, $"反证 2：换工具的 c5 照常执行（参数与 c1 相同）—— fake_other 跑了 1 次（实际 {other.Executions}）");
        Program.Check(result.ToolCallsExecuted == 3, $"统计：真的执行了 3 次（c1/c4/c5）（实际 {result.ToolCallsExecuted}）");
        Program.Check(result.DuplicateCallsSkipped == 2, $"统计：拦下 2 次重复（c2 只差空白、c3 完全相同）（实际 {result.DuplicateCallsSkipped}）");
        Program.Check(result.ToolCallsDenied == 0, "被拦的重复调用不算「拒绝」（它们没到权限门）");

        var blocked = events.OfType<AgentToolFinished>().Where(f => !f.Success).ToList();
        Program.Check(blocked.Count == 2, $"两次重复各发了一条 AgentToolFinished(Success=false)（实际 {blocked.Count}）");
        Program.Check(
            blocked.All(f => f.Summary.Contains("You already called fake_echo with these exact arguments in this turn", StringComparison.Ordinal)),
            "提示里点名了「你已经用完全相同的参数调过 fake_echo」");
        Program.Check(
            blocked.All(f => f.Summary.Contains("result was: pong", StringComparison.Ordinal)),
            "提示里带上了第一次的结果摘要（result was: pong）—— 模型知道该往下走");

        // 回灌给模型的 tool 消息里也必须带摘要（模型看不到界面，只看得到 tool 消息）。
        var second = server.Request(1)?.Body ?? string.Empty;
        Program.Check(
            second.Contains("You already called fake_echo with these exact arguments in this turn; result was: pong", StringComparison.Ordinal),
            "第二次请求体里，被拦的那两次调用带回了「第一次结果是 pong」的摘要");
        Program.Check(
            second.Contains("other-pong", StringComparison.Ordinal),
            "c5（不同工具）的真实结果也照常回灌");
    }

    /// <summary>反证 3：换一个回合（新的一次 SendAsync）→ 同样的调用正常执行。</summary>
    private static async Task NewTurnAsync()
    {
        var echo = new FakeEchoTool();
        var tools = new ToolRegistry();
        tools.Register(echo);

        // 奇数号请求 = 又要调工具；偶数号 = 收尾（每个回合正好吃掉两个请求）。
        using var server = MockServer.Start((index, _) => index % 2 == 1
            ? Sse.Script(
                Sse.ToolCall(0, "same", "fake_echo", "{\"text\":\"ping\"}"),
                Sse.Finish("tool_calls"),
                Sse.Done)
            : Sse.Script(Sse.Text("done"), Sse.Finish("stop"), Sse.Done));

        using var provider = Provider.For(server);
        var session = new AgentSession(provider, tools);

        var first = Runner.Result(await Runner.RunAsync(session, "turn one"));
        var second = Runner.Result(await Runner.RunAsync(session, "turn two, same call"));

        Program.Check(
            echo.Executions == 2 && first.ToolCallsExecuted == 1 && second.ToolCallsExecuted == 1,
            $"反证 3：两个回合各自执行了一次同样的调用（Executions={echo.Executions}，各回合执行 {first.ToolCallsExecuted}/{second.ToolCallsExecuted}）");
        Program.Check(
            first.DuplicateCallsSkipped == 0 && second.DuplicateCallsSkipped == 0,
            "两个回合的重复计数都是 0（账本按回合清零）");
    }

    /// <summary>事故复现：Confirm 工具 + Allow always，同一回合连调两次同名同参数的调用。</summary>
    private static async Task ConfirmToolAccidentAsync()
    {
        var danger = new FakeDangerTool();
        var tools = new ToolRegistry();
        tools.Register(danger);

        using var server = MockServer.Start((index, _) => index == 1
            ? Sse.Script(
                Sse.ToolCall(0, "d1", "fake_danger", "{\"what\":\"rm -rf\"}"),
                Sse.ToolCall(1, "d2", "fake_danger", "{\"what\":\"rm -rf\"}"),
                Sse.Finish("tool_calls"),
                Sse.Done)
            : Sse.Script(Sse.Text("ok"), Sse.Finish("stop"), Sse.Done));

        using var provider = Provider.For(server);
        var approver = new RecordingApprover(ToolApprovalDecision.AllowAlways);
        var session = new AgentSession(provider, tools, approver);

        var result = Runner.Result(await Runner.RunAsync(session, "do it twice"));

        Program.Info($"AllowAlways + 同一个回合里连调两次：approver 被问 {approver.Calls} 次，工具真的跑了 {danger.Executions} 次");
        Program.Check(danger.Executions == 1, $"事故复现：Allow always 之后连调两次，危险动作只真的执行 1 次（实际 {danger.Executions}）");
        Program.Check(approver.Calls == 1, $"只问了用户 1 次（第二次被护栏拦在权限门之前）");
        Program.Check(result.DuplicateCallsSkipped == 1, "统计：拦下 1 次重复");
        Program.Check(
            session.AlwaysAllowedTools.Contains("fake_danger"),
            "AllowAlways 仍然被记住（护栏没有把'记住授权'这件事弄坏）");
    }

    /// <summary>反证 4：把护栏关掉 → 重复调用照常执行（这个开关是真的能关的）。</summary>
    private static async Task GuardDisabledAsync()
    {
        var echo = new FakeEchoTool();
        var tools = new ToolRegistry();
        tools.Register(echo);

        using var server = MockServer.Start((index, _) => index == 1
            ? Sse.Script(
                Sse.ToolCall(0, "x1", "fake_echo", "{\"text\":\"ping\"}"),
                Sse.ToolCall(1, "x2", "fake_echo", "{\"text\":\"ping\"}"),
                Sse.Finish("tool_calls"),
                Sse.Done)
            : Sse.Script(Sse.Text("ok"), Sse.Finish("stop"), Sse.Done));

        using var provider = Provider.For(server);
        var session = new AgentSession(provider, tools) { DeduplicateToolCalls = false };

        var result = Runner.Result(await Runner.RunAsync(session, "call it twice on purpose"));

        Program.Check(result.DuplicateCallsSkipped == 0, "反证 4：关掉护栏后一次都没拦（DuplicateCallsSkipped=0）");
        Program.Check(echo.Executions == 2, $"反证 4：两次相同调用都真的执行了（实际 {echo.Executions}）");
        Program.Check(result.ToolCallsExecuted == 2, $"反证 4：统计为执行 2 次（实际 {result.ToolCallsExecuted}）");
    }

    private static string Describe(AgentEvent evt) => evt switch
    {
        AgentTextDelta delta => $"text(\"{Program.Trim(delta.Text, 30)}\")",
        AgentToolStarting start => $"tool-start({start.Name}, needsApproval={start.NeedsApproval})",
        AgentToolFinished finish => $"tool-end({finish.Name}, ok={finish.Success}, {Program.Trim(finish.Summary, 70)})",
        AgentToolDenied denied => $"tool-denied({denied.Name})",
        AgentError error => $"error({Program.Trim(error.Message, 50)})",
        AgentTurnCompleted => "completed",
        _ => evt.GetType().Name,
    };
}
