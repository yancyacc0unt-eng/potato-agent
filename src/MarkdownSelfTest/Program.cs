// Markdown 渲染层的离线自测 —— 不需要界面、不联网、不开窗口。
//
// 查的是三件事：
//   1) 语法转出来的【结构】对不对（标题层级、粗体、代码块语言、列表嵌套层数、表格行列…）；
//   2) 原始 HTML 真的只是文字（<script> / onerror / javascript: 链接一个都不许活）；
//   3) 畸形输入和流式中途截断【一律不许抛异常】—— 聊天窗口不能因为一条消息崩掉。
//
// 另外查一条本层的硬规矩：渲染出来的元素不许写死 Foreground / FontFamily / Background
// （颜色字体必须继承外层，用户换主题时里面要跟着变）。
//
// 跑法：
//   dotnet build build\MarkdownSelfTest.csproj -c Debug
//   dotnet run --project build\MarkdownSelfTest.csproj -c Debug
// 退出码：0 = 全过，1 = 有失败。

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PotatoAgent.Markdown.SelfTest;

internal static class Program
{
    private static int _checks;
    private static int _failures;

    [STAThread] // WPF 的 FlowDocument / RichTextBox 要 STA 线程
    private static int Main()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 输出被重定向时可能失败，不影响结论
        }

        Console.WriteLine("==================== PotatoAgent.Markdown 离线自测 ====================");
        Console.WriteLine("被测：MarkdownRenderer.ToFlowDocument / ToInlines + MarkdownViewer + ChatAutoScroll");
        Console.WriteLine();

        try
        {
            Test01Headings();
            Test02InlineStyles();
            Test03CodeBlocks();
            Test04Lists();
            Test05QuoteAndRule();
            Test06LinksAndImages();
            Test07Tables();
            Test08HtmlIsInert();
            Test09EdgeCases();
            Test10BigText();
            Test11Streaming();
            Test12ToInlines();
            Test13LayoutAndNoHardcodedStyle();
            Test14InsideChatList();
            Test15WheelScrollsChatList();
            Test16AutoScrollChatList();
            Test17TallMessageBottomReachable();
        }
        catch (Exception error)
        {
            // 走到这里说明有路径没被上面的用例包住（用例自身都会吞异常），算失败。
            Fail($"自测自身抛异常: {error.GetType().Name}: {Trim(error.Message)}");
        }

        return Report();
    }

    // ==================================================================
    //  1. 标题
    // ==================================================================

    private static void Test01Headings()
    {
        Section("1. 标题 1~6：层级与加粗");

        var document = MarkdownRenderer.ToFlowDocument("# H1\n\n## H2\n\n### H3\n\n#### H4\n\n##### H5\n\n###### H6\n");

        for (var level = 1; level <= 6; level++)
        {
            var block = FindBlock(document, MarkdownTags.HeadingTag(level));
            Check(block is Paragraph, $"H{level} 是一个段落，Tag = {MarkdownTags.HeadingTag(level)}");
            Check(block is not null && block.FontWeight == FontWeights.Bold, $"H{level} 是加粗的");
        }

        Check(DocumentText(document) == "H1\nH2\nH3\nH4\nH5\nH6", $"六个标题的文字都在、顺序没乱（实测 \"{Escape(DocumentText(document))}\"）");
    }

    // ==================================================================
    //  2. 行内样式
    // ==================================================================

    private static void Test02InlineStyles()
    {
        Section("2. 行内：粗体 / 斜体 / 删除线 / 行内代码");

        var document = MarkdownRenderer.ToFlowDocument("plain **bold** *italic* ~~strike~~ `code` ***both*** end\n");

        Check(DocumentText(document) == "plain bold italic strike code both end", $"标记被消化、文字一个不丢（实测 \"{Escape(DocumentText(document))}\"）");
        Check(AllInlines(document).Any(i => i is Span span && span.FontWeight == FontWeights.Bold && InlineText(span) == "bold"), "**粗体** → FontWeight = Bold");
        Check(AllInlines(document).Any(i => i is Span span && span.FontStyle == FontStyles.Italic && InlineText(span) == "italic"), "*斜体* → FontStyle = Italic");
        Check(AllInlines(document).Any(i => i is Span span && span.TextDecorations == TextDecorations.Strikethrough && InlineText(span) == "strike"), "~~删除线~~ → Strikethrough");
        Check(AllInlines(document).Any(i => i is Run run && Equals(run.Tag, MarkdownTags.InlineCode) && run.Text == "code"), "`行内代码` → Run，Tag = md:code-inline");
        Check(AllInlines(document).Any(i => i is Span span && span.FontWeight == FontWeights.Bold && span.FontStyle == FontStyles.Italic && InlineText(span) == "both"), "***粗斜*** → 同时 Bold + Italic");
    }

    // ==================================================================
    //  3. 代码块
    // ==================================================================

    private static void Test03CodeBlocks()
    {
        Section("3. 围栏代码块（含语言标记）/ 无语言围栏 / 缩进代码块");

        var document = MarkdownRenderer.ToFlowDocument("```csharp\nvar a = 1;\nvar b = 2;\n```\n");
        var block = FindBlock(document, MarkdownTags.CodeBlock + ":csharp");

        Check(block is Paragraph, "围栏代码块 → 段落，Tag = md:code-block:csharp（语言标记保住了）");
        Check(!DocumentText(document).Contains("```", StringComparison.Ordinal), "围栏本身没被画出来");
        Check(DocumentText(document) == "var a = 1;\nvar b = 2;", $"代码内容逐行保留（实测 \"{Escape(DocumentText(document))}\"）");

        var noLanguage = MarkdownRenderer.ToFlowDocument("```\nplain fence\n```\n");
        Check(FindBlock(noLanguage, MarkdownTags.CodeBlock) is Paragraph, "没写语言的围栏 → Tag = md:code-block（没有语言后缀）");

        var indented = MarkdownRenderer.ToFlowDocument("    indented code line\n");
        Check(DocumentText(indented).Contains("indented code line", StringComparison.Ordinal), "四空格缩进的代码块也认");

        var weird = MarkdownRenderer.ToFlowDocument("```cs title=\"x\" extra\ncode here\n```\n");
        Check(FindBlock(weird, MarkdownTags.CodeBlock + ":cs") is Paragraph, "带附加属性的围栏 → 只取语言那一个词（md:code-block:cs）");
    }

    // ==================================================================
    //  4. 列表
    // ==================================================================

    private static void Test04Lists()
    {
        Section("4. 列表：有序 / 无序 / 嵌套层数");

        var document = MarkdownRenderer.ToFlowDocument("- a\n- b\n  - b1\n    - b11\n- c\n");
        var top = FirstList(document);

        Check(top is not null && top.ListItems.Count == 3, $"无序列表顶层 3 项（实测 {top?.ListItems.Count}）");
        Check(top is not null && top.MarkerStyle == TextMarkerStyle.Disc, "顶层项目符号 = Disc");
        Check(top is not null && ListDepth(top) == 3, $"嵌套层数 = 3（实测 {(top is null ? 0 : ListDepth(top))}）");
        Check(DocumentText(document) == "a\nb\nb1\nb11\nc", $"文字顺序对（实测 \"{Escape(DocumentText(document))}\"）");

        var ordered = MarkdownRenderer.ToFlowDocument("5. five\n6. six\n");
        var list = FirstList(ordered);
        Check(list is not null && list.MarkerStyle == TextMarkerStyle.Decimal, "有序列表 MarkerStyle = Decimal");
        Check(list is not null && list.StartIndex == 5, $"起始编号 5 保住了（实测 {list?.StartIndex}）");

        var taskLike = MarkdownRenderer.ToFlowDocument("* one\n* two\n");
        Check(FirstList(taskLike) is not null, "* 号写的无序列表也认");
    }

    // ==================================================================
    //  5. 引用 / 分割线
    // ==================================================================

    private static void Test05QuoteAndRule()
    {
        Section("5. 引用块 / 分割线");

        var document = MarkdownRenderer.ToFlowDocument("> quoted line\n> second line\n\n---\n");

        var quote = FindBlock(document, MarkdownTags.Quote);
        Check(quote is Section, "引用 → Section，Tag = md:quote");
        Check(quote is Section section && section.BorderThickness.Left > 0, "引用左边有竖线厚度（颜色由宿主按继承的 Foreground 补）");
        Check(DocumentText(document).Contains("quoted line", StringComparison.Ordinal), "引用里的文字在");

        var rule = FindBlock(document, MarkdownTags.Rule);
        Check(rule is Paragraph, "--- → 空段落，Tag = md:rule");
        Check(rule is not null && rule.BorderThickness.Bottom > 0, "分割线有底边框厚度（不画字符，靠边框）");
    }

    // ==================================================================
    //  6. 链接 / 图片
    // ==================================================================

    private static void Test06LinksAndImages()
    {
        Section("6. 链接（含协议白名单）/ 图片");

        var document = MarkdownRenderer.ToFlowDocument("[site](https://example.com/a) and <https://auto.example> and [bad](javascript:alert(1)) and [local](file:///C:/x.txt)\n");
        var links = AllInlines(document).OfType<Hyperlink>().ToList();

        Check(links.Count == 4, $"四个链接都渲染成 Hyperlink（实测 {links.Count}）");
        Check(links.Count(link => link.NavigateUri is not null) == 2, $"只有 http/https 的两个真的可点（实测 {links.Count(link => link.NavigateUri is not null)}）");
        Check(links.All(link => link.NavigateUri is null || link.NavigateUri.Scheme is "http" or "https" or "mailto"), "没有任何链接指向 http/https/mailto 之外的协议（javascript: / file: 都被挡了）");
        Check(links.Any(link => link.NavigateUri?.AbsoluteUri == "https://example.com/a"), "https://example.com/a 拿到了 NavigateUri");

        var image = MarkdownRenderer.ToFlowDocument("![alt text](https://example.com/x.png)\n");
        Check(!AllInlines(image).OfType<Hyperlink>().Any(), "图片不会变成链接（图片不下载、不联网）");
        Check(AllInlines(image).Any(i => Equals(i.Tag, MarkdownTags.Image)), "图片 → Tag = md:image");
        Check(DocumentText(image) == "alt text", $"图片只画 alt 文字（实测 \"{Escape(DocumentText(image))}\"）");
    }

    // ==================================================================
    //  7. 表格
    // ==================================================================

    private static void Test07Tables()
    {
        Section("7. 表格（Markdig 管道表扩展）");

        var document = MarkdownRenderer.ToFlowDocument("| left | right |\n| :--- | ---: |\n| a | b |\n");
        var table = FlattenBlocks(document.Blocks).OfType<Table>().FirstOrDefault();

        Check(table is not null, "管道表 → WPF Table（Tag = md:table）");

        if (table is null)
        {
            return;
        }

        var rows = table.RowGroups.SelectMany(group => group.Rows).ToList();
        Check(rows.Count == 2, $"两行：表头 + 一行数据（实测 {rows.Count}）");
        Check(rows[0].Cells.Count == 2, $"两列（实测 {rows[0].Cells.Count}）");
        Check(rows[0].Cells[0].FontWeight == FontWeights.Bold, "表头加粗");
        Check(rows[1].Cells[1].Blocks.FirstBlock?.TextAlignment == TextAlignment.Right, "---: 的右对齐生效");
        Check(DocumentText(document) == "left\nright\na\nb", $"单元格文字都对（实测 \"{Escape(DocumentText(document))}\"）");
        Check(rows.SelectMany(row => row.Cells).Count(cell => Equals(cell.Tag, MarkdownTags.TableCell)) == 4, "四个单元格都挂了 md:table-cell 标记");
    }

    // ==================================================================
    //  8. 原始 HTML 不执行
    // ==================================================================

    private static void Test08HtmlIsInert()
    {
        Section("8. 原始 HTML 一律当纯文本（防注入）");

        const string Source = "<script>alert('x')</script>\n\n<b>bold</b> and <img src=x onerror=alert(1)>\n\n<a href=\"javascript:alert(1)\">click me</a>\n";
        var document = MarkdownRenderer.ToFlowDocument(Source);
        var text = DocumentText(document);

        Check(text.Contains("<script>alert('x')</script>", StringComparison.Ordinal), $"<script> 原样当文字画出来（实测 \"{Escape(text)}\"）");
        Check(text.Contains("onerror", StringComparison.Ordinal), "<img onerror=...> 也只是文字");
        Check(!AllInlines(document).OfType<Hyperlink>().Any(), "HTML 里的 <a> 没有变成可点链接（防注入的关键一条）");
        Check(FlattenBlocks(document.Blocks).All(block => !block.GetType().Name.Contains("Html", StringComparison.Ordinal)), "文档里没有 Markdig 的 Html 节点（DisableHtml 生效）");
        Check(AllInlines(document).All(inline => !inline.GetType().Name.Contains("Html", StringComparison.Ordinal)), "行内也没有 Markdig 的 HtmlInline 节点");
    }

    // ==================================================================
    //  9. 边界与畸形输入
    // ==================================================================

    private static void Test09EdgeCases()
    {
        Section("9. 边界与畸形输入：一律不许抛异常");

        // 40 层以上的嵌套是故意给的：渲染器自己有 32 层的硬上限（保护栈），要给到它。
        var nestedLists = string.Concat(Enumerable.Range(0, 40).Select(level => new string(' ', level * 2) + "- x\n"));
        var nestedQuotes = string.Concat(Enumerable.Repeat("> ", 60)) + "deep";

        var cases = new (string What, string? Markdown)[]
        {
            ("null", null),
            ("空串", string.Empty),
            ("只有空白", "   \n\t  \n"),
            ("只有换行", "\n\n\n"),
            ("没闭合的代码围栏", "```csharp\nvar a = 1;\nstill code\n"),
            ("只敲了一半的围栏", "```"),
            ("没闭合的粗体", "**bold never closed"),
            ("没闭合的斜体与删除线", "*it ~~st"),
            ("没闭合的行内代码", "`code never closed"),
            ("没闭合的链接", "[a](http://example.com"),
            ("没闭合的图片", "![img]("),
            ("畸形表格", "|||\n|---|\n|||\n"),
            ("一坨符号", "\\\\ \\* \\_ *** --- ### >>>> |||| ~~~~"),
            ("奇怪字符（NUL / 替换符 / emoji / 零宽 / RTL）", "\u0000\ufffd\ud83d\ude00\u200b\u202e abc ***"),
            ("超长单行（2 万字符、没有空格）", new string('x', 20000)),
            ("40 层嵌套列表", nestedLists),
            ("60 层嵌套引用", nestedQuotes),
        };

        foreach (var (what, markdown) in cases)
        {
            var document = Attempt($"ToFlowDocument 不抛：{what}", () => MarkdownRenderer.ToFlowDocument(markdown));
            _ = Attempt($"ToInlines 不抛：{what}", () => MarkdownRenderer.ToInlines(markdown).ToList());

            if (document is not null && what == "没闭合的代码围栏")
            {
                Check(DocumentText(document).Contains("still code", StringComparison.Ordinal), "没闭合的围栏：围栏里的内容一个字没丢");
            }

            if (document is not null && what == "没闭合的粗体")
            {
                Check(DocumentText(document).Contains("bold never closed", StringComparison.Ordinal), "没闭合的粗体：文字还在");
            }
        }

        Check(DocumentText(MarkdownRenderer.ToFlowDocument("   \n\n  ")).Length == 0, "只有空白 → 什么都不画（不是一堆空行）");

        var deepDocument = MarkdownRenderer.ToFlowDocument(nestedQuotes);
        Check(FlattenBlocks(deepDocument.Blocks).Any(block => Equals(block.Tag, MarkdownTags.Quote)), "超深嵌套：引用框一层层画出来了，没抛异常");

        // 备注（实测出来的事实，写在这里免得下次又当成 bug 查一遍）：
        // Markdig 自己只认 32 层块级嵌套。第 33 层往后它在 AST 阶段就把内容丢了
        // （60 层 "> " 的 AST 末尾是一个内容为空的 ParagraphBlock），跟本渲染器无关。
        // 本层另有一道 32 层的递归上限，防的是栈溢出（栈溢出 catch 不住）。
    }

    // ==================================================================
    //  10. 大文本
    // ==================================================================

    private static void Test10BigText()
    {
        Section("10. 几万字的大回答");

        var builder = new StringBuilder();
        for (var i = 0; i < 1400; i++)
        {
            builder.Append("line ").Append(i).Append(" **bold** `code` [link](https://example.com)\n\n");
        }

        var markdown = builder.ToString();
        Check(markdown.Length > 50000, $"样本 {markdown.Length} 个字符");

        var stopwatch = Stopwatch.StartNew();
        var document = MarkdownRenderer.ToFlowDocument(markdown);
        stopwatch.Stop();
        Check(DocumentText(document).Contains("line 1399", StringComparison.Ordinal), $"整篇渲染出来了（{stopwatch.ElapsedMilliseconds} ms）");

        var stopwatch2 = Stopwatch.StartNew();
        var inlines = MarkdownRenderer.ToInlines(markdown).ToList();
        stopwatch2.Stop();
        Check(inlines.Count > 0, $"行内模式也扛住了（{inlines.Count} 个 Inline，{stopwatch2.ElapsedMilliseconds} ms）");
    }

    // ==================================================================
    //  11. 流式约定
    // ==================================================================

    private static void Test11Streaming()
    {
        Section("11. 流式约定：IsStreaming=true 走纯文本快路径，false 才渲染 Markdown");

        var viewer = new MarkdownViewer();
        Check(viewer.Content is RichTextBox, "MarkdownViewer 在无窗口环境里也建得起来");

        viewer.Markdown = "**bold** and `code`";
        viewer.IsStreaming = true;
        Check(HostText(viewer) == "**bold** and `code`", $"流式中：整篇当纯文本，标记原样显示（实测 \"{Escape(HostText(viewer))}\"）");
        Check(!ViewerInlines(viewer).Any(i => i is Span span && span.FontWeight == FontWeights.Bold), "流式中：没有解析出粗体（证明走的是快路径）");
        Check(!ViewerInlines(viewer).Any(i => Equals(i.Tag, MarkdownTags.InlineCode)), "流式中：没有解析出行内代码");

        viewer.IsStreaming = false;
        Check(HostText(viewer) == "bold and code", $"说完之后：标记被消化掉（实测 \"{Escape(HostText(viewer))}\"）");
        Check(ViewerInlines(viewer).Any(i => i is Span span && span.FontWeight == FontWeights.Bold), "说完之后：粗体出来了");
        Check(ViewerInlines(viewer).Any(i => Equals(i.Tag, MarkdownTags.InlineCode)), "说完之后：行内代码出来了");

        // 逐字吐字：每一步都只是"末尾多了几个字"，控件应当只追加、不重建
        var full = "```csharp\nvar a = 1;\n```\n\n**bold** tail";
        var stream = new MarkdownViewer { IsStreaming = true, Markdown = string.Empty };
        NoThrow($"逐字吐字 {full.Length} 次不崩", () =>
        {
            for (var i = 1; i <= full.Length; i++)
            {
                stream.Markdown = full[..i];
            }
        });
        Check(HostText(stream) == full, $"逐字吐完，文本一字不差（实测 \"{Escape(HostText(stream))}\"）");

        stream.IsStreaming = false;
        Check(FindBlock(DocumentOf(stream), MarkdownTags.CodeBlock + ":csharp") is Paragraph, "收尾渲染：代码块 + 语言标记都出来了");

        // 中途截断 / 长度回退 / 流式与非流式来回切
        var truncated = new MarkdownViewer { IsStreaming = true, Markdown = "```csharp\nvar a = " };
        Check(HostText(truncated).Contains("var a =", StringComparison.Ordinal), "半截围栏（流式中途截断）也画得出来");
        NoThrow("截断后文本变短 → 不崩", () => truncated.Markdown = "```");
        NoThrow("截断中途直接切完整渲染 → 不崩", () => truncated.IsStreaming = false);
        NoThrow("再切回流式并继续追加 → 不崩", () =>
        {
            truncated.IsStreaming = true;
            truncated.Markdown = "```csharp\nvar a = 1;";
            truncated.Markdown = "```csharp\nvar a = 1;\n```";
        });
        NoThrow("空文本反复切换 → 不崩", () =>
        {
            truncated.Markdown = string.Empty;
            truncated.IsStreaming = false;
            truncated.Markdown = string.Empty;
        });
    }

    // ==================================================================
    //  12. ToInlines
    // ==================================================================

    private static void Test12ToInlines()
    {
        Section("12. ToInlines：行内场景（直接塞 TextBlock）");

        var inlines = MarkdownRenderer.ToInlines("**bold** `code` [link](https://example.com)\n\n- item one\n- item two\n").ToList();
        var flat = Flatten(inlines).ToList();

        Check(inlines.Count > 0, $"非空（{inlines.Count} 个顶层 Inline、摊平后 {flat.Count} 个）");
        Check(flat.Any(i => i is Span span && span.FontWeight == FontWeights.Bold), "粗体还在");
        Check(flat.Any(i => Equals(i.Tag, MarkdownTags.InlineCode)), "行内代码还在");
        Check(flat.OfType<Hyperlink>().Any(link => link.NavigateUri is not null), "链接还在，而且可点");
        Check(flat.Any(i => i is Run run && run.Text.StartsWith('\u2022')), "列表项带了 • 前缀");
        Check(inlines.OfType<LineBreak>().Any(), "块与块之间有换行");
        Check(!MarkdownRenderer.ToInlines(null).Any(), "null → 空序列");
        Check(!MarkdownRenderer.ToInlines("   \n\n ").Any(), "只有空白 → 空序列");

        var block = new TextBlock();
        NoThrow("能直接塞进 TextBlock.Inlines", () =>
        {
            foreach (var inline in inlines)
            {
                block.Inlines.Add(inline);
            }
        });
        Check(block.Inlines.Count == inlines.Count, "TextBlock 把 Inline 全收下了");
    }

    // ==================================================================
    //  13. 真排版 + 不写死字体颜色
    // ==================================================================

    private static void Test13LayoutAndNoHardcodedStyle()
    {
        Section("13. 真排版（不是 0 高）+ 不写死字体 / 颜色");

        var longViewer = new MarkdownViewer { FontSize = 20, Markdown = "# Title\n\nsome text here\n\n- a\n- b\n- c\n\n---\n" };
        var longHeight = MeasureHeight(longViewer);
        var shortViewer = new MarkdownViewer { FontSize = 20, Markdown = "one" };
        var shortHeight = MeasureHeight(shortViewer);

        Check(longHeight > 0, $"控件真的有高度（{longHeight:0.#} px）");
        Check(longHeight > shortHeight, $"内容多了高度就长（{shortHeight:0.#} → {longHeight:0.#} px）");

        var heading = FindBlock(DocumentOf(longViewer), MarkdownTags.HeadingTag(1));
        Check(heading is not null && heading.FontSize > longViewer.FontSize, $"标题字号按外层的 FontSize 相对放大（外层 20 → 标题 {(heading?.FontSize ?? 0):0.#}）");

        var rule = FindBlock(DocumentOf(longViewer), MarkdownTags.Rule);
        Check(rule is not null && ReferenceEquals(rule.BorderBrush, longViewer.Foreground), "分割线的颜色 = 控件继承到的 Foreground（不是写死的某个颜色）");

        var sample = MarkdownRenderer.ToFlowDocument("# h\n\ntext **b** `c`\n\n> q\n\n---\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n");
        var hardcoded = CountLocalValues(sample, TextElement.ForegroundProperty)
            + CountLocalValues(sample, TextElement.FontFamilyProperty)
            + CountLocalValues(sample, TextElement.BackgroundProperty);
        Check(hardcoded == 0, $"渲染结果里没有一个写死的 Foreground/FontFamily/Background（实测 {hardcoded} 处）");

        var codeViewer = new MarkdownViewer { CodeFontFamily = new FontFamily("Consolas"), Markdown = "```csharp\nvar a = 1;\n```\n" };
        var codeBlock = FindBlock(DocumentOf(codeViewer), MarkdownTags.CodeBlock + ":csharp");
        Check(codeBlock is not null && codeBlock.FontFamily.Source == "Consolas", "CodeFontFamily 设了就生效（默认 null = 跟随外层，不干预）");

        var defaultViewer = new MarkdownViewer { Markdown = "`inline`\n" };
        Check(CountLocalValues(DocumentOf(defaultViewer), TextElement.FontFamilyProperty) == 0, "没设 CodeFontFamily 时，代码字体也是继承的（渲染器不写死字体）");
    }

    // ==================================================================
    //  14. 塞进聊天列表的真实形状
    // ==================================================================

    /// <summary>
    /// MainWindow.xaml 里聊天区就是"ListBox + DataTemplate 里放 MarkdownViewer"。
    /// 这里照那个形状搭一份（同样的绑定写法），量一次布局：
    /// 行容器有没有生成、行有没有高度、模板里的控件有没有真的把 Markdown 画出来。
    /// 全程无窗口、不联网、不发任何请求。
    /// </summary>
    private static void Test14InsideChatList()
    {
        Section("14. 塞进 ListBox（聊天列表的真实形状）也画得出来");

        const string Xaml = """
            <ListBox xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:md="clr-namespace:PotatoAgent.Markdown;assembly=PotatoAgent.Markdown"
                     Width="600" Height="200"
                     HorizontalContentAlignment="Stretch">
              <ListBox.ItemTemplate>
                <DataTemplate>
                  <md:MarkdownViewer Markdown="{Binding}" IsStreaming="False" />
                </DataTemplate>
              </ListBox.ItemTemplate>
            </ListBox>
            """;

        ListBox? list = null;
        NoThrow("XAML 里认得 <md:MarkdownViewer>（和 MainWindow.xaml 一样的写法）", () => list = (ListBox)System.Windows.Markup.XamlReader.Parse(Xaml));

        if (list is null)
        {
            return;
        }

        list.ItemsSource = new[]
        {
            "# Title\n\nsome **bold** text with a [link](https://example.com)\n\n- one\n- two\n",
            "short line",
        };

        NoThrow("ListBox 无窗口排版一次", () =>
        {
            list.Measure(new Size(600, 200));
            list.Arrange(new Rect(0, 0, 600, 200));
            list.UpdateLayout();
        });

        var first = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
        Check(first is not null, "第一行的容器生成了（ListBoxItem）");
        Check(first is not null && first.ActualHeight > 0, $"第一行有实际高度（实测 {(first?.ActualHeight ?? 0):0.#} px）");

        var viewer = first is null ? null : FindViewer(first);
        Check(viewer is not null, "行里真的是 MarkdownViewer（DataTemplate 实例化成功）");
        Check(viewer is not null && FindBlock(DocumentOf(viewer), MarkdownTags.HeadingTag(1)) is Paragraph, "行里的控件真把 Markdown 渲染成了标题");

        var second = list.ItemContainerGenerator.ContainerFromIndex(1) as ListBoxItem;
        var firstHeight = first?.ActualHeight ?? 0;
        var secondHeight = second?.ActualHeight ?? 0;
        Check(second is not null && secondHeight < firstHeight, $"长的那行比短的那行高（长 {firstHeight:0.#} px / 短 {(second is null ? "没生成容器" : secondHeight.ToString("0.#") + " px")}）");

        // 实测记录：不写 HorizontalContentAlignment="Stretch" 的话，列表项会拿"无限宽"量子项，
        // 里面折行的 RichTextBox 直接退化成 10px 宽的一条缝（那一版实测行高 671.5 px、文字全是竖着的折行）。
        // 这一条就是它的回归测试。
        var unbounded = new MarkdownViewer { Markdown = "a line of text with a [link](https://example.com)\n" };
        unbounded.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Check(unbounded.DesiredSize.Width >= 300, $"被无限宽度测量时不会缩成一条缝（实测 {unbounded.DesiredSize.Width:0.#} px 宽）");
    }

    // ==================================================================
    //  15. 滚轮必须能滚外层列表
    // ==================================================================

    /// <summary>
    /// 用户报的 bug 里那条"鼠标停在消息上滚不动"。这里按 MainWindow.xaml 的真实形状搭一份列表
    /// （ListBox + DataTemplate 里 MarkdownViewer），对着消息正文里的 RichTextBox 抛一个 MouseWheel，
    /// 量外层 ScrollViewer 的 VerticalOffset。
    /// </summary>
    /// <remarks>
    /// 实测记录（2026-09-21）：改之前这条<b>也是通的</b>（外层偏移 0 → 48 px）——
    /// RichTextBox 并没有把滚轮吃掉，所以这不是"滚不到底部"的根因（根因见第 17 节）。
    /// 这条测试留着，是为了把"转发 + 不越权 + 不吃横向"这几条约定钉住。
    /// </remarks>
    private static void Test15WheelScrollsChatList()
    {
        Section("15. 滚轮转发：停在消息正文上也要能滚外层聊天列表");

        var (list, viewer, lines) = BuildChatList(shortCount: 30, lastText: "last message\n", autoScroll: false);

        viewer.ScrollToTop();
        Pump();
        var start = viewer.VerticalOffset;

        var host = HostAt(list, 0);
        Check(host is not null, "第一条消息里找得到那个 RichTextBox（鼠标就停在它上面）");

        if (host is null)
        {
            return;
        }

        // 记录"真正冒泡到列表"的事件，检查转发出去的增量有没有被改过
        var deltas = new List<int>();
        list.AddHandler(UIElement.MouseWheelEvent, new MouseWheelEventHandler((_, e) => deltas.Add(e.Delta)), true);

        var wheel = RaiseWheel(host, -120);
        Pump();
        Check(viewer.VerticalOffset > start, $"滚轮(-120)停在消息正文上：外层偏移 {start:0.#} → {viewer.VerticalOffset:0.#} px");
        Check(wheel.Handled, "事件被 MarkdownViewer 接住转发（没被里面的 RichTextBox 吃掉）");
        Check(deltas.Count == 1 && deltas[0] == -120, $"转发出去的增量原样不变（外层收到 [{string.Join(",", deltas)}]）");
        Check(Math.Abs(viewer.HorizontalOffset) < 0.01, $"没连累横向滚动（HorizontalOffset = {viewer.HorizontalOffset:0.#}）");

        // 更深的源头（RichTextBox 模板里那个 PART_ContentHost）抛出来也得一样
        var start2 = viewer.VerticalOffset;
        var inner = host.Template.FindName("PART_ContentHost", host) as UIElement;
        RaiseWheel(inner ?? host, -120);
        Pump();
        Check(viewer.VerticalOffset > start2, $"从更深一层（内层 ScrollViewer）抛：{start2:0.#} → {viewer.VerticalOffset:0.#} px");

        // 反证：已经在顶部再往上滚，不许越界
        viewer.ScrollToTop();
        Pump();
        RaiseWheel(host, 120);
        Pump();
        Check(viewer.VerticalOffset is >= 0 and < 0.01, $"已经在顶部再往上滚：偏移仍是 {viewer.VerticalOffset:0.#}（不越界）");

        // 反证：内层 RichTextBox 自己滚得动的时候，滚轮不许被抢走（将来把滚动条改成 Auto 的情形）
        var standalone = new MarkdownViewer { FontSize = 12, Markdown = BigMarkdown(40) };
        var innerHost = (RichTextBox)standalone.Content;
        innerHost.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        var frame = new Border { Width = 400, Height = 60, Child = standalone };
        frame.Measure(new Size(400, 60));
        frame.Arrange(new Rect(0, 0, 400, 60));
        frame.UpdateLayout();

        Check(
            innerHost.ExtentHeight - innerHost.ViewportHeight > 0,
            $"构造出「内层能滚」的场景（可滚 {innerHost.ExtentHeight - innerHost.ViewportHeight:0.#} px）");

        var deep = innerHost.Template.FindName("PART_ContentHost", innerHost) as UIElement ?? innerHost;
        RaiseWheel(deep, -120);
        Pump();
        Check(innerHost.VerticalOffset > 0, $"内层自己能滚时转发不越权：RichTextBox 自己滚到 {innerHost.VerticalOffset:0.#} px");
    }

    // ==================================================================
    //  16. 自动跟到底
    // ==================================================================

    /// <summary>
    /// ChatAutoScroll 附加行为：追加内容时贴着底就自动跟、用户翻历史时不抢、并且有节流。
    /// 全程无窗口；节流间隔设成 0 让行为同步执行，免得等计时器。
    /// </summary>
    private static void Test16AutoScrollChatList()
    {
        Section("16. 自动跟到底（ChatAutoScroll 附加行为）");

        Check(
            ChatAutoScroll.DefaultThrottleInterval >= TimeSpan.FromMilliseconds(60)
            && ChatAutoScroll.DefaultThrottleInterval <= TimeSpan.FromMilliseconds(100),
            $"默认节流间隔 {ChatAutoScroll.DefaultThrottleInterval.TotalMilliseconds:0} ms（要求 60~100 ms）");

        // ---------- 对照组：不挂行为，追加内容不会自己跟到底 ----------
        var (control, controlViewer, controlLines) = BuildChatList(shortCount: 30, lastText: "short\n", autoScroll: false);
        ScrollToNearBottom(controlViewer, 10);
        var controlBefore = controlViewer.VerticalOffset;
        AppendToLast(control, controlLines, "\n\n又追加了一大段文字，把这条消息撑得更高。\n");
        var controlGap = controlViewer.ScrollableHeight - controlViewer.VerticalOffset;
        Check(controlGap > 20, $"对照（没挂行为）：追加后距底 {controlGap:0.#} px（偏移 {controlBefore:0.#} → {controlViewer.VerticalOffset:0.#}），说明这活儿不是 WPF 白送的");

        // ---------- 挂上行为：贴底附近追加 → 跟到底 ----------
        var (list, viewer, lines) = BuildChatList(shortCount: 30, lastText: "short\n", autoScroll: true);
        Check(viewer.ScrollableHeight > 0, $"列表本身是可滚的（ScrollableHeight = {viewer.ScrollableHeight:0.#} px）");

        ScrollToNearBottom(viewer, 10);
        AppendToLast(list, lines, "\n\n又追加了一大段文字，把这条消息撑得更高。\n");
        var gap = viewer.ScrollableHeight - viewer.VerticalOffset;
        Check(gap <= 1, $"贴底附近追加内容 → 自动跟到底（距底 {gap:0.#} px）");

        // ---------- 反证：用户往上翻历史时，不许把他拉回去 ----------
        ScrollToNearBottom(viewer, 10);
        viewer.ScrollToTop();
        Pump();
        var readingOffset = viewer.VerticalOffset;
        for (var i = 0; i < 3; i++)
        {
            AppendToLast(list, lines, $"\n\n第 {i + 1} 段新的内容，用户正在上面看历史。\n");
        }

        Check(
            Math.Abs(viewer.VerticalOffset - readingOffset) < 1,
            $"反证：用户在最上面看历史时追加 3 段 → 偏移没被拉走（{readingOffset:0.#} → {viewer.VerticalOffset:0.#}）");

        // ---------- 反证：用户滚回底部之后，跟随要恢复 ----------
        ScrollToNearBottom(viewer, 0);
        AppendToLast(list, lines, "\n\n用户又回到底部了，这段应该跟得上。\n");
        Check(
            viewer.ScrollableHeight - viewer.VerticalOffset <= 1,
            $"用户回到底部后跟随恢复（距底 {viewer.ScrollableHeight - viewer.VerticalOffset:0.#} px）");

        // ---------- 节流：一次追加风暴里只真正滚一次 ----------
        var (burstList, burstViewer, burstLines) = BuildChatList(
            shortCount: 30, lastText: "short\n", autoScroll: true, throttle: TimeSpan.FromSeconds(10));
        ScrollToNearBottom(burstViewer, 10);

        // 每一块都单独起一行，保证"每次追加都真的长高"（否则文字没折行，内容根本没变化，
        // 不滚才是对的 —— 这一点本身也说明本行为是"按内容高度"办事，不是瞎滚）。
        var appliedBefore = ChatAutoScroll.GetAppliedScrollCount(burstList);
        for (var i = 0; i < 20; i++)
        {
            AppendToLast(burstList, burstLines, $"流式第 {i} 块文字。\n\n");
        }

        var throttled = ChatAutoScroll.GetAppliedScrollCount(burstList) - appliedBefore;
        Check(throttled is >= 1 and <= 2, $"节流（10 s 窗口）：一次追加 20 块，只真正滚了 {throttled} 次");

        // 同一场风暴，把节流关掉 → 每次追加都滚（证明上面那个 1 是节流压出来的）
        var (fastList, fastViewer, fastLines) = BuildChatList(
            shortCount: 30, lastText: "short\n", autoScroll: true, throttle: TimeSpan.Zero);
        ScrollToNearBottom(fastViewer, 10);

        var fastBefore = ChatAutoScroll.GetAppliedScrollCount(fastList);
        for (var i = 0; i < 20; i++)
        {
            AppendToLast(fastList, fastLines, $"流式第 {i} 块文字。\n\n");
        }

        var unthrottled = ChatAutoScroll.GetAppliedScrollCount(fastList) - fastBefore;
        Check(unthrottled >= 15, $"关掉节流（0 ms）：同样 20 块滚了 {unthrottled} 次（每次都跟）");

        // ---------- 可复用：挂在普通 ScrollViewer 上也一样 ----------
        var content = new StackPanel();
        var scroll = new ScrollViewer
        {
            Width = 400,
            Height = 200,
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        for (var i = 0; i < 20; i++)
        {
            content.Children.Add(new Border { Height = 40, Child = new TextBlock { Text = $"line {i}" } });
        }

        Layout(scroll);
        ChatAutoScroll.SetThrottleInterval(scroll, TimeSpan.Zero);
        ChatAutoScroll.SetIsEnabled(scroll, true);

        ScrollToNearBottom(scroll, 10);
        content.Children.Add(new Border { Height = 40 });
        Layout(scroll);
        Pump();
        var scrollGap = scroll.ScrollableHeight - scroll.VerticalOffset;
        Check(scrollGap <= 1, $"同一个行为挂在普通 ScrollViewer 上也能跟到底（距底 {scrollGap:0.#} px）");
    }

    // ==================================================================
    //  17. 一条比视口高的消息，必须能滚到它的底
    // ==================================================================

    /// <summary>
    /// 2026-09-21 那个"滚动不到底部"的真正根因：<c>ListBox</c> 默认按条滚动
    /// （<c>ScrollViewer.CanContentScroll=True</c>），<c>ExtentHeight</c> 的单位是"条"，
    /// 于是一条比视口高的消息，滚到底也只是把它<b>顶</b>到视口上沿 —— 下半截永远看不见。
    /// MainWindow.xaml 里那行 <c>ScrollViewer.CanContentScroll="False"</c> 就是治它的，这条是它的回归测试。
    /// </summary>
    private static void Test17TallMessageBottomReachable()
    {
        Section("17. 长消息（Markdown 渲染出来比视口高）必须能滚到它的底");

        var (itemList, itemViewer, _) = BuildChatList(
            shortCount: 6, lastText: BigMarkdown(40), autoScroll: false, pixelScroll: false);
        itemViewer.ScrollToEnd();
        Pump();
        var itemHidden = BottomHidden(itemList, itemViewer);
        Check(
            itemHidden > 500,
            $"反证（按条滚动，ListBox 默认）：滚到底后最后一条的底边还在视口下方 {itemHidden:0.#} px —— 这就是用户看到的「滚动不到底部」");

        var (list, viewer, _) = BuildChatList(
            shortCount: 6, lastText: BigMarkdown(40), autoScroll: false, pixelScroll: true);
        viewer.ScrollToEnd();
        Pump();
        var hidden = BottomHidden(list, viewer);
        Check(hidden <= 1, $"像素滚动（MainWindow.xaml 现在的写法）：滚到底后底边离视口底 {hidden:0.#} px，整条看得见");

        var last = list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1) as ListBoxItem;
        Check(last is not null && last.ActualHeight > viewer.ViewportHeight, $"样本确实比视口高（最后一条 {last?.ActualHeight:0.#} px / 视口 {viewer.ViewportHeight:0.#} px）");
    }

    // ==================================================================
    //  小工具
    // ==================================================================

    private static bool Check(bool ok, string what)
    {
        _checks++;
        if (!ok)
        {
            _failures++;
        }

        Console.WriteLine($"    [{(ok ? "PASS" : "FAIL")}] {what}");
        return ok;
    }

    private static void Fail(string what)
    {
        _checks++;
        _failures++;
        Console.WriteLine($"    [FAIL] {what}");
    }

    private static void NoThrow(string what, Action action)
    {
        try
        {
            action();
            Check(true, what);
        }
        catch (Exception error)
        {
            Check(false, $"{what} —— 抛了 {error.GetType().Name}: {Trim(error.Message)}");
        }
    }

    private static T? Attempt<T>(string what, Func<T?> action)
        where T : class
    {
        try
        {
            var value = action();
            Check(true, what);
            return value;
        }
        catch (Exception error)
        {
            Check(false, $"{what} —— 抛了 {error.GetType().Name}: {Trim(error.Message)}");
            return null;
        }
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"  ── {title}");
    }

    private static int Report()
    {
        Console.WriteLine();
        Console.WriteLine("==================================================================");
        Console.WriteLine(_failures == 0
            ? $"全部通过：{_checks} 项检查，0 项失败。"
            : $"有失败：{_checks} 项检查，{_failures} 项失败。");
        Console.WriteLine("==================================================================");
        return _failures == 0 ? 0 : 1;
    }

    // ---------- 聊天列表形状（照 MainWindow.xaml 搭一份，无窗口） ----------

    /// <summary>自测用的一条消息：界面上真正会变的就是 <see cref="Text"/>（流式追加 = 改它）。</summary>
    private sealed class Line : INotifyPropertyChanged
    {
        private string _text = string.Empty;

        /// <summary>正文（绑给 MarkdownViewer.Markdown）。</summary>
        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }

        /// <summary>自测里不需要流式快路径，恒为 false。</summary>
        public bool IsStreaming => false;

        /// <summary>属性变更通知。</summary>
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// 照 MainWindow.xaml 的聊天区搭一份列表：<c>ListBox</c> + 模板里放 <c>MarkdownViewer</c>、
    /// <c>HorizontalContentAlignment=Stretch</c>、像素滚动（<c>CanContentScroll=False</c>）。
    /// 全程无窗口，排一次版就够；返回列表、它内置的 <c>ScrollViewer</c>、以及消息集合。
    /// </summary>
    /// <param name="shortCount">前面铺几条短消息（用来把列表撑到可滚）。</param>
    /// <param name="lastText">最后一条（"最新"那条）的正文。</param>
    /// <param name="autoScroll">要不要挂 <see cref="ChatAutoScroll"/>。</param>
    /// <param name="throttle">节流间隔；不给 = <see cref="TimeSpan.Zero"/>（同步执行，自测好断言）。</param>
    /// <param name="pixelScroll"><c>true</c> = 像素滚动（MainWindow.xaml 现在的写法）；<c>false</c> = ListBox 默认的按条滚动（用来做反证）。</param>
    private static (ListBox List, ScrollViewer Viewer, ObservableCollection<Line> Lines) BuildChatList(
        int shortCount, string lastText, bool autoScroll, TimeSpan? throttle = null, bool pixelScroll = true)
    {
        const string Xaml = """
            <ListBox xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:md="clr-namespace:PotatoAgent.Markdown;assembly=PotatoAgent.Markdown"
                     Width="600" Height="200"
                     HorizontalContentAlignment="Stretch">
              <ListBox.ItemTemplate>
                <DataTemplate>
                  <md:MarkdownViewer Markdown="{Binding Text}" IsStreaming="{Binding IsStreaming}" />
                </DataTemplate>
              </ListBox.ItemTemplate>
            </ListBox>
            """;

        var list = (ListBox)System.Windows.Markup.XamlReader.Parse(Xaml);

        // 像素滚动 = ScrollViewer.CanContentScroll 关掉（单位从"条"变"像素"），别记反了。
        System.Windows.Controls.ScrollViewer.SetCanContentScroll(list, !pixelScroll);

        var lines = new ObservableCollection<Line>();
        for (var i = 0; i < shortCount; i++)
        {
            lines.Add(new Line { Text = $"消息 {i}：一行短文本。\n" });
        }

        lines.Add(new Line { Text = lastText });
        list.ItemsSource = lines;

        Layout(list);
        Pump();

        if (autoScroll)
        {
            // 附加行为靠可视化树找里面的 ScrollViewer，所以必须排完版再挂；
            // 挂上之后再推一次泵，把"初始布局"那批滚动事件排空，后面的断言才好算。
            ChatAutoScroll.SetThrottleInterval(list, throttle ?? TimeSpan.Zero);
            ChatAutoScroll.SetIsEnabled(list, true);
            Pump();
        }

        var viewer = ViewerOf(list) ?? throw new InvalidOperationException("列表里没有 ScrollViewer");
        return (list, viewer, lines);
    }

    /// <summary>列表内置的那个 <c>ScrollViewer</c>（从第一条消息往上找）。</summary>
    private static ScrollViewer? ViewerOf(ListBox list)
        => list.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem item ? FindAncestor<ScrollViewer>(item) : null;

    /// <summary>第 <paramref name="index"/> 条消息里的 <c>RichTextBox</c>（鼠标停在正文上时命中元素就在它里面）。</summary>
    private static RichTextBox? HostAt(ListBox list, int index)
        => list.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item ? Find<RichTextBox>(item) : null;

    /// <summary>最后一条消息的底边还藏在视口下方多少像素（0 = 整条都露出来了）。</summary>
    private static double BottomHidden(ListBox list, ScrollViewer viewer)
    {
        var last = list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1) as ListBoxItem;
        if (last is null)
        {
            return double.NaN;
        }

        var bottom = last.TransformToAncestor(viewer).Transform(new Point(0, last.ActualHeight)).Y;
        return Math.Max(0, bottom - viewer.ViewportHeight);
    }

    /// <summary>把视图放到"距底 <paramref name="slack"/> 像素"处（0 = 正好贴底）。</summary>
    private static void ScrollToNearBottom(ScrollViewer viewer, double slack)
    {
        viewer.ScrollToVerticalOffset(Math.Max(0, viewer.ScrollableHeight - slack));
        Pump();
    }

    /// <summary>给最后一条消息追加文字（= 流式又吐了一块），然后重排 + 推泵。</summary>
    private static void AppendToLast(ListBox list, ObservableCollection<Line> lines, string delta)
    {
        lines[^1].Text += delta;
        Layout(list);
        Pump();
    }

    /// <summary>无窗口排版一次。</summary>
    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height));
        element.UpdateLayout();
    }

    /// <summary>
    /// 把排进队列的活儿跑完。WPF 的滚动、<c>ScrollChanged</c>、布局都是丢给 Dispatcher 的，
    /// 无窗口自测里没人转消息泵，得自己推。推好几轮：一轮里往往又排进新的活儿。
    /// </summary>
    private static void Pump(int rounds = 4)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }
    }

    /// <summary>对着某个元素抛一个滚轮事件（正数 = 往上滚），返回事件参数供检查。</summary>
    private static MouseWheelEventArgs RaiseWheel(UIElement target, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
        };

        target.RaiseEvent(args);
        return args;
    }

    /// <summary>够长够高的 Markdown 正文（把一条消息撑得比视口还高）。</summary>
    private static string BigMarkdown(int lines)
    {
        var builder = new StringBuilder("# 长回答\n\n");
        for (var i = 0; i < lines; i++)
        {
            builder.Append($"第 {i} 行：一段**加粗**的正文，用来把这一条消息撑得比视口还高。\n\n");
        }

        return builder.ToString();
    }

    private static T? FindAncestor<T>(DependencyObject node)
        where T : DependencyObject
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T hit)
            {
                return hit;
            }
        }

        return null;
    }

    /// <summary>在可视化子树里找第一个 <typeparamref name="T"/>。</summary>
    private static T? Find<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T hit)
        {
            return hit;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = Find<T>(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    // ---------- 文档遍历（自测自己走一遍树，不借被测代码的手） ----------

    private static IEnumerable<Block> FlattenBlocks(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;

            switch (block)
            {
                case Section section:
                    foreach (var child in FlattenBlocks(section.Blocks))
                    {
                        yield return child;
                    }

                    break;

                case List list:
                    foreach (var item in list.ListItems)
                    {
                        foreach (var child in FlattenBlocks(item.Blocks))
                        {
                            yield return child;
                        }
                    }

                    break;

                case Table table:
                    foreach (var group in table.RowGroups)
                    {
                        foreach (var row in group.Rows)
                        {
                            foreach (var cell in row.Cells)
                            {
                                foreach (var child in FlattenBlocks(cell.Blocks))
                                {
                                    yield return child;
                                }
                            }
                        }
                    }

                    break;
            }
        }
    }

    private static IEnumerable<Inline> FlattenInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;

            if (inline is Span span)
            {
                foreach (var child in FlattenInlines(span.Inlines))
                {
                    yield return child;
                }
            }
        }
    }

    /// <summary>把一串行内摊平（<see cref="MarkdownRenderer.ToInlines"/> 的产物是嵌套的 Span）。</summary>
    private static IEnumerable<Inline> Flatten(IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;

            if (inline is Span span)
            {
                foreach (var child in Flatten(span.Inlines))
                {
                    yield return child;
                }
            }
        }
    }

    private static List<Inline> AllInlines(FlowDocument document)
    {
        var result = new List<Inline>();

        foreach (var block in FlattenBlocks(document.Blocks))
        {
            if (block is Paragraph paragraph)
            {
                result.AddRange(FlattenInlines(paragraph.Inlines));
            }
        }

        return result;
    }

    private static Block? FindBlock(FlowDocument document, string tag)
        => FlattenBlocks(document.Blocks).FirstOrDefault(block => Equals(block.Tag, tag));

    private static List? FirstList(FlowDocument document)
        => FlattenBlocks(document.Blocks).OfType<List>().FirstOrDefault();

    private static int ListDepth(List list)
    {
        var depth = 1;

        foreach (var item in list.ListItems)
        {
            foreach (var block in item.Blocks)
            {
                if (block is List nested)
                {
                    depth = Math.Max(depth, 1 + ListDepth(nested));
                }
            }
        }

        return depth;
    }

    /// <summary>每个"有文字"的块一行，拼成整篇文本（容器块不算）。</summary>
    private static string DocumentText(FlowDocument document)
        => string.Join("\n", FlattenBlocks(document.Blocks).Select(BlockText).Where(text => text.Length > 0));

    private static string BlockText(Block block) => block is Paragraph paragraph
        ? string.Concat(paragraph.Inlines.Select(InlineText))
        : string.Empty;

    private static string InlineText(Inline inline) => inline switch
    {
        Run run => run.Text,
        LineBreak => "\n",
        Span span => string.Concat(span.Inlines.Select(InlineText)),
        _ => string.Empty,
    };

    private static int CountLocalValues(FlowDocument document, DependencyProperty property)
    {
        var elements = FlattenBlocks(document.Blocks).Cast<DependencyObject>()
            .Concat(AllInlines(document))
            .ToList();

        return elements.Count(element => element.ReadLocalValue(property) != DependencyProperty.UnsetValue);
    }

    // ---------- 控件侧 ----------

    private static RichTextBox? HostOf(MarkdownViewer viewer) => viewer.Content as RichTextBox;

    private static FlowDocument DocumentOf(MarkdownViewer viewer) => HostOf(viewer)?.Document ?? new FlowDocument();

    private static string HostText(MarkdownViewer viewer) => DocumentText(DocumentOf(viewer));

    private static List<Inline> ViewerInlines(MarkdownViewer viewer) => AllInlines(DocumentOf(viewer));

    /// <summary>无窗口排版：量一次高度，验证控件真的把文字排出来了（不是 0 高、不是不可见）。</summary>
    private static double MeasureHeight(FrameworkElement element)
    {
        var border = new Border { Width = 400, Child = element };
        border.Measure(new Size(400, double.PositiveInfinity));
        var height = border.DesiredSize.Height;
        border.Arrange(new Rect(0, 0, 400, height));
        border.UpdateLayout();
        return height;
    }

    /// <summary>在可视化树里找第一个 <see cref="MarkdownViewer"/>。</summary>
    private static MarkdownViewer? FindViewer(DependencyObject root)
    {
        if (root is MarkdownViewer viewer)
        {
            return viewer;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string Trim(string text) => text.Length > 90 ? text[..90] + "…" : text;

    private static string Escape(string text) => text.Replace("\n", "\\n").Replace("\r", "\\r");
}
