namespace GUI.ViewModels;

/// <summary>
/// 聊天记录里的一行。角色有四种：<c>user</c> / <c>assistant</c> / <c>tool</c> / <c>error</c>。
/// </summary>
/// <remarks>
/// <para>
/// 界面上可以只用一个 <c>ItemsControl</c> 绑 <see cref="ChatViewModel.Entries"/>，
/// 每项按 <see cref="IsUser"/> / <see cref="IsAssistant"/> / <see cref="IsTool"/> / <see cref="IsError"/>
/// 这四组布尔值做 DataTrigger 换样式、换对齐、换气泡颜色。
/// </para>
/// <para>
/// 工具调用的那一行用 <see cref="ToolStatus"/> 表示生命周期：
/// 生成时是 <c>"running..."</c>（要审批时是 <c>"waiting for your approval..."</c>），
/// 跑完改成 <c>"done"</c> / <c>"failed"</c>，被拒绝改成 <c>"denied"</c>。
/// <see cref="ToolSucceeded"/> 为 null 表示"还没结果"。
/// </para>
/// </remarks>
public sealed class ChatEntryViewModel : ObservableObject
{
    private string _text = string.Empty;
    private bool _isStreaming;
    private string _toolStatus = string.Empty;
    private string _toolSummary = string.Empty;
    private bool? _toolSucceeded;
    private string _toolElapsedText = string.Empty;
    private int _toolImageCount;

    private ChatEntryViewModel(string role)
    {
        Role = role;
        Timestamp = DateTime.Now;
    }

    // ==================== 工厂（只有这几种行，别在别处 new） ====================

    /// <summary>用户自己说的话。</summary>
    public static ChatEntryViewModel User(string text) => new("user") { Text = text };

    /// <summary>模型的一段回答（会随流式增量不断追加文本）。</summary>
    public static ChatEntryViewModel Assistant() => new("assistant") { IsStreaming = true };

    /// <summary>一条工具调用过程行（模型刚开始要调用工具时就插进来）。</summary>
    public static ChatEntryViewModel ToolCall(string callId, string name, string argumentsJson, string risk, bool needsApproval)
        => new("tool")
        {
            ToolCallId = callId,
            ToolName = name,
            ToolArguments = argumentsJson,
            ToolRisk = risk,
            ToolStatus = needsApproval ? "waiting for your approval..." : "running...",
        };

    /// <summary>一条错误提示行（网络失败、配置不全、进程序列化失败…）。</summary>
    public static ChatEntryViewModel Error(string text) => new("error") { Text = text };

    // ==================== 可绑定属性 ====================

    /// <summary>角色，取值 <c>user</c> / <c>assistant</c> / <c>tool</c> / <c>error</c>。类型 <see cref="string"/>，只读。</summary>
    public string Role { get; }

