using System.Windows;
using GUI.ViewModels;

namespace GUI;

/// <summary>
/// 主窗口（临时脚手架）：聊天页 + 齿轮进设置页。
/// </summary>
/// <remarks>
/// <para>
/// <b>这里只有装配，没有业务</b>：窗口的 <c>DataContext</c> 是 <see cref="ChatViewModel"/>，
/// 界面上的每个控件都靠 XAML 里的 <c>{Binding}</c> 取数据、靠 <c>Command="{Binding}"</c> 触发动作。
/// </para>
/// <para>
/// 重画界面时这个文件只需要保留两件事：<see cref="SettingsViewModel"/> 的存在（齿轮怎么打开设置）
/// 和 <see cref="ChatViewModel.ApprovalHandler"/> 的挂载（危险工具弹窗）。其余都可以删。
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    private readonly ChatViewModel _chat;
    private readonly SettingsViewModel _settings;

    /// <summary>临时脚手架用：正在由代码（而不是用户）改选中项，别把那次 SelectionChanged 当成点击。</summary>
    private bool _syncingSelection;

    /// <summary>建主窗口。<b>参数由 App 的装配代码传入</b>（见 <see cref="App.OnStartup"/>）。</summary>
    /// <param name="chatViewModel">聊天页 ViewModel，会成为本窗口的 DataContext。</param>
    /// <param name="settingsViewModel">设置页 ViewModel，点齿轮时挂到设置窗口上。</param>
    public MainWindow(ChatViewModel chatViewModel, SettingsViewModel settingsViewModel)
    {
        _chat = chatViewModel;
        _settings = settingsViewModel;

        InitializeComponent();

        DataContext = _chat;

        // 危险工具的确认框：ChatViewModel 会保证这个回调在 UI 线程上被调用，所以能直接弹模态窗。
        _chat.ApprovalHandler = (request, ct) => ToolApprovalWindow.Ask(this, request, ct);

        // 设置保存后，下一句话用新 profile 重建会话（历史会被重置）。
        _settings.Saved += OnSettingsSaved;

        // 窗口尺寸跟着工作区走：150% 缩放下逻辑工作区只有 1280x672，
        // 写死的高宽可能比工作区还高，CenterScreen 之后标题栏会被顶到屏幕外。
        MaxWidth = SystemParameters.WorkArea.Width;
        MaxHeight = SystemParameters.WorkArea.Height;

        Closing += OnClosing;
    }

    /// <summary>齿轮按钮：先把设置页重新读一遍盘，再模态打开。</summary>
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.LoadCommand.Execute(null);

        var window = new SettingsWindow
        {
            Owner = this,
            DataContext = _settings,
        };

        window.ShowDialog();
    }

    private void OnSettingsSaved(object? sender, EventArgs e) => _chat.RebuildSession();

    /// <summary>
    /// 临时脚手架：会话列表里点一行就切过去。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么不是纯 Binding：<see cref="SessionListViewModel.Current"/> 是<b>只读</b>属性
    /// （私有 setter），<c>SelectedItem</c> 绑成 <c>TwoWay</c> 就没法回写。所以这里只做
    /// "把被点中的那一行交给 <see cref="SessionListViewModel.SelectCommand"/>"，
    /// 业务（拒绝切会话 / 读历史 / 重放气泡）全在 ViewModel 里。
    /// </para>
    /// <para>
    /// <c>_syncingSelection</c> 挡的是自己人：Programmatic 地改 <c>Sessions.Current</c>
    /// （刷新 / 切会话 / 删会话都会触发）也会让 ListBox 报 SelectionChanged，
    /// 不挡的话就会互相触发成回环。所以只在"用户点的"那一次里干活。
    /// </para>
    /// </remarks>
    private void SessionList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingSelection || e.AddedItems.Count == 0)
        {
            return;
        }

        if (e.AddedItems[0] is not SessionItemViewModel item)
        {
            return;
        }

        // 已经是当前会话就别重复开一遍（刷新后 Current 会指回来，那次 SelectionChanged 是自家人）。
        if (string.Equals(_chat.CurrentSession?.Id, item.Id, StringComparison.Ordinal))
        {
            return;
        }

        var select = _chat.Sessions.SelectCommand;
        if (select.CanExecute(item))
        {
            select.Execute(item);
        }
    }

    /// <summary>
    /// 临时的"最近工作区"下拉框：选中一条就切过去。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 同样是因为"选中即执行命令"没有现成的 Binding 写法（<c>SelectedItem</c> 不能绑只读属性）。
    /// 这里只把用户点的那一项交给 <see cref="WorkspaceViewModel.UseRecentCommand"/>。
    /// </para>
    /// <para>重画这块界面时，这个方法和 <see cref="SessionList_SelectionChanged"/> 都可以整个删掉。</para>
    /// </remarks>
    private void RecentWorkspace_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox combo || e.AddedItems.Count == 0)
        {
            return;
        }

        var command = _chat.Workspace.UseRecentCommand;
        if (command.CanExecute(combo.SelectedItem))
        {
            // 同步执行：命令内部会改 Recent 集合并重建列表，重建又会触发一次 SelectionChanged，
            // 用同一个标记挡掉（标记在下一轮消息循环里清掉，那时重建已经结束）。
            _syncingSelection = true;
            try
            {
                command.Execute(combo.SelectedItem);
            }
            finally
            {
                Dispatcher.BeginInvoke(new Action(() => _syncingSelection = false));
            }
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _settings.Saved -= OnSettingsSaved;

        // 正在跑的一轮先取消，再放掉 Provider（不然进程会因为后台请求多活一会儿）。
        _chat.Shutdown();
    }
}
