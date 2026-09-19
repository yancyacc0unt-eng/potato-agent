namespace PotatoAgent.Core.Tools;

/// <summary>
/// 工具结果里附带的一张图片（截图、图表、渲染结果…），内存里就是一段 base64。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由只有一个：<b>让模型真的"看见"</b>。<c>pc_screenshot</c> 只把文件写到盘上、
/// 回一句"截图存好了"是没有意义的 —— 模型看不到就等于没截。所以图片要一路带到
/// <see cref="PotatoAgent.Core.Agent.AgentSession"/>，由它转成
/// <c>image_url</c>（data URI）回灌给模型。
/// </para>
/// <para>成本提醒：base64 比原始字节大约 1.37 倍，而且会一直留在会话历史里。
/// 截图工具因此提供缩放参数，别每轮都塞一张 1920×1080 的原图。</para>
/// </remarks>
public sealed class ToolImage
{
    /// <summary>建一张图。</summary>
    /// <param name="mimeType">MIME 类型，例如 <c>image/png</c>。</param>
    /// <param name="base64Data">base64 编码的图片字节，<b>不带</b> <c>data:</c> 前缀。</param>
    /// <param name="caption">可选的一句话说明（"这是整个屏幕"之类）。</param>
    public ToolImage(string mimeType, string base64Data, string? caption = null)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            throw new ArgumentException("MIME type is required.", nameof(mimeType));
        }

        if (string.IsNullOrEmpty(base64Data))
        {
            throw new ArgumentException("Image data is required.", nameof(base64Data));
        }

        MimeType = mimeType;
        Base64Data = base64Data;
        Caption = caption;
    }

    /// <summary>MIME 类型，默认 <c>image/png</c>。</summary>
    public string MimeType { get; }

    /// <summary>base64 编码的图片字节。</summary>
    public string Base64Data { get; }

    /// <summary>可选的说明文字。</summary>
    public string? Caption { get; }

    /// <summary>聊天协议里直接可用的 data URI：<c>data:image/png;base64,iVBOR…</c>。</summary>
    public string DataUri => $"data:{MimeType};base64,{Base64Data}";

    /// <summary>原始字节数（由 base64 长度反推，用来估算塞进请求体的代价）。</summary>
    public int ApproximateByteCount => (Base64Data.Length / 4) * 3;

    /// <summary>从原始字节建一张图。</summary>
    public static ToolImage FromBytes(byte[] bytes, string mimeType = "image/png", string? caption = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new ToolImage(mimeType, Convert.ToBase64String(bytes), caption);
    }

    /// <summary>日志用摘要（绝不打 base64 本身）。</summary>
    public override string ToString() => $"{MimeType} ~{ApproximateByteCount} bytes";
}
