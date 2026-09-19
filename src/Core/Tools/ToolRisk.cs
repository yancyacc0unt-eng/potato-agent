namespace PotatoAgent.Core.Tools;

/// <summary>
/// 工具的危险等级。界面层（Chat 页 / Tasks 页）按此决定是否弹确认框：
/// <see cref="Safe"/> 直接执行，<see cref="Confirm"/> 与 <see cref="Dangerous"/> 必须先让用户点确认。
/// Core 只负责如实报告等级，弹框是 UI 的事。
/// </summary>
public enum ToolRisk
{
    /// <summary>只读、无副作用，可以直接跑（例如截图、枚举窗口）。</summary>
    Safe = 0,

    /// <summary>有副作用但可预期，跑之前要用户确认一次（例如移动文件、点击按钮）。</summary>
    Confirm = 1,

    /// <summary>破坏性 / 难以撤销，确认框要写清"将要发生什么"（例如删除文件、关闭窗口）。</summary>
    Dangerous = 2,
}
