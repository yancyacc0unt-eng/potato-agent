using PotatoAgent.Core.Tools;

namespace PotatoAgent.Win32.Tools;

/// <summary>
/// 把 <c>pc_*</c> 这一整套工具一次性注册进 <see cref="ToolRegistry"/>。
/// </summary>
/// <remarks>
/// GUI 侧只要一行：<c>PcTools.RegisterAll(registry);</c>
/// 依赖方向是 Win32 → Core，符合本项目唯一允许的那条引用方向。
/// </remarks>
public static class PcTools
{
    /// <summary>注册全部电脑控制工具（<c>pc_state</c> / <c>pc_windows</c> / <c>pc_screenshot</c> /
    /// <c>pc_click</c> / <c>pc_type</c> / <c>pc_keys</c> / <c>pc_launch</c> / <c>pc_close_window</c>）。</summary>
    /// <param name="registry">目标注册表。</param>
    /// <param name="skipExisting">true = 已存在同名工具时跳过（默认 false，重名直接抛）。</param>
    /// <returns>实际注册进去的数量。</returns>
    public static int RegisterAll(ToolRegistry registry, bool skipExisting = false)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var tools = CreateAll();
        var registered = 0;

        foreach (var tool in tools)
        {
            var ok = skipExisting ? registry.TryRegister(tool) : RegisterAndReport(registry, tool);
            if (ok)
            {
                registered++;
            }
        }

        return registered;
    }

    /// <summary>建出全部工具的实例（不注册；测试里想自己控制注册顺序时用）。</summary>
    public static IReadOnlyList<ITool> CreateAll() => new ITool[]
    {
        new PcStateTool(),
        new PcWindowsTool(),
        new PcScreenshotTool(),
        new PcClickTool(),
        new PcTypeTool(),
        new PcKeysTool(),
        new PcLaunchTool(),
        new PcCloseWindowTool(),
    };

    private static bool RegisterAndReport(ToolRegistry registry, ITool tool)
    {
        registry.Register(tool);
        return true;
    }
}
