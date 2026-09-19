// 动作层所有方法的统一返回值。
//
// 规矩：成功/失败必须明说，绝不吞异常静默返回。
// 静默失败是这一层最危险的 bug —— 调用方以为点过了，其实什么都没发生。

namespace PotatoAgent.Win32;

/// <summary>一次动作的结果。<see cref="Ok"/> 为 false 时 <see cref="Message"/> 一定说清了为什么。</summary>
public readonly record struct ActionResult(bool Ok, string Message)
{
    public static ActionResult Success(string message) => new(true, message);

    public static ActionResult Failure(string message) => new(false, message);

    public override string ToString() => (Ok ? "OK   " : "FAIL ") + Message;
}
