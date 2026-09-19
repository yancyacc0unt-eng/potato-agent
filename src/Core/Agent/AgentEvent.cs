using PotatoAgent.Core.Brain;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Core.Agent;

/// <summary>
/// <see cref="AgentSession.SendAsync"/> 在一轮对话里吐出来的事件。界面照着它刷新 UI：
/// 文本追加到气泡、工具调用显示成一条过程行、出错弹提示、整轮完成时收尾。
/// </summary>
public abstract record AgentEvent;

/// <summary>模型正文的增量文本。追加到当前气泡即可。</summary>
public sealed record AgentTextDelta(string Text) : AgentEvent;

/// <summary>模型要求调用某个工具，<b>即将</b>进入权限门。</summary>
/// <param name="Id">调用 id（回灌结果时用它配对）。</param>
/// <param name="Name">工具名。</param>
/// <param name="ArgumentsJson">模型给的参数原文，直接显示给用户看。</param>
/// <param name="Risk">危险等级。</param>
/// <param name="NeedsApproval">true = 接下来会弹确认框（界面可以显示"等待确认…"）。</param>
public sealed record AgentToolStarting(string Id, string Name, string ArgumentsJson, ToolRisk Risk, bool NeedsApproval) : AgentEvent;

/// <summary>工具跑完了（成功或失败都走这里）。</summary>
/// <param name="Id">调用 id。</param>
/// <param name="Name">工具名。</param>
/// <param name="Success">是否成功。</param>
/// <param name="Summary">结果摘要（已截断 + 去掉换行，可以直接塞进一行过程提示）。</param>
/// <param name="Elapsed">耗时。</param>
/// <param name="ImageCount">这次结果带回来几张图（0 = 纯文本）。</param>
public sealed record AgentToolFinished(string Id, string Name, bool Success, string Summary, TimeSpan Elapsed, int ImageCount = 0) : AgentEvent;

/// <summary>工具被权限门挡下了，<b>没有执行</b>。</summary>
/// <param name="Id">调用 id。</param>
/// <param name="Name">工具名。</param>
/// <param name="Reason">为什么被挡（没有 approver / 用户点了拒绝 / 批准流程坏了）。</param>
public sealed record AgentToolDenied(string Id, string Name, string Reason) : AgentEvent;

/// <summary>这一轮出错了（网络不通、服务端 4xx、协议不对…）。出错后本轮立即结束。</summary>
/// <param name="Message">给用户看的错误描述。</param>
/// <param name="Exception">原始异常，可能为 null。</param>
public sealed record AgentError(string Message, Exception? Exception = null) : AgentEvent;

/// <summary>整轮结束（正常收尾、出错收尾、撞上轮次上限都会发这一条，且一定是最后一条事件）。</summary>
public sealed record AgentTurnCompleted(AgentTurnResult Result) : AgentEvent;

/// <summary>一轮对话的最终结果。</summary>
/// <param name="FinalText">最后一条 assistant 消息的正文（所有文本增量拼起来）。</param>
/// <param name="ToolCallsExecuted">真正执行了的工具调用次数。</param>
/// <param name="ToolCallsDenied">被权限门拒绝的次数（这些没有执行）。</param>
/// <param name="FinishReason">服务端给的结束原因（<c>stop</c> / <c>tool_calls</c> / <c>length</c>…）。</param>
/// <param name="StoppedAtRoundLimit">true = 工具循环撞上了 <see cref="AgentSession.MaxToolRounds"/>，回答可能没说完。</param>
/// <param name="NewMessages">这一轮新进历史的消息（user / assistant / tool / 图片追问）。</param>
public sealed record AgentTurnResult(
    string FinalText,
    int ToolCallsExecuted,
    int ToolCallsDenied,
    string? FinishReason,
    bool StoppedAtRoundLimit,
    IReadOnlyList<ChatMessage> NewMessages);
