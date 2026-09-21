// 网页搜索的 HTML 解析器 —— 只用 BCL 的正则，不引任何 HTML 解析库。
//
// 为什么敢用正则：这两家的结果页结构十几年没变过，而且我们要的三个字段
// （标题锚 / 真实链接 / 摘要）都有稳定的 class 名。真出问题时的兜底是"解析出 0 条"
// → 引擎会退回另一个后端，而不是把脏 HTML 塞给模型。
//
// 两个必须做对的细节：
//   1. DuckDuckGo 的 href 是跳转壳 //duckduckgo.com/l/?uddg=<urlencoded>&rut=…
//      —— 不解出 uddg，模型拿到的是打不开的 duckduckgo.com 链接。
//   2. 标题/摘要里有 <b> 高亮标签和 &amp; / &#x27; / &ensp; 这类实体
//      —— 不剥标签模型会看见一堆尖括号，不解实体标题里会出现 &#x27;。

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PotatoAgent.Web;

/// <summary>
/// 把 DuckDuckGo / Bing 的搜索结果 HTML 解析成 <see cref="WebSearchResult"/> 列表。
/// </summary>
/// <remarks>
/// <para>
/// 纯函数、不碰网络，所以自测可以喂真页面裁出来的 fixture 反复跑（见 <c>src/WebSelfTest</c>）。
/// 解析器<b>不会抛异常</b>：喂 null、空串、半截标签都只是"解析出 0 条"。
/// </para>
/// <para>
/// 解析规则（都照 2026-09-21 抓下来的真页面写）：
/// DuckDuckGo 每条结果是一个 <c>&lt;a class="result__a"&gt;</c>，摘要在同一块里的
/// <c>&lt;a class="result__snippet"&gt;</c>；Bing 每条结果是一个 <c>&lt;li class="b_algo"&gt;</c>，
/// 标题在里面的 <c>&lt;h2&gt;&lt;a href="…"&gt;</c>，摘要在 <c>&lt;p&gt;</c>。
/// </para>
/// </remarks>
public static class SearchHtmlParser
{
    // ---- DuckDuckGo ----

