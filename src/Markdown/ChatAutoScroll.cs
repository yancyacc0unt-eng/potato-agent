using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PotatoAgent.Markdown;

/// <summary>
/// 附加行为：<b>有新内容时自动滚到最新一条</b>（聊天列表用）。挂在 <c>ListBox</c> / <c>ItemsControl</c>
/// 或 <c>ScrollViewer</c> 上，<c>md:ChatAutoScroll.IsEnabled="True"</c> 即可。
/// </summary>
/// <remarks>
/// <para>
/// <b>一、它在哪儿干活。</b>本行为<b>不碰 ViewModel、不改消息列表、不动排版</b>：它只盯着里面那个
/// <c>ScrollViewer</c> 的 <c>ScrollChanged</c>，发现"内容变高了"（<c>ExtentHeightChange &gt; 0</c>：
/// 追加了一条消息，或者流式回答又多了一段）就把视图贴到底。
/// 所以用户发送、流式吐字、工具行更新这几种追加都会自动跟随，不需要界面代码配合。
/// </para>
/// <para>
/// <b>二、礼仪：用户翻历史时不抢。</b>只有当"追加之前"视图就在<b>贴底附近</b>时才跟随；
/// 用户手动往上翻之后，<c>ExtentHeight</c> 再怎么涨也不会把他拉回底部。
/// 贴底状态是<b>在内容变化之前</b>判定的，改完不覆盖 —— 详见 <c>OnScrollChanged</c> 的注释。
/// </para>
/// <para>
/// <b>三、节流。</b>流式期间每来一个字就滚一次会白白折腾布局；本行为最多每
/// <see cref="GetThrottleInterval"/>（默认 <see cref="DefaultThrottleInterval"/> = 80 ms）真正滚一次，
/// 落下的那次补在节流窗口末尾（DispatcherTimer 收尾），保证最后一段文字也跟得到底。
/// 间隔设成 <see cref="TimeSpan.Zero"/> = 关掉节流（自测用，行为完全同步）。
/// </para>
/// <para>
/// <b>四、宿主请配像素滚动。</b><c>ListBox</c> 默认是"按条滚动"（<c>ScrollViewer.CanContentScroll=True</c>），
/// 那种模式下 <c>ExtentHeight</c> 的单位是"条"，而且<i>比视口还高的一条消息永远滚不到它的底</i>
/// （实测：600×200 的列表里放一条 1872 px 高的消息，滚到底之后它的底边还在视口下方 1871 px）。
/// 所以聊天列表请写 <c>ScrollViewer.CanContentScroll="False"</c>：单位变像素，底也够得着了。
/// 本行为两种模式都能跑（按条模式下的"贴底"按"最后一条可见"来算）。
/// </para>
/// <para>
/// <b>五、线程。</b>只能在 UI 线程上挂/摘（附加属性回调本来就在 UI 线程）。控件被
/// <c>Unloaded</c>（例如切到别的页签）时自动摘掉，回来（<c>Loaded</c>）自动重挂。
/// </para>
/// </remarks>
public static class ChatAutoScroll
{
    /// <summary>默认节流间隔：80 ms（约 12 次/秒，肉眼连贯又不会每来一个字就滚一次）。</summary>
    public static readonly TimeSpan DefaultThrottleInterval = TimeSpan.FromMilliseconds(80);

    /// <summary>
    /// 「贴底」判定允许的误差（像素）。滚轮一格约 48 px，所以 24 px 表示"往上滚过一格就不算贴底了"。
    /// </summary>
    private const double BottomSlackPixels = 24;

    /// <summary>按条滚动模式下的"贴底"误差：1 条（最后一条已经露出来了）。</summary>
    private const double BottomSlackItems = 1;

