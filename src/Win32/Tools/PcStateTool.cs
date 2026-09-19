using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Win32.Tools;

/// <summary>
/// <c>pc_state</c>：一次回答"我现在在哪"—— 前台窗口、鼠标位置、屏幕尺寸与 DPI。
/// </summary>
/// <remarks>只读，<see cref="ToolRisk.Safe"/>。比截图便宜得多，模型想了解处境时应该先调它。</remarks>
public sealed class PcStateTool : ITool
{
    /// <inheritdoc />
    public string Name => "pc_state";

    /// <inheritdoc />
    public string Description =>
        "Get the current desktop state: the foreground window (title, process, pid, bounds, whether it is minimized), " +
        "the mouse pointer position, and the screen layout (monitors, size, DPI scale). " +
        "Cheap and instant - prefer this over pc_screenshot when you only need to know where things are. " +
        "All coordinates are physical screen pixels of the whole virtual desktop.";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {},
          "required": []
        }
        """;

    /// <inheritdoc />
    public ToolRisk Risk => ToolRisk.Safe;

    /// <inheritdoc />
    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var state = Desktop.State(new System.Text.Json.Nodes.JsonObject());
        return Task.FromResult(ToolResult.OkJson(
            "Current desktop state. Coordinates are physical screen pixels; x grows right, y grows down.",
            state.ToJsonString()));
    }
}
