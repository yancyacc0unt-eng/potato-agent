// ============================================================================
// 网页搜索（PotatoAgent.Web）自测 —— 可复跑工程，退出码 0 = 全部通过。
//
// 默认（不带参数）= 【全离线】：所有 HTTP 都由假 handler 回 fixture，
// 一份网络请求都不发。smoke.bat 会跑它，所以这里绝不允许依赖网络。
// 想真联网验一遍就加参数：WebSelfTest live（见 LiveCheck.cs，不接进 smoke.bat）。
//
// 覆盖九块：
//   一、DDG fixture 解析：≥3 条、标题/URL/摘要非空、uddg 跳转壳还原、&#x27; 实体会解码
//   二、Bing fixture 解析：≥3 条、<h2> 里的 href、<strong> 剥离、&ensp;/&#0183; 处理
//   三、后端链        ：Bing 有结果就不再问 DDG；Bing 空壳退 DDG；两边都挂时原因分别写清
//   四、limit 与去重  ：默认 5、上限 10、≤0 退回默认、按页面顺序截断、同 URL 只留一条
//   五、坏输入        ：null / 空串 / 半截标签 / 没有 href / 非 http 链接 → 都不抛，只是 0 条
//   六、契约 + HTTP   ：Name/Risk/Schema/Description、UA、Accept-Language、Referer、解压、15 秒超时
//   七、工具层        ：InvokeAsync 的取参、文本格式、Data JSON、失败文本
//   八、门面与注册表  ：CreateAll / RegisterAll / skipExisting / 经注册表端到端调用
//   九、代理兜底      ：系统代理传输层失败 → 直连重试；HTTP 状态码不重试；两条路都记账
//
// fixture 不是手写的假 HTML，是从真页面裁下来的（查询词 "dotnet 10 release notes"）：
//   ddg.html       —— html.duckduckgo.com 真结果区（前 5 个结果块）
//   bing.html      —— www.bing.com 真结果页（前 5 个 li.b_algo 块）
//   ddg-shell.html —— DDG 反爬 challenge 页的一段（一个结果块都没有，用来验"备后端也会挂"）
// （"Bing 回了 200 但 0 条"那种空壳没有真页面 fixture —— Bing 没结果时的结构五花八门 ——
//   用 Program.cs 里手写的 BingEmptyShell 顶一下，它只代表"这个后端这次没结果"这一种状态。）
//
// 跑法：
//   dotnet build build\WebSelfTest.csproj -c Debug
//   dotnet run   -p build\WebSelfTest.csproj -c Debug          （离线）
//   dotnet run   -p build\WebSelfTest.csproj -c Debug - live   （联网，见 LiveCheck）
// ============================================================================

using System.Net;
using System.Text;
using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Web.SelfTest;

internal static class Program
{
    /// <summary>
    /// 手写的"Bing 回了 200 但一条结果都没有"的最小页面。
    /// 这是唯一允许手写的一份 HTML：它不代表真页面的结构（真页面裁出来的 fixture 都在 Fixtures\ 里），
    /// 只用来制造"主后端这次没给结果"这一个状态 —— Bing 没结果时的真页面结构五花八门，没必要去抓一份。
    /// 里面刻意不含 <c>b_algo</c>，也不含 <c>result__a</c>，两个解析器都认不出结果。
    /// </summary>
    private const string BingEmptyShell =
        "<!doctype html><html><head><title>dotnet 10 release notes - Search</title></head>" +
        "<body><ol id=\"b_results\"></ol><div id=\"b_context\">There are no results for this query.</div></body></html>";

