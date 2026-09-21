using System.Collections.ObjectModel;
using PotatoAgent.Core.Agent;
using PotatoAgent.Core.Brain;
using PotatoAgent.Core.Tools;
using PotatoAgent.Sessions;
using PotatoAgent.Workspaces;

namespace GUI.ViewModels;

/// <summary>
/// 聊天页的 ViewModel：把 <see cref="AgentSession.SendAsync"/> 的事件流翻译成可绑定的界面数据，
/// 实现 <see cref="IToolApprover"/> 做危险工具的权限确认，并实现 <see cref="ISessionHost"/> 给会话列表驱动。
/// </summary>
/// <remarks>
/// <para><b>界面怎么接</b>：把本对象设成聊天页的 <c>DataContext</c>。</para>
/// <list type="bullet">
/// <item><c>ItemsControl/ListBox.ItemsSource="{Binding Entries}"</c> —— 每条 <see cref="ChatEntryViewModel"/> 一行。</item>
/// <item><c>TextBox.Text="{Binding InputText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"</c> —— 输入框。</item>
/// <item><c>Button.Command="{Binding SendCommand}"</c> / <c>CancelCommand</c> / <c>ClearCommand</c>。</item>
/// <item><c>TextBlock.Text="{Binding StatusMessage}"</c> —— 一行状态；<c>{Binding IsBusy}</c> 可以驱动转圈。</item>
/// </list>
/// <para><b>工作区面板与会话列表怎么接</b>：这两块各自是一个独立对象，往下钻着绑就行，主窗口的构造参数不用变 ——</para>
/// <list type="bullet">
/// <item>工作区：<c>{Binding Workspace.CurrentName}</c> / <c>CurrentPath</c> / <c>HasWorkspace</c> /
/// <c>Recent</c> / <c>ChooseCommand</c> / <c>UseRecentCommand</c> / <c>ClearCommand</c> / <c>StatusMessage</c>
/// （详见 <see cref="WorkspaceViewModel"/>）。</item>
/// <item>会话列表：<c>{Binding Sessions.Sessions}</c> / <c>SelectCommand</c> / <c>NewSessionCommand</c> /
/// <c>DeleteCommand</c> / <c>Current</c> / <c>StatusMessage</c>（详见 <see cref="SessionListViewModel"/>）。</item>
/// </list>
/// <para><b>顶上那三个开关怎么接</b>（都是 <c>ComboBox</c>，<c>SelectedItem</c> 双向绑定）：</para>
/// <list type="bullet">
/// <item>模型：<c>ItemsSource="{Binding Models}"</c> + <c>SelectedItem="{Binding SelectedModel, Mode=TwoWay}"</c>，
/// 列表来自服务端 <c>GET /v1/models</c>（缓存在档案里），旁边的刷新按钮绑 <c>RefreshModelsCommand</c>。</item>
/// <item>推理等级：<c>ItemsSource="{Binding ReasoningLevels}"</c> + <c>SelectedItem="{Binding SelectedReasoning, Mode=TwoWay}"</c>。</item>
/// <item>权限档位：<c>ItemsSource="{Binding ApprovalModeLevels}"</c> + <c>SelectedItem="{Binding SelectedApprovalMode, Mode=TwoWay}"</c>。</item>
/// </list>
/// <para>三个都是"选中即生效"，不需要额外按钮（模型那个旁边多一个刷新按钮，只是去拉列表，不改模型）；
/// 各自会自己存盘并在 <see cref="StatusMessage"/> 里报告结果。
/// 切模型会重建会话（<b>模型侧的历史会清空</b>），另外两个立即生效、不动历史。</para>
/// <para><b>权限确认怎么接</b>：本类自己实现了 <see cref="IToolApprover"/>。界面只要在启动时挂一次
/// <see cref="ApprovalHandler"/>（弹出自己的确认框并把用户的选择返回即可）。
/// <b>没挂 handler 时一律拒绝</b>（fail-closed，不会出现"忘了接于是乱点用户电脑"）。</para>
/// <para><b>线程</b>：所有可绑定属性的改动都会被送回创建本对象的那条线程（WPF 的 UI 线程），界面不用自己 Dispatcher。</para>
/// </remarks>
public sealed class ChatViewModel : ObservableObject, IToolApprover, ISessionHost
{
    /// <summary>
    /// 临时系统提示词。等 T4 记忆层做完会换成"固定头部 + 全局 md + 工作区 md"，
    /// 现在先写死一段，保证模型知道自己是干什么的、以及哪些工具要先问用户。
    /// </summary>
    private const string SystemPrompt =
        "You are potatoAgent, an assistant running on the user's own Windows PC. " +
        "You can observe and control this computer through the pc_* tools. " +
        "Look before you act: use pc_state / pc_windows / pc_screenshot first when you are unsure. " +
        "pc_state, pc_windows and pc_screenshot are read-only; pc_click, pc_type, pc_keys, pc_launch and pc_close_window " +
        "change the machine and will be shown to the user for approval before they run. " +
        "In one turn the same program is started at most once: if it is already running, pc_launch focuses the existing " +
        "window and returns its hwnd instead of opening a second copy - use that hwnd, do not call pc_launch again. " +
        "To close a window use pc_close_window, never Alt+F4 (many apps ignore an injected Alt+F4 and just keep the " +
        "window open). " +
        "Answer in the user's language, keep answers short, and never claim you did something you did not actually do.";

    /// <summary>推理等级下拉框里代表"不发这个字段"的那一项（用服务端默认）。</summary>
    public const string DefaultReasoningLevel = "Default";

    /// <summary>权限档位下拉框里"基础"那一项的显示名（<c>basic</c> 的人话写法）。</summary>
    public const string BasicLevelName = "Basic";

    /// <summary>权限档位下拉框里"高级"那一项的显示名（<c>advanced</c> 的人话写法）。</summary>
    public const string AdvancedLevelName = "Advanced";

    /// <summary>推理等级里认得的取值（显示名；发给服务端时转成小写）。</summary>
    private static readonly string[] KnownReasoningLevels = { "Low", "Medium", "High", "Max" };

    private readonly ConfigStore _store;
    private readonly ToolRegistry _tools;
    private readonly ISessionStore _sessions;
    private readonly WorkspaceStore _workspaces;
    private readonly SynchronizationContext? _ui;

    private readonly Dictionary<string, ChatEntryViewModel> _toolEntries = new(StringComparer.Ordinal);

    private AgentSession? _session;
    private CancellationTokenSource? _cts;
    private ChatEntryViewModel? _currentAssistant;
    private ChatEntryViewModel? _lastToolEntry;

    /// <summary>当前会话的记录；新建之后还没发出第一条消息时为 null（那时它还没落盘）。</summary>
    private SessionRecord? _currentSession;

