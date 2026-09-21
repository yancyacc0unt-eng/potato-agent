// 搜索执行器：一个查询 → Bing 主 → DDG 备 → 两边都不行就带原因失败。
//
// 这个顺序是 yancy 2026-09-21 拍板换的：DDG 的 html 端点实测连撞 HTTP 202 反爬空壳，所以退居备用。
// 想换回来只改 Backends 那一行 —— "谁是主"全链路都从它推导。
//
// 三个必须做对的地方：
//   1. 浏览器 UA + Accept-Language：不带 UA 会被两家直接挡掉（主 agent 实测过）。
//   2. AutomaticDecompression = GZip | Deflate | Brotli：不带压缩头拿到的可能是乱码/半截。
//   3. 可注入 HttpMessageHandler / HttpClient：自测要能在【不联网】的情况下喂 fixture HTML，
//      所以网络这一层必须能从外面塞进来，绝不能在内部 new 死。

using System.Net;

namespace PotatoAgent.Web;

/// <summary>
/// 免密钥网页搜索的执行器：先问 Bing 的网页端点，拿不到结果再问 DuckDuckGo 的 HTML 端点，
/// 两个都不行就如实报两边的失败原因。
/// </summary>
/// <remarks>
/// <para>
/// <b>会联网</b>：每次 <see cref="SearchAsync"/> 都会把查询词作为 URL 参数发给
/// <c>www.bing.com</c>（失败时再发 <c>html.duckduckgo.com</c>）。除了这两个 GET，不发任何别的请求，
/// 也不读、不改本机任何东西。
/// </para>
/// <para>
/// 不做合并：主后端一旦给出 ≥1 条就直接返回（备后端一次都不请求），
/// 所以正常情况下的网络开销就是一个 GET。
/// </para>
/// <para>
/// <b>系统代理兜底</b>：每个后端先走"跟随系统代理"那条路；只有在<b>传输层</b>失败时
/// （异常 / 非用户取消的超时，压根没拿到 HTTP 响应）才用一条<b>直连</b>的路重试一次。
/// HTTP 状态码（DDG 的 202、Bing 的 500）是后端给出的结论，<b>不</b>触发直连重试。
/// 两条路的记账都留在 <see cref="SearchOutcome.Attempts"/> 里；靠直连拿到结果时
/// <see cref="SearchOutcome.Source"/> 会写成 <c>Bing (direct)</c>，不糊弄。
/// （2026-09-21 实测：这台机器系统代理 127.0.0.1:7890 在听但所有 HTTPS 都握手失败，
/// 而直连正常 —— 没有这条兜底，web_search 在这台机器上必挂。）
/// </para>
/// <para>
/// 自测怎么用：<c>new SearchEngine(fakeHandler)</c>（两条路都走这个 handler）、
/// <c>new SearchEngine(proxyHandler, directHandler)</c>（分别控制两条路，验代理兜底用这个）、
/// 或 <c>new SearchEngine(httpClient)</c> —— 三个构造函数都不会碰真实网络，
/// 只有在 <see cref="SearchAsync"/> 被调用时才轮到假 handler。
/// </para>
/// </remarks>
public sealed class SearchEngine : IDisposable
{
    /// <summary>
    /// 浏览器 UA。**必须带**：不带的话 DuckDuckGo / Bing 会把请求当成脚本挡掉（实测）。
    /// </summary>
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    /// <summary>Accept-Language 头，固定英文（免得结果页变成别的语言、class 名也跟着变）。</summary>
    public const string AcceptLanguage = "en-US,en;q=0.9";

    /// <summary>Accept 头，声明我们要 HTML。</summary>
    public const string AcceptHeader =
        "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

    /// <summary>DuckDuckGo 请求会带上的 Referer（实测能降低撞上 202 反爬空壳的概率）。</summary>
    public const string DuckDuckGoReferer = "https://duckduckgo.com/";

    /// <summary>默认返回条数（模型没给 <c>limit</c> 时用）。</summary>
    public const int DefaultLimit = 5;

