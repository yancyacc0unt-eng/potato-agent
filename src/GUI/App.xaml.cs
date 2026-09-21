using System.IO;          // WPF 工程的隐式 using 里没有 System.IO，而备份坏库要用 File / Directory
using System.Windows;
using System.Windows.Threading;
using GUI.ViewModels;
using PotatoAgent.Core.Brain;
using PotatoAgent.Core.Tools;
using PotatoAgent.Files;
using PotatoAgent.Sessions;
using PotatoAgent.Shell;
using PotatoAgent.Web;
using PotatoAgent.Win32.Tools;
using PotatoAgent.Workspaces;

namespace GUI;

/// <summary>
/// 应用入口，同时是<b>整个程序唯一的装配点</b>（composition root）。
/// </summary>
/// <remarks>
/// <para>
/// 界面重画时这个文件基本不用动：ViewModel 和 Core 的接线全在 <see cref="OnStartup"/> 那一段里，
/// 换界面 = 换 <c>MainWindow.xaml</c> 和下面 <c>new MainWindow(...)</c> 这一行。
/// </para>
/// <para>启动顺序：读配置 → 读工作区 → 建工具表（<c>pc_*</c> / <c>file_*</c> / <c>pc_shell</c> / <c>web_search</c>）
/// → 开会话库（坏库先备份）→ 建两个 ViewModel → 开主窗口。
/// 工作区必须排在工具表前面：<c>file_*</c> 与 <c>pc_shell</c> 注册时要用它解析相对路径。</para>
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

            // 2) 工作区：当前工作区 + 最近打开列表（%APPDATA%\PotatoAgent\workspaces.json）。
            //    Load() 永不抛：文件坏了就降级成"没有工作区"，界面照常起得来。
            //    ⚠ 必须排在工具注册【之前】：file_* 和 pc_shell 要用它把相对路径解析成绝对路径
            //    （没有工作区时相对路径直接报错，不猜、也不落到某个默认目录）。
            var workspaceStore = new WorkspaceStore();
            workspaceStore.Load();

            // 3) 工具：四组一次性注册进来，模型看到的 tools 数组就是它们的并集。
            //    * pc_*       —— 看屏幕 / 鼠标 / 键盘 / 窗口：pc_state / pc_windows / pc_screenshot（只读），
            //                    pc_click / pc_type / pc_keys / pc_launch / pc_close_window（要确认）。
            //    * file_*     —— 文件工具：file_list / file_read / web 之外都靠它；写操作要确认，
            //                    file_delete 是 Dangerous 且只把东西挪进回收站。
            //    * pc_shell   —— 一条 PowerShell 命令，Dangerous：每次都弹确认、超时杀进程树、输出封顶。
            //    * web_search —— 免密钥联网搜索（DuckDuckGo 主、Bing 备），只读。
            var tools = new ToolRegistry();
            PcTools.RegisterAll(tools);
            FileTools.RegisterAll(tools, () => workspaceStore.Current?.Path);
            ShellTools.RegisterAll(tools, () => workspaceStore.Current?.Path);
            WebTools.RegisterAll(tools);

            // 4) 会话库：%APPDATA%\PotatoAgent\data.db（SQLite）。坏库绝不静默重建 ——
            //    把文件改名留档 + 说明一句，再建一次；再不行就让外层弹框并 Shutdown(1)。
            var sessionStore = OpenSessionStore();

            // 5) ViewModel：所有业务逻辑都在它们里面，XAML 只管绑。
            var chatViewModel = new ChatViewModel(configStore, tools, sessionStore, workspaceStore);
            var settingsViewModel = new SettingsViewModel(configStore);

            // 6) 主窗口。
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

    /// <summary>
    /// 开会话库。<see cref="SqliteSessionStore.Initialize"/> 抛异常（坏库 / 不是 SQLite / 被独占）时，
    /// 把库文件改名成 <c>data.db.corrupt-&lt;时间戳&gt;</c> 留档、弹一次框说明，然后重建一次。
    /// <b>绝不删数据</b>：改名失败就原样留着，只让新库去用别的（同一个）路径。
    /// </summary>
    /// <remarks>
    /// 第二次还失败就让它抛给 <see cref="OnStartup"/> 的兜底：弹框 + <c>Shutdown(1)</c>
    /// —— 数据没丢（坏文件在备份或原处），只是这次起不来。
    /// </remarks>
    private static SqliteSessionStore OpenSessionStore()
    {
        var store = new SqliteSessionStore();
        try
        {
            store.Initialize();
            return store;
        }
        catch (Exception ex)
        {
            var backup = BackupCorruptDatabase(store.DatabasePath);

            MessageBox.Show(
                "potatoAgent could not open its conversation database." + Environment.NewLine + Environment.NewLine +
                store.DatabasePath + Environment.NewLine +
                ex.GetType().Name + ": " + ex.Message + Environment.NewLine + Environment.NewLine +
                (backup is null
                    ? "The file could not be renamed and was left where it is."
                    : "The old file was kept as:" + Environment.NewLine + backup) + Environment.NewLine + Environment.NewLine +
                "A new, empty database will be created now. Nothing was deleted.",
                "potatoAgent",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            var retry = new SqliteSessionStore();
            retry.Initialize();
            return retry;
        }
    }

    /// <summary>
    /// 把坏库改名留档（连 <c>-wal</c> / <c>-shm</c> 一起搬），返回备份路径；搬不动就返回 null。
    /// </summary>
    private static string? BackupCorruptDatabase(string databasePath)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backup = databasePath + ".corrupt-" + stamp;

        for (var n = 2; File.Exists(backup) || Directory.Exists(backup); n++)
        {
            backup = databasePath + ".corrupt-" + stamp + "-" + n;
        }

        try
        {
            if (File.Exists(databasePath))
            {
                File.Move(databasePath, backup);
            }

            // WAL / 共享内存跟着一起搬走，免得新库看到一个没有主人的 -wal。
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(databasePath + suffix))
                {
                    File.Move(databasePath + suffix, backup + suffix);
                }
            }

            return backup;
        }
        catch (Exception)
        {
            return null;   // 搬不动就原样留着 —— 反正绝不删
        }
    }
}

/// <summary>
/// 文件夹选择框（<see cref="IWorkspacePicker"/> 的唯一实现）。界面归 yancy 设计，这里只提供"选文件夹"这个口子。
/// </summary>
/// <remarks>
/// 一律在 UI 线程上弹：WPF 的对话框必须在 UI 线程创建，别的线程调进来就转到 Dispatcher 上。
/// 用户取消返回 null（调用方只在状态栏写一句 <c>Folder selection cancelled.</c>，什么都不改）。
/// </remarks>
public sealed class FolderPicker : IWorkspacePicker
{
    /// <summary>弹文件夹选择框，返回绝对路径；用户取消返回 null。可以放心从任意线程调。</summary>
    /// <param name="startAt">打开时停在哪（通常是当前工作区路径）；null / 目录不存在就用系统默认位置。</param>
    public string? PickFolder(string? startAt)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return Show(startAt);
        }

        return dispatcher.Invoke(() => Show(startAt));
    }

    /// <summary>真正弹框（已经在 UI 线程上）。</summary>
    private static string? Show(string? startAt)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a workspace folder",
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(startAt) && Directory.Exists(startAt))
        {
            dialog.InitialDirectory = startAt;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
