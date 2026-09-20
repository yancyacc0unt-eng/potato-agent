using System.Text.Json;

namespace PotatoAgent.Core.Tools;

/// <summary>
/// 可选接口：工具能自己说"这次调用动的是哪一个资源（程序 / 文档 / 窗口）"。
/// <see cref="Agent.AgentSession"/> 用它做第二道护栏 ——<b>同一回合内，同名工具 + 同一资源只做一次</b>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：第一道护栏（<see cref="Agent.ToolCallSignature"/>）只认"参数<b>规范化后逐字节相同</b>"。
/// 真实事故：模型想开记事本，第一次调 <c>{"path":"notepad.exe"}</c>，第二次改写成
/// <c>{"path":"notepad.exe","wait_ms":8000}</c> —— 参数不一样，签名就不一样，护栏拦不住，于是又起一个。
/// </para>
/// <para>
/// 参数怎么写是模型的自由，但"动的是不是同一个东西"是客观事实 —— 所以这个事实由工具自己给出，
/// 而不是让护栏去猜参数的形状。
/// </para>
/// </remarks>
public interface IToolCallIdentity
{
    /// <summary>
    /// 这次调用认领的资源身份。同一个资源必须返回同一个字符串（比较时不区分大小写）。
    /// 返回 null / 空白 = 这次调用<b>不</b>参与身份护栏（例如显式要求"再来一个"的 force_new_instance）。
    /// </summary>
    /// <param name="args">已经解析好的参数对象（一定是 JSON object）。</param>
    string? IdentityOf(JsonElement args);
}