    private static int _pass;
    private static int _fail;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 输出被重定向时可能失败，不影响结论
        }

        if (args.Any(IsLiveArgument))
        {
            return await LiveCheck.RunAsync().ConfigureAwait(false);
        }

        Console.WriteLine("==================== PotatoAgent.Web 网页搜索自测（离线） ====================");
        Console.WriteLine($"fixture 目录 : {Fixtures.Directory ?? "(没找到！)"}");
        Console.WriteLine("本趟全程离线：所有 HTTP 都由假 handler 回 fixture，一个真实请求都不发。");
        Console.WriteLine();

        try
        {
            DuckDuckGoSection();
            BingSection();
            await BackendChainSectionAsync().ConfigureAwait(false);
            await LimitSectionAsync().ConfigureAwait(false);
            RobustnessSection();
            await ContractSectionAsync().ConfigureAwait(false);
            await ToolSectionAsync().ConfigureAwait(false);
            await RegistrationSectionAsync().ConfigureAwait(false);
            await ProxyFallbackSectionAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Fail($"自测自身抛异常（说明被测代码漏了一种异常）: {error.GetType().Name}: {error.Message}");
        }

        GuardSection();
        return Report();
    }

    // ==================== 一、DuckDuckGo fixture 解析 ====================

    private static void DuckDuckGoSection()
    {
        Section("一、DuckDuckGo fixture 解析（真页面裁出来的 markup）");

        Check("ddg.html 在", Fixtures.Exists("ddg.html"));
        var html = Fixtures.Read("ddg.html");
        Info($"ddg.html {html.Length} 字符");
        Check("ddg.html 不是空文件", html.Length > 3000);
        Check("fixture 里真的有 class=\"result__a\" 锚", html.Contains("class=\"result__a\"", StringComparison.Ordinal));
        Check("fixture 里的 href 真的是 uddg 跳转壳", html.Contains("uddg=", StringComparison.Ordinal));

        var results = SearchHtmlParser.ParseDuckDuckGo(html);
        Info($"解析出 {results.Count} 条");

        Check("解析出 ≥3 条", results.Count >= 3);
        Check("每条标题都非空", results.All(r => r.Title.Length > 0));
        Check("每条摘要都非空", results.All(r => r.Snippet.Length > 0));
        Check("每条 URL 都是绝对 https", results.All(r => r.Url.StartsWith("https://", StringComparison.Ordinal)));
        Check("URL 互不重复", results.Select(r => r.Url).Distinct(StringComparer.OrdinalIgnoreCase).Count() == results.Count);

        var first = results[0];
        Info($"第 1 条: {first.Title} → {first.Url}");
        Check("第 1 条标题对得上真页面", first.Title.Contains("core/release-notes/10.0 at main", StringComparison.Ordinal));
        Check("第 1 条 uddg 跳转壳被还原成真实 URL",
            first.Url == "https://github.com/dotnet/core/tree/main/release-notes/10.0");
        Check("还原后的 URL 不再是 duckduckgo.com 的跳转链接",
            !first.Url.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase));
        Check("还原后的 URL 没有残留百分号编码 / &amp; 实体",
            !first.Url.Contains("%3A", StringComparison.OrdinalIgnoreCase) &&
            !first.Url.Contains("&amp;", StringComparison.Ordinal));
        Check("带 ?view= 的那条 query 也完整还原",
            results.Any(r => r.Url.Contains("?view=aspnetcore-10.0", StringComparison.Ordinal)));

        Check("HTML 实体 &#x27; 被解码成 '",
            results.Any(r => r.Title.Contains("What's new in .NET 10", StringComparison.Ordinal)));
        Check("所有文本里都没有 &#… 实体残留",
            results.All(r => !r.Title.Contains("&#", StringComparison.Ordinal) &&
                             !r.Snippet.Contains("&#", StringComparison.Ordinal)));
        Check("所有文本里都没有 HTML 标签残留",
            results.All(r => !r.Title.Contains('<') && !r.Snippet.Contains('<')));
        Check("所有文本里都没有连续两个空格",
            results.All(r => !r.Title.Contains("  ", StringComparison.Ordinal) &&
                             !r.Snippet.Contains("  ", StringComparison.Ordinal)));
        Check("摘要确实抓到了真内容（含 release notes 字样）",
            results.Any(r => r.Snippet.Contains("release notes", StringComparison.OrdinalIgnoreCase)));
    }

    // ==================== 二、Bing fixture 解析 ====================

    private static void BingSection()
    {
        Section("二、Bing fixture 解析（真页面裁出来的 markup）");

        Check("bing.html 在", Fixtures.Exists("bing.html"));
        var html = Fixtures.Read("bing.html");
        Info($"bing.html {html.Length} 字符");
        Check("bing.html 不是空文件", html.Length > 3000);
        Check("fixture 里真的有 li.b_algo 块", html.Contains("class=\"b_algo\"", StringComparison.Ordinal));

        var results = SearchHtmlParser.ParseBing(html);
        Info($"解析出 {results.Count} 条");

        Check("解析出 ≥3 条", results.Count >= 3);
        Check("每条标题都非空", results.All(r => r.Title.Length > 0));
        Check("每条摘要都非空", results.All(r => r.Snippet.Length > 0));
        Check("每条 URL 都是绝对 https", results.All(r => r.Url.StartsWith("https://", StringComparison.Ordinal)));

        Info($"第 1 条: {results[0].Title} → {results[0].Url}");
        Check("第 1 条标题与真页面一字不差（<strong> 已剥离、空白已收敛）",
            results[0].Title == "Download .NET (Linux, macOS, and Windows) | .NET");
        Check("第 1 条 URL 就是 <h2> 里那个 href",
            results[0].Url == "https://dotnet.microsoft.com/en-us/download");
        Check("第 5 条的 dotnet/dotnet 标题没被高亮标签切断",
            results.Any(r => r.Title.Contains("GitHub - dotnet/dotnet", StringComparison.Ordinal)));
        Check("摘要里的 &ensp; / &#0183; 被处理干净（以 Sep 8, 2026 开头且没有 & 残留）",
            results[0].Snippet.StartsWith("Sep 8, 2026", StringComparison.Ordinal) &&
            !results[0].Snippet.Contains('&'));
        Check("所有文本里都没有连续两个空格",
            results.All(r => !r.Title.Contains("  ", StringComparison.Ordinal) &&
                             !r.Snippet.Contains("  ", StringComparison.Ordinal)));

        // 交叉验证：两家的规则互不通用，说明 fixture 真的各自走各自的分支
        Check("Bing 规则吃 DDG fixture → 0 条", SearchHtmlParser.ParseBing(Fixtures.Read("ddg.html")).Count == 0);
        Check("DDG 规则吃 Bing fixture → 0 条", SearchHtmlParser.ParseDuckDuckGo(html).Count == 0);
    }

    // ==================== 三、后端链 ====================

    private static async Task BackendChainSectionAsync()
    {
        Section("三、后端链：Bing 主 → DDG 备 → 两边都失败都要写清各自原因");

        var ddg = Fixtures.Read("ddg.html");
        var bing = Fixtures.Read("bing.html");
        var shell = Fixtures.Read("ddg-shell.html");

        // 1) 主后端给了结果：只发一个请求，不再白问备后端
        var direct = FixtureHandler.Split(ddg, bing);
        using (var engine = new SearchEngine(direct))
        {
            var outcome = await engine.SearchAsync("dotnet 10 release notes", 5).ConfigureAwait(false);
            Check("Bing 有结果时 Success == true", outcome.Success);
            Check("用的是 Bing（主后端）", outcome.Backend == SearchBackend.Bing && outcome.BackendName == "Bing");
            Check("只发了 1 个请求（主后端成功就不碰备后端）", direct.Requests.Count == 1);
            Check("请求打在 www.bing.com/search",
                direct.Requests[0].Host == "www.bing.com" && direct.Requests[0].AbsolutePath == "/search");
            Check("主后端请求带 setlang=en（主 agent 实测过的那条）",
                direct.Requests[0].QueryValue("setlang") == "en");
            Check("尝试记录里 Bing 是成功的那一次",
                outcome.Attempts.Count == 1 && outcome.Attempts[0].Succeeded && outcome.Attempts[0].HttpStatus == 200);
        }

        // 2) 主后端回空壳（0 条）→ 退到 DuckDuckGo
        var fallback = new FixtureHandler(uri => uri.Host.Contains("bing", StringComparison.OrdinalIgnoreCase)
            ? FakeResponse.Html(BingEmptyShell)
            : FakeResponse.Html(ddg));
        using (var engine = new SearchEngine(fallback))
        {
            var outcome = await engine.SearchAsync("dotnet 10 release notes", 5).ConfigureAwait(false);
            Check("Bing 空壳时退到 DuckDuckGo 并成功",
                outcome.Success && outcome.Backend == SearchBackend.DuckDuckGo && outcome.BackendName == "DuckDuckGo");
            Check("一共发了 2 个请求", fallback.Requests.Count == 2);
            Check("第 2 个请求打在 html.duckduckgo.com/html/",
                fallback.Requests[1].Host == "html.duckduckgo.com" && fallback.Requests[1].AbsolutePath == "/html/");
            Check("第 2 个请求是备后端的（带上了 DDG 的 Referer）",
                fallback.Requests[1].Referer == SearchEngine.DuckDuckGoReferer);
            Check("两次尝试都记下来了，第 1 次写的是 Bing 解析出 0 条",
                outcome.Attempts.Count == 2 && !outcome.Attempts[0].Succeeded &&
                outcome.Attempts[0].Backend == SearchBackend.Bing &&
                (outcome.Attempts[0].Error?.Contains("0 results", StringComparison.Ordinal) ?? false));
            Check("失败原因里带了 HTTP 码",
                outcome.Attempts[0].HttpStatus == 200 &&
                (outcome.Attempts[0].Error?.Contains("HTTP 200", StringComparison.Ordinal) ?? false));
        }

        // 3) 两边都回 500：错误文本必须分别点名
        var broken = FixtureHandler.Always(_ => FakeResponse.Empty(500));
        using (var engine = new SearchEngine(broken))
        {
            var outcome = await engine.SearchAsync("dotnet 10", 5).ConfigureAwait(false);
            var text = outcome.DescribeFailure();
            Check("两边都挂时 Success == false", !outcome.Success);
            Check("错误文本同时点名 Bing 与 DuckDuckGo",
                text.Contains("Bing (www.bing.com)", StringComparison.Ordinal) &&
                text.Contains("DuckDuckGo (html.duckduckgo.com)", StringComparison.Ordinal));
            Check("错误文本写了 HTTP 500", text.Contains("500", StringComparison.Ordinal));
            Check("两边各自写了「解析出 0 条」（各一处，不是糊成一句）",
                text.Split("0 results").Length - 1 == 2);
            Check("两边都试过了（Attempts 是 2 条，Bing 在前）",
                outcome.Attempts.Count == 2 &&
                outcome.Attempts[0].Backend == SearchBackend.Bing &&
                outcome.Attempts[1].Backend == SearchBackend.DuckDuckGo);
        }

        // 4) 主后端回 202 空壳 + 备后端直接抛异常（两类不同的失败原因要同时出现）
        var mixed = new FixtureHandler(uri => uri.Host.Contains("bing", StringComparison.OrdinalIgnoreCase)
            ? FakeResponse.Html(BingEmptyShell, 202)
            : throw new HttpRequestException("simulated DNS failure"));
        using (var engine = new SearchEngine(mixed))
        {
            var outcome = await engine.SearchAsync("dotnet 10", 5).ConfigureAwait(false);
            var text = outcome.DescribeFailure();
            Check("Bing 回 202 算失败（HTTP 码如实记下来）",
                !outcome.Success && outcome.Attempts[0].HttpStatus == 202 &&
                outcome.Attempts[0].Backend == SearchBackend.Bing);
            Check("202 那条提示了限流 / 反爬",
                outcome.Attempts[0].Error?.Contains("throttling", StringComparison.Ordinal) ?? false);
            Check("DuckDuckGo 抛异常那条记的是异常类型与消息",
                outcome.Attempts[1].Error?.Contains("HttpRequestException", StringComparison.Ordinal) == true &&
                outcome.Attempts[1].Error?.Contains("simulated DNS failure", StringComparison.Ordinal) == true);
            Check("错误文本同时含 202 与异常原因",
                text.Contains("202", StringComparison.Ordinal) &&
                text.Contains("HttpRequestException", StringComparison.Ordinal));
        }

        // 5) 备后端也挂：Bing 空壳 + DuckDuckGo 回真的 202 反爬页（ddg-shell.html 就是那次抓下来的切片）
        var bothDown = new FixtureHandler(uri => uri.Host.Contains("bing", StringComparison.OrdinalIgnoreCase)
            ? FakeResponse.Html(BingEmptyShell)
            : FakeResponse.Html(shell, 202));
        using (var engine = new SearchEngine(bothDown))
        {
            var outcome = await engine.SearchAsync("dotnet 10", 5).ConfigureAwait(false);
            var text = outcome.DescribeFailure();
            Check("备后端也挂时 Success == false", !outcome.Success);
            Check("真 202 反爬页解析出 0 条（fixture 里一个结果块都没有）",
                outcome.Attempts[1].HttpStatus == 202 && outcome.Attempts[1].ResultCount == 0 &&
                SearchHtmlParser.ParseDuckDuckGo(shell).Count == 0);
            Check("错误文本把两边各自的 HTTP 码分开写清（含各自走的是哪条路）",
                text.Contains("Bing (www.bing.com) [system proxy]: HTTP 200", StringComparison.Ordinal) &&
                text.Contains("DuckDuckGo (html.duckduckgo.com) [system proxy]: HTTP 202", StringComparison.Ordinal));
        }

        // 6) 空查询词：一次网络都不发
        var idle = FixtureHandler.Always(_ => FakeResponse.Html(bing));
        using (var engine = new SearchEngine(idle))
        {
            var outcome = await engine.SearchAsync("   ", 5).ConfigureAwait(false);
            Check("空查询词 → 失败且一个请求都不发", !outcome.Success && idle.Requests.Count == 0);
            Check("空查询词的失败文本说了没试过", outcome.DescribeFailure().Contains("not attempted", StringComparison.Ordinal));
        }
    }

    // ==================== 四、limit 与去重 ====================

    private static async Task LimitSectionAsync()
    {
        Section("四、limit 与去重");

        Check("DefaultLimit == 5", SearchEngine.DefaultLimit == 5);
        Check("MaxLimit == 10", SearchEngine.MaxLimit == 10);
        Check("ClampLimit(3) == 3", SearchEngine.ClampLimit(3) == 3);
        Check("ClampLimit(0) 退回默认 5", SearchEngine.ClampLimit(0) == SearchEngine.DefaultLimit);
        Check("ClampLimit(-7) 退回默认 5", SearchEngine.ClampLimit(-7) == SearchEngine.DefaultLimit);
        Check("ClampLimit(99) 夹到上限 10", SearchEngine.ClampLimit(99) == SearchEngine.MaxLimit);

        var bing = Fixtures.Read("bing.html");
        using (var engine = new SearchEngine(FixtureHandler.Always(_ => FakeResponse.Html(bing))))
        {
            var all = await engine.SearchAsync("dotnet 10", 10).ConfigureAwait(false);
            var two = await engine.SearchAsync("dotnet 10", 2).ConfigureAwait(false);
            var zero = await engine.SearchAsync("dotnet 10", 0).ConfigureAwait(false);

            Check("fixture 5 条 + limit=10 → 全给（5 条）", all.Results.Count == 5 && all.TotalFound == 5);
            Check("没被截断时 WasTruncated 为 false", !all.WasTruncated);
            Check("limit=2 → 只给 2 条", two.Results.Count == 2 && two.TotalFound == 5);
            Check("被截断时 WasTruncated 为 true", two.WasTruncated);
            Check("截断取的是页面顺序的前 2 条",
                two.Results[0].Url == all.Results[0].Url && two.Results[1].Url == all.Results[1].Url);
            Check("limit=0 退回默认 5（5 条全给）", zero.Results.Count == 5);
            Check("生效的 Limit 记在结果里", two.Limit == 2 && zero.Limit == SearchEngine.DefaultLimit);
        }

        var duplicated = new List<WebSearchResult>
        {
            new("A", "https://example.com/a", "s1"),
            new("A 副本", "https://EXAMPLE.com/a", "s2"),
            new("B", "https://example.com/b", string.Empty),
            new("空 URL", string.Empty, "s"),
            new(string.Empty, "https://example.com/c", "s"),
        };
        var deduped = SearchHtmlParser.Deduplicate(duplicated);
        Check("同 URL（大小写不同也算）只留先出现的那条",
            deduped.Count == 2 && deduped[0].Title == "A" && deduped[1].Title == "B");
        Check("空标题 / 空 URL 的条目被丢掉", deduped.All(r => r.Title.Length > 0 && r.Url.Length > 0));
        Check("摘要允许为空（B 那条）", deduped[1].Snippet.Length == 0);
        Check("Deduplicate(null) 给空列表不抛", SearchHtmlParser.Deduplicate(null).Count == 0);
    }

    // ==================== 五、坏输入 / 边界 ====================

    private static void RobustnessSection()
    {
        Section("五、坏输入 / 边界（一律不抛异常）");

        Check("ParseDuckDuckGo(null) → 0 条", SearchHtmlParser.ParseDuckDuckGo(null).Count == 0);
        Check("ParseDuckDuckGo(\"\") → 0 条", SearchHtmlParser.ParseDuckDuckGo(string.Empty).Count == 0);
        Check("ParseBing(null) → 0 条", SearchHtmlParser.ParseBing(null).Count == 0);
        Check("ParseBing(\"   \") → 0 条", SearchHtmlParser.ParseBing("   ").Count == 0);
        Check("Parse(html, backend) 分派正确",
            SearchHtmlParser.Parse(Fixtures.Read("bing.html"), SearchBackend.Bing).Count >= 3 &&
            SearchHtmlParser.Parse(Fixtures.Read("bing.html"), SearchBackend.DuckDuckGo).Count == 0);

        var halfAnchor =
            "<div class=\"result results_links\"><h2 class=\"result__title\">" +
            "<a class=\"result__a\" href=\"//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fp\">半截标题";
        Check("半截标签（没有 </a> 收尾）→ 0 条不抛", SearchHtmlParser.ParseDuckDuckGo(halfAnchor).Count == 0);

        Check("只有开标签、没有 href → 0 条",
            SearchHtmlParser.ParseDuckDuckGo("<a class=\"result__a\">标题</a>").Count == 0);
        Check("href 是 javascript: → 0 条（丢掉而不是塞给模型）",
            SearchHtmlParser.ParseDuckDuckGo("<a class=\"result__a\" href=\"javascript:void(0)\">标题</a>").Count == 0);
        Check("href 是相对路径 → 0 条",
            SearchHtmlParser.ParseDuckDuckGo("<a class=\"result__a\" href=\"/local/page\">标题</a>").Count == 0);
        Check("标题是空白的 → 0 条",
            SearchHtmlParser.ParseDuckDuckGo("<a class=\"result__a\" href=\"https://ok.example/x\">   </a>").Count == 0);

        var minimal = SearchHtmlParser.ParseDuckDuckGo(
            "<a class=\"result__a\" href=\"https://ok.example/x\">T</a><a class=\"result__snippet\">S</a>");
        Check("最小可解析 markup → 1 条（标题 T / 摘要 S）",
            minimal.Count == 1 && minimal[0].Title == "T" && minimal[0].Snippet == "S" &&
            minimal[0].Url == "https://ok.example/x");

        var nested = SearchHtmlParser.ParseBing(
            "<ol id=\"b_results\">" +
            "<li class=\"b_algo\"><h2><a href=\"https://a.example/1\">A</a></h2><ul><li>子项</li></ul><p>pa</p></li>" +
            "<li class=\"b_algo\"><h2><a href=\"https://b.example/2\">B</a></h2><p>pb</p></li>" +
            "</ol>");
        Check("b_algo 里嵌了 <li> 也不会把下一块切坏（2 条、摘要各归各）",
            nested.Count == 2 && nested[0].Title == "A" && nested[0].Snippet == "pa" &&
            nested[1].Title == "B" && nested[1].Snippet == "pb");

        Check("Bing 块里没有 <h2> → 0 条",
            SearchHtmlParser.ParseBing("<li class=\"b_algo\"><p>只有摘要</p></li>").Count == 0);

        // 跳转壳还原
        var bingRedirect =
            "https://www.bing.com/ck/a?!&amp;&amp;p=deadbeef&amp;ptn=3&amp;hsh=4&amp;u=a1aHR0cHM6Ly9leGFtcGxlLmNvbS9wYWdl&amp;ntb=1";
        Check("Bing 的 /ck/a?u=a1<base64url> 跳转壳被还原",
            SearchHtmlParser.ResolveResultUrl(bingRedirect, SearchBackend.Bing) == "https://example.com/page");
        Check("协议相对地址 //host/path 补上 https",
            SearchHtmlParser.ResolveResultUrl("//example.com/x", SearchBackend.DuckDuckGo) == "https://example.com/x");
        Check("本来就是绝对地址的原样返回",
            SearchHtmlParser.ResolveResultUrl("https://example.com/y", SearchBackend.Bing) == "https://example.com/y");
        Check("坏输入（空 / 百分号垃圾 / javascript:）一律 null",
            SearchHtmlParser.ResolveResultUrl(null, SearchBackend.DuckDuckGo) is null &&
            SearchHtmlParser.ResolveResultUrl("   ", SearchBackend.DuckDuckGo) is null &&
            SearchHtmlParser.ResolveResultUrl("%%%", SearchBackend.DuckDuckGo) is null &&
            SearchHtmlParser.ResolveResultUrl("javascript:alert(1)", SearchBackend.Bing) is null);

        // 文本清洗
        Check("NormalizeText(null) == \"\"", SearchHtmlParser.NormalizeText(null).Length == 0);
        Check("NormalizeText 收敛空白", SearchHtmlParser.NormalizeText("  a\r\n\t b ") == "a b");
        Check("NormalizeText 解实体并剥标签", SearchHtmlParser.NormalizeText("<b>x</b>&amp;y") == "x&y");
        Check("NormalizeText 先剥标签再解实体（&lt;b&gt; 保留成文字）",
            SearchHtmlParser.NormalizeText("&lt;b&gt;") == "<b>");
    }

    // ==================== 六、契约与 HTTP 细节 ====================

    private static async Task ContractSectionAsync()
    {
        Section("六、工具契约 + HTTP 细节");

        var ddg = Fixtures.Read("ddg.html");
        var bing = Fixtures.Read("bing.html");

        using var engine = new SearchEngine(FixtureHandler.Split(ddg, bing));
        var tool = new WebSearchTool(engine);

        Check("Name == web_search", tool.Name == "web_search");
        Check("Risk == Safe（只读联网，不弹确认框）", tool.Risk == ToolRisk.Safe);
        Check("Description 写了查询词会发给 DuckDuckGo / Bing",
            tool.Description.Contains("DuckDuckGo", StringComparison.Ordinal) &&
            tool.Description.Contains("Bing", StringComparison.Ordinal) &&
            tool.Description.Contains("leaves this machine", StringComparison.Ordinal));
        Check("Description 是纯 ASCII 英文", tool.Description.All(c => c < 128));
        Check("Description 够写清楚（> 200 字符）", tool.Description.Length > 200);

        using (var schema = JsonDocument.Parse(tool.ParametersJsonSchema))
        {
            var root = schema.RootElement;
            Check("schema 是 JSON 对象", root.ValueKind == JsonValueKind.Object);
            Check("schema.required 含 query",
                root.TryGetProperty("required", out var required) &&
                required.EnumerateArray().Any(e => e.GetString() == "query"));

            var hasProperties = root.TryGetProperty("properties", out var props);
            Check("schema 声明了 query 与 limit",
                hasProperties && props.TryGetProperty("query", out _) && props.TryGetProperty("limit", out _));
            Check("limit 声明成 integer",
                hasProperties && props.TryGetProperty("limit", out var limit) &&
                limit.GetProperty("type").GetString() == "integer");
        }

        // 真实请求长什么样（假 handler 把请求截下来了）。
        // 让主后端回空壳，这样两个后端各发一个请求，能同时看到"带 Referer 的"和"不带 Referer 的"。
        var handler = new FixtureHandler(uri => uri.Host.Contains("bing", StringComparison.OrdinalIgnoreCase)
            ? FakeResponse.Html(BingEmptyShell)
            : FakeResponse.Html(ddg));
        using (var probe = new SearchEngine(handler))
        {
            await probe.SearchAsync("c# async & await", 3).ConfigureAwait(false);
        }

        var request = handler.Requests[0];
        Info($"实际发出去的 UA: {request.UserAgent}");
        Check("第 1 个请求打在主后端 Bing 上", request.Host == "www.bing.com");
        Check("UA 与常量一字不差（真发到线上就是这一行）", request.UserAgent == SearchEngine.BrowserUserAgent);
        Check("UA 是浏览器样子的（Mozilla/5.0 + Chrome/126.0）",
            request.UserAgent.Contains("Mozilla/5.0", StringComparison.Ordinal) &&
            request.UserAgent.Contains("Chrome/126.0 Safari/537.36", StringComparison.Ordinal));
        Check("带 Accept-Language: en-US", request.AcceptLanguage.StartsWith("en-US", StringComparison.Ordinal));
        Check("带 Accept: text/html", request.Accept.Contains("text/html", StringComparison.Ordinal));
        Check("Bing 请求不用 Referer", request.Referer is null);
        Check("DDG 请求带 Referer",
            handler.Requests.Count == 2 && handler.Requests[1].Referer == SearchEngine.DuckDuckGoReferer);
        Check("查询词被正确编码（# 与 & 没漏进别的参数）",
            request.RawQuery.Contains("%23", StringComparison.Ordinal) &&
            request.RawQuery.Contains("%26", StringComparison.Ordinal) &&
            request.QueryValue("q") == "c# async & await");
        Check("两个后端拿到的查询词一模一样",
            handler.Requests.Count == 2 && handler.Requests[1].QueryValue("q") == "c# async & await");

        using var defaultHandler = SearchEngine.CreateDefaultHandler();
        var flags = defaultHandler.AutomaticDecompression;
        Check("默认 handler 自动解压 GZip | Deflate | Brotli",
            flags.HasFlag(DecompressionMethods.GZip) &&
            flags.HasFlag(DecompressionMethods.Deflate) &&
            flags.HasFlag(DecompressionMethods.Brotli));
        Check("跟随 302 跳转", defaultHandler.AllowAutoRedirect);
        Check("超时是 15 秒", SearchEngine.RequestTimeout == TimeSpan.FromSeconds(15));
        Check("后端顺序是 Bing 在前、DDG 在后（yancy 2026-09-21 拍板）",
            SearchEngine.Backends.Count == 2 &&
            SearchEngine.Backends[0] == SearchBackend.Bing &&
            SearchEngine.Backends[1] == SearchBackend.DuckDuckGo);
        Check("后端 URL 拼装对得上实测的那两条",
            SearchBackend.DuckDuckGo.BuildUrl("a b") == "https://html.duckduckgo.com/html/?q=a%20b" &&
            SearchBackend.Bing.BuildUrl("a b").StartsWith("https://www.bing.com/search?q=a%20b&setlang=en", StringComparison.Ordinal));

        using var realEngine = new SearchEngine();
        Check("默认构造的引擎超时也是 15 秒", realEngine.Client.Timeout == SearchEngine.RequestTimeout);
        Check("UA 是逐请求加的（HttpClient 默认头里没有 UA，注入 client 时也照样带）",
            realEngine.Client.DefaultRequestHeaders.UserAgent.Count == 0);
    }

    // ==================== 七、工具层 ====================

    private static async Task ToolSectionAsync()
    {
        Section("七、工具调用层（InvokeAsync）");

        var ddg = Fixtures.Read("ddg.html");
        var bing = Fixtures.Read("bing.html");
        var expected = SearchHtmlParser.ParseBing(bing);

        using var engine = new SearchEngine(FixtureHandler.Split(ddg, bing));
        var tool = new WebSearchTool(engine);

        var missing = await tool.InvokeAsync(Args("""{"limit":3}"""), CancellationToken.None).ConfigureAwait(false);
        Check("缺 query → 失败结果", !missing.Success);
        Check("缺 query 的提示里写了 required", missing.Content.Contains("required", StringComparison.Ordinal));

        var wrongType = await tool.InvokeAsync(Args("""{"query":123}"""), CancellationToken.None).ConfigureAwait(false);
        Check("query 类型不对（数字）→ 失败而不是崩", !wrongType.Success);

        var notObject = await tool.InvokeAsync(Args("""[1,2,3]"""), CancellationToken.None).ConfigureAwait(false);
        Check("args 不是对象 → 失败而不是崩", !notObject.Success);

        var ok = await tool.InvokeAsync(
            Args("""{"query":"dotnet 10 release notes","limit":3}"""), CancellationToken.None).ConfigureAwait(false);
        Check("正常调用成功", ok.Success);

        var lines = ok.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Info($"文本第 1 行: {lines[0]}");
        Info($"文本第 2 行: {lines[1]}");
        Check("第 1 行写了条数 + 查询词 + 用了哪个后端（主后端 Bing）",
            lines[0] == "Found 5 result(s) for \"dotnet 10 release notes\" via Bing (showing the top 3).");
        Check("第 2 行是「1. 标题 — url」格式",
            lines[1] == $"1. {expected[0].Title} — {expected[0].Url}");
        Check("第 3 行是缩进 3 格的摘要", lines[2] == $"   {expected[0].Snippet}");
        Check("limit=3 时正好 3 条（1 行表头 + 3 组两行）", lines.Length == 7);
        Check("编号连续（1. 2. 3.）",
            lines[3].StartsWith("2. ", StringComparison.Ordinal) &&
            lines[5].StartsWith("3. ", StringComparison.Ordinal));

        Check("Data 是 JSON 数组", ok.Data is { ValueKind: JsonValueKind.Array });
        var data = ok.Data!.Value;
        Check("Data 有 3 个元素", data.GetArrayLength() == 3);
        Check("Data 的元素是 {title,url,snippet}",
            data[0].TryGetProperty("title", out var t) && data[0].TryGetProperty("url", out var u) &&
            data[0].TryGetProperty("snippet", out var s) &&
            t.GetString() == expected[0].Title && u.GetString() == expected[0].Url &&
            s.GetString() == expected[0].Snippet);
        Check("DataJson 是合法 JSON",
            !string.IsNullOrEmpty(ok.DataJson) && JsonDocument.Parse(ok.DataJson!).RootElement.ValueKind == JsonValueKind.Array);

        var one = await tool.InvokeAsync(
            Args("""{"query":"dotnet 10","limit":"1"}"""), CancellationToken.None).ConfigureAwait(false);
        Check("limit 写成字符串 \"1\" 也认（宽松取参）",
            one.Success && one.Data!.Value.GetArrayLength() == 1 &&
            one.Content.Contains("(showing the top 1)", StringComparison.Ordinal));

        var huge = await tool.InvokeAsync(
            Args("""{"query":"dotnet 10","limit":99}"""), CancellationToken.None).ConfigureAwait(false);
        Check("limit=99 被夹到上限（fixture 只有 5 条，就返回 5 条）",
            huge.Success && huge.Data!.Value.GetArrayLength() == 5);

        // 工具层也要看得见"主后端空壳 → 备后端接手"
        using var fallbackEngine = new SearchEngine(new FixtureHandler(
            uri => uri.Host.Contains("bing", StringComparison.OrdinalIgnoreCase)
                ? FakeResponse.Html(BingEmptyShell)
                : FakeResponse.Html(ddg)));
        var fallbackTool = new WebSearchTool(fallbackEngine);
        var viaFallback = await fallbackTool
            .InvokeAsync(Args("""{"query":"dotnet 10","limit":2}"""), CancellationToken.None).ConfigureAwait(false);
        Check("主后端空壳时工具文本写的是 via DuckDuckGo（备后端接手）",
            viaFallback.Success && viaFallback.Content.Contains("via DuckDuckGo", StringComparison.Ordinal));
        Check("备后端的结果确实来自 DDG fixture",
            viaFallback.Data is { ValueKind: JsonValueKind.Array } &&
            viaFallback.Data!.Value[0].GetProperty("url").GetString() ==
            SearchHtmlParser.ParseDuckDuckGo(ddg)[0].Url);

        using var brokenEngine = new SearchEngine(FixtureHandler.Always(_ => FakeResponse.Empty(503)));
        var brokenTool = new WebSearchTool(brokenEngine);
        var failed = await brokenTool.InvokeAsync(Args("""{"query":"dotnet 10"}"""), CancellationToken.None).ConfigureAwait(false);
        Check("两边都挂 → 工具返回失败结果（不是抛异常）", !failed.Success);
        Check("失败文本里两边原因 + HTTP 码都在",
            failed.Content.Contains("DuckDuckGo (html.duckduckgo.com)", StringComparison.Ordinal) &&
            failed.Content.Contains("Bing (www.bing.com)", StringComparison.Ordinal) &&
            failed.Content.Contains("503", StringComparison.Ordinal));
        Check("失败结果没有 Data", failed.Data is null);
        Check("ToModelText 会给失败文本加 ERROR: 前缀", failed.ToModelText().StartsWith("ERROR: ", StringComparison.Ordinal));

        using var cancelSource = new CancellationTokenSource();
        cancelSource.Cancel();
        var cancelled = false;
        try
        {
            await tool.InvokeAsync(Args("""{"query":"dotnet 10"}"""), cancelSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Check("已取消的令牌会原样上抛 OperationCanceledException（不伪装成失败）", cancelled);
    }

    // ==================== 八、门面与注册表 ====================

    private static async Task RegistrationSectionAsync()
    {
        Section("八、门面 WebTools 与注册表接线");

        var tools = WebTools.CreateAll();
        Check("CreateAll 返回 1 个工具", tools.Count == 1);
        Check("工具名是 web_search", tools.Count == 1 && tools[0].Name == "web_search");
        Check("它是 Safe 级", tools.Count == 1 && tools[0].Risk == ToolRisk.Safe);

        var registry = new ToolRegistry();
        Check("RegisterAll 返回 1", WebTools.RegisterAll(registry) == 1);
        Check("注册表里能查到 web_search",
            registry.TryGet("web_search", out var got) && got is WebSearchTool);
        Check("重名 + skipExisting:true → 返回 0 且不抛", WebTools.RegisterAll(registry, skipExisting: true) == 0);
        Check("重名 + skipExisting:false → 抛 ArgumentException",
            Throws<ArgumentException>(() => WebTools.RegisterAll(registry)));
        Check("抛完之后注册表还是 1 个", registry.Count == 1);
        Check("RegisterAll(null) 抛 ArgumentNullException", Throws<ArgumentNullException>(() => WebTools.RegisterAll(null!)));

        var bing = Fixtures.Read("bing.html");
        using var engine = new SearchEngine(FixtureHandler.Always(_ => FakeResponse.Html(bing)));
        var registry2 = new ToolRegistry();
        registry2.Register(new WebSearchTool(engine));

        var viaRegistry = await registry2
            .InvokeAsync("web_search", Args("""{"query":"dotnet 10 release notes","limit":2}"""))
            .ConfigureAwait(false);
        Check("经注册表端到端调用成功", viaRegistry.Success);
        Check("结果里写了来源后端（主后端 Bing）", viaRegistry.Content.Contains("via Bing", StringComparison.Ordinal));

        using var exported = JsonDocument.Parse(registry2.BuildToolsJson());
        var function = exported.RootElement[0].GetProperty("function");
        Check("导出的 tools JSON 里函数名是 web_search", function.GetProperty("name").GetString() == "web_search");
        Check("导出的参数 schema 里 required 含 query",
            function.GetProperty("parameters").GetProperty("required")
                .EnumerateArray().Any(e => e.GetString() == "query"));

        var viaUnknown = await registry2.InvokeAsync("nope", Args("""{}""")).ConfigureAwait(false);
        Check("调不存在的工具 → 注册表吞成失败结果（护栏没被破坏）", !viaUnknown.Success);
    }

    // ==================== 九、系统代理失败 → 直连兜底 ====================
    // 实测背景（2026-09-21）：这台机器的系统代理 127.0.0.1:7890 在听，但所有 HTTPS 都握手失败；
    // .NET 默认走系统代理，于是每个请求都报 "The SSL connection could not be established"。
    // 所以：传输层失败 → 同一个后端换直连重试一次；HTTP 状态码（202/500）不重试；
    // 两条路都要如实记账。这里用双 handler 把两条路分开控制。

    private static async Task ProxyFallbackSectionAsync()
    {
        Section("九、系统代理失败 → 直连兜底（两条路分别可控）");

        var ddg = Fixtures.Read("ddg.html");
        var bing = Fixtures.Read("bing.html");
        var shell = Fixtures.Read("ddg-shell.html");

        Check("路由顺序是 [系统代理, 直连]",
            SearchEngine.Routes.Count == 2 &&
            SearchEngine.Routes[0] == SearchRoute.SystemProxy &&
            SearchEngine.Routes[1] == SearchRoute.Direct);
        Check("默认 handler 跟随系统代理", SearchEngine.CreateDefaultHandler().UseProxy);
        Check("直连 handler 关掉了代理", !SearchEngine.CreateDirectHandler().UseProxy);

        // 1) 代理那条传输层失败（SSL 握手失败就是这种）→ 直连拿到结果 → 成功，两条记账都在
        var proxySsl = new FixtureHandler(_ =>
            throw new HttpRequestException("The SSL connection could not be established, see inner exception."));
        var directOk = new FixtureHandler(_ => FakeResponse.Html(bing));
        using (var engine = new SearchEngine(proxySsl, directOk))
        {
            var outcome = await engine.SearchAsync("dotnet 10 release notes", 5).ConfigureAwait(false);
            var attempt = outcome.Attempts[0];

            Check("代理那条挂 → 直连重试后成功", outcome.Success && outcome.Backend == SearchBackend.Bing);
            Check("成功结果标了是直连拿到的", outcome.Direct && outcome.Source == "Bing (direct)");
            Check("只问了主后端（Bing 直连成功就不用 DDG）", outcome.Attempts.Count == 1);
            Check("两条路各发 1 个请求", proxySsl.Requests.Count == 1 && directOk.Requests.Count == 1);
            Check("两条记账都在（[system proxy] 与 [direct]）",
                attempt.Routes.Count == 2 &&
                attempt.Routes[0].Route == SearchRoute.SystemProxy &&
                attempt.Routes[1].Route == SearchRoute.Direct);
            Check("代理那条如实记成传输层失败 + 异常原因",
                attempt.Routes[0].IsTransportFailure && !attempt.Routes[0].Succeeded &&
                (attempt.Routes[0].Error?.Contains("SSL connection", StringComparison.Ordinal) ?? false));
            Check("直连那条记成 HTTP 200 / 5 条（fixture 里就 5 个结果块）",
                attempt.Routes[1].Succeeded && attempt.Routes[1].HttpStatus == 200 &&
                attempt.Routes[1].ResultCount == 5);
            Check("记账文本两条都在",
                attempt.Describe().Contains("[system proxy]: HttpRequestException", StringComparison.Ordinal) &&
                attempt.Describe().Contains("[direct]: HTTP 200, 5 result(s)", StringComparison.Ordinal));
            Check("记账文本里带上了后端与主机名",
                attempt.Describe().StartsWith("Bing (www.bing.com) ", StringComparison.Ordinal));

            // 工具层也要看得见"直连"
            using var directEngine = new SearchEngine(
                new FixtureHandler(_ => throw new HttpRequestException("SSL handshake failed")),
                new FixtureHandler(_ => FakeResponse.Html(bing)));
            var viaDirect = await new WebSearchTool(directEngine)
                .InvokeAsync(Args("""{"query":"dotnet 10","limit":2}"""), CancellationToken.None).ConfigureAwait(false);
            Check("工具文本写的是 via Bing (direct)",
                viaDirect.Success && viaDirect.Content.Contains("via Bing (direct)", StringComparison.Ordinal));
        }

        // 2) 代理那条回了 HTTP 202（反爬）= 后端的结论 → 不许触发直连重试
        var proxy202 = new FixtureHandler(_ => FakeResponse.Html(shell, 202));
        var directMustNotRun = new FixtureHandler(_ =>
            throw new InvalidOperationException("HTTP 层失败不该触发直连重试，直连这条路不该被碰到"));
        using (var engine = new SearchEngine(proxy202, directMustNotRun))
        {
            var outcome = await engine.SearchAsync("dotnet 10", 5).ConfigureAwait(false);

            Check("HTTP 202 不触发直连重试（直连 handler 一次都没被调用）",
                directMustNotRun.Requests.Count == 0);
            Check("每个后端只走了一条路（记账里没有 [direct]）",
                outcome.Attempts.All(a => a.Routes.Count == 1) &&
                !outcome.Attempts[0].Describe().Contains("[direct]", StringComparison.Ordinal));
            Check("202 记成 HTTP 层失败（不是传输层失败）",
                outcome.Attempts[0].HttpStatus == 202 &&
                !outcome.Attempts[0].Routes[0].IsTransportFailure &&
                (outcome.Attempts[0].Routes[0].Error?.Contains("throttling", StringComparison.Ordinal) ?? false));
            Check("HTTP 500 同理不重试（换一个状态码再验一次）",
                await NoRetryOnHttpFailureAsync().ConfigureAwait(false));
            Check("202 之后照常退到备后端（两个后端各 1 条记账）",
                !outcome.Success && outcome.Attempts.Count == 2 &&
                outcome.Attempts[1].Backend == SearchBackend.DuckDuckGo);
        }

        // 3) 两条路都失败 → 最终错误文本里两条原因都在
        var proxyDown = new FixtureHandler(_ =>
            throw new HttpRequestException("The SSL connection could not be established, see inner exception."));
        var directBlocked = new FixtureHandler(_ => FakeResponse.Empty(503));
        using (var engine = new SearchEngine(proxyDown, directBlocked))
        {
            var outcome = await engine.SearchAsync("dotnet 10", 5).ConfigureAwait(false);
            var text = outcome.DescribeFailure();

            Check("两条路都失败 → 最终失败", !outcome.Success);
            Check("每个后端都试了两条路（Bing / DDG 各 2 条记账）",
                outcome.Attempts.Count == 2 && outcome.Attempts.All(a => a.Routes.Count == 2));
            Check("错误文本里代理那条的异常原因在",
                text.Contains("[system proxy]: HttpRequestException: The SSL connection could not be established",
                    StringComparison.Ordinal));
            Check("错误文本里直连那条的 HTTP 码也在",
                text.Contains("[direct]: HTTP 503", StringComparison.Ordinal));
            Check("两条路都失败时成功标志/来源说明都是空的",
                !outcome.Direct && outcome.Source.Length == 0);
        }

        // 4) 非用户取消的超时（TaskCanceledException）算传输层失败 → 也走直连重试
        var proxyTimeout = new FixtureHandler(_ =>
            throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing."));
        var directAfterTimeout = new FixtureHandler(_ => FakeResponse.Html(ddg));
        using (var engine = new SearchEngine(proxyTimeout, directAfterTimeout))
        {
            var outcome = await engine.SearchAsync("dotnet 10", 5).ConfigureAwait(false);
            Check("代理那条超时 → 直连重试后成功（备后端接手也算数）",
                outcome.Success && outcome.Backend == SearchBackend.DuckDuckGo && outcome.Direct);
            Check("超时记成传输层失败", outcome.Attempts[0].Routes[0].IsTransportFailure &&
                (outcome.Attempts[0].Routes[0].Error?.Contains("timed out", StringComparison.Ordinal) ?? false));
            Check("超时的记账里 [system proxy] 与 [direct] 都在（Bing 两条、DDG 两条）",
                outcome.Attempts.All(a => a.Routes.Count == 2));
        }

        // 5) 用户取消不算传输层失败：原样上抛，绝不换条路再试
        var proxyCancelled = new FixtureHandler(_ => FakeResponse.Html(bing));
        var directAfterCancel = new FixtureHandler(_ => throw new InvalidOperationException("用户取消后不该再发请求"));
        using (var engine = new SearchEngine(proxyCancelled, directAfterCancel))
        {
            using var cancelSource = new CancellationTokenSource();
            cancelSource.Cancel();
            var threw = false;
            try
            {
                await engine.SearchAsync("dotnet 10", 5, cancelSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                threw = true;
            }

            Check("用户取消 → 原样抛 OperationCanceledException", threw);
            Check("取消之后一条请求都没发（更没换路由重试）",
                proxyCancelled.Requests.Count == 0 && directAfterCancel.Requests.Count == 0);
        }
    }

    /// <summary>HTTP 层失败（500）同样不该触发直连重试 —— 单独一个小用例。</summary>
    private static async Task<bool> NoRetryOnHttpFailureAsync()
    {
        var proxy500 = new FixtureHandler(_ => FakeResponse.Empty(500));
        var directNever = new FixtureHandler(_ => throw new InvalidOperationException("不该被调用"));

        using var engine = new SearchEngine(proxy500, directNever);
        var outcome = await engine.SearchAsync("dotnet 10", 5).ConfigureAwait(false);

        return directNever.Requests.Count == 0 &&
               outcome.Attempts.All(a => a.Routes.Count == 1) &&
               outcome.Attempts.All(a => a.HttpStatus == 500);
    }

    // ==================== 护栏 ====================

    private static void GuardSection()
    {
        Section("护栏");

        Check("三份 fixture 都在（缺一份就说明自测根本没验到真 markup）",
            Fixtures.Exists("ddg.html") && Fixtures.Exists("bing.html") && Fixtures.Exists("ddg-shell.html"));
        Check("fixture 都是从真页面裁的（> 1 KB，不是手写的小假 HTML）",
            Fixtures.Read("ddg.html").Length > 1000 && Fixtures.Read("bing.html").Length > 1000);
        Check("离线这趟所有 HTTP 都落在假 handler 上（没碰真实网络）", FixtureHandler.TotalIntercepted > 10);
        Info($"本趟一共拦下 {FixtureHandler.TotalIntercepted} 个请求，全部由 fixture 回答");
        Console.WriteLine();
    }

    // ==================== 断言、小工具与汇总 ====================

    private static void Check(string name, bool condition)
    {
        if (condition) { _pass++; } else { _fail++; }
        Console.WriteLine($"    [{(condition ? "PASS" : "FAIL")}] {name}");
    }

    private static void Fail(string name)
    {
        _fail++;
        Console.WriteLine($"    [FAIL] {name}");
    }

    private static void Info(string message) => Console.WriteLine($"    (i) {message}");

    private static void Section(string title) => Console.WriteLine($"---- {title} ----");

    /// <summary>把一段 JSON 文本变成工具参数（Clone 过的独立 JsonElement）。</summary>
    private static JsonElement Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>断言某段代码抛指定的异常。</summary>
    private static bool Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLiveArgument(string argument) =>
        string.Equals(argument, "live", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(argument, "--live", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(argument, "-live", StringComparison.OrdinalIgnoreCase);

    private static int Report()
    {
        Console.WriteLine("==================================================================");
        Console.WriteLine($"PASS {_pass} / FAIL {_fail}");
        Console.WriteLine("==================================================================");
        return _fail == 0 ? 0 : 1;
    }
}
