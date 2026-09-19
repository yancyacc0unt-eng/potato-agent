namespace PotatoAgent.Core.Brain;

/// <summary>
/// 流式响应里吐出来的事件。<see cref="OpenAiProvider.StreamCompletionAsync"/> 一次请求吐一串。
/// </summary>
public abstract record ChatStreamEvent;

/// <summary>文本增量。拼起来就是这一轮的回复正文。<paramref name="Text"/> 可能是一个字，也可能是一整段。</summary>
public sealed record ChatTextDelta(string Text) : ChatStreamEvent;

/// <summary>
/// 工具调用增量。<b>同一个工具调用的名字和参数会被拆成好几个 chunk 发过来</b>，
/// 靠 <paramref name="Index"/> 归并（见 <see cref="ToolCallAccumulator"/>）。
/// </summary>
/// <param name="Index">工具调用序号；服务端没给时为 null（罕见，靠 id 归并）。</param>
/// <param name="Id">调用 id，通常只在第一片里有。</param>
/// <param name="Name">函数名，通常只在第一片里有。</param>
/// <param name="ArgumentsFragment">参数字符串的碎片，需要按顺序首尾相接。</param>
public sealed record ChatToolCallDelta(int? Index, string? Id, string? Name, string ArgumentsFragment) : ChatStreamEvent;

/// <summary>服务端在流的最后给出的结束原因（<c>stop</c> / <c>tool_calls</c> / <c>length</c>…）。</summary>
public sealed record ChatFinished(string? FinishReason, TokenUsage? Usage) : ChatStreamEvent;

/// <summary>token 用量（服务端给了才有）。</summary>
public sealed record TokenUsage(int PromptTokens, int CompletionTokens, int TotalTokens);
