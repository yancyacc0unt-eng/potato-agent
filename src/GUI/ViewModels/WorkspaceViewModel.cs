using System.Collections.ObjectModel;
using PotatoAgent.Workspaces;

namespace GUI.ViewModels;

/// <summary>
/// 工作区面板的 ViewModel：显示"当前工作区 + 最近打开"，并提供换工作区 / 清空的命令。
/// </summary>
/// <remarks>
/// <para>
/// <b>界面怎么接</b>（本对象给工作区那一块当 <c>DataContext</c>；从聊天页里往下钻着绑就是
/// <c>{Binding Workspace.Xxx}</c>）：
/// </para>
/// <list type="bullet">
/// <item><c>TextBlock.Text="{Binding CurrentName}"</c> —— 当前工作区的目录名，没选过时是 <c>No workspace</c>。</item>
/// <item><c>TextBlock.Text="{Binding CurrentPath}"</c> —— 当前工作区的完整路径，没选过时是空串。</item>
/// <item><c>Visibility="{Binding HasWorkspace, Converter=...}"</c> —— 有没有工作区（可以拿它禁用"清除"按钮）。</item>
/// <item><c>ItemsControl.ItemsSource="{Binding Recent}"</c> —— 最近打开列表，每项是 <see cref="Workspace"/>
/// （<c>{Binding Name}</c> 是目录名，<c>{Binding Path}</c> 是完整路径）；每项对应的按钮绑
/// <c>Command="{Binding DataContext.UseRecentCommand, RelativeSource=...}"</c> +
/// <c>CommandParameter="{Binding}"</c>。</item>
/// <item><c>Button.Command="{Binding ChooseCommand}"</c> —— "选择文件夹"。</item>
/// <item><c>Button.Command="{Binding ClearCommand}"</c> —— "清除当前工作区"。</item>
/// <item><c>TextBlock.Text="{Binding StatusMessage}"</c> —— 一行英文状态（成功 / 取消 / 失败原因）。</item>
/// </list>
/// <para>
/// <b>线程</b>：所有可绑定属性的改动都会被送回创建本对象的那条线程（WPF 的 UI 线程），界面不用自己 Dispatcher。
/// </para>
/// </remarks>
public sealed class WorkspaceViewModel : ObservableObject
{
    /// <summary>还没选工作区时 <see cref="CurrentName"/> 显示的英文文案。</summary>
    public const string NoWorkspaceName = "No workspace";

    private readonly WorkspaceStore _store;
    private readonly IWorkspacePicker _picker;
    private readonly SynchronizationContext? _ui;

    private string _currentPath = string.Empty;
    private string _currentName = NoWorkspaceName;
    private bool _hasWorkspace;
    private string _statusMessage = "No workspace selected yet.";

    /// <summary>
    /// 建一个工作区面板。<b>请在 UI 线程上构造</b>（它会记住当前线程用来回送属性通知）。
    /// </summary>
    /// <param name="store">工作区仓库（装配点里已经 <c>Load()</c> 过，这里直接读它的当前状态）。</param>
    /// <param name="picker">挑文件夹的对话框；实现放装配点（见 <see cref="IWorkspacePicker"/>）。</param>
    public WorkspaceViewModel(WorkspaceStore store, IWorkspacePicker picker)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
        _ui = SynchronizationContext.Current;

        ChooseCommand = new RelayCommand(Choose);
        UseRecentCommand = new RelayCommand<object?>(UseRecent);
        ClearCommand = new RelayCommand(Clear);

