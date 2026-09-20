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

using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

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
        Console.WriteLine("被测：MarkdownRenderer.ToFlowDocument / ToInlines + MarkdownViewer");
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