    /// <summary>等着装回大脑的历史（切会话时 Provider 还没配好就先存这儿，建会话时再装）。</summary>
    private IReadOnlyList<ChatMessage>? _pendingRestore;

    /// <summary>上面那份历史属于哪个工作区（装回时系统提示词里那句要用它）。</summary>
    private string? _pendingRestoreWorkspace;

    /// <summary>当前会话第 0 条 system 消息里写的是哪个工作区（变了就要更新那句）。</summary>
    private string? _sessionWorkspacePath;

    /// <summary>工作区在"正跑着一轮"的时候被换了：等这一轮收尾再去更新系统提示词那句。</summary>
    private bool _workspacePromptStale;

    /// <summary>刚跑完那一轮的统计与新增消息（收尾时用它落盘）。</summary>
    private AgentTurnResult? _lastTurnResult;

    private string _inputText = string.Empty;
    private string _statusMessage = "Ready. Fill in Settings first if you have not saved a profile yet.";
    private bool _isBusy;
    private bool _isAwaitingApproval;
    private string? _pendingApprovalTool;
    private string _profileSummary = "(no provider configured yet)";
    private string _alwaysAllowedText = "(none)";
    private string? _selectedModel;
    private string _selectedReasoning = DefaultReasoningLevel;
    private string _selectedApprovalMode = BasicLevelName;

    /// <summary>
    /// 为 true 表示三个下拉框正在"对齐配置文件"（构造 / 重建会话时），
    /// 属性 setter 看到它就不要把这次赋值当成用户在切换。
    /// </summary>
    private bool _switchingSelection;

    /// <summary>建一个聊天页 ViewModel。<b>请在 UI 线程上构造</b>（它会记住当前线程用来回送属性通知）。</summary>
    /// <param name="store">配置仓库：每轮对话前用它拿当前档案建 Provider。</param>
    /// <param name="tools">工具表（记得先 <c>PcTools.RegisterAll(registry)</c>）。</param>
    /// <param name="sessions">会话仓库：历史落盘 / 切会话读回来都走它（<c>Initialize()</c> 由装配点负责）。</param>
    /// <param name="workspaces">工作区仓库：系统提示词里那句"当前工作区"取它的当前值。</param>
    public ChatViewModel(ConfigStore store, ToolRegistry tools, ISessionStore sessions, WorkspaceStore workspaces)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _ui = SynchronizationContext.Current;

        // 两个子面板：界面直接 {Binding Workspace.Xxx} / {Binding Sessions.Xxx} 往下钻，
        // 所以 MainWindow 的构造参数一个都不用改。
        Workspace = new WorkspaceViewModel(_workspaces, new FolderPicker());
        Sessions = new SessionListViewModel(_sessions, this);
        _workspaces.Changed += OnWorkspaceStoreChanged;

        SendCommand = new AsyncRelayCommand(
            SendAsync,
            () => !IsBusy,
            ex => AddError(Describe(ex)));
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        ClearCommand = new RelayCommand(ClearConversation, () => !IsBusy);
        ResetApprovalsCommand = new RelayCommand(ResetApprovals, () => !IsBusy);
        RefreshModelsCommand = new AsyncRelayCommand(
            RefreshModelsAsync,
            () => !IsBusy && _store.ActiveProfile is not null,
            ex => AddError(Describe(ex)));

        // 当前档案摘要 + 三个开关先跟配置文件对齐（App 启动时已经 Load 过了）。
        // 摘要不在这里算的话，启动瞬间会显示 "(no provider configured yet)"，
        // 而旁边的模型下拉框明明已经选中了某个档案 —— 自相矛盾。
        ProfileSummary = DescribeActiveProfile();
        SyncSelectionsFromConfig();

