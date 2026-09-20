using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using Markdig;
using Markdig.Syntax;
using MarkdigParser = Markdig.Markdown;
using MdAutolinkInline = Markdig.Syntax.Inlines.AutolinkInline;
using MdBlock = Markdig.Syntax.Block;
using MdCodeInline = Markdig.Syntax.Inlines.CodeInline;
using MdContainerInline = Markdig.Syntax.Inlines.ContainerInline;
using MdEmphasisInline = Markdig.Syntax.Inlines.EmphasisInline;
using MdHtmlInline = Markdig.Syntax.Inlines.HtmlInline;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdLineBreakInline = Markdig.Syntax.Inlines.LineBreakInline;
using MdLinkInline = Markdig.Syntax.Inlines.LinkInline;
using MdLiteralInline = Markdig.Syntax.Inlines.LiteralInline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableAlign = Markdig.Extensions.Tables.TableColumnAlign;
using WpfList = System.Windows.Documents.List;

namespace PotatoAgent.Markdown;

/// <summary>
/// Markdown → WPF 富文本的渲染引擎。两种产物：整篇文档（<see cref="ToFlowDocument"/>，块级结构齐全）
/// 和一段行内序列（<see cref="ToInlines"/>，直接塞 <c>TextBlock.Inlines</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>一、配色和字体不归这一层管。</b>渲染出来的元素不设 <c>Foreground</c> / <c>Background</c> /
/// <c>FontFamily</c>，全部走 WPF 的属性继承 —— 外层换主题，里面自动跟着变。
/// 只设"Markdown 语义本身要求"的东西：加粗=FontWeight、斜体=FontStyle、删除线=TextDecorations、
/// 列表/引用/代码块的缩进、表格框线厚度、标题/表格头的加粗。颜色一个都不写。
/// 每个元素都挂了 <see cref="MarkdownTags"/> 里的语义标记（<c>Tag</c>），要自己上样式就从这里认。
/// </para>
/// <para>
/// <b>二、原始 HTML 一律不执行。</b>管道上开了 Markdig 的 <c>DisableHtml()</c>，<c>&lt;script&gt;</c>、
/// <c>&lt;img onerror=...&gt;</c>、<c>&lt;a href="javascript:..."&gt;</c> 全部按纯文本画出来，
/// 既不会生成按钮也不会生成可点的链接。另外只有 <c>http</c> / <c>https</c> / <c>mailto</c> 三种协议的链接
/// 才会拿到 <c>NavigateUri</c>，别的协议（javascript:、file:、vscode:…）一律不可点。
/// </para>
/// <para>
/// <b>三、永不抛异常。</b>畸形输入（未闭合的围栏 / 粗体、几万字的单行、几百层嵌套的引用、控制字符）
/// 最多渲染得难看，绝不会把聊天窗口带崩：解析或渲染中途出任何意外，整篇退回纯文本。
/// 递归深度另有硬上限 <see cref="MaxDepth"/>，防的是栈溢出（栈溢出是 catch 不住的）。
/// </para>
/// <para>
/// <b>四、流式约定（重要）。</b>模型逐块吐字时<em>不要</em>每来一个字就调这里重解析整篇：
/// 那是 O(n²)，长回答会卡、还会闪。正确做法见 <see cref="MarkdownViewer"/>：
/// <c>IsStreaming == true</c> 期间走纯文本快路径（一个字符都不解析），这一句说完把
/// <c>IsStreaming</c> 置回 false，那时才解析一次整篇 Markdown。
/// </para>
/// </remarks>
public static class MarkdownRenderer
{
    /// <summary>块级嵌套的硬上限，超过就按纯文本画（保护栈，也保护用户的时间）。</summary>
    private const int MaxDepth = 32;

    /// <summary>缩进类排版（不是配色）：引用块左边留白。</summary>
    private const double QuoteIndent = 12;

    /// <summary>缩进类排版（不是配色）：代码块左边留白。</summary>
    private const double CodeIndent = 12;

    /// <summary>表格单元格内边距。</summary>
    private const double CellPadding = 6;

    /// <summary>行内模式里分割线的替代画法：一串横线字符。</summary>
    private const int InlineRuleWidth = 16;