    /// <summary>DDG 的标题锚：<c>&lt;a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=…"&gt;标题&lt;/a&gt;</c>。</summary>
    private static readonly Regex DuckTitleAnchor = new(
        """<a\b[^>]*class="[^"]*result__a[^"]*"[^>]*>(?<text>[\s\S]*?)</a>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>DDG 的摘要锚：<c>&lt;a class="result__snippet" href="…"&gt;摘要&lt;/a&gt;</c>。</summary>
    private static readonly Regex DuckSnippetAnchor = new(
        """<a\b[^>]*class="[^"]*result__snippet[^"]*"[^>]*>(?<text>[\s\S]*?)</a>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ---- Bing ----

    /// <summary>Bing 的结果块起点：<c>&lt;li class="b_algo" …&gt;</c>。</summary>
    private static readonly Regex BingBlock = new(
        """<li\b[^>]*class="[^"]*\bb_algo\b[^"]*"[^>]*>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Bing 的标题：<c>&lt;h2 class=""&gt;&lt;a target="_blank" href="…"&gt;标题&lt;/a&gt;</c>。</summary>
    private static readonly Regex BingHeading = new(
        """<h2\b[^>]*>\s*<a\b(?<attrs>[^>]*)>(?<text>[\s\S]*?)</a>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Bing 的摘要：<c>&lt;p class="b_lineclamp2"&gt;…&lt;/p&gt;</c>。</summary>
    private static readonly Regex BingParagraph = new(
        """<p\b[^>]*>(?<text>[\s\S]*?)</p>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ---- 公共小件 ----

    /// <summary>取 href（单双引号都认）。</summary>
    private static readonly Regex HrefAttribute = new(
        """href\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)')""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>一个 HTML 标签（够用就行：属性里带 &gt; 的写法这两家都不产出）。</summary>
    private static readonly Regex HtmlTag = new("""<[^>]*>""", RegexOptions.CultureInvariant);

    /// <summary>连续的空白（含 &amp;ensp; 解出来的 U+2002、&amp;nbsp; 解出来的 U+00A0）。</summary>
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>DuckDuckGo 跳转壳里的真实地址参数。</summary>
    private static readonly Regex UddgParameter = new(@"[?&]uddg=([^&]*)", RegexOptions.CultureInvariant);

    /// <summary>Bing 跳转壳里的真实地址参数（<c>u=a1&lt;base64url&gt;</c>）。</summary>
    private static readonly Regex BingUrlParameter = new(@"[?&]u=([^&]*)", RegexOptions.CultureInvariant);

    private static readonly IReadOnlyList<WebSearchResult> NoResults = Array.Empty<WebSearchResult>();

    /// <summary>
    /// 按后端解析结果页 HTML。空串 / null / 结构不对的 HTML 都会得到空列表，不会抛。
    /// </summary>
    /// <param name="html">结果页 HTML（真页面或 fixture 都行）。</param>
    /// <param name="backend">按哪家的规则解析。</param>
    /// <returns>解析出来并按 URL 去重后的结果，顺序保持页面顺序。</returns>
    public static IReadOnlyList<WebSearchResult> Parse(string? html, SearchBackend backend) =>
        backend == SearchBackend.Bing ? ParseBing(html) : ParseDuckDuckGo(html);

    /// <summary>
    /// 解析 DuckDuckGo 的 HTML 结果页（<c>html.duckduckgo.com/html/?q=…</c>）。
    /// </summary>
    /// <param name="html">结果页 HTML。</param>
    /// <returns>结果列表；一条都没解析出来时是空列表。</returns>
    public static IReadOnlyList<WebSearchResult> ParseDuckDuckGo(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return NoResults;
        }

        var anchors = DuckTitleAnchor.Matches(html);
        if (anchors.Count == 0)
        {
            return NoResults;
        }

        var results = new List<WebSearchResult>(anchors.Count);

        for (var i = 0; i < anchors.Count; i++)
        {
            var anchor = anchors[i];

            // 这条结果的地盘 = 本锚结束 → 下一个 result__a 开始。
            // 摘要只在这段里找，否则"某条结果没摘要"时会偷走隔壁那条的摘要。
            var bodyStart = anchor.Index + anchor.Length;
            var bodyEnd = i + 1 < anchors.Count ? anchors[i + 1].Index : html.Length;
            if (bodyEnd < bodyStart)
            {
                bodyEnd = bodyStart;
            }

            var openTag = html.Substring(anchor.Index, anchor.Length);
            var url = ResolveResultUrl(FirstHref(openTag), SearchBackend.DuckDuckGo);
            if (url is null)
            {
                continue;
            }

            var title = NormalizeText(anchor.Groups["text"].Value);
            if (title.Length == 0)
            {
                continue;
            }

            var snippetMatch = DuckSnippetAnchor.Match(html, bodyStart, bodyEnd - bodyStart);
            var snippet = snippetMatch.Success ? NormalizeText(snippetMatch.Groups["text"].Value) : string.Empty;

            results.Add(new WebSearchResult(title, url, snippet));
        }

        return Deduplicate(results);
    }

    /// <summary>
    /// 解析 Bing 的网页结果页（<c>www.bing.com/search?q=…</c>）。
    /// </summary>
    /// <param name="html">结果页 HTML。</param>
    /// <returns>结果列表；一条都没解析出来时是空列表。</returns>
    public static IReadOnlyList<WebSearchResult> ParseBing(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return NoResults;
        }

        var blocks = BingBlock.Matches(html);
        if (blocks.Count == 0)
        {
            return NoResults;
        }

        var results = new List<WebSearchResult>(blocks.Count);

        for (var i = 0; i < blocks.Count; i++)
        {
            // <li> 里还可能嵌 <li>，所以按"下一个 b_algo 开始"切块，而不是按 </li> 切。
            var blockStart = blocks[i].Index + blocks[i].Length;
            var blockEnd = i + 1 < blocks.Count ? blocks[i + 1].Index : html.Length;
            if (blockEnd <= blockStart)
            {
                continue;
            }

            var heading = BingHeading.Match(html, blockStart, blockEnd - blockStart);
            if (!heading.Success)
            {
                continue;
            }

            var url = ResolveResultUrl(FirstHref(heading.Groups["attrs"].Value), SearchBackend.Bing);
            if (url is null)
            {
                continue;
            }

            var title = NormalizeText(heading.Groups["text"].Value);
            if (title.Length == 0)
            {
                continue;
            }

            // 摘要在标题之后找；万一标题之后没有 <p>，退回这一块里的第一个 <p>。
            // （别用 Match.Empty 当占位：它是 Success == true 的空匹配，会让这里的判断变得很绕。）
            var pStart = heading.Index + heading.Length;
            Match? snippetMatch = pStart < blockEnd ? BingParagraph.Match(html, pStart, blockEnd - pStart) : null;
            if (snippetMatch is null || !snippetMatch.Success)
            {
                var fallback = BingParagraph.Match(html, blockStart, blockEnd - blockStart);
                if (fallback.Success)
                {
                    snippetMatch = fallback;
                }
            }

            var snippet = snippetMatch is { Success: true }
                ? NormalizeText(snippetMatch.Groups["text"].Value)
                : string.Empty;

            results.Add(new WebSearchResult(title, url, snippet));
        }

        return Deduplicate(results);
    }

