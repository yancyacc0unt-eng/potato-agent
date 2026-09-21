// 一次搜索的"过程记录"与"最终结论"。
//
// 为什么要记 Attempts：工具失败时必须能分别说清两边各自的失败原因
// （HTTP 码 / 解析出 0 条 / 异常），而不是糊成一句"搜索失败"。
// 所以每个后端都留一份 BackendAttempt —— 它里面再按【路由】细分：
// 系统代理那条和直连那条各一行，谁成功了、谁为什么失败，都留在文本里。

using System.Text;

namespace PotatoAgent.Web;

/// <summary>
/// 对某一个后端的一次尝试（成功或失败都记）：里面按路由细分（系统代理 / 直连）。
/// </summary>
/// <remarks>
/// <see cref="Describe"/> 就是给模型看的那行记账，例如：
/// <c>Bing (www.bing.com) [system proxy]: HttpRequestException: The SSL connection could not be established…; [direct]: HTTP 200, 10 result(s)</c>。
/// </remarks>
public sealed class BackendAttempt
{
    private static readonly IReadOnlyList<WebSearchResult> NoResults = Array.Empty<WebSearchResult>();

    private BackendAttempt(SearchBackend backend, IReadOnlyList<RouteAttempt> routes, RouteAttempt? winning)
    {
        Backend = backend;
        Routes = routes;
        WinningRoute = winning;
        Results = winning?.Results ?? NoResults;
    }

    /// <summary>试的是哪个后端。</summary>
    public SearchBackend Backend { get; }

    /// <summary>这个后端走过的每条路（顺序：系统代理 → 直连），失败时至少一条。</summary>
    public IReadOnlyList<RouteAttempt> Routes { get; }

    /// <summary>成功的那条路；失败时为 null。</summary>
    public RouteAttempt? WinningRoute { get; }

    /// <summary>这次尝试拿到结果了没有（HTTP 200 但一条都没解析出来 = false）。</summary>
    public bool Succeeded => WinningRoute is not null;

    /// <summary>成功那条路的 HTTP 状态码；失败时取最后一条路的状态码（没走到 HTTP 就是 null）。</summary>
    public int? HttpStatus => Succeeded
        ? WinningRoute!.HttpStatus
        : Routes.Count > 0 ? Routes[^1].HttpStatus : null;

    /// <summary>解析出来的结果（失败时是空列表）。</summary>
    public IReadOnlyList<WebSearchResult> Results { get; }

    /// <summary>
    /// 失败原因（英文）：把每条路的记账拼起来，所以"系统代理为什么不行 + 直连为什么也不行"都在里面。
    /// 成功时为 null。
    /// </summary>
    public string? Error => Succeeded ? null : Describe();

    /// <summary>解析出来的条数。</summary>
    public int ResultCount => Results.Count;

    /// <summary>后端显示名，例如 <c>DuckDuckGo</c>。</summary>
    public string BackendName => Backend.DisplayName();

    /// <summary>后端主机名，例如 <c>html.duckduckgo.com</c>。</summary>
    public string Host => Backend.Host();

    /// <summary>
    /// 结果是不是靠<b>直连</b>拿到的（系统代理那条挂了、直连那条成了）。
    /// 给模型看的来源说明据此写成 <c>Bing (direct)</c>，不糊弄。
    /// </summary>
    public bool Direct => Succeeded && WinningRoute!.Route == SearchRoute.Direct;

    /// <summary>最后走的那条路的名字（成功时是成功那条）：<c>system proxy</c> / <c>direct</c>。</summary>
    public string RouteName => (Succeeded ? WinningRoute!.Route : Routes.Count > 0 ? Routes[^1].Route : SearchRoute.SystemProxy)
        .DisplayName();

    /// <summary>成功：带上这个后端走过的所有路由记录。</summary>
    /// <param name="backend">后端。</param>
    /// <param name="routes">路由记录，最后一条是成功的那条。</param>
    public static BackendAttempt Ok(SearchBackend backend, IReadOnlyList<RouteAttempt> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var winning = routes.LastOrDefault(static route => route.Succeeded)
            ?? throw new ArgumentException("BackendAttempt.Ok needs at least one successful route.", nameof(routes));

        return new BackendAttempt(backend, routes, winning);
    }

    /// <summary>失败：带上这个后端走过的所有路由记录（每条都要有失败原因）。</summary>
    /// <param name="backend">后端。</param>
    /// <param name="routes">路由记录，至少一条。</param>
    public static BackendAttempt Failed(SearchBackend backend, IReadOnlyList<RouteAttempt> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.Count == 0)
        {
            throw new ArgumentException("BackendAttempt.Failed needs at least one route record.", nameof(routes));
        }

        return new BackendAttempt(backend, routes, null);
    }

    /// <summary>
    /// 一行记账：<c>Bing (www.bing.com) [system proxy]: HTTP 200, 10 result(s)</c>；
    /// 试过两条路时两条都写：<c>… [system proxy]: HttpRequestException: …; [direct]: HTTP 200, 10 result(s)</c>。
    /// </summary>
    public string Describe() =>
        $"{BackendName} ({Host}) {string.Join("; ", Routes.Select(route => route.Describe()))}";

    /// <inheritdoc />
    public override string ToString() => Describe();
}

