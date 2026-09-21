// ============================================================================
// WebSelfTest live —— 真联网那一趟（不接进 smoke.bat，主 agent 手动跑）。
//
//   dotnet run -p build\WebSelfTest.csproj -c Debug - live
//
// 它做三件事：真发一次搜索 → 断言至少 1 条结果 → 把前 3 条打出来给人看。
// 退出码 0 = 至少一个查询词拿到了结果；1 = 全都没拿到（会把两边的失败原因原样打出来）。
//
// 为什么要试几个词：两个后端都是网页直读，都可能概率性失手
// （DDG 的 html 端点实测连着几次回 HTTP 202 反爬空壳；Bing 偶发返回与查询词无关的缓存页）。
// 一次红就报红会误导人，所以按顺序试，命中即停，并把每一次的结果都如实打出来。
// ============================================================================

using PotatoAgent.Core.Tools;

namespace PotatoAgent.Web.SelfTest;

/// <summary>真联网那一趟的具体步骤。</summary>
internal static class LiveCheck
{
    private static readonly string[] Queries =
    {
        "dotnet 10 release notes",
        "windows 11 dotnet sdk download",
        "c# async await best practices",
    };

    /// <summary>跑联网自测，返回进程退出码。</summary>
    internal static async Task<int> RunAsync()
    {
        // 这台机器上系统代理（注册表里的 127.0.0.1:7890）一开着，.NET 的 HttpClient 就会走它；
        // 生产路径现在自带"传输层失败 → 直连重试"的兜底（见 SearchEngine），所以默认模式也能出结果。
        // 这个环境变量只是自测开关：让这趟 live 直接从直连那条路起手（不经过系统代理），
        // 用来对比"代理那条到底挂在哪"。生产代码不读环境变量。
        //   $env:POTATO_WEB_LIVE_NO_PROXY = "1"
        var bypassProxy = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POTATO_WEB_LIVE_NO_PROXY"));

        Console.WriteLine("==================== PotatoAgent.Web 网页搜索自测（真联网） ====================");
        Console.WriteLine($"UA       : {SearchEngine.BrowserUserAgent}");
        Console.WriteLine($"超时     : {SearchEngine.RequestTimeout.TotalSeconds:0} 秒/后端，后端链 {SearchEngine.Backends[0].DisplayName()} → {SearchEngine.Backends[1].DisplayName()}");
        Console.WriteLine($"路由     : {string.Join(" → ", SearchEngine.Routes.Select(r => r.DisplayName()))}");
        Console.WriteLine($"代理     : {(bypassProxy ? "自测开关已开：两条路都注入直连 handler，不经过系统代理（记账里的 [system proxy] 只是第一条路由的位置名）" : "跟随系统代理设置（Windows 注册表 / 环境变量），失败会自动直连重试")}");
        Console.WriteLine();

        var anyHit = false;

        foreach (var query in Queries)
        {
            Console.WriteLine($"---- 搜索: \"{query}\" ----");

            using var engine = CreateEngine(bypassProxy);
            SearchOutcome outcome;
            try
            {
                outcome = await engine.SearchAsync(query, SearchEngine.DefaultLimit).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Console.WriteLine($"    [FAIL] 引擎抛异常: {error.GetType().Name}: {error.Message}");
                continue;
            }

            // 第一次请求（= 主后端）中没中，单独明说一行：报告里要给的就是这个数。
            Console.WriteLine(outcome.Attempts.Count > 0
                ? $"    第一次请求(主后端): {outcome.Attempts[0].Describe()}"
                : "    第一次请求(主后端): (没发出去)");

            foreach (var attempt in outcome.Attempts)
            {
                Console.WriteLine($"    尝试 {attempt.Describe()}");
            }

            if (!outcome.Success)
            {
                Console.WriteLine("    [FAIL] 一条结果都没拿到，两边原因如下：");
                Console.WriteLine(Indent(outcome.DescribeFailure(), 6));

                if (!bypassProxy && outcome.DescribeFailure().Contains("SSL connection", StringComparison.Ordinal))
                {
                    Console.WriteLine("    (i) 两条路都挂了（系统代理 + 直连都是 SSL 失败）：网络本身不通，或者两个后端都够不着");
                }

                Console.WriteLine();
                continue;
            }

            anyHit = true;
            Console.WriteLine($"    [PASS] {outcome.Source} 给了 {outcome.TotalFound} 条，取前 {outcome.Results.Count} 条：");
            Console.WriteLine();

            var index = 1;
            foreach (var result in outcome.Results.Take(3))
            {
                Console.WriteLine($"    {index}. {result.Title}");
                Console.WriteLine($"       {result.Url}");
                Console.WriteLine($"       {Truncate(result.Snippet, 200)}");
                Console.WriteLine();
                index++;
            }

            // 再验一次"工具层"：同样的网络路径，从 ITool 出去应当也能拿到东西。
            var tool = new WebSearchTool(CreateEngine(bypassProxy));
            var viaTool = await tool.InvokeAsync(Args($"{{\"query\":\"{query}\",\"limit\":3}}"), CancellationToken.None)
                .ConfigureAwait(false);
            Console.WriteLine(viaTool.Success
                ? "    [PASS] 经 ITool（web_search）调用也成功，Data 是 JSON 数组"
                : $"    [FAIL] 经 ITool 调用失败: {Truncate(viaTool.Content, 300)}");
            Console.WriteLine();
            break;
        }

        Console.WriteLine("==================================================================");
        Console.WriteLine(anyHit ? "LIVE PASS（至少一个查询词拿到了真实结果）" : "LIVE FAIL（所有查询词都没拿到结果）");
        Console.WriteLine("==================================================================");
        return anyHit ? 0 : 1;
    }

    private static string Indent(string text, int spaces) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(line => new string(' ', spaces) + line.TrimEnd('\r')));

    /// <summary>
    /// 建引擎：默认就是生产路径（<see cref="SearchEngine()"/>：先系统代理、传输层失败再直连）；
    /// <paramref name="bypassProxy"/> 为 true（自测开关）时，两条路都用直连 handler，不经过系统代理。
    /// </summary>
    private static SearchEngine CreateEngine(bool bypassProxy) =>
        bypassProxy ? new SearchEngine(SearchEngine.CreateDirectHandler()) : new SearchEngine();

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static System.Text.Json.JsonElement Args(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
