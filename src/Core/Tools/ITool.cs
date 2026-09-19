using System.Text.Json;

namespace PotatoAgent.Core.Tools;

/// <summary>
/// 一个可以被模型调用的工具。六个页签（Chat / Control / Files / Tasks / Memory / Board）共用这一套契约，
/// 不给任何页签另开小灶。
/// </summary>
/// <remarks>
/// 实现约定：
/// <list type="bullet">
/// <item><see cref="Name"/> 必须是小写 snake_case（<c>pc_screenshot</c> 这种），OpenAI 兼容接口对名字有字符限制。</item>
/// <item><see cref="Description"/> 写英文，是给模型看的提示词，不是给用户看的界面文案。</item>
/// <item><see cref="ParametersJsonSchema"/> 是 JSON Schema 对象字面量，例如
/// <c>{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}</c>。
/// 没有参数也要给 <c>{"type":"object","properties":{}}</c>。</item>
/// <item>实现里**可以随便抛异常**：<see cref="ToolRegistry"/> 会统一吞成失败结果，绝不会炸掉对话主循环。</item>
/// </list>
/// </remarks>
public interface ITool
{
    /// <summary>工具名，小写 snake_case，例如 <c>pc_screenshot</c>。全局唯一。</summary>
    string Name { get; }

    /// <summary>英文描述，写清"这个工具干什么、什么时候用"，模型靠它选工具。</summary>
    string Description { get; }

    /// <summary>参数的 JSON Schema 字符串（对象形式）。</summary>
    string ParametersJsonSchema { get; }

    /// <summary>危险等级，决定界面是否弹确认。</summary>
    ToolRisk Risk { get; }

    /// <summary>
    /// 执行工具。<paramref name="args"/> 是模型给出的参数对象（一定是个 JSON object，但字段可能缺、可能类型不对，
    /// 实现里自己兜住）；<paramref name="ct"/> 取消时要尽量及时退出。
    /// 抛出的任何异常都会被 <see cref="ToolRegistry"/> 转成 <see cref="ToolResult"/> 失败结果。
    /// </summary>
    Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct);
}