        // 启动不自动建会话：库里已经有会话就接着最近打开的那一个，一个都没有就留空白
        // （空白会话等第一条消息才 Create，所以绝不会攒出一堆空会话）。
        RestoreMostRecentSession();
    }

    /// <summary>
    /// 权限确认的出口：参数是待批准的工具调用，返回用户的选择。
    /// 界面启动时挂一次即可（例如 <c>chat.ApprovalHandler = (req, ct) =&gt; MyDialog.Ask(req, ct);</c>）。
    /// <b>为 null 时危险工具一律拒绝。</b>回调保证在 UI 线程上被调用，所以可以直接弹模态窗。
    /// </summary>
    public Func<ToolApprovalRequest, CancellationToken, Task<ToolApprovalDecision>>? ApprovalHandler { get; set; }

    // ==================== 可绑定属性 ====================

    /// <summary>
    /// 整段对话的记录，按时间顺序。类型 <see cref="ObservableCollection{T}"/>（<see cref="ChatEntryViewModel"/>），只读。
    /// 用户消息、模型的多段回答、每条工具调用、错误提示都各占一行。
    /// </summary>
    public ObservableCollection<ChatEntryViewModel> Entries { get; } = new();

    /// <summary>
    /// 输入框里的文本。类型 <see cref="string"/>，双向绑定
    /// （建议 <c>UpdateSourceTrigger=PropertyChanged</c>，这样 <see cref="SendCommand"/> 能实时知道有没有内容）。
    /// 发送成功后会被自动清空。
    /// </summary>
    public string InputText
    {
        get => _inputText;
        set => SetProperty(ref _inputText, value);
    }

    /// <summary>
    /// 正在跑一轮对话（流式回答中 / 工具执行中 / 等用户点确认）。类型 <see cref="bool"/>，只读。
    /// 为 true 时 <see cref="SendCommand"/> 不能点，<see cref="CancelCommand"/> 能点。
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommandStates();
            }
        }
    }

    /// <summary>
    /// 一行状态文本：正在发送、工具统计、错误原因都得看它。类型 <see cref="string"/>，只读。
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// 是否正有一个确认框等着用户点。类型 <see cref="bool"/>，只读。
    /// 界面可以绑它做遮罩 / 高亮；真正的弹窗由 <see cref="ApprovalHandler"/> 负责。
    /// </summary>
    public bool IsAwaitingApproval
    {
        get => _isAwaitingApproval;
        private set => SetProperty(ref _isAwaitingApproval, value);
    }

    /// <summary>正在等确认的工具名；没有就是 null。类型 <see cref="string?"/>，只读。</summary>
    public string? PendingApprovalTool
    {
        get => _pendingApprovalTool;
        private set => SetProperty(ref _pendingApprovalTool, value);
    }

    /// <summary>
    /// 当前生效的档案摘要（名字 / 地址 / 模型，<b>不含密钥</b>）。类型 <see cref="string"/>，只读。
    /// 建会话和保存设置后自动更新。
    /// </summary>
    public string ProfileSummary
    {
        get => _profileSummary;
        private set => SetProperty(ref _profileSummary, value);
    }

    /// <summary>
    /// 本次会话里用户点过"总是允许"的工具名。类型 <see cref="string"/>，只读。
    /// 这些工具再调用时不再弹确认框，直到 <see cref="ResetApprovalsCommand"/> 或重建会话。
    /// </summary>
    public string AlwaysAllowedText
    {
        get => _alwaysAllowedText;
        private set => SetProperty(ref _alwaysAllowedText, value);
    }

    /// <summary>底层会话对象（想直接调 Core 时用）。没建过会话时为 null。类型 <see cref="AgentSession?"/>，只读。</summary>
    public AgentSession? Session => _session;

    /// <summary>
    /// 工作区面板（当前工作区 / 最近打开 / 选择文件夹 / 清除）。类型 <see cref="WorkspaceViewModel"/>，只读。
    /// </summary>
    /// <remarks>
    /// 界面直接从聊天页往下钻着绑：<c>{Binding Workspace.CurrentName}</c>（目录名，没选过是 <c>No workspace</c>）、
    /// <c>{Binding Workspace.CurrentPath}</c>（完整路径，没选过是空串）、<c>{Binding Workspace.HasWorkspace}</c>、
    /// <c>{Binding Workspace.Recent}</c>（每项 <c>Name</c> / <c>Path</c>）、<c>{Binding Workspace.ChooseCommand}</c>、
    /// <c>{Binding Workspace.UseRecentCommand}</c>（配 <c>CommandParameter="{Binding}"</c>）、
    /// <c>{Binding Workspace.ClearCommand}</c>、<c>{Binding Workspace.StatusMessage}</c>。
    /// 换工作区之后系统提示词里那句"当前工作区"会跟着更新（正在跑一轮时等这轮收尾再更新，绝不每轮追加）。
    /// </remarks>
    public WorkspaceViewModel Workspace { get; }

    /// <summary>
    /// 会话列表面板（列出 / 切换 / 新建 / 改名 / 删除）。类型 <see cref="SessionListViewModel"/>，只读。
    /// </summary>
    /// <remarks>
    /// 界面直接从聊天页往下钻着绑：<c>{Binding Sessions.Sessions}</c>（每项 <see cref="SessionItemViewModel"/>）、
    /// <c>{Binding Sessions.SelectCommand}</c> / <c>{Binding Sessions.DeleteCommand}</c>
    /// （两个都要配 <c>CommandParameter="{Binding}"</c>）、<c>{Binding Sessions.NewSessionCommand}</c>、
    /// <c>{Binding Sessions.Current}</c>、<c>{Binding Sessions.StatusMessage}</c>。
    /// </remarks>
    public SessionListViewModel Sessions { get; }

    /// <summary>
    /// 当前会话的记录（<see cref="ISessionHost"/> 的实现）。类型 <see cref="SessionRecord?"/>，只读。
    /// <b>新建之后还没发出第一条消息时为 null</b> —— 那时候它还没落盘。
    /// </summary>
    public SessionRecord? CurrentSession => _currentSession;

    // ==================== 三个开关：模型 / 推理等级 / 权限档位 ====================

    /// <summary>
    /// 可选的模型列表（服务端 <c>GET /v1/models</c> 拉到的模型名，存在档案里当缓存）。
    /// 类型 <see cref="ObservableCollection{T}"/>（元素是 <see cref="string"/>），只读，绑 <c>ComboBox.ItemsSource</c>。
    /// </summary>
    /// <remarks>
    /// 启动时填的是<b>上次拉到的缓存</b>（加上当前档案正在用的那个模型名），不联网 ——
    /// 想真的重拉就点 <see cref="RefreshModelsCommand"/>（那个会打一次 <c>GET {base}/v1/models</c>）。
    /// </remarks>
    public ObservableCollection<string> Models { get; } = new();

    /// <summary>
    /// 当前模型名（<see cref="Models"/> 里选中的那一个）。类型 <see cref="string?"/>，双向绑定。
    /// <b>改它会立刻写回当前档案、存盘并重建会话</b>：换了模型，模型侧那串上下文接不上，所以历史会被清空
    /// （界面上的文字记录保留）。正在跑一轮时切不动 —— 会被回滚并在 <see cref="StatusMessage"/> 里说明。
    /// </summary>
    public string? SelectedModel
    {
        get => _selectedModel;
        set
        {
            var previous = _selectedModel;
            if (!SetProperty(ref _selectedModel, value) || _switchingSelection || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (IsBusy)
            {
                RevertSelection(() => SelectedModel = previous);
                StatusMessage = "Finish or cancel the current turn before switching the model.";
                return;
            }

            ApplyModel(value!);
        }
    }

    /// <summary>
    /// 异步命令：联网拉一次服务端模型列表（<c>GET {base}/v1/models</c>），成功就刷新 <see cref="Models"/>
    /// 并写进档案当缓存。<b>失败不抛</b>，变成一句 <see cref="StatusMessage"/>
    /// （密钥不对、服务端没实现这个端点都很常见，那是正常情况不是崩溃）。
    /// </summary>
    public AsyncRelayCommand RefreshModelsCommand { get; }

    /// <summary>
    /// 推理等级下拉框的选项。第一项 <see cref="DefaultReasoningLevel"/> = 一个字段都不发（服务端默认），
    /// 其余是发给服务端的 <c>reasoning_effort</c> 取值。类型 <see cref="IReadOnlyList{T}"/>（<see cref="string"/>），只读。
    /// </summary>
    public IReadOnlyList<string> ReasoningLevels { get; } = [DefaultReasoningLevel, .. KnownReasoningLevels];

    /// <summary>
    /// 当前推理等级（<see cref="ReasoningLevels"/> 里选中的那一项）。类型 <see cref="string"/>，双向绑定。
    /// <b>立即生效、不重建会话、不丢历史</b>：它只是写回当前档案，下一次请求就带上新值。
    /// </summary>
    public string SelectedReasoning
    {
        get => _selectedReasoning;
        set
        {
            if (!SetProperty(ref _selectedReasoning, value) || _switchingSelection || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            ApplyReasoning(value);
        }
    }

    /// <summary>
    /// 权限档位下拉框的选项：<see cref="BasicLevelName"/>（Confirm / Dangerous 都先问）与
    /// <see cref="AdvancedLevelName"/>（只有 Dangerous 才问，其余静默放行）。
    /// 类型 <see cref="IReadOnlyList{T}"/>（<see cref="string"/>），只读。
    /// </summary>
    public IReadOnlyList<string> ApprovalModeLevels { get; } = new[] { BasicLevelName, AdvancedLevelName };

    /// <summary>
    /// 当前权限档位（<see cref="ApprovalModeLevels"/> 里选中的那一项）。类型 <see cref="string"/>，双向绑定。
    /// <b>立即生效并存盘</b>：正在跑的对话不用中断，下一次工具调用就按新档位判定要不要弹确认框。
    /// </summary>
    public string SelectedApprovalMode
    {
        get => _selectedApprovalMode;
        set
        {
            if (!SetProperty(ref _selectedApprovalMode, value) || _switchingSelection || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            ApplyApprovalMode(value);
        }
    }

    // ==================== 命令 ====================

    /// <summary>
    /// 异步命令：把 <see cref="InputText"/> 发给模型，并逐块刷新 <see cref="Entries"/>。
    /// 网络/配置错误不会抛，而是变成一条 <c>error</c> 行 + <see cref="StatusMessage"/>。
    /// </summary>
    public AsyncRelayCommand SendCommand { get; }

    /// <summary>命令：取消正在跑的这一轮（<see cref="IsBusy"/> 为 false 时不能点）。</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>命令：清空上下文和界面上的记录（<c>AgentSession.Reset()</c>），并忘记"总是允许"。</summary>
    public RelayCommand ClearCommand { get; }

    /// <summary>命令：只清掉"总是允许"名单，下次危险工具重新弹确认框。</summary>
    public RelayCommand ResetApprovalsCommand { get; }

    // ==================== 会话生命周期 ====================

    /// <summary>
    /// 设置页保存后调这个：丢掉旧的 Provider / 上下文，下一句话用新配置重新建会话。
    /// <b>对话历史会被清空</b>（模型那边的上下文），但界面上的文字记录保留。
    /// </summary>
    public void RebuildSession()
    {
        RebuildSessionCore();
        StatusMessage = "Settings applied: the next message will use the new profile. Conversation history was reset.";
    }

    /// <summary>
    /// <see cref="RebuildSession"/> 的动作部分：丢掉旧 Provider / 上下文 / 工具行账本，并把三个下拉框对齐配置。
    /// </summary>
    /// <remarks>
    /// 状态文本由调用方自己写 —— 切会话、换工作区也走这条路径，但要说的话不一样。
    /// </remarks>
    private void RebuildSessionCore()
    {
        var old = _session;
        _session = null;
        _toolEntries.Clear();
        _lastToolEntry = null;

        if (_currentAssistant is not null)
        {
            _currentAssistant.IsStreaming = false;
            _currentAssistant = null;
        }

        try
        {
            old?.Provider.Dispose();
        }
        catch (Exception)
        {
            // Provider 的 Dispose 出问题不值得打扰用户。
        }

        AlwaysAllowedText = "(none)";
        ProfileSummary = DescribeActiveProfile();
        SyncSelectionsFromConfig();

        // 换了 Provider 就是换了上下文：等着装回的历史和"那句工作区"一起作废。
        _pendingRestore = null;
        _pendingRestoreWorkspace = null;
        _sessionWorkspacePath = null;
        _workspacePromptStale = false;
    }

    /// <summary>关窗口时调：取消正在跑的一轮并释放 Provider。</summary>
    public void Shutdown()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经取消过，无所谓。
        }

        try
        {
            _session?.Provider.Dispose();
        }
        catch (Exception)
        {
            // 同上，退出路径上不抛。
        }

        // 界面都要关了，别再让工作区仓库的事件回调进来。
        _workspaces.Changed -= OnWorkspaceStoreChanged;

        // 会话库（SqliteSessionStore）实现了 IDisposable：退出前关掉它，数据早就落盘了。
        if (_sessions is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception)
            {
                // 退出路径上不抛。
            }
        }
    }

    /// <summary>正在跑的话就取消（关窗口用，不弹提示）。</summary>
    public void CancelIfBusy()
    {
        if (IsBusy)
        {
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已经结束。
            }
        }
    }

    // ==================== 会话：切 / 新建 / 落盘（ISessionHost） ====================

    /// <summary>
    /// 启动时接着上次：把最近打开的那个会话读回来。一个都没有（或读库失败）就留空白 ——
    /// <b>绝不自动建空会话</b>，第一条消息才 Create。
    /// </summary>
    private void RestoreMostRecentSession()
    {
        SessionRecord? latest;
        try
        {
            latest = _sessions.List().FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not read the saved conversations: {Describe(ex)}";
            return;
        }

        if (latest is null)
        {
            return;   // 全新用户：留空白，等第一条消息
        }

        _ = OpenSessionAsync(latest);
    }

    /// <summary>
    /// 切到某个会话：读历史 → 装回大脑 → 重建界面上的气泡。实现 <see cref="ISessionHost.OpenSessionAsync"/>。
    /// </summary>
    /// <remarks>
    /// <para><b>正在跑一轮时拒绝</b>（写一句英文状态）：换上下文会让这一轮的结果落进错误的会话。</para>
    /// <para><b>只重放 user / assistant 的文本气泡</b>：工具过程行、纯 tool_calls 的消息、带图的消息都不重放
    /// —— 那些是当时的现场，重放出来只会让人以为工具又跑了一遍。这一点会在状态栏里用英文说明。</para>
    /// <para>读历史失败、或 Provider 建不出来（还没配好 API key）都不抛：前者写状态并保持原样，
    /// 后者把历史先存着，等用户发出第一条消息建会话时再装回去。</para>
    /// </remarks>
    public Task OpenSessionAsync(SessionRecord session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (IsBusy)
        {
            StatusMessage = "Finish or cancel the current turn before switching sessions.";
            return Task.CompletedTask;
        }

        IReadOnlyList<ChatMessage> messages;
        try
        {
            messages = _sessions.LoadMessages(session.Id);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open '{session.Title}': {Describe(ex)}";
            return Task.CompletedTask;
        }

        // 沿用现有的"重建 AgentSession"路径：丢旧 Provider / 上下文（这里会顺带清掉待装回的历史）。
        RebuildSessionCore();
        _currentSession = session;
        RebuildEntries(messages);

        var workspace = SessionWorkspace(session);
        try
        {
            EnsureSession(workspace).RestoreHistory(WithSystemPrompt(workspace, messages));
            StatusMessage = $"Opened '{session.Title}': {messages.Count} saved message(s), {Entries.Count} bubble(s) " +
                            "rebuilt. Tool activity from the past is not replayed.";
        }
        catch (Exception ex)
        {
            // Provider 还没配好：历史别丢，等建会话的时候再装（见 EnsureSession）。
            _pendingRestore = messages;
            _pendingRestoreWorkspace = workspace;
            StatusMessage = $"Opened '{session.Title}' ({messages.Count} message(s)), but the provider is not ready yet " +
                            $"({Describe(ex)}). The history will be loaded with your next message.";
        }

        Sessions.Refresh();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 新建一个空会话并切过去。实现 <see cref="ISessionHost.NewSessionAsync"/>。
    /// <b>不落盘</b>：等它发出第一条消息才 Create，所以连点也不会攒出一堆空会话。
    /// </summary>
    public Task NewSessionAsync()
    {
        if (IsBusy)
        {
            StatusMessage = "Finish or cancel the current turn before starting a new chat.";
            return Task.CompletedTask;
        }

        RebuildSessionCore();
        _currentSession = null;
        Entries.Clear();
        UpdateAlwaysAllowedText();
        Sessions.Refresh();
        StatusMessage = "New chat. It will be saved as soon as you send the first message.";
        return Task.CompletedTask;
    }

    /// <summary>第一条消息才真的落盘建记录（标题先用默认的 "New chat"，一轮结束后再按首句话命名）。</summary>
    private void EnsureSessionRecord(string? workspace)
    {
        if (_currentSession is not null)
        {
            return;
        }

        try
        {
            _currentSession = _sessions.Create(title: null, workspace: workspace);
            Sessions.Refresh();
        }
        catch (Exception ex)
        {
            StatusMessage = $"This turn will not be saved (could not create the session): {Describe(ex)}";
        }
    }

    /// <summary>一轮结束后落盘：追加这一轮的新消息、按首句话自动命名、刷新会话列表。</summary>
    private void PersistTurn(string userText)
    {
        var result = _lastTurnResult;
        _lastTurnResult = null;

        var session = _currentSession;
        if (session is null)
        {
            return;   // 本轮没能建记录，没什么可写的
        }

        try
        {
            if (result is not null && result.NewMessages.Count > 0)
            {
                // 图片不进库（base64 太大，旧截图也不会再喂回模型）：落盘前过一遍清洗。
                _sessions.Append(session.Id, result.NewMessages.Select(SessionMessageSanitizer.Sanitize));
            }

            // 标题还是默认值时才自动命名 —— 用户改过就绝不覆盖。
            var stored = _sessions.Get(session.Id);
            if (stored is not null)
            {
                if (string.Equals(stored.Title, SessionTitle.DefaultTitle, StringComparison.Ordinal))
                {
                    var title = SessionTitle.FromFirstMessage(userText);
                    if (!string.Equals(title, SessionTitle.DefaultTitle, StringComparison.Ordinal) &&
                        _sessions.Rename(session.Id, title))
                    {
                        stored = _sessions.Get(session.Id) ?? stored;
                    }
                }

                _currentSession = stored;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"This turn could not be saved: {Describe(ex)}";
        }

        Sessions.Refresh();
    }

    /// <summary>
    /// 用一份历史重建界面气泡（切会话时用）。<b>只画 user / assistant 的文本气泡</b>：
    /// 工具过程行、纯 tool_calls 的 assistant 消息、带图的消息都不重放 —— 它们是当时的现场，
    /// 重放出来会让人以为工具又跑了一遍。
    /// </summary>
    private void RebuildEntries(IReadOnlyList<ChatMessage> messages)
    {
        Entries.Clear();
        _toolEntries.Clear();
        _lastToolEntry = null;
        _currentAssistant = null;

        foreach (var message in messages)
        {
            if (string.IsNullOrEmpty(message.Content))
            {
                continue;
            }

            if (string.Equals(message.Role, "user", StringComparison.Ordinal))
            {
                Entries.Add(ChatEntryViewModel.User(message.Content!));
            }
            else if (string.Equals(message.Role, "assistant", StringComparison.Ordinal))
            {
                var entry = ChatEntryViewModel.Assistant();
                entry.Text = message.Content!;
                entry.IsStreaming = false;
                Entries.Add(entry);
            }
        }
    }

    // ==================== 系统提示词里的"当前工作区"那句 ====================

    /// <summary>
    /// 系统提示词 = 固定人设 + 一句"当前工作区"。
    /// </summary>
    /// <remarks>
    /// 这句<b>只在建会话 / 工作区变化时</b>生成，<b>绝不每轮追加</b> —— 系统提示词是历史第 0 号节点，
    /// 每轮变化的内容会让前缀缓存整段失效（踩过的坑）。
    /// </remarks>
    private static string BuildSystemPrompt(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return SystemPrompt +
                " No workspace folder is selected right now: ask the user which folder to work in before you touch their files.";
        }

        return SystemPrompt +
            $" The current workspace folder is {workspace.Trim()}. Treat it as the working directory when you look for or create files.";
    }

    /// <summary>给一份历史补上第 0 条 system 消息（工作区那句按参数现场生成）。</summary>
    private static List<ChatMessage> WithSystemPrompt(string? workspace, IReadOnlyList<ChatMessage> messages)
    {
        var restored = new List<ChatMessage>(messages.Count + 1) { ChatMessage.System(BuildSystemPrompt(workspace)) };
        restored.AddRange(messages);
        return restored;
    }

    /// <summary>当前工作区的路径；没选过就是 null。</summary>
    private string? CurrentWorkspacePath()
    {
        var path = _workspaces.Current?.Path;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    /// <summary>切回某个已落盘的会话时，系统提示词里该写哪个工作区（它自己绑的那个优先）。</summary>
    private string? SessionWorkspace(SessionRecord session) =>
        string.IsNullOrWhiteSpace(session.Workspace) ? CurrentWorkspacePath() : session.Workspace;

    /// <summary>两个工作区路径是不是同一个（Windows 上大小写不敏感）。</summary>
    private static bool PathEquals(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>工作区仓库变了（换 / 清空）：系统提示词里那句要跟着更新。</summary>
    private void OnWorkspaceStoreChanged(object? sender, EventArgs e) => OnUi(HandleWorkspaceChanged);

    /// <summary>工作区变了：没在跑一轮就立刻更新那句，正在跑就先记下来、等收尾再更新。</summary>
    private void HandleWorkspaceChanged()
    {
        var workspace = CurrentWorkspacePath();
        if (PathEquals(_sessionWorkspacePath, workspace))
        {
            return;   // 没变（或者本来就是同一个目录）
        }

        if (IsBusy)
        {
            // 跑着一轮的时候绝不能碰会话（旧 Provider 正在被用）：等这轮收尾再更新。
            _workspacePromptStale = true;
            return;
        }

        if (_session is null)
        {
            // 还没建会话：下一句话建会话时自然会带上新的工作区那句。
            _sessionWorkspacePath = workspace;
            return;
        }

        ApplyWorkspaceToSession(workspace);
    }

    /// <summary>
    /// 把第 0 条 system 消息换成"按当前工作区生成"的那一句，<b>对话历史原样保留</b>
    /// （换工作区不该把聊天记录清掉）。注意 <see cref="AgentSession.RestoreHistory"/> 会清空"总是允许"名单。
    /// </summary>
    private void ApplyWorkspaceToSession(string? workspace)
    {
        var session = _session;
        if (session is null)
        {
            _sessionWorkspacePath = workspace;
            return;
        }

        var rebuilt = new List<ChatMessage> { ChatMessage.System(BuildSystemPrompt(workspace)) };
        foreach (var message in session.History)
        {
            if (!string.Equals(message.Role, "system", StringComparison.Ordinal))
            {
                rebuilt.Add(message);
            }
        }

        session.RestoreHistory(rebuilt);
        _sessionWorkspacePath = workspace;
        UpdateAlwaysAllowedText();   // RestoreHistory 清空了"总是允许"名单
        StatusMessage = workspace is null
            ? "Workspace cleared. The system prompt no longer names a folder (the conversation is kept, 'always allow' was reset)."
            : $"Workspace: {workspace}. The system prompt was updated (the conversation is kept, 'always allow' was reset).";
    }

    /// <summary>工作区在跑一轮的时候被换了：这一轮收尾后再去更新系统提示词那句。</summary>
    private void ApplyPendingWorkspaceChange()
    {
        if (!_workspacePromptStale)
        {
            return;
        }

        _workspacePromptStale = false;
        ApplyWorkspaceToSession(CurrentWorkspacePath());
    }

    // ==================== 发一轮 ====================

    private async Task SendAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var text = (InputText ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            StatusMessage = "Type something first.";
            return;
        }

        // 建会话时该用哪个工作区：有历史待装回就用那个会话自己绑的，否则用当前工作区。
        var workspace = _pendingRestore is null ? CurrentWorkspacePath() : _pendingRestoreWorkspace;

        var session = _session;
        if (session is null)
        {
            try
            {
                session = EnsureSession(workspace);
            }
            catch (ProviderException ex)
            {
                AddError(ex.Message);
                StatusMessage = "Not sent - open Settings and check Base URL / API key / model.";
                return;
            }
            catch (Exception ex)
            {
                AddError(Describe(ex));
                StatusMessage = "Not sent - the provider could not be created.";
                return;
            }
        }

        // 第一条消息才落盘建会话记录（"New chat" 那时还没进库）：建不出来不拦着发消息，只是这一轮存不下来。
        EnsureSessionRecord(workspace);

        InputText = string.Empty;
        Add(ChatEntryViewModel.User(text));
        _currentAssistant = null;

        IsBusy = true;
        StatusMessage = "Sending...";
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var started = DateTime.UtcNow;

        try
        {
            await foreach (var evt in session!.SendAsync(text, token))
            {
                Handle(evt);
            }

            StatusMessage = $"Turn finished in {(DateTime.UtcNow - started).TotalSeconds:0.0} s. {StatusMessage}";
        }
        catch (OperationCanceledException)
        {
            AddNote("cancelled by the user.");
            StatusMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            // 兜底：Core 承诺不抛，但界面绝不该因为意外异常而崩。
            AddError(Describe(ex));
            StatusMessage = "Turn failed: " + Describe(ex);
        }
        finally
        {
            if (_currentAssistant is not null)
            {
                _currentAssistant.IsStreaming = false;
                _currentAssistant = null;
            }

            foreach (var entry in _toolEntries.Values)
            {
                if (entry.ToolSucceeded is null)
                {
                    entry.CancelTool("no result was reported for this call.");
                }
            }

            _toolEntries.Clear();
            _lastToolEntry = null;
            IsAwaitingApproval = false;
            PendingApprovalTool = null;
            UpdateAlwaysAllowedText();
            IsBusy = false;

            // 落盘 + 自动命名 + 刷新会话列表；再补一次"跑的时候被换掉的工作区那句"。
            PersistTurn(text);
            ApplyPendingWorkspaceChange();

            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>把 Core 的一个事件翻译成界面上的变化。</summary>
    private void Handle(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentTextDelta delta:
                AppendAssistantText(delta.Text);
                break;

            case AgentToolStarting starting:
            {
                var entry = ChatEntryViewModel.ToolCall(
                    starting.Id, starting.Name, starting.ArgumentsJson, starting.Risk.ToString(), starting.NeedsApproval);
                _toolEntries[starting.Id] = entry;
                _lastToolEntry = entry;

                // 工具调用会打断模型的回答：后面再吐字就另起一段。
                if (_currentAssistant is not null)
                {
                    _currentAssistant.IsStreaming = false;
                    _currentAssistant = null;
                }

                Add(entry);

                // 不问确认框有两种原因：本来就没危险（Safe），或者当前档位/已授权放行了。
                // 后者要写在状态里，免得用户在"几乎不警告"档位下以为确认框坏了。
                var note = starting.NeedsApproval
                    ? " - waiting for your approval"
                    : starting.Risk == ToolRisk.Safe
                        ? string.Empty
                        : " - allowed without asking (current permission mode)";

                StatusMessage = $"Calling {starting.Name} (risk {starting.Risk}){note}...";
                break;
            }

            case AgentToolFinished finished:
            {
                var entry = FindToolEntry(finished.Id, finished.Name);
                entry.CompleteTool(finished.Success, finished.Summary, finished.Elapsed, finished.ImageCount);
                StatusMessage = finished.Success
                    ? $"{finished.Name} finished in {finished.Elapsed.TotalMilliseconds:0} ms."
                    : $"{finished.Name} failed: {finished.Summary}";
                break;
            }

            case AgentToolDenied denied:
            {
                var entry = FindToolEntry(denied.Id, denied.Name);
                entry.DenyTool(denied.Reason);
                StatusMessage = $"{denied.Name} was not executed: {denied.Reason}";
                break;
            }

            case AgentError error:
                AddError(error.Message);
                StatusMessage = "Error: " + error.Message;
                break;

            case AgentTurnCompleted completed:
            {
                var result = completed.Result;
                _lastTurnResult = result;   // 收尾时用它把这一轮的新消息落盘
                var tail = result.StoppedAtRoundLimit
                    ? " (stopped at the tool-round limit, the answer may be unfinished)"
                    : string.Empty;
                var duplicates = result.DuplicateCallsSkipped > 0
                    ? $", {result.DuplicateCallsSkipped} duplicate call(s) blocked"
                    : string.Empty;
                StatusMessage =
                    $"turn done: {result.ToolCallsExecuted} tool call(s) executed, {result.ToolCallsDenied} denied" +
                    $"{duplicates}, finish={result.FinishReason ?? "n/a"}{tail}";
                UpdateAlwaysAllowedText();
                break;
            }
        }
    }

    private void AppendAssistantText(string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        if (_currentAssistant is null)
        {
            _currentAssistant = ChatEntryViewModel.Assistant();
            Add(_currentAssistant);
        }

        _currentAssistant.AppendText(delta);
    }

    private ChatEntryViewModel FindToolEntry(string id, string name)
    {
        if (_toolEntries.TryGetValue(id, out var entry))
        {
            return entry;
        }

        // 理论上不会走到这里（Core 保证 Starting 一定先于 Finished）。
        var fallback = ChatEntryViewModel.ToolCall(id, name, string.Empty, "unknown", false);
        Add(fallback);
        return fallback;
    }

    private void Add(ChatEntryViewModel entry) => OnUi(() => Entries.Add(entry));

    private void AddError(string message)
    {
        Add(ChatEntryViewModel.Error(message));
        OnUi(() => StatusMessage = message);
    }

    private void AddNote(string text) => Add(ChatEntryViewModel.Error(text));

    private void Cancel()
    {
        try
        {
            StatusMessage = "Cancelling...";
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 刚好跑完了，忽略。
        }
    }

    private void ClearConversation()
    {
        _session?.Reset();
        Entries.Clear();
        _toolEntries.Clear();
        _lastToolEntry = null;
        _currentAssistant = null;
        UpdateAlwaysAllowedText();
        StatusMessage = "Conversation cleared.";
    }

    private void ResetApprovals()
    {
        _session?.ClearRememberedApprovals();
        UpdateAlwaysAllowedText();
        StatusMessage = "Forgot the 'always allow' list - risky tools will ask again.";
    }

    private void UpdateAlwaysAllowedText()
    {
        var allowed = _session?.AlwaysAllowedTools;
        AlwaysAllowedText = allowed is null || allowed.Count == 0 ? "(none)" : string.Join(", ", allowed);
    }

    private void RefreshCommandStates()
    {
        SendCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
        ResetApprovalsCommand.RaiseCanExecuteChanged();
        RefreshModelsCommand.RaiseCanExecuteChanged();
    }

    // ==================== IToolApprover ====================

    /// <summary>
    /// Core 在跑危险工具前来问一句。这里把问题交给 <see cref="ApprovalHandler"/>（界面弹窗），
    /// <b>没挂 handler 或弹窗自己坏了都按"拒绝"处理</b>。
    /// </summary>
    async Task<ToolApprovalDecision> IToolApprover.ApproveAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        var entry = _lastToolEntry;
        OnUi(() =>
        {
            IsAwaitingApproval = true;
            PendingApprovalTool = request.ToolName;
            StatusMessage = $"{request.ToolName} wants to run (risk {request.Risk}) - waiting for your decision...";
            entry?.SetToolStatus("waiting for your approval...");
        });

        var handler = ApprovalHandler;
        ToolApprovalDecision decision;

        if (handler is null)
        {
            decision = ToolApprovalDecision.Deny;
            OnUi(() => StatusMessage =
                $"No approval UI is wired up, so {request.ToolName} was denied (fail-closed). " +
                "Set ChatViewModel.ApprovalHandler to enable it.");
        }
        else
        {
            try
            {
                decision = await AskOnUiAsync(() => handler(request, ct)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                OnUi(() =>
                {
                    IsAwaitingApproval = false;
                    PendingApprovalTool = null;
                });
                throw;
            }
            catch (Exception ex)
            {
                decision = ToolApprovalDecision.Deny;
                OnUi(() => StatusMessage = $"The approval prompt failed ({Describe(ex)}), so {request.ToolName} was denied.");
            }
        }

        OnUi(() =>
        {
            IsAwaitingApproval = false;
            PendingApprovalTool = null;

            if (decision == ToolApprovalDecision.AllowAlways)
            {
                entry?.SetToolStatus("approved (always) - running...");
            }

            UpdateAlwaysAllowedText();
            StatusMessage = decision switch
            {
                ToolApprovalDecision.AllowAlways => $"Always allowing {request.ToolName} for this session.",
                ToolApprovalDecision.AllowOnce => $"Allowed {request.ToolName} once.",
                _ => $"Denied {request.ToolName}.",
            };
        });

        return decision;
    }

    // ==================== 三个开关的实现 ====================

    /// <summary>切模型：写回当前档案的模型名 → 存盘 → 重建会话（历史清空）→ 报告结果。</summary>
    private void ApplyModel(string name)
    {
        var profile = _store.ActiveProfile;
        if (profile is null)
        {
            StatusMessage = "No provider profile yet - add one in Settings first.";
            SyncSelectionsFromConfig();
            return;
        }

        if (string.Equals(profile.Model, name, StringComparison.Ordinal))
        {
            return;   // 值没变（例如只是把列表对齐了一下），别白清一次历史。
        }

        profile.Model = name;
        var saved = TrySaveConfig(out var saveError);

        // RebuildSession 会丢掉旧 Provider 和旧上下文，也会自己把三个下拉框对齐一遍。
        RebuildSession();

        StatusMessage = saved
            ? $"Model switched to '{name}'. Conversation history was reset."
            : $"Model switched to '{name}' for this session, but saving config.json failed: {saveError}";
    }

    /// <summary>点刷新：开一个临时 Provider 拉一次模型列表，成功后写进档案当缓存。失败只报告，不抛。</summary>
    private async Task RefreshModelsAsync()
    {
        var profile = _store.ActiveProfile;
        if (profile is null)
        {
            StatusMessage = "No provider profile yet - add one in Settings first.";
            return;
        }

        StatusMessage = $"Loading the model list from {profile.BaseUrl}...";

        try
        {
            // 临时建一个 Provider（自己 new 的 HttpClient 由它自己释放），不给当前会话添乱。
            using var provider = OpenAiProvider.FromActiveProfile(_store);
            var names = await provider.ListModelsAsync().ConfigureAwait(true);

            profile.KnownModels = names.ToList();
            var saved = TrySaveConfig(out var saveError);

            SyncSelectionsFromConfig();
            StatusMessage = saved
                ? $"Model list updated: {names.Count} model(s) available on the server."
                : $"Model list updated for this session, but saving config.json failed: {saveError}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load the model list: {Describe(ex)}";
        }
    }

    /// <summary>切推理等级：写回当前档案，下一次请求生效。不重建会话、不丢历史。</summary>
    private void ApplyReasoning(string level)
    {
        var profile = _store.ActiveProfile;
        if (profile is null)
        {
            StatusMessage = "No provider profile yet - add one in Settings first.";
            return;
        }

        // Provider 持有的是同一个 profile 对象，所以改完下一轮请求就带上了新值。
        profile.ReasoningEffort = ToCoreReasoning(level);

        var saved = TrySaveConfig(out var saveError);
        var what = profile.ReasoningEffort is null
            ? "server default (no reasoning_effort field is sent)"
            : profile.ReasoningEffort;

        StatusMessage = saved
            ? $"Reasoning effort: {what}. It takes effect on the next request."
            : $"Reasoning effort: {what} (this session only) - saving config.json failed: {saveError}";
    }

    /// <summary>切权限档位：立刻改在会话上并存盘（跑着的对话不用中断）。</summary>
    private void ApplyApprovalMode(string level)
    {
        var mode = ToolApprovalModeNames.Parse(level);
        _store.Current.ApprovalMode = mode;

        // 会话可能还没建（第一句话还没发出去），那就等建的时候由 EnsureSession 带上。
        if (_session is not null)
        {
            _session.ApprovalMode = mode;
        }

        var saved = TrySaveConfig(out var saveError);
        var what = mode == ToolApprovalMode.Advanced
            ? "Advanced - only Dangerous tools ask first, Confirm tools run without a prompt"
            : "Basic - every tool that touches this PC asks first";

        StatusMessage = saved
            ? $"Permissions: {what}."
            : $"Permissions: {what} (this session only) - saving config.json failed: {saveError}";
    }

    /// <summary>把三个下拉框重新对齐配置文件（只改显示，不触发任何"切换"动作）。</summary>
    private void SyncSelectionsFromConfig()
    {
        _switchingSelection = true;
        try
        {
            RefreshModels();
            SelectedReasoning = FromCoreReasoning(_store.ActiveProfile?.ReasoningEffort);
            SelectedApprovalMode = ApprovalDisplayName(_store.Current.ApprovalMode);
        }
        finally
        {
            _switchingSelection = false;
        }
    }

    /// <summary>
    /// 用档案里的缓存重建 <see cref="Models"/>：缓存里的模型名 + 当前正在用的那个
    /// （当前值不在列表里时补进去，免得下拉框一片空白）。<b>只读缓存，不联网</b>。
    /// </summary>
    private void RefreshModels()
    {
        var profile = _store.ActiveProfile;
        var current = profile?.Model;

        Models.Clear();
        foreach (var name in profile?.KnownModels ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                Models.Add(name);
            }
        }

        if (!string.IsNullOrWhiteSpace(current) &&
            !Models.Any(m => string.Equals(m, current, StringComparison.OrdinalIgnoreCase)))
        {
            Models.Add(current!);
        }

        SelectedModel = string.IsNullOrWhiteSpace(current) ? null : current;
    }

    /// <summary>下拉框选项 → 发给服务端的值：<see cref="DefaultReasoningLevel"/> = null（这个字段一个都不发）。</summary>
    private static string? ToCoreReasoning(string level) =>
        string.Equals(level, DefaultReasoningLevel, StringComparison.OrdinalIgnoreCase)
            ? null
            : level.Trim().ToLowerInvariant();

    /// <summary>配置里的值 → 下拉框选项；认不出来的原样显示（别悄悄改掉用户手写的配置）。</summary>
    private static string FromCoreReasoning(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return DefaultReasoningLevel;
        }

        var trimmed = effort.Trim();
        foreach (var known in KnownReasoningLevels)
        {
            if (string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return trimmed;
    }

    /// <summary>权限档位 → 下拉框里的显示名。</summary>
    private static string ApprovalDisplayName(ToolApprovalMode mode) =>
        mode == ToolApprovalMode.Advanced ? AdvancedLevelName : BasicLevelName;

    /// <summary>存盘。失败只回报一句话，绝不往外抛 —— 换个开关不该把界面搞崩。</summary>
    private bool TrySaveConfig(out string? error)
    {
        try
        {
            _store.Save();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = Describe(ex);
            return false;
        }
    }

    /// <summary>把一次选择改动回滚成原值，并且不让 setter 把它当成"用户在切换"。</summary>
    private void RevertSelection(Action revert)
    {
        _switchingSelection = true;
        try
        {
            revert();
        }
        finally
        {
            _switchingSelection = false;
        }
    }

    // ==================== 内部：会话与线程 ====================

    /// <summary>按当前档案建会话。配置不全时抛 <see cref="ProviderConfigurationException"/>（调用方翻译成人话）。</summary>
    /// <param name="workspace">
    /// 系统提示词里那句"当前工作区"要写哪个路径；null = 现在没有工作区（提示词改成让模型先问用户）。
    /// </param>
    private AgentSession EnsureSession(string? workspace)
    {
        var provider = OpenAiProvider.FromActiveProfile(_store);
        var session = new AgentSession(
            provider,
            _tools,
            this,
            new[] { ChatMessage.System(BuildSystemPrompt(workspace)) })
        {
            // 权限档位跟着配置走：用户在工具条上切过"高级"，新会话要照样免问。
            ApprovalMode = _store.Current.ApprovalMode,
        };

        _session = session;
        _sessionWorkspacePath = workspace;
        ProfileSummary = DescribeActiveProfile();
        UpdateAlwaysAllowedText();

        // 切会话时如果 Provider 还没配好，历史先存在这儿 —— 现在补装回去（system 那句在上面已经生成）。
        var pending = _pendingRestore;
        _pendingRestore = null;
        _pendingRestoreWorkspace = null;
        if (pending is { Count: > 0 })
        {
            session.RestoreHistory(WithSystemPrompt(workspace, pending));
        }

        return session;
    }

    private string DescribeActiveProfile()
    {
        var profile = _store.ActiveProfile;
        if (profile is null)
        {
            return "(no provider configured yet)";
        }

        var key = profile.HasApiKey ? "key=set" : profile.HasUndecryptableKey ? "key=undecryptable" : "key=missing";
        return $"{profile.Name} @ {profile.BaseUrl} / {profile.Model} / {key}";
    }

    /// <summary>把一段改动送回 UI 线程（本类的方法可能在 Core 的线程池线程上被调）。</summary>
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

    /// <summary>在 UI 线程上跑一段"会弹窗的异步逻辑"并等它的结果（模态框必须在 UI 线程上建）。</summary>
    private async Task<T> AskOnUiAsync<T>(Func<Task<T>> ask)
    {
        var ui = _ui;
        if (ui is null || ReferenceEquals(ui, SynchronizationContext.Current))
        {
            return await ask().ConfigureAwait(false);
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(
            async _ =>
            {
                try
                {
                    tcs.TrySetResult(await ask().ConfigureAwait(true));
                }
                catch (OperationCanceledException ex)
                {
                    tcs.TrySetCanceled(ex.CancellationToken);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            },
            null);

        return await tcs.Task.ConfigureAwait(false);
    }

    private static string Describe(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}";
        if (ex.InnerException is not null)
        {
            text += $" ({ex.InnerException.GetType().Name}: {ex.InnerException.Message})";
        }

        return text;
    }
}