    /// <summary>返回条数上限，超过就夹到 10（再多也是噪音，还费 token）。</summary>
    public const int MaxLimit = 10;

    /// <summary>响应体的字符上限（超过就截断再看）：Bing 的结果页能到 130 KB，没必要更大。</summary>
    public const int MaxResponseChars = 2_000_000;

    /// <summary>单个后端的超时（15 秒）。两个后端串起来最坏 30 秒，可接受。</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 后端顺序：<b>Bing 主，DuckDuckGo 备</b>。改这个列表就等于改整条后端链
    /// （2026-09-21 yancy 拍板把 Bing 提为主：DDG 那侧实测连着 3 次都是 HTTP 202 反爬空壳）。
    /// </summary>
    public static readonly IReadOnlyList<SearchBackend> Backends =
        new[] { SearchBackend.Bing, SearchBackend.DuckDuckGo };

    /// <summary>
    /// 每个后端按这个顺序试网络路径：先跟随系统代理，传输层失败再直连。
    /// 正常情况下只会用到第一条；第二条是这台机器上"系统代理开着但根本不通"的兜底。
    /// </summary>
    public static readonly IReadOnlyList<SearchRoute> Routes =
        new[] { SearchRoute.SystemProxy, SearchRoute.Direct };

    private readonly HttpClient _proxyClient;
    private readonly HttpClient _directClient;
    private readonly bool _ownsProxyClient;
    private readonly bool _ownsDirectClient;

    /// <summary>
    /// 真联网的构造：系统代理那条跟随系统设置，直连那条 <c>UseProxy = false</c>；两个都带
    /// GZip/Deflate/Brotli 自动解压、15 秒超时。
    /// </summary>
    public SearchEngine()
        : this(CreateProxyClient(), CreateDirectClient(), ownsProxyClient: true, ownsDirectClient: true)
    {
    }

    /// <summary>
    /// 注入<b>一个</b> <see cref="HttpMessageHandler"/> 的构造：<b>自测专用</b>，
    /// 两条路（系统代理 / 直连）都走这个 handler —— 想分别验"代理挂了"和"直连怎么样"时，
    /// 用双 handler 的那个构造。
    /// </summary>
    /// <param name="handler">自定义 handler，不能为 null。</param>
    public SearchEngine(HttpMessageHandler handler)
        : this(CreateClient(handler), CreateClient(handler), ownsProxyClient: true, ownsDirectClient: true)
    {
    }

    /// <summary>
    /// 分别注入"系统代理那条"和"直连那条"的 handler：<b>自测专用</b>，
    /// 用来验"代理传输层失败 → 同一个后端直连重试"。
    /// </summary>
    /// <param name="proxyHandler">系统代理那条用的 handler，不能为 null。</param>
    /// <param name="directHandler">直连那条用的 handler，不能为 null。</param>
    public SearchEngine(HttpMessageHandler proxyHandler, HttpMessageHandler directHandler)
        : this(CreateClient(proxyHandler), CreateClient(directHandler), ownsProxyClient: true, ownsDirectClient: true)
    {
    }

    /// <summary>
    /// 注入现成的 <see cref="HttpClient"/>（想和别的组件共用连接池时用）：它当"系统代理那条"。
    /// <b>不</b>负责释放它，超时也保持调用方设的那个；"直连那条"由本对象自己建、自己释放。
    /// </summary>
    /// <param name="client">现成的 HttpClient，不能为 null。</param>
    public SearchEngine(HttpClient client)
        : this(client, CreateDirectClient(), ownsProxyClient: false, ownsDirectClient: true)
    {
    }

    private SearchEngine(HttpClient proxyClient, HttpClient directClient, bool ownsProxyClient, bool ownsDirectClient)
    {
        _proxyClient = proxyClient ?? throw new ArgumentNullException(nameof(proxyClient));
        _directClient = directClient ?? throw new ArgumentNullException(nameof(directClient));
        _ownsProxyClient = ownsProxyClient;
        _ownsDirectClient = ownsDirectClient;
    }

