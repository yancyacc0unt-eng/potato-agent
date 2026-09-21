using System.Globalization;
using PotatoAgent.Sessions;

namespace GUI.ViewModels;

/// <summary>
/// 会话列表里的一行：包一个 <see cref="SessionRecord"/>，加上"改名"和"选中态"这两样界面才需要的东西。
/// </summary>
/// <remarks>
/// <para><b>界面怎么接</b>（一般在 <c>ItemsControl.ItemTemplate</c> 里，DataContext 就是本对象）：</para>
/// <list type="bullet">
/// <item><c>TextBlock.Text="{Binding Title}"</c> —— 标题；想就地改名就换成
/// <c>TextBox.Text="{Binding Title, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"</c>，
/// 再配一个按钮 <c>Command="{Binding CommitRenameCommand}"</c>。</item>
/// <item><c>TextBlock.Text="{Binding Subtitle}"</c> —— 例如 <c>12 messages · 2026-09-21 20:11</c>（本地时间）。</item>
/// <item><c>DataTrigger Binding="{Binding IsCurrent}"</c> —— 当前打开的那一条高亮 / 加个圆点。</item>
/// </list>
/// <para>每一行的"点一下切过去 / 删掉"由外层列表的命令负责（见
/// <see cref="SessionListViewModel.SelectCommand"/> / <see cref="SessionListViewModel.DeleteCommand"/>），
/// 它们靠 <c>CommandParameter="{Binding}"</c> 拿到本对象。</para>
/// </remarks>
public sealed class SessionItemViewModel : ObservableObject
{
    private readonly Action<SessionItemViewModel>? _commitRename;
    private string _title;
    private bool _isCurrent;

    /// <summary>建一行（只读展示用：没有改名回调，<see cref="CommitRenameCommand"/> 点了没反应）。</summary>
    /// <param name="record">这一行的元数据。</param>
    public SessionItemViewModel(SessionRecord record)
        : this(record, null)
    {
    }

    /// <summary>建一行。</summary>
    /// <param name="record">这一行的元数据。</param>
    /// <param name="commitRename">
    /// 点"提交改名"时回调（<see cref="SessionListViewModel"/> 用它落库 + 刷新列表）；null = 这一行不可改名。
    /// </param>
    public SessionItemViewModel(SessionRecord record, Action<SessionItemViewModel>? commitRename)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        _commitRename = commitRename;
        _title = record.Title;

        CommitRenameCommand = new RelayCommand(
            CommitRename,
            () => _commitRename is not null && !string.IsNullOrWhiteSpace(_title));
    }

    /// <summary>这一行的元数据（切会话 / 删会话时需要它的 <see cref="SessionRecord.Id"/>）。只读。</summary>
    public SessionRecord Record { get; }

    /// <summary>会话 id（12 位短 id）。类型 <see cref="string"/>，只读。</summary>
    public string Id => Record.Id;

    /// <summary>
    /// 标题。类型 <see cref="string"/>，<b>双向可写</b>：写它是"用户正在编辑"，
    /// 只有点 <see cref="CommitRenameCommand"/> 才会真的落库。空标题时那个命令点不动。
    /// </summary>
    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                CommitRenameCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// 副标题：条数 + 最后更新时间的本地写法，例如 <c>12 messages · 2026-09-21 20:11</c>。
    /// 类型 <see cref="string"/>，只读（时间取的是 <see cref="SessionRecord.UpdatedAt"/> 的本地时间）。
    /// </summary>
    public string Subtitle
    {
        get
        {
            var count = Record.MessageCount;
            var what = count == 1 ? "message" : "messages";
            var when = Record.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return $"{count} {what} · {when}";
        }
    }

    /// <summary>
    /// 这一条是不是"当前打开的那个会话"。类型 <see cref="bool"/>，可写（实际由
    /// <see cref="SessionListViewModel"/> 在刷新时统一维护，XAML 一般只读它做高亮）。
    /// </summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    /// <summary>
    /// 命令（无参数）：把编辑框里的 <see cref="Title"/> 提交上去（落库 + 刷新列表）。
    /// 标题为空、或者这一行没有改名回调时点不动。
    /// </summary>
    public RelayCommand CommitRenameCommand { get; }

    /// <summary>把编辑框里的标题交给列表去落库（真正的重命名在
    /// <see cref="SessionListViewModel"/> 里做，那里有仓库和状态文本）。</summary>
    private void CommitRename()
    {
        if (!string.IsNullOrWhiteSpace(_title))
        {
            _commitRename?.Invoke(this);
        }
    }

    /// <summary>调试 / UIA 时看得懂的一行。</summary>
    public override string ToString() => $"{Title} [{Id}] ({Subtitle})";
}
