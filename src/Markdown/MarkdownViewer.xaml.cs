using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace PotatoAgent.Markdown;

/// <summary>
/// 把一段 Markdown 画成富文本的控件：依赖属性 <see cref="Markdown"/>（原文）+
/// <see cref="IsStreaming"/>（还在吐字吗）+ <see cref="CodeFontFamily"/>（代码字体，可选）。
/// </summary>
/// <remarks>
/// <para>
/// <b>一、只负责渲染，不负责长相。</b>控件不设 <c>Foreground</c> / <c>Background</c> / <c>FontFamily</c> /
/// <c>FontSize</c>，全部从外层继承（唯一的例外是 <see cref="CodeFontFamily"/>，默认 <c>null</c> = 也继承）。
/// 唯一"相对外层"的排版是标题字号：按控件拿到的 <c>FontSize</c> 乘一个系数，外层换字号它跟着变。
/// 分割线 / 引用竖线 / 表格框线需要"某个颜色"，用的是继承到的 <c>Foreground</c>，同样不写死。
/// </para>
/// <para>
/// <b>二、流式约定（性能就靠它）。</b>
/// <list type="number">
/// <item><c>IsStreaming == true</c>（模型正在逐块吐字）：走<b>纯文本快路径</b> —— 整篇当一个段落画，
/// <b>一个字符都不做 Markdown 解析</b>。而且只追加新增的那几个字，不重建文档，所以不闪。</item>
/// <item>这一句说完了（一轮结束、或者模型转去调工具）：把 <c>IsStreaming</c> 置回 <c>false</c>，
/// 这时才把整篇 Markdown 解析一次、换成完整富文本。</item>
/// </list>
/// 为什么不逐字渲染 Markdown：每来一个字就重解析整篇是 O(n²)，长回答会肉眼可见地卡，
/// 而且每次重排都会让文本跳一下（闪）。
/// </para>
/// <para>
/// <b>三、绑定示例</b>（<c>ChatEntryViewModel</c> 上现成的两个属性）：
/// <code>
/// &lt;md:MarkdownViewer Markdown="{Binding MarkdownText}" IsStreaming="{Binding IsStreaming}" /&gt;
/// </code>
/// </para>
/// <para>
/// <b>四、不用管异常。</b>畸形 Markdown（未闭合的围栏 / 粗体、几万字、奇怪字符）不会抛 ——
/// 渲染器承诺永不抛，最差是退回纯文本。
/// </para>
/// <para>
/// <b>五、滚轮会转发给外层。</b>鼠标停在本控件上滚轮时，事件不会停在里面的
/// <see cref="RichTextBox"/> 上，而是被重新抛给外层滚动容器（见 <c>OnPreviewMouseWheel</c>）——
/// 这样把本控件塞进 <c>ListBox</c> / <c>ScrollViewer</c> 时，聊天列表照常滚得动。
/// </para>
/// </remarks>
public partial class MarkdownViewer : UserControl
{
    /// <summary>标题 1~6 级的字号系数（乘以控件自己的 <c>FontSize</c>）。觉得不合适就改这张表。</summary>
    private static readonly double[] HeadingScale = { 1.8, 1.5, 1.25, 1.1, 1.0, 0.85 };

    /// <summary>
    /// 被"无限宽"测量时用的兜底宽度（本机实测：RichTextBox 在无限宽度下会退化成 10px 宽的一条缝，
    /// 文字全部竖着折行。见 <see cref="MeasureOverride"/>）。
    /// </summary>
    private const double UnboundedFallbackWidth = 400;

    /// <summary>上一次已经画出来的原文，用来判断流式增量（只追加，不重建）。</summary>
    private string _renderedText = string.Empty;

    /// <summary>上一次画的是不是"纯文本快路径"。</summary>
    private bool _renderedStreaming;

    /// <summary>上一次画的时候控件继承到的字号，用来判断"进了可视化树之后字号变了"要不要重画。</summary>
    private double _renderedBaseFontSize;

