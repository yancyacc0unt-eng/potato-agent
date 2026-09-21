namespace PotatoAgent.Core.Tools;

/// <summary>
/// 工具权限档位：决定哪些"会动用户电脑"的调用需要先弹确认框。
/// </summary>
/// <remarks>
/// <para>分档只看 <see cref="ToolRisk"/>，跟具体是哪个工具无关：</para>
/// <list type="bullet">
/// <item><see cref="Basic"/>：<see cref="ToolRisk.Confirm"/> 和 <see cref="ToolRisk.Dangerous"/> 都要先问用户。</item>
/// <item><see cref="Advanced"/>：只有 <see cref="ToolRisk.Dangerous"/> 才问，<see cref="ToolRisk.Confirm"/> 直接放行。</item>
/// </list>
/// <para><see cref="ToolRisk.Safe"/> 在任何档位下都不会问；用户点过"总是允许"的工具同样不再问。</para>
/// <para>档位只是"要不要弹确认框"，<b>不改变工具本身的危险等级，也不改变 fail-closed 的底线</b>：
/// 没挂 approver 时，需要问的调用一律拒绝（见 <c>AgentSession.DecideAsync</c>）。</para>
/// </remarks>
public enum ToolApprovalMode
{
    /// <summary>基础：Confirm / Dangerous 都先问用户一次（默认，最安全）。</summary>
    Basic = 0,

    /// <summary>高级：只有 Dangerous 才问，Confirm 静默放行（几乎不打断用户）。</summary>
    Advanced = 1,
}

/// <summary>
/// <see cref="ToolApprovalMode"/> 与配置文件里的字符串（<c>"basic"</c> / <c>"advanced"</c>）之间的转换。
/// </summary>
/// <remarks>
/// 配置里刻意存字符串而不是枚举序号：用户手改 <c>config.json</c> 写错一个词时，
/// 只该退回默认档，不该因为一个 JsonException 把整份配置判成损坏。
/// </remarks>
public static class ToolApprovalModeNames
{
    /// <summary>基础档在配置里的写法。</summary>
    public const string Basic = "basic";

    /// <summary>高级档在配置里的写法。</summary>
    public const string Advanced = "advanced";

    /// <summary>全部档位名，按"从严到宽"排列（界面下拉框可以直接绑它）。</summary>
    public static readonly string[] All = { Basic, Advanced };

    /// <summary>枚举 → 配置里的字符串。</summary>
    public static string ToName(ToolApprovalMode mode) =>
        mode == ToolApprovalMode.Advanced ? Advanced : Basic;

    /// <summary>
    /// 配置里的字符串 → 枚举。null / 空 / 拼错 / 大小写不一都算 <see cref="ToolApprovalMode.Basic"/>。
    /// </summary>
    public static ToolApprovalMode Parse(string? name) =>
        string.Equals(name?.Trim(), Advanced, StringComparison.OrdinalIgnoreCase)
            ? ToolApprovalMode.Advanced
            : ToolApprovalMode.Basic;
}
