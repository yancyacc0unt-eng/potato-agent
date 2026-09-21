// 后端枚举 + 它的小工具（显示名 / 主机名 / 请求 URL）。
//
// URL 只在这一个地方拼，解析器和引擎都从这里取，免得"某个地方少编码一个空格"
// 这种 bug 散在两处。

namespace PotatoAgent.Web;

/// <summary>
/// 搜索后端。<see cref="Bing"/> 是主，<see cref="DuckDuckGo"/> 是备（2026-09-21 yancy 拍板换的顺序）。
/// </summary>
/// <remarks>
/// 两个都走"网页 HTML 直读"这条路（<c>html.duckduckgo.com</c> / <c>www.bing.com</c>），
/// 不用任何 API key —— 这正是这个工具存在的理由。
/// 实测（2026-09-21）：<c>lite.duckduckgo.com</c> 只回 HTTP 202 空壳、
/// <c>api.duckduckgo.com/?format=json</c> 只回 1.2 KB 空壳，两者都已弃用，别再加回来。
/// </remarks>
public enum SearchBackend
{
    /// <summary>
    /// DuckDuckGo 的 HTML 版（<c>https://html.duckduckgo.com/html/?q=…</c>）。<b>备用后端</b>
    /// —— 它现在更容易回 HTTP 202 反爬空壳，所以让 Bing 打头阵。
    /// </summary>
    DuckDuckGo = 0,

    /// <summary>
    /// Bing 的网页版（<c>https://www.bing.com/search?q=…&amp;setlang=en</c>）。<b>主后端</b>。
    /// </summary>
    Bing = 1,
}

/// <summary>
/// <see cref="SearchBackend"/> 的显示名 / 主机名 / 请求 URL。
/// 抽出来是为了让错误文本、日志、自测断言都说同一套名字。
/// </summary>
public static class SearchBackendExtensions
{
    /// <summary>人看的名字：<c>DuckDuckGo</c> / <c>Bing</c>（错误文本和工具输出都用它）。</summary>
    /// <param name="backend">后端。</param>
    public static string DisplayName(this SearchBackend backend) =>
        backend == SearchBackend.Bing ? "Bing" : "DuckDuckGo";

    /// <summary>请求打到哪台主机（写进错误文本，让模型和日志一眼知道是谁挂了）。</summary>
    /// <param name="backend">后端。</param>
    public static string Host(this SearchBackend backend) =>
        backend == SearchBackend.Bing ? "www.bing.com" : "html.duckduckgo.com";

    /// <summary>
    /// 拼出该后端的搜索 URL，查询词走 <see cref="Uri.EscapeDataString(string)"/> 编码
    /// （空格变 <c>%20</c>、<c>&amp;</c> 变 <c>%26</c>，绝不会把查询词漏进别的参数里）。
    /// </summary>
    /// <param name="backend">后端。</param>
    /// <param name="query">原始查询词，未编码。</param>
    /// <returns>绝对 URL。</returns>
    public static string BuildUrl(this SearchBackend backend, string query)
    {
        var encoded = Uri.EscapeDataString(query ?? string.Empty);

        // Bing 带 cc=us：实测不带国家码时偶发返回与查询词无关的缓存页（查询 dotnet 却回快递追踪），
        // 带上之后连着几次都是正常结果。setlang=en 是主 agent 实测过的那条。
        return backend == SearchBackend.Bing
            ? $"https://www.bing.com/search?q={encoded}&setlang=en&cc=us"
            : $"https://html.duckduckgo.com/html/?q={encoded}";
    }
}