    /// <summary>系统代理那条用的 <see cref="HttpClient"/>（诊断 / 自测看超时和头用；别改它的配置）。</summary>
    public HttpClient Client => _proxyClient;

    /// <summary>直连那条用的 <see cref="HttpClient"/>（只在系统代理那条传输层失败后才会被用到）。</summary>
    public HttpClient DirectClient => _directClient;

    /// <summary>
    /// 建一个"跟随系统代理"的 handler：自动解压 GZip / Deflate / Brotli，跟随 302 跳转，
    /// <c>UseProxy</c> 保持默认 true（用户要翻墙时就得靠它）。
    /// </summary>
    /// <returns>新的 <see cref="HttpClientHandler"/>。</returns>
    public static HttpClientHandler CreateDefaultHandler() => new()
    {
        AutomaticDecompression =
            DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        AllowAutoRedirect = true,
        UseProxy = true,
    };

    /// <summary>
    /// 建一个"绕开系统代理直连"的 handler：其余设置同 <see cref="CreateDefaultHandler"/>，
    /// 只把 <c>UseProxy</c> 关掉。用于系统代理传输层失败后的重试。
    /// </summary>
    /// <returns>新的 <see cref="HttpClientHandler"/>。</returns>
    public static HttpClientHandler CreateDirectHandler()
    {
        var handler = CreateDefaultHandler();
        handler.UseProxy = false;
        return handler;
    }

    /// <summary>
    /// 把 <paramref name="limit"/> 夹进合理区间：≤0 用默认 5，&gt;10 夹到 10。
    /// </summary>
    /// <param name="limit">模型给的原始值。</param>
    /// <returns>真正生效的条数。</returns>
    public static int ClampLimit(int limit) =>
        limit <= 0 ? DefaultLimit : limit > MaxLimit ? MaxLimit : limit;

    /// <summary>
    /// 搜一个词：Bing 主 → DDG 备，第一个给出结果的就算赢，按 <paramref name="limit"/> 截断（先按 URL 去重）。
    /// </summary>
    /// <param name="query">查询词，空白会被去掉；空查询词不联网，直接返回"没试过"的失败。</param>
    /// <param name="limit">要几条，默认 <see cref="DefaultLimit"/>，上限 <see cref="MaxLimit"/>。</param>
    /// <param name="ct">取消令牌（用户按"停止"）：取消时抛 <see cref="OperationCanceledException"/>，不伪装成失败。</param>
    /// <returns>成功（含用了哪个后端）或失败（含两边各自的失败原因）。</returns>
    public async Task<SearchOutcome> SearchAsync(string? query, int limit = DefaultLimit, CancellationToken ct = default)
    {
        var trimmed = (query ?? string.Empty).Trim();
        var take = ClampLimit(limit);

        if (trimmed.Length == 0)
        {
            return SearchOutcome.Miss(string.Empty, take);
        }

        var attempts = new List<BackendAttempt>(Backends.Count);

        foreach (var backend in Backends)
        {
            ct.ThrowIfCancellationRequested();

            var attempt = await TryBackendAsync(backend, trimmed, ct).ConfigureAwait(false);
            attempts.Add(attempt);

            if (attempt.ResultCount == 0)
            {
                continue;
            }

            var unique = SearchHtmlParser.Deduplicate(attempt.Results);
            var total = unique.Count;
            var page = total > take ? unique.Take(take).ToArray() : unique.ToArray();

            return SearchOutcome.Hit(trimmed, take, backend, page, total, attempts, attempt.Direct);
        }

        return SearchOutcome.Miss(trimmed, take, attempts);
    }

    /// <summary>只释放自己 new 出来的 HttpClient（注入进来的那个归调用方）。</summary>
    public void Dispose()
    {
        if (_ownsProxyClient)
        {
            _proxyClient.Dispose();
        }

        if (_ownsDirectClient)
        {
            _directClient.Dispose();
        }
    }