    /// <summary>
    /// 解析管道。刻意只开需要的扩展：表格（管道表 + 网格表）、删除线等强调扩展、裸链接自动识别。
    /// <c>DisableHtml</c> 保证 HTML 进不了 AST（见类注释第二条）。
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseGridTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    // ==================================================================
    //  对外 API
    // ==================================================================

    /// <summary>
    /// 把整篇 Markdown 渲染成 <see cref="FlowDocument"/>（标题 / 段落 / 列表 / 引用 / 代码块 / 表格 / 分割线都在）。
    /// </summary>
    /// <param name="markdown">Markdown 原文，可以是 <c>null</c>（当空串处理）。</param>
    /// <returns>
    /// 新建的文档，每次调用都是新实例（可以放心塞给 <c>RichTextBox.Document</c> / <c>FlowDocumentScrollViewer.Document</c>）。
    /// 文档本身不设字体和颜色 —— 挂到哪个宿主上就继承那个宿主的样子。
    /// </returns>
    /// <remarks>
    /// 分割线、表格框线只设了"厚度"，颜色由宿主给（<see cref="MarkdownViewer"/> 的做法是拿继承到的
    /// <c>Foreground</c>）。直接用这个方法的宿主如果想看见框线，遍历一遍文档、把
    /// <see cref="MarkdownTags.Rule"/> / <see cref="MarkdownTags.TableCell"/> / <see cref="MarkdownTags.Quote"/>
    /// 这三个标记的 <c>BorderBrush</c> 设成自己的前景色即可。
    /// </remarks>
    public static FlowDocument ToFlowDocument(string? markdown)
    {
        var document = new FlowDocument();
        var text = markdown ?? string.Empty;

        if (text.Length == 0)
        {
            return document;
        }

        try
        {
            var ast = MarkdigParser.Parse(text, Pipeline);
            AddBlocks(document.Blocks, ast, 0);
        }
        catch (Exception)
        {
            // 渲染器只承诺"不抛"：万一 Markdig 或本层在某段畸形文本上翻车，
            // 退成纯文本总比让聊天窗口崩掉强（原文一个字都不丢）。
            document.Blocks.Clear();
            AddLiteralBlocks(document.Blocks, text, MarkdownTags.Html);
        }

        return document;
    }

    /// <summary>
    /// 把 Markdown 摊平成一段行内序列，给"只有一行、塞不进块"的地方用（例如 <c>TextBlock.Inlines</c>）。
    /// </summary>
    /// <param name="markdown">Markdown 原文，可以是 <c>null</c>（当空串处理）。</param>
    /// <returns>
    /// 新建的 Inline 实例序列，块与块之间是 <see cref="LineBreak"/>。行内语法（粗体 / 斜体 / 删除线 /
    /// 行内代码 / 链接）原样保留。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 块级结构没有地方放，只能就地摊平，约定如下：标题=加粗的一段；段落=一行；
    /// 列表项=前缀 <c>"• "</c> 或 <c>"1. "</c>，每嵌一层多缩进两个空格；
    /// 引用=前缀 <c>"&gt; "</c>；代码块=原文逐行（不带围栏、不带语言标记）；分割线=一串 <c>─</c>；表格=每个单元格用 <c> | </c> 连起来。
    /// </para>
    /// <para>
    /// 返回的 Inline 每次调用都是新实例，且<em>只能挂到一处</em>（WPF 的 Inline 是单亲的）——
    /// 想同时放两个 <c>TextBlock</c> 就调两次。
    /// </para>
    /// </remarks>
    public static IEnumerable<Inline> ToInlines(string? markdown)
    {
        var result = new List<Inline>();
        var text = markdown ?? string.Empty;

        if (text.Length == 0)
        {
            return result;
        }

        try
        {
            var ast = MarkdigParser.Parse(text, Pipeline);
            AppendBlockInlines(result, ast, 0, string.Empty);
        }
        catch (Exception)
        {
            result.Clear();
            AddPlainText(result, text);
        }

        return result;
    }

    // ==================================================================
    //  文档模式：块
    // ==================================================================

