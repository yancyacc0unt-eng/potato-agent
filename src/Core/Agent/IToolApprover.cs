using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Core.Agent;

/// <summary>用户对一次"要动他电脑"的工具调用给出的答复。</summary>
public enum ToolApprovalDecision
{
    /// <summary>拒绝。这一次不执行，模型会收到一条"被拒绝"的工具结果。</summary>
    Deny = 0,

    /// <summary>只允许这一次。下次同样的工具还会再问。</summary>
    AllowOnce = 1,

    /// <summary>允许，并且在本次会话里不再问同一个工具（用户勾了"以后别再问我"）。</summary>
    AllowAlways = 2,
}

/// <summary>
/// 一次待批准的工具调用。界面拿它弹确认框，必须把 <see cref="Risk"/> 和 <see cref="ArgumentsJson"/> 原样展示给用户。
/// </summary>
/// <param name="ToolName">工具名，例如 <c>pc_click</c>。</param>
/// <param name="ToolDescription">工具自己的英文说明（可以给用户看个大意）。</param>
/// <param name="Risk">危险等级，一定不是 <see cref="ToolRisk.Safe"/>（安全的根本不会来问）。</param>
/// <param name="Arguments">模型给的参数（已解析）。</param>
/// <param name="ArgumentsJson">模型给的参数原文（原样展示，别美化 —— 用户要看的就是它）。</param>
public sealed record ToolApprovalRequest(
    string ToolName,
    string ToolDescription,
    ToolRisk Risk,
    JsonElement Arguments,
    string ArgumentsJson)
{
    /// <summary>确认框标题用的一句话，例如 <c>Allow pc_click? (risk: Confirm)</c>。</summary>
    public string Title => $"Allow {ToolName}? (risk: {Risk})";
}

/// <summary>
/// 权限门：<see cref="ToolRisk.Confirm"/> / <see cref="ToolRisk.Dangerous"/> 的工具在真正执行前，
/// 必须由界面实现的这个东西点头。
/// </summary>
/// <remarks>
/// <para>
/// <b>没有 approver 就等于全部拒绝</b>（fail-closed）。这条是硬约定：
/// 忘了给 AgentSession 挂 approver 的后果必须是"危险工具跑不了"，而不是"危险工具静默乱点用户的电脑"。
/// </para>
/// <para>
/// 实现方（WPF 的确认框、控制台问答、自动化脚本）注意：<b>不要在这里抛异常来表达拒绝</b>，
/// 返回 <see cref="ToolApprovalDecision.Deny"/>；抛异常会被 AgentSession 当成"批准流程坏了"并同样按拒绝处理，
/// 但用户看到的原因会是一句技术错误而不是"你自己点了拒绝"。
/// </para>
/// <para>取消（<see cref="OperationCanceledException"/>）会原样上抛给对话主循环。</para>
/// </remarks>
public interface IToolApprover
{
    /// <summary>问一次。返回决定；界面被关掉/超时之类的情况请返回 <see cref="ToolApprovalDecision.Deny"/>。</summary>
    /// <param name="request">待批准的工具调用。</param>
    /// <param name="ct">取消令牌。</param>
    Task<ToolApprovalDecision> ApproveAsync(ToolApprovalRequest request, CancellationToken ct);
}

/// <summary>
/// 把一段回调包成 <see cref="IToolApprover"/>，给简单界面 / 测试用。
/// </summary>
/// <remarks>正式界面请自己实现 <see cref="IToolApprover"/> 以便把确认框做成模态。</remarks>
public sealed class DelegateToolApprover : IToolApprover
{
    private readonly Func<ToolApprovalRequest, CancellationToken, Task<ToolApprovalDecision>> _callback;

    /// <summary>用一段异步回调建一个 approver。</summary>
    public DelegateToolApprover(Func<ToolApprovalRequest, CancellationToken, Task<ToolApprovalDecision>> callback)
    {
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    /// <summary>用一段同步回调建一个 approver（控制台 <c>Console.ReadLine</c> 这类）。</summary>
    public DelegateToolApprover(Func<ToolApprovalRequest, ToolApprovalDecision> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callback = (request, _) => Task.FromResult(callback(request));
    }

    /// <summary>固定返回同一个决定（测试 / 全自动模式用）。</summary>
    public static DelegateToolApprover Always(ToolApprovalDecision decision) =>
        new DelegateToolApprover(new Func<ToolApprovalRequest, CancellationToken, Task<ToolApprovalDecision>>(
            (_, _) => Task.FromResult(decision)));

    /// <inheritdoc />
    public Task<ToolApprovalDecision> ApproveAsync(ToolApprovalRequest request, CancellationToken ct) =>
        _callback(request, ct);
}
