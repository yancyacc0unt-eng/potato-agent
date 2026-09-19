using PotatoAgent.Core.Tools;

namespace PotatoAgent.Core.Brain;

/// <summary>
/// 一轮对话（用户说一句 → 模型可能调几次工具 → 给出最终回答）过程中吐出来的事件。
/// 界面照着这个刷气泡和"正在调用 pc_screenshot"这类过程提示。
/// </summary>
public abstract record ChatTurnEvent;

/// <summary>模型正文的增量文本。</summary>
public sealed record TurnTextDelta(string Text) : ChatTurnEvent;

/// <summary>模型要求调用某个工具，即将执行。界面可以在这里显示"正在调用 xxx"。</summary>
public sealed record TurnToolCallStarting(string Id, string Name, string ArgumentsJson, ToolRisk Risk) : ChatTurnEvent;

/// <summary>工具执行完了（成功或失败都有）。</summary>
public sealed record TurnToolCallFinished(string Id, string Name, ToolResult Result, TimeSpan Elapsed) : ChatTurnEvent;

/// <summary>模型这一轮的话说完了（可能带工具调用请求），已经进历史。</summary>
public sealed record TurnAssistantMessage(ChatMessage Message) : ChatTurnEvent;

/// <summary>整轮结束。<paramref name="FinalText"/> 是所有文本增量的拼接。</summary>
public sealed record TurnCompleted(
    string FinalText,
    int ToolCallsExecuted,
    string? FinishReason,
    bool StoppedAtRoundLimit,
    IReadOnlyList<ChatMessage> NewMessages) : ChatTurnEvent;

/// <summary><see cref="ChatSession.SendAndCollectAsync"/> 的返回值（不关心过程时的简版结果）。</summary>
/// <param name="FinalText">最终回答文本。</param>
/// <param name="ToolCallsExecuted">这轮一共跑了几个工具。</param>
/// <param name="FinishReason">服务端给的结束原因。</param>
/// <param name="NewMessages">这轮新进历史的消息（user / assistant / tool）。</param>
public sealed record ChatTurnResult(
    string FinalText,
    int ToolCallsExecuted,
    string? FinishReason,
    IReadOnlyList<ChatMessage> NewMessages);
