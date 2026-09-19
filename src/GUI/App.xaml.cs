using System.Windows;
using System.Windows.Threading;
using GUI.ViewModels;
using PotatoAgent.Core.Brain;
using PotatoAgent.Core.Tools;
using PotatoAgent.Win32.Tools;

namespace GUI;

/// <summary>
/// 应用入口，同时是<b>整个程序唯一的装配点</b>（composition root）。
/// </summary>
/// <remarks>
/// <para>
/// 界面重画时这个文件基本不用动：ViewModel 和 Core 的接线全在 <see cref="OnStartup"/> 那一段里，
/// 换界面 = 换 <c>MainWindow.xaml</c> 和下面 <c>new MainWindow(...)</c> 这一行。
/// </para>
/// <para>启动顺序：读配置 → 建工具表（<c>pc_*</c> 七个）→ 建两个 ViewModel → 开主窗口。</para>
/// </remarks>
public partial class App : Application
{
    private ConfigStore? _configStore;
    private ChatViewModel? _chat;

    /// <summary>启动。</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 任何漏网的异常都变成一句提示，而不是"程序已停止工作"。
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            // ==================================================================
            //  装配点（要替换配置来源 / 换界面，改这一段就够了）
            // ==================================================================

            // 1) 配置：%APPDATA%\PotatoAgent\config.json，密钥 DPAPI 加密。Load() 永不抛。
            var configStore = new ConfigStore();
            configStore.Load();

            // 2) 工具：把 Win32 层的 pc_state / pc_windows / pc_screenshot（只读）
            //    和 pc_click / pc_type / pc_keys / pc_launch（要确认）一次性注册进来。
            var tools = new ToolRegistry();
            PcTools.RegisterAll(tools);

            // 3) ViewModel：所有业务逻辑都在它们里面，XAML 只管绑。
            var chatViewModel = new ChatViewModel(configStore, tools);
            var settingsViewModel = new SettingsViewModel(configStore);

            // 4) 主窗口。
            var window = new MainWindow(chatViewModel, settingsViewModel);

            // ==================================================================

            _configStore = configStore;
            _chat = chatViewModel;

            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "potatoAgent could not start." + Environment.NewLine + Environment.NewLine +
                ex.GetType().Name + ": " + ex.Message,
                "potatoAgent",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    /// <summary>退出前把正在跑的一轮对话停掉。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        _chat?.Shutdown();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 界面线程上的意外异常：提示一下，让程序继续跑（脚手架阶段比崩溃有用）。
        MessageBox.Show(
            "Unexpected error (the app will keep running):" + Environment.NewLine + Environment.NewLine +
            e.Exception.GetType().Name + ": " + e.Exception.Message,
            "potatoAgent",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true;
    }

    /// <summary>当前配置仓库（调试/后续页面用得上；没启动完就是 null）。</summary>
    public ConfigStore? Config => _configStore;
}
