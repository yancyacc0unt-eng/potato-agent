namespace PotatoAgent.Core;

/// <summary>
/// Core 层占位类型。
/// 这里是与 UI 无关的纯逻辑层（会话、任务、记忆、文件索引等后续都放这层），
/// 现在只提供最小可编译的骨架，方便 GUI 先引用起来。
/// </summary>
public static class PotatoAgentCore
{
    /// <summary>产品名，GUI 标题栏等地方复用。</summary>
    public const string ProductName = "potatoAgent";

    /// <summary>占位方法，后续会被真正的业务入口替换。</summary>
    public static string Describe() => $"{ProductName} core (placeholder)";
}