    /// <summary>把 Markdig 的块容器翻成 WPF 的块集合。<paramref name="depth"/> 是块级嵌套深度。</summary>
    private static void AddBlocks(BlockCollection target, ContainerBlock container, int depth)
    {
        if (depth > MaxDepth)
        {
            // 嵌套太深（畸形文档），整块按纯文本落地，不再往下递归。
            AddLiteralBlocks(target, ContainerText(container), MarkdownTags.Html);
            return;
        }

        foreach (var block in container)
        {
            switch (block)
            {
                // ⚠ FencedCodeBlock 必须排在 CodeBlock 前面（前者是后者的派生类型）
                case FencedCodeBlock fenced:
                    target.Add(BuildCodeBlock(fenced, LanguageOf(fenced)));
                    break;

                case CodeBlock code:
                    target.Add(BuildCodeBlock(code, string.Empty));
                    break;

                case HeadingBlock heading:
                {
                    var paragraph = new Paragraph
                    {
                        Tag = MarkdownTags.HeadingTag(heading.Level),
                        FontWeight = FontWeights.Bold,
                    };
                    AppendInlines(paragraph.Inlines, heading.Inline, depth);
                    target.Add(paragraph);
                    break;
                }

                case ParagraphBlock paragraphBlock:
                {
                    var paragraph = new Paragraph { Tag = MarkdownTags.Paragraph };
                    AppendInlines(paragraph.Inlines, paragraphBlock.Inline, depth);
                    target.Add(paragraph);
                    break;
                }

                case ListBlock list:
                    target.Add(BuildList(list, depth));
                    break;

                case QuoteBlock quote:
                {
                    var section = new Section
                    {
                        Tag = MarkdownTags.Quote,
                        Padding = new Thickness(QuoteIndent, 0, 0, 0),
                        BorderThickness = new Thickness(2, 0, 0, 0),
                    };
                    AddBlocks(section.Blocks, quote, depth + 1);
                    target.Add(section);
                    break;
                }

                case ThematicBreakBlock:
                    // 空段落 + 一条底边框 = 横线；颜色由宿主补（见 ToFlowDocument 的注释）
                    target.Add(new Paragraph
                    {
                        Tag = MarkdownTags.Rule,
                        BorderThickness = new Thickness(0, 0, 0, 1),
                    });
                    break;

                // ⚠ Table 也是容器，必须排在 ContainerBlock 兜底之前
                case MdTable table:
                    target.Add(BuildTable(table, depth));
                    break;

                case HtmlBlock html:
                    // 开了 DisableHtml 之后基本见不到；这里再兜一道，保证"HTML 只会变成文字"
                    AddLiteralBlocks(target, LeafText(html), MarkdownTags.Html);
                    break;

                case ContainerBlock other:
                    // 扩展引入的未知容器：递归下去，尽量别丢内容
                    AddBlocks(target, other, depth + 1);
                    break;

                case LeafBlock leaf:
                    // 未知叶子：原文落地
                    AddLiteralBlocks(target, LeafText(leaf), MarkdownTags.Html);
                    break;
            }
        }
    }

    private static Paragraph BuildCodeBlock(LeafBlock code, string language)
    {
        var paragraph = new Paragraph
        {
            Tag = language.Length == 0 ? MarkdownTags.CodeBlock : MarkdownTags.CodeBlock + ":" + language,
            Padding = new Thickness(CodeIndent, 0, 0, 0),
        };

        AddPlainText(paragraph.Inlines, LeafText(code));
        return paragraph;
    }

    private static WpfList BuildList(ListBlock list, int depth)
    {
        var result = new WpfList
        {
            Tag = MarkdownTags.List,
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : BulletStyleFor(depth),
        };

        if (list.IsOrdered
            && int.TryParse(list.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)
            && start > 0)
        {
            result.StartIndex = start;
        }

        foreach (var item in list)
        {
            var listItem = new ListItem();
            if (item is ListItemBlock itemBlock)
            {
                AddBlocks(listItem.Blocks, itemBlock, depth + 1);
            }
            else
            {
                AddLiteralBlocks(listItem.Blocks, ContainerText(item), MarkdownTags.Html);
            }

            result.ListItems.Add(listItem);
        }

        return result;
    }

