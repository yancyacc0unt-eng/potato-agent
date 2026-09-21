using PotatoAgent.Core.Brain;

namespace PotatoAgent.Sessions;

/// <summary>落盘前的消息清洗：截图 base64 太占地方，不存图片。</summary>
/// <remarks>
/// <para>
/// 一次 <c>pc_screenshot</c> 的 data URI 动辄几百 KB 到几 MB，全存进 SQLite 会让库迅速膨胀，
/// 而历史里的旧截图本来也不会再喂回模型。所以存之前把图片段换成一行文字占位。
/// </para>
/// <para>
/// 同时会被"喂回模型"的那条内存消息不受影响 —— 清洗只作用在写库的那一份副本上。
/// </para>
/// </remarks>
public static class SessionMessageSanitizer
{
    /// <summary>图片段被替换成的占位文本。</summary>
    public const string ImagePlaceholder = "[image omitted]";

    /// <summary>清洗一条待落盘的消息：图片段换成文字占位，其余原样。</summary>
    /// <remarks>
    /// 保留 <see cref="ChatMessage.Role"/> / <see cref="ChatMessage.ToolCalls"/> /
    /// <see cref="ChatMessage.ToolCallId"/> / <see cref="ChatMessage.Name"/> 和全部文本段；
    /// 只有 <c>image_url</c> 段会被换成 <see cref="ImagePlaceholder"/> 文本段。
    /// 没有图片段时<b>原样返回同一个实例</b>（不做无谓的拷贝），有图片段时返回新实例，不改动入参。
    /// </remarks>
    /// <param name="message">待落盘的消息；不能为 null。</param>
    /// <returns>可以安全写库的消息。</returns>
    public static ChatMessage Sanitize(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Parts is not { Count: > 0 })
        {
            return message;
        }

        var hasImage = false;
        foreach (var part in message.Parts)
        {
            if (IsImage(part))
            {
                hasImage = true;
                break;
            }
        }

        if (!hasImage)
        {
            return message;
        }

        var parts = new List<ChatContentPart>(message.Parts.Count);
        foreach (var part in message.Parts)
        {
            parts.Add(IsImage(part) ? ChatContentPart.FromText(ImagePlaceholder) : part);
        }

        return new ChatMessage
        {
            Role = message.Role,
            Content = message.Content,
            ToolCalls = message.ToolCalls,
            ToolCallId = message.ToolCallId,
            Name = message.Name,
            Parts = parts,
        };
    }

    /// <summary>判断一段是不是图片（按 <c>type</c> 或是否带 <c>image_url</c> 载荷，两者任一成立即算）。</summary>
    private static bool IsImage(ChatContentPart? part) =>
        part is not null &&
        (part.ImageUrl is not null || string.Equals(part.Type, "image_url", StringComparison.OrdinalIgnoreCase));
}
