using System.Text.Json;

namespace PotatoAgent.Core.Tools;

/// <summary>
/// 工具执行结果：成功/失败标志 + 给模型看的文本 + 可选的结构化 JSON + 可选的图片。
/// </summary>
/// <remarks>
/// 刻意做成不可变类而不是 <c>record</c>，避免有人用 <c>with</c> 改出个半成品。
/// 用 <see cref="Ok(string)"/> / <see cref="Error(string)"/> 构造。
/// </remarks>
public sealed class ToolResult
{
    private static readonly IReadOnlyList<ToolImage> NoImages = Array.Empty<ToolImage>();

    private ToolResult(bool success, string content, JsonElement? data, IReadOnlyList<ToolImage>? images)
    {
        Success = success;
        Content = content;
        Data = data;
        Images = images ?? NoImages;
    }

    /// <summary>true = 成功；false = 失败（失败也要有可读的 <see cref="Content"/>，模型需要知道为什么失败）。</summary>
    public bool Success { get; }

    /// <summary>给模型看的文本，不要塞二进制。</summary>
    public string Content { get; }

    /// <summary>可选的结构化 JSON（已 Clone，可以安全持有，不受原 JsonDocument 释放影响）。</summary>
    public JsonElement? Data { get; }

    /// <summary>
    /// 附带的图片（截图这类）。空列表表示没有。
    /// 图片本身<b>不会</b>进 <see cref="ToModelText"/>，由 AgentSession 转成 <c>image_url</c> 单独回灌。
    /// </summary>
    public IReadOnlyList<ToolImage> Images { get; }

    /// <summary>有没有附带图片。</summary>
    public bool HasImages => Images.Count > 0;

    /// <summary>结构化 JSON 的原始文本；没有则 null。</summary>
    public string? DataJson => Data?.GetRawText();

    /// <summary>成功，只给文本。</summary>
    public static ToolResult Ok(string content) => new(true, content ?? string.Empty, null, null);

    /// <summary>成功，附带结构化 JSON。<paramref name="data"/> 会被 Clone，调用方可以立刻释放自己的 JsonDocument。</summary>
    public static ToolResult Ok(string content, JsonElement data) => new(true, content ?? string.Empty, data.Clone(), null);

    /// <summary>成功，附带一张或多张图片（截图工具用这个，模型才真的看得见）。</summary>
    public static ToolResult OkWithImages(string content, IEnumerable<ToolImage>? images) =>
        new(true, content ?? string.Empty, null, images?.ToArray());

    /// <summary>成功，同时带结构化 JSON 和图片。</summary>
    public static ToolResult OkWithImages(string content, JsonElement data, IEnumerable<ToolImage>? images) =>
        new(true, content ?? string.Empty, data.Clone(), images?.ToArray());


    /// <summary>
    /// 成功，附带结构化 JSON（已序列化的字符串）。字符串不是合法 JSON 时退化成"只有文本"，
    /// 不抛异常 —— 工具自己写坏了 JSON 不该拖垮对话。
    /// </summary>
    public static ToolResult OkJson(string content, string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return Ok(content);
        }

        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            return new ToolResult(true, content ?? string.Empty, doc.RootElement.Clone(), null);
        }
        catch (JsonException)
        {
            return Ok(content);
        }
    }

    /// <summary>失败。文本会以 ERROR 前缀回灌给模型。</summary>
    public static ToolResult Error(string message) => new(false, message ?? "unknown error", null, null);

    /// <summary>回灌给模型的文本：失败加 <c>ERROR:</c> 前缀，结构化 JSON 附在后面。</summary>
    public string ToModelText()
    {
        var text = Success ? Content : "ERROR: " + Content;
        if (Data is { } data)
        {
            text = text + Environment.NewLine + data.GetRawText();
        }

        return text;
    }

    /// <summary>日志用的一行摘要（截断，别把整份数据打进日志）。</summary>
    public override string ToString()
    {
        var preview = Content.Length <= 120 ? Content : Content[..120] + "…";
        var images = HasImages ? $" [+{Images.Count} image(s)]" : string.Empty;
        return (Success ? "ok: " : "error: ") + preview + images;
    }
}