/// <summary>
/// 一次 <see cref="SearchEngine.SearchAsync"/> 的结论：成功（含用了哪个后端、走没走直连）
/// 或失败（含两边、两条路各自的原因）。
/// </summary>
public sealed class SearchOutcome
{
    private static readonly IReadOnlyList<WebSearchResult> NoResults = Array.Empty<WebSearchResult>();
    private static readonly IReadOnlyList<BackendAttempt> NoAttempts = Array.Empty<BackendAttempt>();

    private SearchOutcome(
        bool success,
        string query,
        int limit,
        SearchBackend? backend,
        IReadOnlyList<WebSearchResult>? results,
        int totalFound,
        IReadOnlyList<BackendAttempt>? attempts,
        string? failure,
        bool direct)
    {
        Success = success;
        Query = query;
        Limit = limit;
        Backend = backend;
        Results = results ?? NoResults;
        TotalFound = totalFound;
        Attempts = attempts ?? NoAttempts;
        Failure = failure;
        Direct = direct;
    }

    /// <summary>true = 拿到了至少一条结果。</summary>
    public bool Success { get; }

    /// <summary>这次搜的查询词（已去首尾空白）。</summary>
    public string Query { get; }

    /// <summary>生效的条数上限（已夹到 1..10）。</summary>
    public int Limit { get; }

    /// <summary>成功时是哪个后端给出的结果；失败时为 null。</summary>
    public SearchBackend? Backend { get; }

    /// <summary>最终结果，已经去重并按 <see cref="Limit"/> 截断。</summary>
    public IReadOnlyList<WebSearchResult> Results { get; }

    /// <summary>截断前一共解析出多少条（比 <see cref="Results"/> 多就是被 limit 截了）。</summary>
    public int TotalFound { get; }

    /// <summary>每个后端各试了一次，失败时按尝试顺序排（主后端在前，见 <see cref="SearchEngine.Backends"/>）。</summary>
    public IReadOnlyList<BackendAttempt> Attempts { get; }

    /// <summary>失败的一句话总因（英文）；成功时为 null。</summary>
    public string? Failure { get; }

    /// <summary>成功那条结果是不是靠<b>直连</b>拿到的（系统代理那条挂了）。失败时恒为 false。</summary>
    public bool Direct { get; }

    /// <summary>结果是不是被 <see cref="Limit"/> 截过。</summary>
    public bool WasTruncated => TotalFound > Results.Count;

    /// <summary>成功时用的后端名（<c>Bing</c> / <c>DuckDuckGo</c>）；失败时是空串。</summary>
    public string BackendName => Backend?.DisplayName() ?? string.Empty;

    /// <summary>
    /// 给模型看的来源说明：<c>Bing</c>，直连拿到的则是 <c>Bing (direct)</c>；失败时是空串。
    /// 工具那行 <c>via …</c> 用的就是它 —— 走没走直连要如实说。
    /// </summary>
    public string Source => Success ? (Direct ? $"{BackendName} (direct)" : BackendName) : string.Empty;

    /// <summary>成功：带结果、用了哪个后端、是不是直连。</summary>
    /// <param name="query">查询词。</param>
    /// <param name="limit">生效的条数上限。</param>
    /// <param name="backend">给出结果的后端。</param>
    /// <param name="results">已截断的结果。</param>
    /// <param name="totalFound">截断前的条数。</param>
    /// <param name="attempts">尝试记录。</param>
    /// <param name="direct">结果是不是靠直连拿到的。</param>
    public static SearchOutcome Hit(
        string query, int limit, SearchBackend backend, IReadOnlyList<WebSearchResult> results, int totalFound,
        IReadOnlyList<BackendAttempt>? attempts = null, bool direct = false) =>
        new(true, query, limit, backend, results, totalFound, attempts, null, direct);

    /// <summary>失败：带上每个后端、每条路各自的失败原因。</summary>
    /// <param name="query">查询词（空查询词时是空串）。</param>
    /// <param name="limit">生效的条数上限。</param>
    /// <param name="attempts">尝试记录，可为空（查询词本身是空的时候就一次都没试）。</param>
    /// <param name="failure">可选的一句话总因；不给就按尝试记录自动生成。</param>
    public static SearchOutcome Miss(
        string query, int limit, IReadOnlyList<BackendAttempt>? attempts = null, string? failure = null) =>
        new(false, query, limit, null, null, 0, attempts, failure, false);

    /// <summary>
    /// 给模型看的失败文本：总因 + 每个后端（每条路）各自的原因，一行一个。
    /// 这是"两个后端都不行"时唯一的事实来源，别在别处再拼一遍。
    /// </summary>
    public string DescribeFailure()
    {
        if (!string.IsNullOrEmpty(Failure))
        {
            return Failure!;
        }

        if (Attempts.Count == 0)
        {
            return "Web search was not attempted (the query was empty).";
        }

        var text = new StringBuilder();
        text.Append("Web search failed for \"").Append(Query).Append("\" — all ")
            .Append(Attempts.Count).Append(" backend(s) came back empty:");
        foreach (var attempt in Attempts)
        {
            text.Append(Environment.NewLine).Append("  - ").Append(attempt.Describe());
        }

        text.Append(Environment.NewLine)
            .Append("Each backend is tried over the system proxy first and then, if that fails at the transport ")
            .Append("level, directly. Check the network connection, or retry in a moment (a search engine may be ")
            .Append("rate limiting this client).");
        return text.ToString();
    }

    /// <inheritdoc />
    public override string ToString() =>
        Success ? $"ok: {Results.Count} result(s) from {Source} for \"{Query}\"" : $"error: {DescribeFailure()}";
}
