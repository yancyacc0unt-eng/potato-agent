namespace PotatoAgent.Markdown;

/// <summary>
/// 渲染出来的文档元素上挂的语义标记，值是 <see cref="System.Windows.FrameworkContentElement.Tag"/> 字符串。
/// </summary>
/// <remarks>
/// <para>
/// 这一层只描述"这是什么"（结构语义），不描述"长什么样"。样式由<em>外层</em>决定：
/// 用 <c>Tag</c> 做 DataTrigger / 自己遍历文档挂 Style 都行，渲染器永远不写死颜色和字体。
/// </para>
/// <para>
/// 具体取值约定：
/// <list type="bullet">
/// <item><see cref="Heading"/>：<c>"md:heading:1"</c> … <c>"md:heading:6"</c>（冒号后面是标题层级）。</item>
/// <item><see cref="CodeBlock"/>：无语言是 <c>"md:code-block"</c>，有语言是 <c>"md:code-block:csharp"</c>
/// （围栏的语言标记不画在界面上，挂在这里，要语法高亮就从这个后缀取）。</item>
/// <item>其余都是固定串，见各自注释。</item>
/// </list>
/// </para>
/// </remarks>
public static class MarkdownTags
{
    /// <summary>标题所在的块（<see cref="System.Windows.Documents.Paragraph"/>）。值是 <c>md:heading:层级</c>，层级 1~6。</summary>
    public const string Heading = "md:heading";

    /// <summary>普通段落（<see cref="System.Windows.Documents.Paragraph"/>）。</summary>
    public const string Paragraph = "md:paragraph";

    /// <summary>围栏 / 缩进代码块（<see cref="System.Windows.Documents.Paragraph"/>）。有语言时值是 <c>md:code-block:语言</c>。</summary>
    public const string CodeBlock = "md:code-block";

    /// <summary>行内代码（<see cref="System.Windows.Documents.Run"/>）。</summary>
    public const string InlineCode = "md:code-inline";

    /// <summary>引用块（<see cref="System.Windows.Documents.Section"/>）。</summary>
    public const string Quote = "md:quote";

    /// <summary>列表（<see cref="System.Windows.Documents.List"/>）。</summary>
    public const string List = "md:list";

    /// <summary>分割线（<see cref="System.Windows.Documents.Paragraph"/>，块本身是空的，只有一条底边框）。</summary>
    public const string Rule = "md:rule";

    /// <summary>表格（<see cref="System.Windows.Documents.Table"/>）。</summary>
    public const string Table = "md:table";

    /// <summary>表格单元格（<see cref="System.Windows.Documents.TableCell"/>）。</summary>
    public const string TableCell = "md:table-cell";

    /// <summary>链接（<see cref="System.Windows.Documents.Hyperlink"/>）。</summary>
    public const string Link = "md:link";

    /// <summary>图片。图片不会被下载，只把 alt 文本画出来（<see cref="System.Windows.Documents.Span"/>）。</summary>
    public const string Image = "md:image";

    /// <summary>HTML 被当成纯文本画出来时挂的标记（<see cref="System.Windows.Documents.Run"/> 或段落）。</summary>
    public const string Html = "md:html";

    /// <summary>拼标题用的 Tag 值，例如 <c>HeadingTag(2)</c> → <c>"md:heading:2"</c>。</summary>
    /// <param name="level">标题层级，1~6。</param>
    /// <returns>挂在该标题段落 <c>Tag</c> 上的字符串。</returns>
    public static string HeadingTag(int level) => Heading + ":" + level.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
