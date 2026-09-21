// 网页搜索结果的数据形状。
//
// 刻意做成不可变的小类而不是 record：跟 Core 的 ToolResult 一个风格，
// 建出来就只读，谁也别想在中途把它改半个。

namespace PotatoAgent.Web;

/// <summary>
/// 一条网页搜索结果：标题 + 真实网址 + 摘要。
/// </summary>
/// <remarks>
/// <see cref="Url"/> 一定是能直接打开的绝对 http(s) 地址 —— DuckDuckGo 的
/// <c>//duckduckgo.com/l/?uddg=…</c> 跳转壳、Bing 的 <c>/ck/a?…&amp;u=a1…</c> 跳转壳
/// 都在解析阶段被还原掉了（见 <see cref="SearchHtmlParser.ResolveResultUrl"/>）。
/// </remarks>
public sealed class WebSearchResult
{
    /// <summary>建一条结果。三个字段都会被解析器去空白（可能是空串，但不会是 null）。</summary>
    /// <param name="title">标题，HTML 标签与实体都已处理干净。</param>
    /// <param name="url">真实网址，绝对 http(s)。</param>
    /// <param name="snippet">摘要，可能为空串（有的结果本身就没摘要）。</param>
    public WebSearchResult(string title, string url, string snippet)
    {
        Title = title ?? string.Empty;
        Url = url ?? string.Empty;
        Snippet = snippet ?? string.Empty;
    }

    /// <summary>标题，纯文本（已剥标签、解实体）。</summary>
    public string Title { get; }

    /// <summary>真实网址，绝对 http(s)，可直接打开。</summary>
    public string Url { get; }

    /// <summary>摘要，纯文本；没有摘要时是空串。</summary>
    public string Snippet { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Title} — {Url}";
}