    /// <summary>建一个空的渲染控件（<see cref="Markdown"/> 默认空串 = 什么都不画）。</summary>
    public MarkdownViewer()
    {
        InitializeComponent();

        // XAML 里绑定是"模板实例化 → 赋 DataContext → 求值绑定"这个顺序，
        // 求值时控件可能还没挂进可视化树（继承链没就位，FontSize 还是默认值）。
        // 挂上去之后对一次账：字号真的不一样就重画一次，让标题缩放用上真正的字号。
        Loaded += (_, _) => RebuildIfBaseFontChanged();

        // 滚轮转发，见 OnPreviewMouseWheel 的说明。挂在预览（隧道）阶段，
        // 这样里面的 RichTextBox 还没机会把事件标记成 Handled。
        PreviewMouseWheel += OnPreviewMouseWheel;

        Rebuild();
    }

    /// <summary>
    /// 把鼠标滚轮"借道"给外层滚动容器：在预览（隧道）阶段把事件接住，再往父级重新抛一个同样的
    /// <c>MouseWheel</c>，让它照常冒泡到外层的 <c>ScrollViewer</c>（聊天列表）。
    /// </summary>
    /// <param name="sender">本控件。</param>
    /// <param name="e">滚轮事件（隧道阶段，此时 <c>Handled</c> 还是 false）。</param>
    /// <remarks>
    /// <para>
    /// <b>为什么需要它。</b>消息正文是一个 <see cref="RichTextBox"/>，它的模板里自带一个
    /// <c>ScrollViewer</c>（<c>PART_ContentHost</c>）。WPF 的老规矩是"冒泡路径上第一个
    /// <c>ScrollViewer</c> 会把滚轮吃掉，哪怕它已经滚不动了"；那样鼠标停在消息上时就滚不动外层列表。
    /// 本控件在预览（隧道）阶段把滚轮接过来、往外层重新抛一个，这条路径就不再取决于内层怎么处理。
    /// 实测记录（2026-09-21，无窗口树）：原来的事件其实也能冒到外层（偏移 0 → 48 px），
    /// 所以这不是那次"滚不到底部"的根因（根因是 <c>CanContentScroll</c>，见 MainWindow.xaml）；
    /// 转发是为了把行为钉死，真窗口里内层 <c>ScrollViewer</c> 的动作不受这里控制。
    /// 回归测试：<c>MarkdownSelfTest</c> 第 15 节。
    /// </para>
    /// <para>
    /// <b>不会死循环。</b>抛出去的是<b>冒泡</b>的 <c>MouseWheel</c>，从父级出发往上走；
    /// 本控件不在那条路径上，所以不会再回到这个处理器（本处理器只听隧道阶段的
    /// <c>PreviewMouseWheel</c>）。
    /// </para>
    /// <para>
    /// <b>不吃掉横向滚动。</b><see cref="MouseWheelEventArgs.Delta"/> 原样转发、键盘修饰键不动，
    /// 所以按住 Shift 的横向滚轮在外层该怎么样还怎么样；本控件自己也不会去动横向偏移。
    /// </para>
    /// <para>
    /// <b>内层自己能滚就不抢。</b>万一将来把 <c>Host</c> 的滚动条改成 <c>Auto</c>（内容比控件高），
    /// 这个判断会把事件留给 <see cref="RichTextBox"/> 自己滚，转发不越权。
    /// </para>
    /// </remarks>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || HostCanScroll(e.Delta))
        {
            return;
        }

        // 先标记已处理，免得里面的 RichTextBox 再处理一遍；然后在父级重新抛一个一模一样的事件。
        e.Handled = true;

