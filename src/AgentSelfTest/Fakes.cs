// 自测用的小工具件：一个只读假工具、一个"危险"假工具、几个 approver 记录器。

using System.Text.Json;
using PotatoAgent.Core.Agent;
using PotatoAgent.Core.Brain;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Agent.SelfTest;

/// <summary>只读假工具：回显参数，记下被调了几次、拿到什么参数。</summary>
internal sealed class FakeEchoTool : ITool
{
    public string Name => "fake_echo";

    public string Description => "Test tool: echoes the given text back.";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}
        """;

    public ToolRisk Risk => ToolRisk.Safe;

    public int Executions { get; private set; }

    public string? LastText { get; private set; }

    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        Executions++;
        LastText = args.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;

        return Task.FromResult(ToolResult.Ok("pong"));
    }
}

/// <summary>第二个只读假工具：证明护栏只看"名字 + 参数"，不会把两个不同工具当成同一个。</summary>
internal sealed class FakeOtherTool : ITool
{
    public string Name => "fake_other";

    public string Description => "Test tool: a second, different tool.";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}
        """;

    public ToolRisk Risk => ToolRisk.Safe;

    /// <summary>被执行了几次。</summary>
    public int Executions { get; private set; }

    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        Executions++;
        return Task.FromResult(ToolResult.Ok("other-pong"));
    }
}

/// <summary>"会动用户电脑"的假工具：<see cref="ToolRisk.Confirm"/>，用来验证权限门。</summary>
internal sealed class FakeDangerTool : ITool
{
    public string Name => "fake_danger";

    public string Description => "Test tool: pretends to do something destructive on the user's machine.";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{"what":{"type":"string"}},"required":["what"]}
        """;

    public ToolRisk Risk => ToolRisk.Confirm;

    /// <summary>真的被执行了几次 —— 权限门的反证就靠它。</summary>
    public int Executions { get; private set; }

    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        Executions++;
        var what = args.TryGetProperty("what", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : "?";
        return Task.FromResult(ToolResult.Ok($"dangerous thing '{what}' was done"));
    }
}

/// <summary>
/// <see cref="ToolRisk.Dangerous"/> 的假工具：验证"高级"档位下<b>该问的还是会问</b>
/// —— 高级只是免掉 Confirm，不是把所有警告都关掉。
/// </summary>
internal sealed class FakeVeryDangerTool : ITool
{
    public string Name => "fake_very_danger";

    public string Description => "Test tool: pretends to do something irreversible on the user's machine.";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{"what":{"type":"string"}},"required":["what"]}
        """;

    public ToolRisk Risk => ToolRisk.Dangerous;

    /// <summary>真的被执行了几次。</summary>
    public int Executions { get; private set; }

    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        Executions++;
        return Task.FromResult(ToolResult.Ok("irreversible thing was done"));
    }
}

/// <summary>
/// 带"资源身份"的假工具：<b>参数换个写法，身份不变</b> —— 用来验证 <see cref="IToolCallIdentity"/>
/// 这道护栏（参数逐字节那道拦不住它）。
/// </summary>
internal sealed class FakeTargetTool : ITool, IToolCallIdentity
{
    public string Name => "fake_target";

    public string Description => "Test tool: acts on a named target and reports which target that is.";

    public string ParametersJsonSchema => """
        {"type":"object","properties":{"target":{"type":"string"},"note":{"type":"string"}},"required":["target"]}
        """;

    public ToolRisk Risk => ToolRisk.Safe;

    /// <summary>真的被执行了几次。</summary>
    public int Executions { get; private set; }

    /// <summary>身份 = target（去掉首尾空白，忽略大小写）。note 只是换个写法的"噪声"。</summary>
    public string? IdentityOf(JsonElement args)
    {
        var target = args.TryGetProperty("target", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

        return string.IsNullOrWhiteSpace(target) ? null : target!.Trim();
    }

    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        Executions++;
        return Task.FromResult(ToolResult.Ok("acted"));
    }
}

/// <summary>记录调用次数、按剧本作答的 approver。</summary>
internal sealed class RecordingApprover : IToolApprover
{
    private readonly ToolApprovalDecision _decision;

    public RecordingApprover(ToolApprovalDecision decision) => _decision = decision;

    public int Calls { get; private set; }

    public string? LastToolName { get; private set; }

    public string? LastArgumentsJson { get; private set; }

    public ToolRisk LastRisk { get; private set; }

    public Task<ToolApprovalDecision> ApproveAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        Calls++;
        LastToolName = request.ToolName;
        LastArgumentsJson = request.ArgumentsJson;
        LastRisk = request.Risk;
        return Task.FromResult(_decision);
    }
}

/// <summary>自测里造 Provider 的公共部分。</summary>
internal static class Provider
{
    public static OpenAiProvider For(MockServer server) => For(server.BaseUrl);

    /// <summary>直接按 base URL 建（慢速假服务端这类不走 <see cref="MockServer"/> 的服务端用）。</summary>
    public static OpenAiProvider For(string baseUrl) => new(new ProviderProfile
    {
        Name = "mock",
        BaseUrl = baseUrl,
        Model = "mock-model",
        ApiKey = "test-key-not-real",
        Temperature = 0,
        MaxTokens = 128,
    });
}

/// <summary>跑一轮并把事件收齐。</summary>
internal static class Runner
{
    public static async Task<List<AgentEvent>> RunAsync(AgentSession session, string text)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in session.SendAsync(text))
        {
            events.Add(evt);
        }

        return events;
    }

    public static AgentTurnResult Result(List<AgentEvent> events) =>
        events.OfType<AgentTurnCompleted>().LastOrDefault()?.Result
        ?? throw new InvalidOperationException("这一轮没有发出 AgentTurnCompleted —— 对话主循环没跑完。");
}