    private static HttpClient CreateProxyClient() =>
        new(CreateDefaultHandler()) { Timeout = RequestTimeout };

    private static HttpClient CreateDirectClient() =>
        new(CreateDirectHandler()) { Timeout = RequestTimeout };

    private static HttpClient CreateClient(HttpMessageHandler handler) =>
        new(handler ?? throw new ArgumentNullException(nameof(handler))) { Timeout = RequestTimeout };

    /// <summary>装一个请求：UA / Accept / Accept-Language 一律在请求上带，不依赖 HttpClient 的默认头。</summary>
    private static HttpRequestMessage BuildRequest(SearchBackend backend, string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, backend.BuildUrl(query));
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        request.Headers.Accept.ParseAdd(AcceptHeader);
        request.Headers.AcceptLanguage.ParseAdd(AcceptLanguage);

        if (backend == SearchBackend.DuckDuckGo)
        {
            // 实测（2026-09-21）：同一个 UA，不带 Referer 时 DDG 更容易回 202 反爬空壳；
            // 带上自家 Referer 命中真结果页的比例明显更高。仍然是概率性的，所以才必须有 Bing 兜底。
            request.Headers.Referrer = new Uri(DuckDuckGoReferer);
        }

        return request;
    }

    /// <summary>
    /// 试一个后端：按 <see cref="Routes"/> 依次走网络路径，拿到结果就成功；一条路都拿不到就返回带原因的失败记录。
    /// <b>只有传输层失败才换下一条路</b>（HTTP 202 / 500 是后端给出的结论，重试没意义）。绝不抛（取消除外）。
    /// </summary>
    private async Task<BackendAttempt> TryBackendAsync(SearchBackend backend, string query, CancellationToken ct)
    {
        var routes = new List<RouteAttempt>(Routes.Count);

        foreach (var route in Routes)
        {
            ct.ThrowIfCancellationRequested();

            var attempt = await TryRouteAsync(route, backend, query, ct).ConfigureAwait(false);
            routes.Add(attempt);

            if (attempt.Succeeded)
            {
                return BackendAttempt.Ok(backend, routes);
            }

            if (!attempt.IsTransportFailure)
            {
                // 拿到过 HTTP 响应：这是后端的结论（限流 / 反爬 / 没结果），换路由重试没有意义。
                break;
            }
        }

        return BackendAttempt.Failed(backend, routes);
    }

    /// <summary>
    /// 走一条路试一个后端：拿不到结果就返回一条带原因的记录，绝不抛（取消除外）。
    /// </summary>
    private async Task<RouteAttempt> TryRouteAsync(
        SearchRoute route, SearchBackend backend, string query, CancellationToken ct)
    {
        var client = route == SearchRoute.Direct ? _directClient : _proxyClient;

        try
        {
            using var request = BuildRequest(backend, query);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;
            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (html.Length > MaxResponseChars)
            {
                html = html[..MaxResponseChars];
            }

            var parsed = SearchHtmlParser.Parse(html, backend);
            if (parsed.Count == 0)
            {
                var reason =
                    $"HTTP {status} ({response.ReasonPhrase ?? "?"}) but 0 results could be parsed out of a {html.Length}-char body";

                if (status is 202 or 403 or 429 or 503)
                {
                    reason += " - the engine is most likely throttling or challenging this client";
                }

                return RouteAttempt.HttpFailure(route, status, reason);
            }

            return RouteAttempt.Ok(route, status, parsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户按了停止：原样上抛，别伪装成"搜索失败"，更不许换条路再试一次。
            throw;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return RouteAttempt.TransportFailure(
                route, $"timed out after {RequestTimeout.TotalSeconds:0} seconds (HttpClient.Timeout)");
        }
        catch (Exception ex)
        {
            // SSL 握手失败 / 连接被拒 / DNS 挂了都落这里 —— 都属于"这条路不通"，可以换下一条路。
            return RouteAttempt.TransportFailure(route, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