    /// <summary>
    /// 把一段 HTML 片段变成干净的纯文本：剥标签 → 解实体 → 把连续空白收成一个空格再 trim。
    /// </summary>
    /// <param name="fragment">含标签 / 实体的片段；null 或空串得到空串。</param>
    /// <returns>纯文本。</returns>
    public static string NormalizeText(string? fragment)
    {
        if (string.IsNullOrEmpty(fragment))
        {
            return string.Empty;
        }

        // 先剥标签再解实体：反过来的话 &lt;b&gt; 会被解成真标签再被剥掉，信息就丢了。
        var withoutTags = HtmlTag.Replace(fragment, string.Empty);
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return Whitespace.Replace(decoded, " ").Trim();
    }

    /// <summary>
    /// 把结果里的 href 还原成"能直接打开的绝对 http(s) 地址"，还原不出来返回 null（那条结果会被丢掉）。
    /// </summary>
    /// <param name="href">HTML 里原样的 href（可以带 <c>&amp;amp;</c> 这种实体）。</param>
    /// <param name="backend">按哪家的跳转壳规则还原。</param>
    /// <returns>绝对 http(s) URL；相对地址 / javascript: / 空值一律 null。</returns>
    /// <remarks>
    /// 会认三种写法：DuckDuckGo 的 <c>//duckduckgo.com/l/?uddg=&lt;urlencoded&gt;</c>、
    /// Bing 的 <c>/ck/a?…&amp;u=a1&lt;base64url&gt;</c>、以及本来就是绝对地址的普通 href。
    /// 协议相对的 <c>//host/path</c> 会补上 https。
    /// </remarks>
    public static string? ResolveResultUrl(string? href, SearchBackend backend)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        // 属性里的 &amp; 必须先解开，否则 uddg 的值会被 &amp;rut=… 粘住。
        var raw = WebUtility.HtmlDecode(href.Trim());
        if (raw.Length == 0)
        {
            return null;
        }

        var uddg = UddgParameter.Match(raw);
        if (uddg.Success)
        {
            var decoded = Unescape(uddg.Groups[1].Value);
            if (IsHttpUrl(decoded))
            {
                return decoded;
            }
        }

        var bingRedirect = BingUrlParameter.Match(raw);
        if (bingRedirect.Success && bingRedirect.Groups[1].Value.StartsWith("a1", StringComparison.Ordinal))
        {
            var decoded = DecodeBingRedirect(bingRedirect.Groups[1].Value);
            if (IsHttpUrl(decoded))
            {
                return decoded;
            }
        }

        if (raw.StartsWith("//", StringComparison.Ordinal))
        {
            raw = "https:" + raw;
        }

        return IsHttpUrl(raw) ? raw : null;
    }

    /// <summary>
    /// 按 URL（大小写不敏感）去重，并丢掉标题或 URL 为空的条目。
    /// </summary>
    /// <param name="results">候选结果。</param>
    /// <returns>去重后的新列表，保持首次出现的顺序。</returns>
    public static IReadOnlyList<WebSearchResult> Deduplicate(IEnumerable<WebSearchResult>? results)
    {
        if (results is null)
        {
            return NoResults;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<WebSearchResult>();

        foreach (var result in results)
        {
            if (result is null || string.IsNullOrWhiteSpace(result.Url) || string.IsNullOrWhiteSpace(result.Title))
            {
                continue;
            }

            if (seen.Add(result.Url))
            {
                kept.Add(result);
            }
        }

        return kept;
    }

    /// <summary>取标签里的 href 原始值；没有 href 返回 null。</summary>
    private static string? FirstHref(string? tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return null;
        }

        var match = HrefAttribute.Match(tag);
        return match.Success ? match.Groups["value"].Value : null;
    }

    /// <summary>是不是绝对 http(s) 地址。</summary>
    private static bool IsHttpUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>百分号解码；坏转义序列不抛，退回 null。</summary>
    private static string? Unescape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>解 Bing 的 <c>u=a1&lt;base64url&gt;</c> 跳转壳（base64url = 标准 base64 的 +/ 换成 -_）。</summary>
    private static string? DecodeBingRedirect(string value)
    {
        var payload = value[2..];
        if (payload.Length == 0)
        {
            return null;
        }

        var standard = payload.Replace('-', '+').Replace('_', '/');
        switch (standard.Length % 4)
        {
            case 2: standard += "=="; break;
            case 3: standard += "="; break;
            case 1: return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(standard));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
