using System.Text.Json.Serialization;

namespace PotatoAgent.Core.Brain;

/// <summary>
/// OpenAI 兼容协议里的一条消息（也用于本地保存会话历史）。
/// </summary>
/// <remarks>
/// <para>
/// 四个角色：<c>system</c> / <c>user</c> / <c>assistant</c> / <c>tool</c>。
/// 工具结果那条必须带 <see cref="ToolCallId"/>，且必须是模型上一轮 assistant 消息里 tool_calls 的 id —— 对不上服务端会 400。
/// </para>
/// <para>
/// 要带图（例如 <c>pc_screenshot</c> 的截图）就填 <see cref="Parts"/>，序列化时会自动把
/// <c>content</c> 写成 <c>[{"type":"text",…},{"type":"image_url",…}]</c> 数组；不填就还是普通字符串。
/// </para>
/// </remarks>
[JsonConverter(typeof(ChatMessageJsonConverter))]
public sealed class ChatMessage
{
    /// <summary>角色：system / user / assistant / tool。</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    /// <summary>文本内容。assistant 只调工具不说话的轮次，这里可能是 null。</summary>
    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    /// <summary>模型要求调用的工具（只有 assistant 消息会有）。</summary>
    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolCall>? ToolCalls { get; set; }

    /// <summary>这条是哪个工具调用的结果（只有 role=tool 会有）。</summary>
    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }

    /// <summary>可选的名字（部分服务端要求 tool 消息带工具名）。</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>
    /// 多模态内容段。非空时序列化会把 <c>content</c> 写成数组，<see cref="Content"/> 被忽略。
    /// 单发一条纯文本消息时保持为 null。
    /// </summary>
    [JsonIgnore]
    public List<ChatContentPart>? Parts { get; set; }

    /// <summary>system 消息。</summary>
    public static ChatMessage System(string content) => new() { Role = "system", Content = content };

    /// <summary>user 消息。</summary>
    public static ChatMessage User(string content) => new() { Role = "user", Content = content };

    /// <summary>assistant 消息（可能同时带文本和工具调用）。</summary>
    public static ChatMessage Assistant(string? content, List<ToolCall>? toolCalls = null) =>
        new() { Role = "assistant", Content = content, ToolCalls = toolCalls };

    /// <summary>tool 消息：把工具结果回灌给模型。</summary>
    public static ChatMessage Tool(string toolCallId, string content, string? toolName = null) =>
        new() { Role = "tool", ToolCallId = toolCallId, Content = content, Name = toolName };

    /// <summary>带图（多模态数组）的 user 消息。</summary>
    public static ChatMessage UserWithParts(IEnumerable<ChatContentPart> parts) =>
        new() { Role = "user", Parts = parts?.ToList() };

    /// <summary>带图（多模态数组）的 tool 消息 —— 工具结果本身要带图时用。</summary>
    public static ChatMessage ToolWithParts(string toolCallId, IEnumerable<ChatContentPart> parts, string? toolName = null) =>
        new() { Role = "tool", ToolCallId = toolCallId, Parts = parts?.ToList(), Name = toolName };

    /// <summary>这条消息是不是多模态数组形式。</summary>
    [JsonIgnore]
    public bool HasParts => Parts is { Count: > 0 };

    /// <summary>是否是要调工具的轮次。</summary>
    [JsonIgnore]
    public bool WantsToolCall => ToolCalls is { Count: > 0 };

    /// <summary>日志用摘要。</summary>
    public override string ToString()
    {
        var calls = ToolCalls is { Count: > 0 } ? $" toolCalls={ToolCalls.Count}" : string.Empty;
        var parts = HasParts ? $" parts={Parts!.Count}" : string.Empty;
        var text = Content ?? string.Empty;
        if (text.Length > 80)
        {
            text = text[..80] + "…";
        }

        return $"[{Role}{calls}{parts}] {text}";
    }
}

/// <summary>模型要求的一次工具调用。</summary>
public sealed class ToolCall
{
    /// <summary>调用 id，回灌 tool 结果时要对上。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>固定 <c>function</c>（协议里目前只有这一种）。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    /// <summary>函数名 + 参数。</summary>
    [JsonPropertyName("function")]
    public ToolCallFunction Function { get; set; } = new();

    /// <summary>日志用。</summary>
    public override string ToString() => $"{Function.Name}({Function.Arguments})";
}

/// <summary>工具调用的函数部分。</summary>
public sealed class ToolCallFunction
{
    /// <summary>工具名（对应 <c>ITool.Name</c>）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 参数，<b>是 JSON 字符串而不是对象</b>（协议就是这么设计的，模型还经常吐半截非法 JSON）。
    /// 流式返回时这个字段会被拆成多片，需要自己拼（见 <see cref="ToolCallAccumulator"/>）。
    /// </summary>
    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}
