using System.Windows;
using PotatoAgent.Core.Agent;

namespace GUI;

/// <summary>
/// 危险工具的权限确认框（临时脚手架）：模态问一句"允许一次 / 总是允许 / 拒绝"。
/// </summary>
/// <remarks>
/// <para>入口是 <see cref="Ask"/>，由 <c>ChatViewModel.ApprovalHandler</c> 挂上。
/// <b>模态</b>：确认框关掉之前那一轮对话会一直等着。</para>
/// <para><b>失败一律按拒绝</b>：用户点 X 关窗、按 Esc、或者对话被取消，返回的都是
/// <see cref="ToolApprovalDecision.Deny"/>，不会出现"没回答于是当成同意"。</para>
/// </remarks>
public partial class ToolApprovalWindow : Window
{
    private ToolApprovalDecision _decision = ToolApprovalDecision.Deny;

    private ToolApprovalWindow() => InitializeComponent();

    /// <summary>
    /// 弹一个模态确认框并等用户回答。<b>必须在 UI 线程上调用</b>
    /// （<c>ChatViewModel</c> 已经保证这一点）。
    /// </summary>
    /// <param name="owner">父窗口，用来居中并禁用主窗口。</param>
    /// <param name="request">待批准的工具调用（工具名 / 风险 / 参数原文）。</param>
    /// <param name="ct">取消令牌：对话被取消时确认框会自动关掉并按拒绝处理。</param>
    /// <returns>用户的决定。</returns>
    public static Task<ToolApprovalDecision> Ask(Window owner, ToolApprovalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(ToolApprovalDecision.Deny);
        }

        var window = new ToolApprovalWindow
        {
            DataContext = new ApprovalView(request),
        };

        if (owner is not null && owner.IsLoaded)
        {
            window.Owner = owner;
        }

        using var registration = ct.Register(() => window.Dispatcher.BeginInvoke(new Action(window.Close)));

        window.ShowDialog();
        return Task.FromResult(window._decision);
    }

    private void AllowOnce_Click(object sender, RoutedEventArgs e)
    {
        _decision = ToolApprovalDecision.AllowOnce;
        Close();
    }

    private void AllowAlways_Click(object sender, RoutedEventArgs e)
    {
        _decision = ToolApprovalDecision.AllowAlways;
        Close();
    }

    /// <summary>确认框显示用的只读数据壳；没有任何逻辑，纯粹是把 <see cref="ToolApprovalRequest"/> 摊平给 XAML。</summary>
    public sealed class ApprovalView
    {
        /// <summary>建一个只读视图对象。</summary>
        public ApprovalView(ToolApprovalRequest request)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
        }

        /// <summary>原始请求。类型 <see cref="ToolApprovalRequest"/>，只读。</summary>
        public ToolApprovalRequest Request { get; }

        /// <summary>标题，例如 <c>Allow pc_click? (risk: Confirm)</c>。类型 <see cref="string"/>，只读。</summary>
        public string Title => Request.Title;

        /// <summary>风险文案。类型 <see cref="string"/>，只读。</summary>
        public string RiskLine => $"Risk: {Request.Risk}. This executes on this computer. Read the arguments before allowing.";

        /// <summary>工具自己的说明。类型 <see cref="string"/>，只读。</summary>
        public string Description => Request.ToolDescription;

        /// <summary>模型给的参数原文（原样展示，不美化）。类型 <see cref="string"/>，只读。</summary>
        public string ArgumentsJson => string.IsNullOrWhiteSpace(Request.ArgumentsJson)
            ? "(no arguments)"
            : Request.ArgumentsJson;
    }
}
