// web_search 工具本体。
//
// 它是 Safe 级：只读联网，不动用户电脑上的任何东西，所以不需要弹确认框。
// 但"查询词会离开这台机器"这件事必须在 Description 里如实写给模型看
// —— 模型据此才知道不要把密码 / 私密内容塞进 query。

using System.Text;
using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Web;

/// <summary>
/// <c>web_search</c>：免密钥网页搜索（Bing 主 + DuckDuckGo 备），返回标题 / 真实网址 / 摘要。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ToolRisk.Safe"/>：只读联网，不改本机状态，界面不必弹确认。
/// 但查询词会明文发给 DuckDuckGo / Bing —— Description 里写明了这一点。
/// </para>
/// <para>
/// 失败时返回的 <see cref="ToolResult"/> 一定分别写清两个后端各自的原因
/// （HTTP 码 / 解析出 0 条 / 异常），模型据此能判断"是该换个词，还是网络真的不通"。
/// </para>
/// </remarks>
public sealed class WebSearchTool : ITool
{
    /// <summary>工具名（也是注册表里的键）。</summary>
    public const string ToolName = "web_search";

    private readonly SearchEngine _engine;
    private readonly bool _ownsEngine;

    /// <summary>
    /// 建一个真联网的 <c>web_search</c>（自己建 <see cref="SearchEngine"/>）。
    /// </summary>
    public WebSearchTool()
        : this(new SearchEngine(), ownsEngine: true)
    {
    }

    /// <summary>
    /// 用现成的 <see cref="SearchEngine"/> 建工具 —— <b>自测专用</b>：塞一个带假
    /// <see cref="HttpMessageHandler"/> 的引擎进来，就能离线验证整条链路。
    /// </summary>
    /// <param name="engine">搜索引擎，不能为 null。工具不负责释放它。</param>
    public WebSearchTool(SearchEngine engine)
        : this(engine, ownsEngine: false)
    {
    }

    private WebSearchTool(SearchEngine engine, bool ownsEngine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _ownsEngine = ownsEngine;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Search the web and return a ranked list of results (title, url and a short snippet). " +
        "Use it whenever the answer depends on up-to-date or external information: current events, " +
        "software or library versions, release notes, API documentation, unfamiliar error messages, " +
        "or any fact you are not certain about. " +
        "The query string is sent over the network to Bing (www.bing.com) and, if that returns nothing, " +
        "to DuckDuckGo (html.duckduckgo.com) - no API key is needed, and nothing on the user's computer is " +
        "read or changed. Do not put secrets (passwords, API keys, private data) into the query, because the " +
        "query leaves this machine. Returns the most relevant results first; follow the urls with a fetch " +
        "tool if you need the full page.";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "What to search for. Write it like a search engine query: keywords, version numbers, exact error messages. Example: \"dotnet 10 release notes\"."
            },
            "limit": {
              "type": "integer",
              "description": "How many results to return. Default 5, maximum 10."
            }
          },
          "required": ["query"]
        }
        """;

    /// <inheritdoc />
    public ToolRisk Risk => ToolRisk.Safe;

    /// <summary>内部用的搜索引擎（自测想直接看 <see cref="SearchOutcome"/> 时用）。</summary>
    public SearchEngine Engine => _engine;

    /// <inheritdoc />
    public async Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var query = args.Text("query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return ToolResult.Error("The 'query' argument is required and must be a non-empty string.");
        }

        var limit = args.Integer("limit") ?? SearchEngine.DefaultLimit;

        var outcome = await _engine.SearchAsync(query, limit, ct).ConfigureAwait(false);
        if (!outcome.Success)
        {
            return ToolResult.Error(outcome.DescribeFailure());
        }

        return ToolResult.Ok(FormatForModel(outcome), BuildData(outcome));
    }

    /// <summary>
    /// 给模型看的文本：一行一句 <c>1. 标题 — url</c>，摘要缩进在下一行。
    /// </summary>
    private static string FormatForModel(SearchOutcome outcome)
    {
        var text = new StringBuilder();
        text.Append("Found ").Append(outcome.TotalFound).Append(" result(s) for \"")
            .Append(outcome.Query).Append("\" via ").Append(outcome.Source);

        if (outcome.WasTruncated)
        {
            text.Append(" (showing the top ").Append(outcome.Results.Count).Append(')');
        }

        text.Append('.');

        var index = 1;
        foreach (var result in outcome.Results)
        {
            text.Append(Environment.NewLine)
                .Append(index++).Append(". ").Append(result.Title)
                .Append(" — ").Append(result.Url);

            if (result.Snippet.Length > 0)
            {
                text.Append(Environment.NewLine).Append("   ").Append(result.Snippet);
            }
        }

        return text.ToString();
    }

    /// <summary>结构化数据：<c>[{"title":…,"url":…,"snippet":…}]</c>。</summary>
    private static JsonElement BuildData(SearchOutcome outcome)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var result in outcome.Results)
            {
                writer.WriteStartObject();
                writer.WriteString("title", result.Title);
                writer.WriteString("url", result.Url);
                writer.WriteString("snippet", result.Snippet);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }
}

/// <summary>
/// 从模型给的 <see cref="JsonElement"/> 里<b>宽松</b>取参数（照抄 Win32 层 ToolArgs 的思路）。
/// </summary>
/// <remarks>
/// 模型的参数永远可能缺字段、类型写错、给个 null：这里一律退回默认值，
/// 绝不因为一个坏字段抛异常 —— 那会把一次普通调用变成"工具挂了"。
/// </remarks>
internal static class WebToolArgs
{
    /// <summary>取字符串；不是字符串或字段不存在返回 null。</summary>
    internal static string? Text(this JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// 取整数：数字直接要；<b>写成字符串的数字也认</b>（模型时不时给 <c>"limit": "5"</c>）；
    /// 其余情况返回 null，由调用方用默认值兜。
    /// </summary>
    internal static int? Integer(this JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return (int)Math.Round(number);
        }

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