    private static Table BuildTable(MdTable table, int depth)
    {
        var result = new Table { Tag = MarkdownTags.Table, CellSpacing = 0 };

        var columnCount = table.ColumnDefinitions.Count;
        for (var i = 0; i < columnCount; i++)
        {
            result.Columns.Add(new TableColumn());
        }

        var group = new TableRowGroup();

        foreach (var block in table)
        {
            if (block is not MdTableRow tableRow)
            {
                continue;
            }

            var row = new TableRow();
            var column = 0;

            foreach (var block2 in tableRow)
            {
                if (block2 is not MdTableCell tableCell)
                {
                    continue;
                }

                var cell = new TableCell
                {
                    Tag = MarkdownTags.TableCell,
                    Padding = new Thickness(CellPadding, 2, CellPadding, 2),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                };

                if (tableRow.IsHeader)
                {
                    // 表头加粗是 Markdown 表格的语义（不是配色）
                    cell.FontWeight = FontWeights.Bold;
                }

                AddBlocks(cell.Blocks, tableCell, depth + 1);

                var alignment = AlignmentOf(table, column);
                if (alignment != TextAlignment.Left)
                {
                    foreach (var cellBlock in cell.Blocks)
                    {
                        cellBlock.TextAlignment = alignment;
                    }
                }

                row.Cells.Add(cell);
                column++;
            }

            group.Rows.Add(row);
        }

        result.RowGroups.Add(group);
        return result;
    }

    // ==================================================================
    //  文档模式：行内
    // ==================================================================

    private static void AppendInlines(InlineCollection target, MdContainerInline? container, int depth)
    {
        if (container is null || depth > MaxDepth)
        {
            return;
        }

        foreach (var inline in container)
        {
            AppendInline(target, inline, depth);
        }
    }

    private static void AppendInline(InlineCollection target, MdInline inline, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        switch (inline)
        {
            case MdLiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;

            case MdCodeInline code:
                target.Add(new Run(code.Content) { Tag = MarkdownTags.InlineCode });
                break;

            case MdEmphasisInline emphasis:
                target.Add(BuildEmphasis(emphasis, depth));
                break;

            case MdLinkInline link:
                target.Add(BuildLink(link, depth));
                break;

            case MdAutolinkInline autolink:
            {
                var hyperlink = new Hyperlink { Tag = MarkdownTags.Link };
                hyperlink.Inlines.Add(new Run(autolink.Url));
                var uri = SafeUri(autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url);
                if (uri is not null)
                {
                    hyperlink.NavigateUri = uri;
                }

                target.Add(hyperlink);
                break;
            }

            case MdLineBreakInline lineBreak:
                // 软换行按 CommonMark 就是一个空格；硬换行（行尾两个空格或反斜杠）才是真换行
                target.Add(lineBreak.IsHard ? new LineBreak() : new Run(" "));
                break;

            case MdHtmlInline html:
                // Markdig 1.x 的 HtmlInline 把整段标签放在 Tag 上（旧版叫 Content）
                target.Add(new Run(html.Tag ?? string.Empty) { Tag = MarkdownTags.Html });
                break;

            case MdContainerInline nested:
                // 扩展引入的未知容器：递归，别丢内容
                AppendInlines(target, nested, depth + 1);
                break;
        }
    }

    private static Span BuildEmphasis(MdEmphasisInline emphasis, int depth)
    {
        var span = new Span();
        var count = emphasis.DelimiterCount;

        switch (emphasis.DelimiterChar)
        {
            case '~':
                if (count >= 2)
                {
                    span.TextDecorations = TextDecorations.Strikethrough;
                }
                else
                {
                    span.BaselineAlignment = BaselineAlignment.Subscript;
                }

                break;

            case '+':
                if (count >= 2)
                {
                    span.TextDecorations = TextDecorations.Underline;
                }
                else
                {
                    span.BaselineAlignment = BaselineAlignment.Superscript;
                }

                break;

            case '=':
                // ==高亮== 需要一个底色，而"配色"不归渲染引擎管 → 只还原文字，不铺底色。
                // 想要高亮就自己按 Tag 样式化（这里不给 Tag，直接当普通文字）。
                break;

            default:
                if (count >= 2)
                {
                    span.FontWeight = FontWeights.Bold;
                }

                if (count % 2 == 1)
                {
                    span.FontStyle = FontStyles.Italic;
                }

                break;
        }

        AppendInlines(span.Inlines, emphasis, depth + 1);
        return span;
    }

