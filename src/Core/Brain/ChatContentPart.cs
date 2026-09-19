using System.Text.Json;
using System.Text.Json.Serialization;

namespace PotatoAgent.Core.Brain;

/// <summary>
/// OpenAI 多模态消息里 <c>content</c> 数组的一段：一段文本，或一张图片。
/// </summary>
/// <remarks>
/// <para>
/// 纯文本消息的 <c>content</c> 是一个字符串；一旦要带图，<c>content</c> 就必须变成数组：
/// <c>[{"type":"text","text":"…"},{"type":"image_url","image_url":{"url":"data:image/png;base64,…"}}]</c>。
/// <see cref="ChatMessage.Parts"/> 非空时，<see cref="ChatMessageJsonConverter"/> 就按数组写出去。
/// </para>
/// <para>用 <see cref="FromText"/> / <see cref="FromImageDataUri"/> 构造，别自己拼 JSON。</para>
/// </remarks>
public sealed class ChatContentPart
{
    /// <summary><c>text</c> 或 <c>image_url</c>。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    /// <summary>文本段的内容；图片段为 null。</summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    /// <summary>图片段的载荷；文本段为 null。</summary>
    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatImageUrl? ImageUrl { get; set; }

    /// <summary>一段文本。</summary>
    public static ChatContentPart FromText(string text) => new() { Type = "text", Text = text ?? string.Empty };

    /// <summary>
    /// 一张图片，<paramref name="dataUri"/> 形如 <c>data:image/png;base64,iVBOR…</c>。
    /// 用 data URI 而不是 http URL：截图是本机内存里的东西，没有可访问的地址，也不该为了看一眼图先落盘再起个服务。
    /// </summary>
    /// <param name="dataUri">data URI 形式的图片。</param>
    /// <param name="detail">可选的 <c>low</c> / <c>high</c> / <c>auto</c>；服务端不认识时会被忽略。</param>
    public static ChatContentPart FromImageDataUri(string dataUri, string? detail = null) => new()
    {
        Type = "image_url",
        ImageUrl = new ChatImageUrl { Url = dataUri ?? string.Empty, Detail = detail },
    };
}

/// <summary><see cref="ChatContentPart"/> 里 <c>image_url</c> 对象。</summary>
public sealed class ChatImageUrl
{
    /// <summary>图片地址：data URI 或 http(s) URL。</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>可选的细节档位（low / high / auto）。</summary>
    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }
}

/// <summary>
/// <see cref="ChatMessage"/> 的序列化器：让同一条消息既能写成纯文本 <c>content</c>，
/// 也能在带图时写成 <c>content</c> 数组。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不直接给 ChatMessage 加两个 JsonPropertyName 都叫 content 的属性</b>：System.Text.Json 不允许重名。
/// 所以这里手写一遍写出逻辑。必须保证 <see cref="ChatMessage.Parts"/> 为 null 时输出与默认序列化<b>逐字节一致</b>
/// （属性顺序 role → content → tool_calls → tool_call_id → name，null 一律不写），
/// 否则会惊动已经在跑的 CoreSelfTest。
/// </para>
/// <para>反序列化只做基本还原（本地存档用），从不参与协议往返。</para>
/// </remarks>
public sealed class ChatMessageJsonConverter : JsonConverter<ChatMessage>
{
    /// <summary>写出一条消息。</summary>
    public override void Write(Utf8JsonWriter writer, ChatMessage value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteString("role", value.Role);

        if (value.Parts is { Count: > 0 })
        {
            writer.WritePropertyName("content");
            JsonSerializer.Serialize(writer, value.Parts, options);
        }
        else if (value.Content is not null)
        {
            writer.WriteString("content", value.Content);
        }

        if (value.ToolCalls is { Count: > 0 })
        {
            writer.WritePropertyName("tool_calls");
            JsonSerializer.Serialize(writer, value.ToolCalls, options);
        }

        if (value.ToolCallId is not null)
        {
            writer.WriteString("tool_call_id", value.ToolCallId);
        }

        if (value.Name is not null)
        {
            writer.WriteString("name", value.Name);
        }

        writer.WriteEndObject();
    }

    /// <summary>读回一条消息（本地存档用）。</summary>
    public override ChatMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var message = new ChatMessage();

        if (root.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String)
        {
            message.Role = role.GetString() ?? "user";
        }

        if (root.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                message.Content = content.GetString();
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                message.Parts = JsonSerializer.Deserialize<List<ChatContentPart>>(content.GetRawText(), options);
            }
        }

        if (root.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            message.ToolCalls = JsonSerializer.Deserialize<List<ToolCall>>(calls.GetRawText(), options);
        }

        if (root.TryGetProperty("tool_call_id", out var callId) && callId.ValueKind == JsonValueKind.String)
        {
            message.ToolCallId = callId.GetString();
        }

        if (root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            message.Name = name.GetString();
        }

        return message;
    }
}
