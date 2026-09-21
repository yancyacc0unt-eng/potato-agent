using System.Collections.ObjectModel;
using PotatoAgent.Sessions;

namespace GUI.ViewModels;

/// <summary>会话列表要驱动的那一头，由 ChatViewModel 实现。</summary>
/// <remarks>
/// 列表本身不碰消息、不碰大脑：它只把"用户点了哪一条"转成一次调用，剩下的交给聊天页。
/// 列表也不认识 <see cref="ChatViewModel"/> 这个具体类型，界面重画时两边互不影响。
/// </remarks>
public interface ISessionHost
{
    /// <summary>切到这个会话：读历史 → 灌回大脑 → 重建界面气泡。</summary>
    Task OpenSessionAsync(SessionRecord session);

    /// <summary>新建一个空会话并切过去（还没落盘，等第一条消息才落盘）。</summary>
    Task NewSessionAsync();

    /// <summary>当前会话（可能还没落盘，此时为 null）。</summary>
    SessionRecord? CurrentSession { get; }

    /// <summary>
    /// 正在跑一轮对话。为 true 时列表要拒绝"切会话 / 新建 / 删除"——
    /// 一轮跑着的时候换上下文会让这一轮的结果落进错误的会话里。
    /// </summary>
    /// <remarks>
    /// 这个成员是接线时补进接口的：需求要求"正在跑一轮就拒绝"，而拒绝的依据只有聊天页知道
    /// （<see cref="ChatViewModel.IsBusy"/>，它本来就是这个语义，直接隐式实现）。
    /// </remarks>
    bool IsBusy { get; }
}

/// <summary>
/// 会话列表的 ViewModel：列出所有会话、切会话、新建、改名、删除。
/// </summary>
/// <remarks>
/// <para><b>界面怎么接</b>（本对象给会话列表那一块当 <c>DataContext</c>；从聊天页里绑就是
/// <c>{Binding Sessions.Xxx}</c>，因为 <see cref="ChatViewModel.Sessions"/> 就是这个对象）：</para>
/// <list type="bullet">
/// <item><c>ListBox.ItemsSource="{Binding Sessions}"</c> —— 每项是 <see cref="SessionItemViewModel"/>
/// （<c>ItemTemplate</c> 里绑 <c>Title</c> / <c>Subtitle</c> / <c>IsCurrent</c>）。</item>
/// <item>每一行点一下切会话：<c>Command="{Binding DataContext.SelectCommand, RelativeSource=...}"</c>
/// + <c>CommandParameter="{Binding}"</c>；删掉那一行同理绑 <c>DeleteCommand</c>。</item>
/// <item><c>Button.Command="{Binding NewSessionCommand}"</c> —— 新建会话（还没有第一条消息时不会落盘）。</item>
/// <item><c>TextBlock.Text="{Binding StatusMessage}"</c> —— 一行英文状态（拒绝、改名/删除的结果）。</item>
/// <item><c>{Binding Current}</c> —— 当前打开的那一条（只读，用来滚动到它 / 做选中态）。</item>
/// </list>
/// <para><b>何时刷新</b>：切会话、追问一轮结束后由聊天页调 <see cref="Refresh"/>；这里不自己轮询。</para>
/// <para><b>线程</b>：所有可绑定属性的改动都会被送回创建本对象的那条线程（WPF 的 UI 线程）。</para>
/// </remarks>
public sealed class SessionListViewModel : ObservableObject
{
    private readonly ISessionStore _store;
    private readonly ISessionHost _host;
    private readonly SynchronizationContext? _ui;

    private SessionItemViewModel? _current;
    private string _statusMessage = "Ready.";

    /// <summary>
    /// 建一个会话列表。<b>请在 UI 线程上构造</b>（它会记住当前线程用来回送属性通知）。
    /// </summary>
    /// <param name="store">会话仓库（只读列表 / 改名 / 删除；消息本体不归这里管）。</param>
    /// <param name="host">被驱动的聊天页（切会话、新建都转给它）。</param>
    public SessionListViewModel(ISessionStore store, ISessionHost host)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _ui = SynchronizationContext.Current;

        NewSessionCommand = new RelayCommand(NewSession);
        SelectCommand = new RelayCommand<SessionItemViewModel>(Select);
        DeleteCommand = new RelayCommand<SessionItemViewModel>(Delete);

