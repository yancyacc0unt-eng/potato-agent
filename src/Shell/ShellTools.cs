using PotatoAgent.Core.Tools;
using PotatoAgent.Shell.Tools;

namespace PotatoAgent.Shell;

/// <summary>
/// 把受限的本地控制台工具（<c>pc_shell</c>）一次性注册进 <see cref="ToolRegistry"/>。
/// </summary>
/// <remarks>
/// <para>接线方只要一行：<c>ShellTools.RegisterAll(registry, () =&gt; workspace?.Path);</c></para>
/// <para>依赖方向是 Shell → Core，符合本项目唯一允许的那条引用方向。</para>
/// <para>
/// <b>这里是"自动化任务的脚本能力边界"落地的地方</b>（SPEC §9 的待定项）：自动化任务与聊天页共用同一个
/// <c>pc_shell</c>，也就是说任务允许跑 PowerShell，但【必须】走同一扇门 —— 同样是
/// <see cref="ToolRisk.Dangerous"/>，同样每次都要用户确认，没有"任务可以免确认"的旁路。
/// 换言之：能力上不设限（不做黑名单），流程上一律经用户点头。
/// </para>
/// </remarks>
public static class ShellTools
{
    /// <summary>
    /// 注册全部受限控制台工具（当前只有 <c>pc_shell</c> 一个）。
    /// </summary>
    /// <param name="registry">目标注册表，不能为 null。</param>
    /// <param name="workspaceRoot">
    /// 返回"当前工作区根目录"的回调；没有工作区时返回 null。
    /// 工具是在<b>每次调用时</b>才去问它的（用户中途切了工作区立刻生效），用来解析相对 <c>cwd</c> 与决定默认工作目录。
    /// 传 null 等同于"永远没有工作区"：相对 <c>cwd</c> 会直接报错，默认工作目录退回 %USERPROFILE%。
    /// </param>
    /// <param name="skipExisting">true = 遇到同名工具就跳过；false（默认）= 重名直接抛 <see cref="ArgumentException"/>。</param>
    /// <returns>实际注册进去的数量。</returns>
    public static int RegisterAll(ToolRegistry registry, Func<string?>? workspaceRoot = null, bool skipExisting = false)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var registered = 0;

        foreach (var tool in CreateAll(workspaceRoot))
        {
            var ok = skipExisting ? registry.TryRegister(tool) : RegisterAndReport(registry, tool);
            if (ok)
            {
                registered++;
            }
        }

        return registered;
    }

    /// <summary>建出全部工具的实例（不注册；自测里想自己控制注册顺序、或想直接调 <c>InvokeAsync</c> 时用）。</summary>
    /// <param name="workspaceRoot">同 <see cref="RegisterAll"/>：返回当前工作区根目录的回调，没有工作区返回 null。</param>
    /// <returns>工具列表（当前只有 <c>pc_shell</c> 一个）。</returns>
    public static IReadOnlyList<ITool> CreateAll(Func<string?>? workspaceRoot = null) => new ITool[]
    {
        new PcShellTool(workspaceRoot),
    };

    private static bool RegisterAndReport(ToolRegistry registry, ITool tool)
    {
        registry.Register(tool);
        return true;
    }
}
