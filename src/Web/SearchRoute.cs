// 路由：一个请求走哪条网络路径。
//
// 为什么要有这个概念（2026-09-21 实测踩到的坑）：这台机器的系统代理（注册表里的
// ProxyEnable=1 / 127.0.0.1:7890）**开着但根本不通** —— `curl -x http://127.0.0.1:7890`
// 对 bing / ddg / example.com 全是 http=000。而 .NET 的 HttpClient 默认就走系统代理，
// 于是生产路径的 web_search 每个请求都死在 "The SSL connection could not be established"。
//
// 所以一个后端最多试两条路：
//   1. 先按系统设置走（用户要翻墙时就得靠它，这个默认行为绝不能改）；
//   2. 只有【传输层】失败（SSL / 连接被拒 / DNS / 非用户取消的超时）才直连重试一次。
// HTTP 状态码（DDG 的 202、Bing 的 500）是后端给出的结论，不是"网络不通"，重试没有意义。

namespace PotatoAgent.Web;

/// <summary>一个请求走哪条网络路径。</summary>
public enum SearchRoute
{
    /// <summary>跟随系统代理设置（默认，也是生产环境正常情况下该走的那条）。</summary>
    SystemProxy = 0,

    /// <summary>绕开系统代理直连（只在系统代理那条传输层失败时才用）。</summary>
    Direct = 1,
}

/// <summary><see cref="SearchRoute"/> 的显示名（写进给模型看的失败文本，英文）。</summary>
public static class SearchRouteExtensions
{
    /// <summary>显示名：<c>system proxy</c> / <c>direct</c>。</summary>
    /// <param name="route">路由。</param>
    /// <returns>英文短名，用在记账文本里，例如 <c>[system proxy]: …</c>。</returns>
    public static string DisplayName(this SearchRoute route) =>
        route == SearchRoute.Direct ? "direct" : "system proxy";
}

/// <summary>
/// 一次路由尝试的结果：同一个后端的"系统代理"与"直连"各算一条，都要如实记账。
/// </summary>
/// <remarks>
/// <see cref="IsTransportFailure"/> 是直连重试的开关：只有它为 true 才换下一条路。
/// 它必须**只有**在"压根没拿到 HTTP 响应"（异常 / 超时）时才为 true ——
/// 拿到 202 / 500 都算拿到了响应，是后端的结论。
/// </remarks>
public sealed class RouteAttempt
{
    private static readonly IReadOnlyList<WebSearchResult> NoResults = Array.Empty<WebSearchResult>();

    private RouteAttempt(
        SearchRoute route, bool succeeded, int? httpStatus, IReadOnlyList<WebSearchResult>? results,
        string? error, bool isTransportFailure)
    {
        Route = route;
        Succeeded = succeeded;
        HttpStatus = httpStatus;
        Results = results ?? NoResults;
        Error = error;
        IsTransportFailure = isTransportFailure;
    }

    /// <summary>走的是哪条路。</summary>
    public SearchRoute Route { get; }

    /// <summary>路由显示名：<c>system proxy</c> / <c>direct</c>。</summary>
    public string RouteName => Route.DisplayName();

    /// <summary>这条路上拿到结果了没有。</summary>
    public bool Succeeded { get; }

    /// <summary>HTTP 状态码；压根没走到 HTTP（DNS / 连接被拒 / SSL / 超时）时为 null。</summary>
    public int? HttpStatus { get; }

    /// <summary>这条路上解析出来的结果（失败时是空列表）。</summary>
    public IReadOnlyList<WebSearchResult> Results { get; }

    /// <summary>解析出来的条数。</summary>
    public int ResultCount => Results.Count;

    /// <summary>失败原因（英文）；成功时为 null。</summary>
    public string? Error { get; }

    /// <summary>
    /// 是不是<b>传输层</b>失败（异常 / 超时，压根没拿到 HTTP 响应）。
    /// 只有 true 才允许换下一条路重试。
    /// </summary>
    public bool IsTransportFailure { get; }

    /// <summary>成功：带上这条路上解析出来的结果。</summary>
    /// <param name="route">路由。</param>
    /// <param name="httpStatus">HTTP 状态码。</param>
    /// <param name="results">解析结果，至少一条。</param>
    public static RouteAttempt Ok(SearchRoute route, int httpStatus, IReadOnlyList<WebSearchResult> results) =>
        new(route, true, httpStatus, results, null, false);

    /// <summary>HTTP 层的失败（202 空壳 / 500 / 200 但 0 条）：<b>不</b>触发直连重试。</summary>
    /// <param name="route">路由。</param>
    /// <param name="httpStatus">HTTP 状态码。</param>
    /// <param name="error">失败原因。</param>
    public static RouteAttempt HttpFailure(SearchRoute route, int httpStatus, string error) =>
        new(route, false, httpStatus, null, error, false);

    /// <summary>传输层的失败（异常 / 非用户取消的超时）：<b>会</b>触发直连重试。</summary>
    /// <param name="route">路由。</param>
    /// <param name="error">失败原因（异常类型 + 消息）。</param>
    public static RouteAttempt TransportFailure(SearchRoute route, string error) =>
        new(route, false, null, null, error, true);

    /// <summary>一条路的记账：<c>[direct]: HTTP 200, 10 result(s)</c> 或 <c>[system proxy]: HttpRequestException: …</c>。</summary>
    public string Describe() => Succeeded
        ? $"[{RouteName}]: HTTP {HttpStatus?.ToString() ?? "?"}, {ResultCount} result(s)"
        : $"[{RouteName}]: {Error}";

    /// <inheritdoc />
    public override string ToString() => Describe();
}