    /// <summary>是否启用本行为。类型 <see cref="bool"/>，默认 <c>false</c>，附加属性（可写在 XAML 里）。</summary>
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ChatAutoScroll),
        new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>
    /// 节流间隔。类型 <see cref="TimeSpan"/>，默认 <see cref="DefaultThrottleInterval"/>（80 ms），
    /// 附加属性；设成 <see cref="TimeSpan.Zero"/> = 每次追加都立刻滚（自测用）。
    /// </summary>
    public static readonly DependencyProperty ThrottleIntervalProperty = DependencyProperty.RegisterAttached(
        "ThrottleInterval",
        typeof(TimeSpan),
        typeof(ChatAutoScroll),
        new PropertyMetadata(DefaultThrottleInterval));

    /// <summary>每个元素自己的行为状态（挂在元素上，元素没了状态就没了，不会泄漏）。</summary>
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(State),
        typeof(ChatAutoScroll),
        new PropertyMetadata(null));

    /// <summary>
    /// 只读附加属性：本行为在这个元素上真正执行过几次"滚到底"。类型 <see cref="int"/>。
    /// <b>给自测数节流次数用的</b>（业务代码不用它）：同一次追加风暴里这个数只涨 1，就说明节流生效了。
    /// </summary>
    public static readonly DependencyProperty AppliedScrollCountProperty = DependencyProperty.RegisterAttached(
        "AppliedScrollCount",
        typeof(int),
        typeof(ChatAutoScroll),
        new PropertyMetadata(0));

    /// <summary>读 <see cref="IsEnabledProperty"/>。</summary>
    /// <param name="element">挂了行为的元素。</param>
    /// <returns>启用了就返回 <c>true</c>。</returns>
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    /// <summary>写 <see cref="IsEnabledProperty"/>。</summary>
    /// <param name="element">挂了行为的元素。</param>
    /// <param name="value">是否启用。</param>
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>读 <see cref="ThrottleIntervalProperty"/>。</summary>
    /// <param name="element">挂了行为的元素。</param>
    /// <returns>节流间隔。</returns>
    public static TimeSpan GetThrottleInterval(DependencyObject element)
        => (TimeSpan)element.GetValue(ThrottleIntervalProperty);

    /// <summary>写 <see cref="ThrottleIntervalProperty"/>。</summary>
    /// <param name="element">挂了行为的元素。</param>
    /// <param name="value">节流间隔（<see cref="TimeSpan.Zero"/> = 不节流）。</param>
    public static void SetThrottleInterval(DependencyObject element, TimeSpan value)
        => element.SetValue(ThrottleIntervalProperty, value);

    /// <summary>读 <see cref="AppliedScrollCountProperty"/>（自测/诊断用）。</summary>
    /// <param name="element">挂了行为的元素。</param>
    /// <returns>真正"滚到底"过几次。</returns>
    public static int GetAppliedScrollCount(DependencyObject element)
        => (int)element.GetValue(AppliedScrollCountProperty);

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement owner)
        {
            return;
        }

        if (args.NewValue is true)
        {
            Attach(owner);
        }
        else
        {
            Detach(owner);
        }
    }

    /// <summary>挂上：找里面的 <c>ScrollViewer</c> 并开始听它。</summary>
    private static void Attach(FrameworkElement owner)
    {
        var state = GetState(owner);
        if (state is null)
        {
            state = new State(owner);
            owner.SetValue(StateProperty, state);
            owner.Loaded += state.OnOwnerLoaded;
            owner.Unloaded += state.OnOwnerUnloaded;
        }

        state.Hook();
        owner.SetValue(AppliedScrollCountProperty, state.Applied);
    }

    /// <summary>摘掉：不再听、停掉节流计时器（状态留着，"贴不贴底"下次回来还认）。</summary>
    private static void Detach(FrameworkElement owner) => GetState(owner)?.Unhook();

    private static State? GetState(DependencyObject element) => (State?)element.GetValue(StateProperty);

    /// <summary>一个元素对应的一份状态：里面的 <c>ScrollViewer</c>、贴不贴底、节流计时器。</summary>
    private sealed class State
    {
        private readonly FrameworkElement _owner;
        private ScrollViewer? _viewer;
        private DateTime _lastApplied = DateTime.MinValue;
        private DispatcherTimer? _timer;

        internal State(FrameworkElement owner) => _owner = owner;

        /// <summary>视图现在是不是贴着底（追加之前的状态，决定下一次追加要不要跟）。</summary>
        internal bool StickToBottom { get; private set; } = true;

        /// <summary>真正执行过几次"滚到底"。</summary>
        internal int Applied { get; private set; }

        internal void OnOwnerLoaded(object sender, RoutedEventArgs e) => Hook();

        internal void OnOwnerUnloaded(object sender, RoutedEventArgs e) => Unhook();

        /// <summary>开始监听（重复调用无害）。找不到 <c>ScrollViewer</c>（模板还没展开）就下次再说。</summary>
        internal void Hook()
        {
            // 每次都重新找一遍：控件被卸载又挂回来、或者模板被重新套用（换主题）时，
            // 里面那个 ScrollViewer 可能已经换成新实例了。
            var viewer = FindScrollViewer(_owner);
            if (viewer is null)
            {
                return;
            }

            _viewer = viewer;
            viewer.ScrollChanged -= OnScrollChanged;
            viewer.ScrollChanged += OnScrollChanged;
        }

        /// <summary>停止监听并停表。</summary>
        internal void Unhook()
        {
            _timer?.Stop();

            if (_viewer is not null)
            {
                _viewer.ScrollChanged -= OnScrollChanged;
                _viewer = null;
            }
        }

        /// <summary>
        /// 内容高度变了 → 该跟就跟；只是偏移变了 → 重新判定"用户还贴不贴底"。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>为什么要区分这两种变化。</b>"贴不贴底"必须用<b>变化之前</b>的状态来判断：
        /// 内容一长高，偏移量原地不动就自动离底更远了，此时再去看"现在贴不贴底"永远是"不贴"，
        /// 于是永远不跟随。所以只在纯偏移变化（<c>ExtentHeightChange == 0</c>，即用户滚动
        /// 或我们自己滚动）时刷新这个状态。
        /// </para>
        /// <para>
        /// <b>为什么要看 <c>OriginalSource</c>。</b><c>ScrollChanged</c> 是<b>冒泡</b>事件：
        /// 列表里每条消息的 <c>RichTextBox</c> 内部那个 <c>ScrollViewer</c> 也会冒上来。
        /// 那些不算数（实测它们的 <c>ExtentHeight</c> 是各自消息的高度）。
        /// </para>
        /// </remarks>
        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            var viewer = _viewer;
            if (viewer is null
                || (!ReferenceEquals(e.OriginalSource, viewer) && !ReferenceEquals(e.Source, viewer)))
            {
                return;
            }

            if (e.ExtentHeightChange > 0)
            {
                if (StickToBottom)
                {
                    Request();
                }

                return;
            }

            StickToBottom = IsNearBottom(viewer);
        }

        /// <summary>节流后的"滚到底"：够时间就立刻滚，不够就补一次（DispatcherTimer）在窗口末尾滚。</summary>
        private void Request()
        {
            var interval = GetThrottleInterval(_owner);
            if (interval <= TimeSpan.Zero || DateTime.UtcNow - _lastApplied >= interval)
            {
                Apply();
                return;
            }

            // 计时器已经在跑就别重启它：否则流式期间它永远等不到"安静下来"的那一刻。
            if (_timer is null)
            {
                _timer = new DispatcherTimer(interval, DispatcherPriority.Background, OnThrottleTick, _owner.Dispatcher);
            }

            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }

        private void OnThrottleTick(object? sender, EventArgs e)
        {
            _timer?.Stop();

            if (StickToBottom)
            {
                Apply();
            }
        }

        private void Apply()
        {
            var viewer = _viewer;
            if (viewer is null)
            {
                return;
            }

            _lastApplied = DateTime.UtcNow;
            viewer.ScrollToEnd();

            Applied++;
            _owner.SetValue(AppliedScrollCountProperty, Applied);
        }

        /// <summary>视图离底部够近吗（像素模式下按 <see cref="BottomSlackPixels"/>，按条模式下按一条）。</summary>
        private static bool IsNearBottom(ScrollViewer viewer)
        {
            var scrollable = viewer.ScrollableHeight;
            if (scrollable <= 0)
            {
                return true;
            }

            var slack = viewer.CanContentScroll ? BottomSlackItems : BottomSlackPixels;
            return viewer.VerticalOffset >= scrollable - slack;
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer hit)
            {
                return hit;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