    private static Inline BuildLink(MdLinkInline link, int depth)
    {
        if (link.IsImage)
        {
            // 图片不下载、不联网（既省事也不泄露 URL），只把 alt 文本画出来
            var image = new Span { Tag = MarkdownTags.Image };
            AppendInlines(image.Inlines, link, depth + 1);
            if (image.Inlines.Count == 0)
            {
                image.Inlines.Add(new Run(link.Url ?? string.Empty));
            }

            return image;
        }

        var hyperlink = new Hyperlink { Tag = MarkdownTags.Link };
        AppendInlines(hyperlink.Inlines, link, depth + 1);
        if (hyperlink.Inlines.Count == 0)
        {
            hyperlink.Inlines.Add(new Run(link.Url ?? string.Empty));
        }

        var uri = SafeUri(link.Url);
        if (uri is not null)
        {
            hyperlink.NavigateUri = uri;
        }

        return hyperlink;
    }

    /// <summary>只放行 http / https / mailto：别的协议（javascript:、file:、自定义 shell 协议…）不给导航目标，点了也没反应。</summary>
    private static Uri? SafeUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme switch
        {
            "http" or "https" or "mailto" => uri,
            _ => null,
        };
    }

    // ==================================================================
    //  行内模式：块级内容就地摊平
    // ==================================================================

    private static void AppendBlockInlines(List<Inline> sink, ContainerBlock container, int depth, string prefix)
    {
        if (depth > MaxDepth)
        {
            AddPrefixed(sink, prefix, ContainerText(container), MarkdownTags.Html, FontWeights.Normal);
            return;
        }

        foreach (var block in container)
        {
            switch (block)
            {
                case FencedCodeBlock fenced:
                    AddPrefixed(sink, prefix, LeafText(fenced), MarkdownTags.CodeBlock, FontWeights.Normal);
                    break;

                case CodeBlock code:
                    AddPrefixed(sink, prefix, LeafText(code), MarkdownTags.CodeBlock, FontWeights.Normal);
                    break;

                case HeadingBlock heading:
                {
                    var span = StartBlock(sink, prefix, MarkdownTags.HeadingTag(heading.Level), FontWeights.Bold);
                    AppendInlines(span.Inlines, heading.Inline, depth);
                    break;
                }

                case ParagraphBlock paragraph:
                {
                    var span = StartBlock(sink, prefix, MarkdownTags.Paragraph, FontWeights.Normal);
                    AppendInlines(span.Inlines, paragraph.Inline, depth);
                    break;
                }

                case ListBlock list:
                {
                    var index = 1;
                    foreach (var item in list)
                    {
                        var marker = list.IsOrdered
                            ? index.ToString(CultureInfo.InvariantCulture) + ". "
                            : "\u2022 ";
                        var childPrefix = prefix + new string(' ', depth * 2) + marker;

                        if (item is ListItemBlock itemBlock)
                        {
                            AppendBlockInlines(sink, itemBlock, depth + 1, childPrefix);
                        }
                        else
                        {
                            AddPrefixed(sink, childPrefix, ContainerText(item), MarkdownTags.List, FontWeights.Normal);
                        }

                        index++;
                    }

                    break;
                }

                case QuoteBlock quote:
                    AppendBlockInlines(sink, quote, depth + 1, prefix + "> ");
                    break;

                case ThematicBreakBlock:
                    AddPrefixed(sink, prefix, new string('\u2500', InlineRuleWidth), MarkdownTags.Rule, FontWeights.Normal);
                    break;

                case MdTable table:
                {
                    foreach (var tableBlock in table)
                    {
                        if (tableBlock is not MdTableRow row)
                        {
                            continue;
                        }

                        var span = StartBlock(sink, prefix, MarkdownTags.Table, FontWeights.Normal);
                        var first = true;
                        foreach (var cellBlock in row)
                        {
                            if (cellBlock is not MdTableCell cell)
                            {
                                continue;
                            }

                            if (!first)
                            {
                                span.Inlines.Add(new Run(" | "));
                            }

                            first = false;
                            var only = new List<Inline>();
                            AppendBlockInlines(only, cell, depth + 1, string.Empty);
                            foreach (var inline in only)
                            {
                                span.Inlines.Add(inline);
                            }
                        }

                        break;
                    }

                    break;
                }

                case HtmlBlock html:
                    AddPrefixed(sink, prefix, LeafText(html), MarkdownTags.Html, FontWeights.Normal);
                    break;

                case ContainerBlock other:
                    AppendBlockInlines(sink, other, depth + 1, prefix);
                    break;

                case LeafBlock leaf:
                    AddPrefixed(sink, prefix, LeafText(leaf), MarkdownTags.Html, FontWeights.Normal);
                    break;
            }
        }
    }

    /// <summary>开一个新"行"（前面补一个换行），返回往里塞行内内容的 Span。</summary>
    private static Span StartBlock(List<Inline> sink, string prefix, string tag, FontWeight weight)
    {
        if (sink.Count > 0)
        {
            sink.Add(new LineBreak());
        }

        var span = new Span { Tag = tag, FontWeight = weight };
        if (prefix.Length > 0)
        {
            span.Inlines.Add(new Run(prefix));
        }

        sink.Add(span);
        return span;
    }

    private static void AddPrefixed(List<Inline> sink, string prefix, string text, string tag, FontWeight weight)
    {
        var span = StartBlock(sink, prefix, tag, weight);
        AddPlainText(span.Inlines, text);
    }

    // ==================================================================
    //  共用小工具
    // ==================================================================

    /// <summary>把纯文本按行塞进行内集合：换行符 → <see cref="LineBreak"/>（控件流式追加增量时也用它）。</summary>
    internal static void AddPlainText(InlineCollection target, string text) => AddPlainText(target.Add, text);

    /// <summary>同上，但目标是 <see cref="List{T}"/>（行内模式用）。</summary>
    private static void AddPlainText(List<Inline> target, string text) => AddPlainText(target.Add, text);

    private static void AddPlainText(Action<Inline> add, string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                add(new LineBreak());
            }

            if (lines[i].Length > 0)
            {
                add(new Run(lines[i]));
            }
        }
    }

    private static void AddLiteralBlocks(BlockCollection target, string text, string tag)
    {
        if (text.Length == 0)
        {
            return;
        }

        var paragraph = new Paragraph { Tag = tag };
        AddPlainText(paragraph.Inlines, text);
        target.Add(paragraph);
    }

    /// <summary>叶子块的原文（代码块内容、HTML 原文…）：逐行取出，行尾统一成 <c>\n</c>。</summary>
    private static string LeafText(LeafBlock block)
    {
        var lines = block.Lines;
        if (lines.Lines is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var count = Math.Min(lines.Count, lines.Lines.Length);

        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(lines.Lines[i].Slice.ToString());
        }

        return builder.ToString();
    }

    /// <summary>容器块的原文（兜底用，尽量别丢东西）。</summary>
    private static string ContainerText(MdBlock? block)
    {
        if (block is null)
        {
            return string.Empty;
        }

        if (block is LeafBlock leaf)
        {
            return LeafText(leaf);
        }

        if (block is not ContainerBlock container)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var child in container)
        {
            var text = ContainerText(child);
            if (text.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(text);
        }

        return builder.ToString();
    }

    /// <summary>围栏的语言标记：<c>```csharp title=x</c> 里只取第一个词。</summary>
    private static string LanguageOf(FencedCodeBlock block)
    {
        var info = block.Info;
        if (string.IsNullOrWhiteSpace(info))
        {
            return string.Empty;
        }

        var trimmed = info.Trim();
        var space = trimmed.IndexOf(' ');
        var word = space < 0 ? trimmed : trimmed[..space];

        // 语言标记只用来做标记，不参与渲染 → 收成一个安全的小写单词
        return new string(word.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static TextMarkerStyle BulletStyleFor(int depth) => (depth % 3) switch
    {
        1 => TextMarkerStyle.Circle,
        2 => TextMarkerStyle.Square,
        _ => TextMarkerStyle.Disc,
    };

    private static TextAlignment AlignmentOf(MdTable table, int column)
    {
        if (column >= table.ColumnDefinitions.Count)
        {
            return TextAlignment.Left;
        }

        return table.ColumnDefinitions[column].Alignment switch
        {
            MdTableAlign.Center => TextAlignment.Center,
            MdTableAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
    }

}