        // 仓库自己会在换工作区 / 清空之后喊一声；这里只负责把它翻译成属性变化。
        _store.Changed += OnStoreChanged;
        Refresh();
    }

    // ==================== 可绑定属性 ====================

    /// <summary>
    /// 当前工作区的完整路径。类型 <see cref="string"/>，只读；<b>没有工作区时是空串</b>（不是 null）。
    /// 界面上适合配 <see cref="HasWorkspace"/> 决定要不要显示。
    /// </summary>
    public string CurrentPath
    {
        get => _currentPath;
        private set => SetProperty(ref _currentPath, value);
    }

    /// <summary>
    /// 当前工作区的显示名（就是目录名）。类型 <see cref="string"/>，只读；
    /// 没有工作区时是 <see cref="NoWorkspaceName"/>（英文 <c>No workspace</c>）。
    /// </summary>
    public string CurrentName
    {
        get => _currentName;
        private set => SetProperty(ref _currentName, value);
    }

    /// <summary>
    /// 现在有没有工作区。类型 <see cref="bool"/>，只读。
    /// 为 false 时 <see cref="CurrentPath"/> 是空串、<see cref="CurrentName"/> 是 <c>No workspace</c>。
    /// </summary>
    public bool HasWorkspace
    {
        get => _hasWorkspace;
        private set => SetProperty(ref _hasWorkspace, value);
    }

    /// <summary>
    /// 最近打开过的工作区（最近打开的在前，不含当前工作区）。类型
    /// <see cref="ObservableCollection{T}"/>（元素是 <see cref="Workspace"/>），只读，绑 <c>ItemsControl.ItemsSource</c>。
    /// </summary>
    /// <remarks>
    /// 内容跟着 <see cref="WorkspaceStore.Changed"/> 自动刷新（仓库每次给的都是新快照，这里整份重建）。
    /// </remarks>
    public ObservableCollection<Workspace> Recent { get; } = new();

    /// <summary>
    /// 一行英文状态：选好了、用户取消了、还是那个文件夹不能用。类型 <see cref="string"/>，只读。
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    // ==================== 命令 ====================

    /// <summary>
    /// 命令（无参数）：弹文件夹选择框，选中就把它设为当前工作区。
    /// 用户取消时只在 <see cref="StatusMessage"/> 里写一句 <c>Folder selection cancelled.</c>，什么都不改；
    /// 选到的目录不存在 / 是文件时，把仓库给的英文原因原样写进 <see cref="StatusMessage"/>。
    /// </summary>
    public RelayCommand ChooseCommand { get; }

    /// <summary>
    /// 命令（<c>CommandParameter</c> = 一个 <see cref="Workspace"/>，或者它的路径字符串）：
    /// 把那一项切成当前工作区。参数认不出来（null / 别的类型）时只写一句状态，不抛异常。
    /// </summary>
    public RelayCommand<object?> UseRecentCommand { get; }

    /// <summary>
    /// 命令（无参数）：清掉当前工作区（最近打开列表保留）。本来就没有工作区时只写一句状态。
    /// </summary>
    public RelayCommand ClearCommand { get; }

    // ==================== 实现 ====================

    /// <summary>选文件夹 → 设成当前工作区。失败的原因写成 <see cref="StatusMessage"/>，不抛。</summary>
    private void Choose()
    {
        string? picked;
        try
        {
            picked = _picker.PickFolder(HasWorkspace ? CurrentPath : null);
        }
        catch (Exception ex)
        {
            StatusMessage = $"The folder picker failed: {ex.GetType().Name}: {ex.Message}";
            return;
        }

        if (string.IsNullOrWhiteSpace(picked))
        {
            StatusMessage = "Folder selection cancelled.";
            return;
        }

        Apply(picked!);
    }

    /// <summary>点最近列表里的一项：参数可能直接给 <see cref="Workspace"/>，也可能给路径字符串。</summary>
    private void UseRecent(object? parameter)
    {
        string? path = parameter switch
        {
            Workspace workspace => workspace.Path,
            string text when !string.IsNullOrWhiteSpace(text) => text,
            _ => null,
        };

        if (path is null)
        {
            StatusMessage = "Pick a folder from the recent list first.";
            return;
        }

        Apply(path);
    }

    /// <summary>清掉当前工作区（最近列表保留 —— 想在几个目录之间来回切的时候很有用）。</summary>
    private void Clear()
    {
        if (!HasWorkspace)
        {
            StatusMessage = "There is no workspace to clear.";
            return;
        }

        _store.ClearCurrent();
        StatusMessage = "Workspace cleared. The recent folder list is kept.";
    }

    /// <summary>真正去改仓库；只认它给的英文错误原因，成功则由 <see cref="WorkspaceStore.Changed"/> 把界面刷新掉。</summary>
    private void Apply(string path)
    {
        if (!_store.TrySetCurrent(path, out var error))
        {
            StatusMessage = error ?? "That folder could not be used as a workspace.";
            return;
        }

        var current = _store.Current;
        StatusMessage = current is null
            ? $"Workspace: {path}"
            : $"Workspace: {current.Name} ({current.Path})";
    }

    /// <summary>仓库变了（在调用方线程上同步触发），把刷新送回 UI 线程。</summary>
    private void OnStoreChanged(object? sender, EventArgs e) => OnUi(Refresh);

    /// <summary>把当前状态抄进可绑定属性。</summary>
    private void Refresh()
    {
        var current = _store.Current;

        CurrentPath = current?.Path ?? string.Empty;
        CurrentName = current?.Name ?? NoWorkspaceName;
        HasWorkspace = current is not null;

        // 整份重建：Workspace 是不可变值对象，重建比逐条比对简单也更不容易错。
        Recent.Clear();
        foreach (var workspace in _store.Recent)
        {
            Recent.Add(workspace);
        }
    }

    /// <summary>把一段改动送回 UI 线程（仓库的事件可能在别的线程上触发）。</summary>
    private void OnUi(Action action)
    {
        var ui = _ui;
        if (ui is null || ReferenceEquals(ui, SynchronizationContext.Current))
        {
            action();
            return;
        }

        ui.Post(_ => action(), null);
    }
}
