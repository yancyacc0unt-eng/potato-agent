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

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _settings.Saved -= OnSettingsSaved;

        // 正在跑的一轮先取消，再放掉 Provider（不然进程会因为后台请求多活一会儿）。
        _chat.Shutdown();
    }
}
