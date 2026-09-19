using System.Windows;
using System.Windows.Controls;
using GUI.ViewModels;

namespace GUI;

/// <summary>
/// 设置页（临时脚手架）：多档案编辑 + 测试连接。DataContext 是 <see cref="SettingsViewModel"/>。
/// </summary>
/// <remarks>
/// 本文件只有两件事：把窗口 clamp 进工作区、把 <c>PasswordBox</c> 的值桥接给 ViewModel。
/// 界面重画时这个文件可以整个删掉，只要新的界面记得做同样两件事（或者干脆不用密码框）。
/// </remarks>
public partial class SettingsWindow : Window
{
    /// <summary>建设置窗口。<c>DataContext</c> 由打开它的人（<see cref="MainWindow"/>）负责挂。</summary>
    public SettingsWindow()
    {
        InitializeComponent();

        MaxWidth = SystemParameters.WorkArea.Width;
        MaxHeight = SystemParameters.WorkArea.Height;

        DataContextChanged += OnDataContextChanged;
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SettingsViewModel old)
        {
            old.Saved -= OnSaved;
        }

        if (e.NewValue is SettingsViewModel current)
        {
            // 保存成功后清空密码框：明文不该继续留在界面上。
            current.Saved += OnSaved;
        }
    }

    private void OnSaved(object? sender, EventArgs e) => ApiKeyBox.Clear();

    /// <summary>
    /// WPF 的 <c>PasswordBox.Password</c> 不是依赖属性、不能绑定，所以用这个事件把值推进 ViewModel。
    /// 只有这一个方向：ViewModel 永远不会把已存的密钥回填到这里（<c>ApiKeyInput</c> 载入时是空串）。
    /// </summary>
    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && sender is PasswordBox box)
        {
            vm.ApiKeyInput = box.Password;
        }
    }
}