        var target = Parent as UIElement ?? this;
        target.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
        });
    }

    /// <summary>里面的 <see cref="RichTextBox"/> 自己还能不能往 <paramref name="delta"/> 的方向滚。</summary>
    /// <param name="delta">滚轮增量：正数 = 往上滚。</param>
    /// <returns>能滚就返回 <c>true</c>（这时不该抢事件）。</returns>
    /// <remarks>
    /// <see cref="RichTextBox"/> 没有 <c>ScrollableHeight</c>（那是 <c>ScrollViewer</c> 的），
    /// 它的滚动量要用 <c>ExtentHeight - ViewportHeight</c> 算（单位都是像素）。
    /// 纵向滚动条是 <c>Disabled</c> 时两者相等 = 滚不动，于是滚轮交给外层。
    /// </remarks>
    private bool HostCanScroll(int delta)
    {
        var scrollable = Host.ExtentHeight - Host.ViewportHeight;
        if (scrollable <= 0)
        {
            return false;
        }

        return delta > 0 ? Host.VerticalOffset > 0 : Host.VerticalOffset < scrollable;
    }

    /// <summary>
    /// 要渲染的 Markdown 原文。类型 <see cref="string"/>，默认空串，可绑定、可双向（一般单向就够了）。
    /// 改它就会重画；<see cref="IsStreaming"/> 为 <c>true</c> 时按纯文本画（见类注释的流式约定）。
    /// </summary>
    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>
    /// 模型是不是还在往这段文字里吐字。类型 <see cref="bool"/>，默认 <c>false</c>，可绑定。
    /// <c>true</c> = 纯文本快路径（不解析 Markdown、只追加）；
    /// 这一句说完置回 <c>false</c> = 解析一次整篇、换成完整富文本。
    /// </summary>
    public bool IsStreaming
    {
        get => (bool)GetValue(IsStreamingProperty);
        set => SetValue(IsStreamingProperty, value);
    }

    /// <summary>
    /// 代码块 / 行内代码用哪个字体。类型 <see cref="System.Windows.Media.FontFamily"/>，
    /// 默认 <c>null</c> = <b>不干预</b>，跟正文一样继承外层（想区分代码就自己设成 <c>Consolas</c> 之类，
    /// 这是你的设计决定，控件不替你定）。
    /// </summary>
    public System.Windows.Media.FontFamily? CodeFontFamily
    {
        get => (System.Windows.Media.FontFamily?)GetValue(CodeFontFamilyProperty);
        set => SetValue(CodeFontFamilyProperty, value);
    }

    /// <summary><see cref="Markdown"/> 的依赖属性。</summary>
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownViewer),
        new FrameworkPropertyMetadata(string.Empty, OnRenderInputChanged));

    /// <summary><see cref="IsStreaming"/> 的依赖属性。</summary>
    public static readonly DependencyProperty IsStreamingProperty = DependencyProperty.Register(
        nameof(IsStreaming),
        typeof(bool),
        typeof(MarkdownViewer),
        new FrameworkPropertyMetadata(false, OnRenderInputChanged));

    /// <summary><see cref="CodeFontFamily"/> 的依赖属性。</summary>
    public static readonly DependencyProperty CodeFontFamilyProperty = DependencyProperty.Register(
        nameof(CodeFontFamily),
        typeof(System.Windows.Media.FontFamily),
        typeof(MarkdownViewer),
        new FrameworkPropertyMetadata(null, OnRenderInputChanged));

    private static void OnRenderInputChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
        => ((MarkdownViewer)element).Rebuild();

    /// <summary>
    /// 被"无限宽"测量时给个兜底宽度。
    /// </summary>
    /// <param name="availableSize">父容器给的可用尺寸。</param>
    /// <returns>本控件的期望尺寸。</returns>
    /// <remarks>
    /// <para>
    /// 为什么要这一手（实测出来的坑，别删）：里面是 <c>RichTextBox</c>（横向滚动条关掉 = 按宽度折行）。
    /// WPF 拿"无限宽"去量它时，它不知道该折多宽，会报出 <b>10px</b> 宽、然后按 10px 折行 ——
    /// 实测：同一条消息 600 宽度下是 <c>600 x 91</c>，无限宽度下变成 <c>10 x 594</c>（一条竖着的缝）。
    /// </para>
    /// <para>
    /// 而"无限宽"这种量法很常见：<c>ListBox</c> 默认左对齐就是拿无限宽量子项的
    /// （所以聊天列表要配 <c>HorizontalContentAlignment="Stretch"</c>，见 MainWindow.xaml）。
    /// 这里兜一手，别的宿主万一也这么量，至少不会缩成一条缝。
    /// </para>
    /// </remarks>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsInfinity(availableSize.Width))
        {
            availableSize = new Size(UnboundedFallbackWidth, availableSize.Height);
        }

        return base.MeasureOverride(availableSize);
    }

    /// <summary>
    /// 重画。三种情况：流式追加（最便宜）、流式重建（纯文本一份）、完整渲染（解析一次 Markdown）。
    /// </summary>
    private void Rebuild()
    {
        var text = Markdown ?? string.Empty;

        if (IsStreaming)
        {
            // 快路径：不解析 Markdown。已经在流式、而且只是"末尾又多了几个字"时，
            // 连文档都不重建 —— 只把增量追到最后一个段落上（不闪的关键）。
            if (_renderedStreaming
                && text.Length >= _renderedText.Length
                && text.StartsWith(_renderedText, StringComparison.Ordinal)
                && AppendStreamed(text[_renderedText.Length..]))
            {
                _renderedText = text;
                _renderedBaseFontSize = FontSize;
                return;
            }

            Host.Document = BuildPlainDocument(text);
        }
        else
        {
            var document = MarkdownRenderer.ToFlowDocument(text);
            ApplyInheritedPresentation(document);
            Host.Document = document;
        }

        _renderedText = text;
        _renderedStreaming = IsStreaming;
        _renderedBaseFontSize = FontSize;
    }

    /// <summary>把流式增量追加到最后一个段落上；文档结构不对（不是纯文本那份）就返回 false，让调用方重建。</summary>
    private bool AppendStreamed(string delta)
    {
        if (delta.Length == 0)
        {
            return true;
        }

        if (Host.Document?.Blocks.LastBlock is not Paragraph paragraph)
        {
            return false;
        }

        MarkdownRenderer.AddPlainText(paragraph.Inlines, delta);
        return true;
    }

    /// <summary>流式快路径的文档：整篇就是一个段落，换行照原样，不做任何 Markdown 解析。</summary>
    private static FlowDocument BuildPlainDocument(string text)
    {
        var document = new FlowDocument();
        var paragraph = new Paragraph { Tag = MarkdownTags.Paragraph };
        MarkdownRenderer.AddPlainText(paragraph.Inlines, text);
        document.Blocks.Add(paragraph);
        return document;
    }

    /// <summary>
    /// 把"需要宿主才知道"的两件事补上：标题的相对字号、框线的颜色。
    /// 两者都来自控件<em>继承</em>到的值，所以外层换主题 / 换字号，这里跟着变。
    /// </summary>
    private void ApplyInheritedPresentation(FlowDocument document)
    {
        var headingPrefix = MarkdownTags.Heading + ":";
        var codeBlockPrefix = MarkdownTags.CodeBlock + ":";
        var foreground = Foreground;

        foreach (var element in Walk(document.Blocks))
        {
            if (element.Tag is not string tag || tag.Length == 0)
            {
                continue;
            }

            if (tag.StartsWith(headingPrefix, StringComparison.Ordinal)
                && int.TryParse(tag.AsSpan(headingPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
                && level >= 1
                && level <= HeadingScale.Length)
            {
                element.FontSize = FontSize * HeadingScale[level - 1];
                continue;
            }

            switch (tag)
            {
                // 渲染器只给了"框线有多粗"，颜色在这里补：用继承到的前景色，不写死。
                case MarkdownTags.Rule:
                case MarkdownTags.Quote:
                case MarkdownTags.TableCell:
                    if (element is Block framed)
                    {
                        framed.BorderBrush = foreground;
                    }

                    break;
            }

            if (CodeFontFamily is not null
                && (tag == MarkdownTags.InlineCode
                    || tag == MarkdownTags.CodeBlock
                    || tag.StartsWith(codeBlockPrefix, StringComparison.Ordinal)))
            {
                element.FontFamily = CodeFontFamily;
            }
        }
    }

    private void RebuildIfBaseFontChanged()
    {
        if (!IsStreaming && Math.Abs(FontSize - _renderedBaseFontSize) > 0.01)
        {
            Rebuild();
        }
    }

    /// <summary>递归走一遍文档里的块和行内（列表项、表格单元格、嵌套 Span 都要走到）。</summary>
    private static IEnumerable<TextElement> Walk(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;

            switch (block)
            {
                case Section section:
                    foreach (var child in Walk(section.Blocks))
                    {
                        yield return child;
                    }

                    break;

                case List list:
                    foreach (var item in list.ListItems)
                    {
                        foreach (var child in Walk(item.Blocks))
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
                                foreach (var child in Walk(cell.Blocks))
                                {
                                    yield return child;
                                }
                            }
                        }
                    }

                    break;
            }

            if (block is Paragraph paragraph)
            {
                foreach (var inline in WalkInlines(paragraph.Inlines))
                {
                    yield return inline;
                }
            }
        }
    }

    private static IEnumerable<TextElement> WalkInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;

            if (inline is Span span)
            {
                foreach (var child in WalkInlines(span.Inlines))
                {
                    yield return child;
                }
            }
        }
    }
}