    /// <summary>这一行的正文。类型 <see cref="string"/>，双向可写（流式回答会不断追加）。</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(MarkdownText));
            }
        }
    }

    /// <summary>这一行是不是用户发的。类型 <see cref="bool"/>，只读（给 DataTrigger 用）。</summary>
    public bool IsUser => Role == "user";

    /// <summary>这一行是不是模型回答。类型 <see cref="bool"/>，只读。</summary>
    public bool IsAssistant => Role == "assistant";

    /// <summary>这一行是不是工具调用过程。类型 <see cref="bool"/>，只读。</summary>
    public bool IsTool => Role == "tool";

    /// <summary>这一行是不是错误。类型 <see cref="bool"/>，只读。</summary>
    public bool IsError => Role == "error";

    /// <summary>
    /// 模型还在往这一行里吐字。类型 <see cref="bool"/>，只读。
    /// 界面可以绑它做"正在输入"的光标；一轮结束（或插入工具行）后自动变 false。
    /// </summary>
    public bool IsStreaming
    {
        get => _isStreaming;
        set => SetProperty(ref _isStreaming, value);
    }

    /// <summary>这一行是什么时候产生的。类型 <see cref="DateTime"/>，只读。</summary>
    public DateTime Timestamp { get; }

    /// <summary>时间戳，<c>HH:mm:ss</c> 文本。类型 <see cref="string"/>，只读。</summary>
    public string TimeText => Timestamp.ToString("HH:mm:ss");

    /// <summary>
    /// 给"最朴素渲染"用的一行文本（列表里直接 <c>Text="{Binding DisplayText}"</c> 就能看）。
    /// 类型 <see cref="string"/>，只读，随其他属性自动更新。做正式气泡时可以不绑它。
    /// </summary>
    public string DisplayText => Role switch
    {
        "user" => $"you: {Text}",
        "assistant" => $"agent: {Text}",
        "error" => $"ERROR: {Text}",
        "tool" => BuildToolLine(),
        _ => $"{Role}: {Text}",
    };

    /// <summary>
    /// 交给 Markdown 控件渲染的正文（<c>MarkdownViewer.Markdown</c> 绑它）。类型 <see cref="string"/>，只读。
    /// </summary>
    /// <remarks>
    /// 普通行就是 <see cref="Text"/> 原文（用户 / 模型 / 报错都算），工具行没有 Markdown 正文，
    /// 给的是 <see cref="BuildToolLine"/> 那一行摘要 —— 否则工具行会渲染成空白，工具生命周期就看不见了。
    /// 和 <see cref="DisplayText"/> 的唯一差别：这里【不带】<c>you:</c> / <c>agent:</c> / <c>ERROR:</c> 前缀。
    /// 前缀会粘在首行上，把首行的 Markdown 语法（<c># 标题</c>、围栏等）废掉。
    /// </remarks>
    public string MarkdownText => IsTool ? BuildToolLine() : Text;

    // ==================== 工具行专用（非工具行保持默认值即可） ====================

    /// <summary>这次工具调用的 id（模型给的，用来配对结果）。类型 <see cref="string"/>，只读。</summary>
    public string ToolCallId { get; private init; } = string.Empty;

    /// <summary>工具名，例如 <c>pc_screenshot</c>。类型 <see cref="string"/>，只读。</summary>
    public string ToolName { get; private init; } = string.Empty;

    /// <summary>模型给的参数原文（JSON 字符串）。类型 <see cref="string"/>，只读。</summary>
    public string ToolArguments { get; private init; } = string.Empty;

    /// <summary>危险等级文本：<c>Safe</c> / <c>Confirm</c> / <c>Dangerous</c>。类型 <see cref="string"/>，只读。</summary>
    public string ToolRisk { get; private init; } = string.Empty;

    /// <summary>
    /// 工具这一行的状态文本：<c>running...</c> / <c>waiting for your approval...</c> / <c>done</c> /
    /// <c>failed</c> / <c>denied</c> / <c>cancelled</c>。类型 <see cref="string"/>，只读。
    /// </summary>
    public string ToolStatus
    {
        get => _toolStatus;
        private set
        {
            if (SetProperty(ref _toolStatus, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(MarkdownText));
            }
        }
    }

    /// <summary>结果摘要（Core 已经截断成一行，可能以 <c>ERROR: </c> 开头）。类型 <see cref="string"/>，只读。</summary>
    public string ToolSummary
    {
        get => _toolSummary;
        private set
        {
            if (SetProperty(ref _toolSummary, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(MarkdownText));
            }
        }
    }

    /// <summary>成功 / 失败 / null=还没结果。类型 <see cref="bool?"/>，只读。</summary>
    public bool? ToolSucceeded
    {
        get => _toolSucceeded;
        private set
        {
            if (SetProperty(ref _toolSucceeded, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(MarkdownText));
            }
        }
    }

    /// <summary>耗时文本，例如 <c>312 ms</c> / <c>1.42 s</c>；没结果时是空串。类型 <see cref="string"/>，只读。</summary>
    public string ToolElapsedText
    {
        get => _toolElapsedText;
        private set
        {
            if (SetProperty(ref _toolElapsedText, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(MarkdownText));
            }
        }
    }

    /// <summary>这次工具返回了几张图（0 = 纯文本）。类型 <see cref="int"/>，只读。</summary>
    public int ToolImageCount
    {
        get => _toolImageCount;
        private set
        {
            if (SetProperty(ref _toolImageCount, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(MarkdownText));
            }
        }
    }

    // ==================== 给 ChatViewModel 用的更新入口 ====================

    /// <summary>追加一段流式文本（模型逐块吐字时调）。</summary>
    public void AppendText(string delta)
    {
        if (!string.IsNullOrEmpty(delta))
        {
            Text += delta;
        }
    }

    /// <summary>工具跑完了，填结果。</summary>
    /// <param name="success">成没成。</param>
    /// <param name="summary">结果摘要。</param>
    /// <param name="elapsed">耗时。</param>
    /// <param name="imageCount">带回来几张图。</param>
    public void CompleteTool(bool success, string summary, TimeSpan elapsed, int imageCount)
    {
        ToolSucceeded = success;
        ToolSummary = summary;
        ToolElapsedText = FormatElapsed(elapsed);
        ToolImageCount = imageCount;
        ToolStatus = success ? "done" : "failed";
    }

    /// <summary>工具被权限门挡下了（没有执行）。</summary>
    public void DenyTool(string reason)
    {
        ToolSucceeded = false;
        ToolSummary = reason;
        ToolStatus = "denied";
    }

    /// <summary>工具因为用户取消而没跑完 / 没跑。</summary>
    public void CancelTool(string reason)
    {
        ToolSucceeded = false;
        ToolSummary = reason;
        ToolStatus = "cancelled";
    }

    /// <summary>改状态文本（审批等待、超时等）。</summary>
    public void SetToolStatus(string status) => ToolStatus = status;

    /// <summary>UIA / 调试时看得懂的一行（列表项的可访问名字就是它）。</summary>
    public override string ToString() => DisplayText;

    private string BuildToolLine()
    {
        var line = $"[tool] {ToolName} ({ToolRisk}) {ToolStatus}";

        if (ToolElapsedText.Length > 0)
        {
            line += $" in {ToolElapsedText}";
        }

        if (ToolImageCount > 0)
        {
            line += $" [{ToolImageCount} image(s)]";
        }

        if (ToolArguments.Length > 0)
        {
            line += $" args={ToolArguments}";
        }

        if (ToolSummary.Length > 0)
        {
            line += $" -> {ToolSummary}";
        }

        return line;
    }

    private static string FormatElapsed(TimeSpan elapsed)
        => elapsed.TotalSeconds >= 1
            ? $"{elapsed.TotalSeconds:0.00} s"
            : $"{elapsed.TotalMilliseconds:0} ms";
}
