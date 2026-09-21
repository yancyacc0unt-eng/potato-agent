// 自测用的假网络层：把 fixture HTML 当 HTTP 响应喂回去，全程不碰真实网络。
//
// 两个职责：
//   1. FixtureHandler —— 按请求 URI 决定回哪份 HTML（或抛异常 / 回错误码），并把每个请求截下来
//      （必须"截图式"保存：HttpRequestMessage 会被引擎释放，事后不能再读它）。
//   2. Fixtures       —— 找到并读取 src\WebSelfTest\Fixtures\*.html 那三份真页面裁出来的 fixture。

using System.Net;
using System.Text;

namespace PotatoAgent.Web.SelfTest;

/// <summary>被截下来的一次请求（值拷贝，原 request 早就被释放了）。</summary>
internal sealed class CapturedRequest
{
    internal CapturedRequest(Uri uri, string userAgent, string acceptLanguage, string accept, string? referer)
    {
        Uri = uri;
        UserAgent = userAgent;
        AcceptLanguage = acceptLanguage;
        Accept = accept;
        Referer = referer;
    }

    internal Uri Uri { get; }

    internal string UserAgent { get; }

    internal string AcceptLanguage { get; }

    internal string Accept { get; }

    internal string? Referer { get; }

    internal string Host => Uri.Host;

    internal string AbsolutePath => Uri.AbsolutePath;

    /// <summary>原始 query（没解码）。</summary>
    internal string RawQuery => Uri.Query;

    /// <summary>查询串里某个参数的值（已百分号解码）；没有这个参数返回 null。</summary>
    internal string? QueryValue(string name)
    {
        var query = Uri.Query;
        foreach (var piece in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = piece.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            if (string.Equals(piece[..eq], name, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(piece[(eq + 1)..]);
            }
        }

        return null;
    }

    internal static CapturedRequest From(HttpRequestMessage request) => new(
        request.RequestUri ?? new Uri("about:blank"),
        Header(request, "User-Agent"),
        Header(request, "Accept-Language"),
        Header(request, "Accept"),
        request.Headers.Referrer?.ToString());

    /// <summary>
    /// 读一个请求头（没有就返回空串）。
    /// ⚠ UA 要读类型化的 <c>Headers.UserAgent</c>：<c>TryGetValues("User-Agent")</c> 拿到的是
    /// .NET 拆开的多个值（"Mozilla/5.0", "(Windows NT 10.0; Win64; x64)", "AppleWebKit/537.36", …），
    /// 拼起来跟真正发到网线上的那一行不一样。类型化集合的 ToString() 才是线上原样。
    /// </summary>
    private static string Header(HttpRequestMessage request, string name) =>
        string.Equals(name, "User-Agent", StringComparison.OrdinalIgnoreCase)
            ? request.Headers.UserAgent.ToString()
            : request.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : string.Empty;
}

/// <summary>按请求回响应的假 handler；同时记录每一个被拦截的请求。</summary>
internal sealed class FixtureHandler : HttpMessageHandler
{
    private readonly Func<Uri, HttpResponseMessage> _respond;

    internal FixtureHandler(Func<Uri, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    /// <summary>所有被拦截的请求，按发生顺序。</summary>
    internal List<CapturedRequest> Requests { get; } = new();

    /// <summary>全程一共拦下多少个请求（离线自测的护栏断言用它证明"真的一个都没漏到真网络去"）。</summary>
    internal static int TotalIntercepted { get; private set; }

    /// <summary>只回同一个响应的简化构造。</summary>
    internal static FixtureHandler Always(Func<Uri, HttpResponseMessage> respond) => new(respond);

    /// <summary>DDG 回 <paramref name="duckHtml"/>，Bing 回 <paramref name="bingHtml"/>。</summary>
    internal static FixtureHandler Split(string duckHtml, string bingHtml) => new(uri =>
        uri.Host.Contains("duckduckgo", StringComparison.OrdinalIgnoreCase)
            ? FakeResponse.Html(duckHtml)
            : FakeResponse.Html(bingHtml));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(CapturedRequest.From(request));
        TotalIntercepted++;
        return Task.FromResult(_respond(request.RequestUri ?? new Uri("about:blank")));
    }
}

/// <summary>造假 HTTP 响应的几个小工厂。</summary>
internal static class FakeResponse
{
    /// <summary>HTML 响应（默认 200）。</summary>
    internal static HttpResponseMessage Html(string html, int status = 200) => new((HttpStatusCode)status)
    {
        Content = new StringContent(html, Encoding.UTF8, "text/html"),
    };

    /// <summary>只有状态码、正文是空壳的响应。</summary>
    internal static HttpResponseMessage Empty(int status) => new((HttpStatusCode)status)
    {
        Content = new StringContent(string.Empty, Encoding.UTF8, "text/html"),
    };

    /// <summary>JSON 响应。</summary>
    internal static HttpResponseMessage Json(string json, int status = 200) => new((HttpStatusCode)status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}

/// <summary>真页面裁出来的 fixture（<c>src\WebSelfTest\Fixtures\*.html</c>）。</summary>
internal static class Fixtures
{
    private static readonly Lazy<string?> Resolved = new(FindDirectory);

    /// <summary>找到的 fixture 目录；找不到时是 null。</summary>
    internal static string? Directory => Resolved.Value;

    /// <summary>读一份 fixture；读不到就抛（自测里这属于"工程坏了"，必须大声失败）。</summary>
    internal static string Read(string fileName)
    {
        var path = Path(fileName);
        return File.ReadAllText(path, Encoding.UTF8);
    }

    /// <summary>fixture 的完整路径（不检查存在）。</summary>
    internal static string Path(string fileName) =>
        System.IO.Path.Combine(Directory ?? "(fixtures-not-found)", fileName);

    /// <summary>某份 fixture 存在不存在。</summary>
    internal static bool Exists(string fileName) => File.Exists(Path(fileName));

    /// <summary>
    /// 依次找：输出目录的 Fixtures\、从输出目录往上找 src\WebSelfTest\Fixtures、从当前目录往上找。
    /// </summary>
    private static string? FindDirectory()
    {
        var candidates = new List<string> { System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures") };

        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var probe = new DirectoryInfo(start);
            while (probe is not null)
            {
                candidates.Add(System.IO.Path.Combine(probe.FullName, "src", "WebSelfTest", "Fixtures"));
                candidates.Add(System.IO.Path.Combine(probe.FullName, "Fixtures"));
                probe = probe.Parent;
            }
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(System.IO.Path.Combine(candidate, "ddg.html")))
            {
                return candidate;
            }
        }

        return null;
    }
}