        Refresh();
    }

    // ==================== 可绑定属性 ====================

    /// <summary>
    /// 全部会话，按"最近动过的在前"（<see cref="SessionRecord.UpdatedAt"/> 倒序）。
    /// 类型 <see cref="ObservableCollection{T}"/>（元素是 <see cref="SessionItemViewModel"/>），只读，绑 <c>ItemsControl.ItemsSource</c>。
    /// </summary>
    public ObservableCollection<SessionItemViewModel> Sessions { get; } = new();

    /// <summary>
    /// 当前打开的那一条（没打开任何已落盘的会话时是 null）。类型 <see cref="SessionItemViewModel?"/>，只读。
    /// </summary>
    /// <remarks>
    /// 由 <see cref="Refresh"/> 按 <see cref="ISessionHost.CurrentSession"/> 重算，所以"新建但还没发消息"
    /// 的这段空窗期它是 null —— 那时候列表里确实还没有对应的条目。
    /// </remarks>
    public SessionItemViewModel? Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }

    /// <summary>
    /// 一行英文状态：正在跑一轮所以被拒绝、改名 / 删除的结果、读库失败的原因。类型 <see cref="string"/>，只读。
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    // ==================== 命令 ====================

    /// <summary>
    /// 命令（无参数）：新建一个空会话并切过去。<b>不落盘</b> —— 要等它在聊天页里发出第一条消息才建记录，
    /// 所以连点几次也不会攒出一堆空会话。正在跑一轮时拒绝（写一句 <see cref="StatusMessage"/> 并什么都不做）。
    /// </summary>
    public RelayCommand NewSessionCommand { get; }

    /// <summary>
    /// 命令（<c>CommandParameter</c> = 被点的那一个 <see cref="SessionItemViewModel"/>）：切到那个会话。
    /// 真正的切换与"正在跑一轮就拒绝"在 <see cref="ISessionHost.OpenSessionAsync"/> 里做，
    /// 拒绝的原因会写在聊天页那条状态栏上（用户看得见）。
    /// </summary>
    public RelayCommand<SessionItemViewModel> SelectCommand { get; }

    /// <summary>
    /// 命令（<c>CommandParameter</c> = 被点的那一个 <see cref="SessionItemViewModel"/>）：删掉那个会话（不可撤销）。
    /// 正在跑一轮时拒绝；删掉的正好是当前打开的那个时，会让聊天页回到空白新会话。
    /// 想加二次确认框的话，在 XAML 上包一层即可（ViewModel 里不弹窗）。
    /// </summary>
    public RelayCommand<SessionItemViewModel> DeleteCommand { get; }

    // ==================== 刷新 ====================

    /// <summary>
    /// 重新从仓库读一遍列表（切会话之后、每轮问答结束之后调它）。
    /// 读库失败不会抛，只写一句英文 <see cref="StatusMessage"/>。
    /// </summary>
    public void Refresh() => OnUi(RefreshCore);

    // ==================== 实现 ====================

    /// <summary>把当前会话的上下文交给聊天页去处理。</summary>
    private void Select(SessionItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        StatusMessage = _host.IsBusy
            ? "Finish or cancel the current turn before switching sessions."
            : $"Opening '{item.Title}'...";

        // host 的契约是"不抛异常，失败写成状态文本"，所以这里不用接异常。
        _ = _host.OpenSessionAsync(item.Record);
    }

    /// <summary>新建一个空会话。真正的"清空 + 换上下文"在聊天页里做。</summary>
    private void NewSession()
    {
        if (_host.IsBusy)
        {
            StatusMessage = "Finish or cancel the current turn before starting a new chat.";
            return;
        }

        _ = _host.NewSessionAsync();
        Refresh();
    }

    /// <summary>删掉一条会话；删的是当前打开的那个时，顺手把聊天页切回空白。</summary>
    private void Delete(SessionItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (_host.IsBusy)
        {
            StatusMessage = "Finish or cancel the current turn before deleting a session.";
            return;
        }

        try
        {
            if (!_store.Delete(item.Id))
            {
                StatusMessage = $"'{item.Title}' was already gone.";
                Refresh();
                return;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not delete '{item.Title}': {Describe(ex)}";
            return;
        }

        var wasCurrent = string.Equals(_host.CurrentSession?.Id, item.Id, StringComparison.Ordinal);
        StatusMessage = $"Deleted '{item.Title}'.";

        if (wasCurrent)
        {
            // 不切走的话，聊天页还攥着一个已经不存在的 id，下一轮就会往空处写。
            _ = _host.NewSessionAsync();
        }

        Refresh();
    }

    /// <summary>提交改名（由 <see cref="SessionItemViewModel.CommitRenameCommand"/> 回调进来）。</summary>
    private void CommitRename(SessionItemViewModel item)
    {
        var title = (item.Title ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            StatusMessage = "A session title cannot be empty.";
            Refresh();   // 把编辑框里的内容拉回库里的标题
            return;
        }

        try
        {
            if (!_store.Rename(item.Id, title))
            {
                StatusMessage = $"'{item.Id}' is gone - the new title was not saved.";
                Refresh();
                return;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not rename the session: {Describe(ex)}";
            return;
        }

        StatusMessage = $"Renamed to '{title}'.";
        Refresh();
    }

    /// <summary>真正重建列表（已经在 UI 线程上）。</summary>
    private void RefreshCore()
    {
        IReadOnlyList<SessionRecord> records;
        try
        {
            records = _store.List();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not read the session list: {Describe(ex)}";
            return;
        }

        var currentId = _host.CurrentSession?.Id;

        Sessions.Clear();
        foreach (var record in records)
        {
            Sessions.Add(new SessionItemViewModel(record, CommitRename)
            {
                IsCurrent = currentId is not null && string.Equals(record.Id, currentId, StringComparison.Ordinal),
            });
        }

        Current = Sessions.FirstOrDefault(item => item.IsCurrent);
    }

    /// <summary>把一段改动送回 UI 线程（聊天页可能在别的线程上收尾一轮）。</summary>
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

    /// <summary>异常 → 一行英文（界面只显示文本，不显示堆栈）。</summary>
    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
