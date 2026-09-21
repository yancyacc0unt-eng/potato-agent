namespace GUI.ViewModels;

/// <summary>挑一个文件夹的对话框。界面由 yancy 设计，所以这里只留一个口子，实现放装配点。</summary>
/// <remarks>
/// <para>
/// <see cref="WorkspaceViewModel"/> 只依赖这个接口，不依赖任何 WPF 对话框 ——
/// 于是"选工作区"这件事可以换成别的方式（拖一个文件夹进来、从命令行带路径…），
/// 也可以在没有窗口的自测里给一个假实现。
/// </para>
/// <para>
/// 现成的实现在装配点 <c>App.xaml.cs</c>（<c>FolderPicker</c>，走 <c>Microsoft.Win32.OpenFolderDialog</c>），
/// 它保证在 UI 线程上弹框。
/// </para>
/// </remarks>
public interface IWorkspacePicker
{
    /// <summary>弹文件夹选择框，返回绝对路径；用户取消返回 null。</summary>
    /// <param name="startAt">
    /// 打开时停在哪（通常是当前工作区路径）；null / 空 / 目录不存在时由实现自己决定从哪开始。
    /// </param>
    string? PickFolder(string? startAt);
}
