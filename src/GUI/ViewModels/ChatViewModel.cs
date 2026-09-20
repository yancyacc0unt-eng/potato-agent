using System.Collections.ObjectModel;
using PotatoAgent.Core.Agent;
using PotatoAgent.Core.Brain;
using PotatoAgent.Core.Tools;

namespace GUI.ViewModels;

/// <summary>
/// 聊天页的 ViewModel：把 <see cref="AgentSession.SendAsync"/> 的事件流翻译成可绑定的界面数据，
/// 并实现 <see cref="IToolApprover"/> 做危险工具的权限确认。
/// </summary>
/// <remarks>
/// <para><b>界面怎么接</b>：把本对象设成聊天页的 <c>DataContext</c>。</para>
/// <list type="bullet">
/// <item><c>ItemsControl/ListBox.ItemsSource="{Binding Entries}"</c> —— 每条 <see cref="ChatEntryViewModel"/> 一行。</item>
/// <item><c>TextBox.Text="{Binding InputText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"</c> —— 输入框。</item>
/// <item><c>Button.Command="{Binding SendCommand}"</c> / <c>CancelCommand</c> / <c>ClearCommand</c>。</item>
/// <item><c>TextBlock.Text="{Binding StatusMessage}"</c> —— 一行状态；<c>{Binding IsBusy}</c> 可以驱动转圈。</item>
/// </list>
/// <para><b>权限确认怎么接</b>：本类自己实现了 <see cref="IToolApprover"/>。界面只要在启动时挂一次
/// <see cref="ApprovalHandler"/>（弹出自己的确认框并把用户的选择返回即可）。
/// <b>没挂 handler 时一律拒绝</b>（fail-closed，不会出现"忘了接于是乱点用户电脑"）。</para>
/// <para><b>线程</b>：所有可绑定属性的改动都会被送回创建本对象的那条线程（WPF 的 UI 线程），界面不用自己 Dispatcher。</para>
/// </remarks>
public sealed class ChatViewModel : ObservableObject, IToolApprover
{
    /// <summary>
    /// 临时系统提示词。等 T4 记忆层做完会换成"固定头部 + 全局 md + 工作区 md"，
    /// 现在先写死一段，保证模型知道自己是干什么的、以及哪些工具要先问用户。
    /// </summary>
    private const string SystemPrompt =
        "You are potatoAgent, an assistant running on the user's own Windows PC. " +
        "You can observe and control this computer through the pc_* tools. " +
        "Look before you act: use pc_state / pc_windows / pc_screenshot first when you are unsure. " +
        "pc_state, pc_windows and pc_screenshot are read-only; pc_click, pc_type, pc_keys and pc_launch change the machine " +
        "and will be shown to the user for approval before they run. " +
        "Answer in the user's language, keep answers short, and never claim you did something you did not actually do.";

    private readonly ConfigStore _store;
    private readonly ToolRegistry _tools;
    private readonly SynchronizationContext? _ui;

    private readonly Dictionary<string, ChatEntryViewModel> _toolEntries = new(StringComparer.Ordinal);

    private AgentSession? _session;
    private CancellationTokenSource? _cts;
    private ChatEntryViewModel? _currentAssistant;
    private ChatEntryViewModel? _lastToolEntry;

    private string _inputText = string.Empty;
    private string _statusMessage = "Ready. Fill in Settings first if you have not saved a profile yet.";
    private bool _isBusy;
    private bool _isAwaitingApproval;
    private string? _pendingApprovalTool;
    private string _profileSummary = "(no provider configured yet)";
    private string _alwaysAllowedText = "(none)";

    /// <summary>建一个聊天页 ViewModel。<b>请在 UI 线程上构造</b>（它会记住当前线程用来回送属性通知）。</summary>
    /// <param name="store">配置仓库：每轮对话前用它拿当前档案建 Provider。</param>
    /// <param name="tools">工具表（记得先 <c>PcTools.RegisterAll(registry)</c>）。</param>
    public ChatViewModel(ConfigStore store, ToolRegistry tools)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _ui = SynchronizationContext.Current;

        SendCommand = new AsyncRelayCommand(
            SendAsync,
            () => !IsBusy,
            ex => AddError(Describe(ex)));
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        ClearCommand = new RelayCommand(ClearConversation, () => !IsBusy);
        ResetApprovalsCommand = new RelayCommand(ResetApprovals, () => !IsBusy);
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
        StatusMessage = "Settings applied: the next message will use the new profile. Conversation history was reset.";
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

        var session = _session;
        if (session is null)
        {
            try
            {
                session = EnsureSession();
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
                StatusMessage = starting.NeedsApproval
                    ? $"Calling {starting.Name} (risk {starting.Risk}) - waiting for your approval..."
                    : $"Calling {starting.Name} (risk {starting.Risk})...";
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

    // ==================== 内部：会话与线程 ====================

    /// <summary>按当前档案建会话。配置不全时抛 <see cref="ProviderConfigurationException"/>（调用方翻译成人话）。</summary>
    private AgentSession EnsureSession()
    {
        var provider = OpenAiProvider.FromActiveProfile(_store);
        var session = new AgentSession(
            provider,
            _tools,
            this,
            new[] { ChatMessage.System(SystemPrompt) });

        _session = session;
        ProfileSummary = DescribeActiveProfile();
        UpdateAlwaysAllowedText();
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
