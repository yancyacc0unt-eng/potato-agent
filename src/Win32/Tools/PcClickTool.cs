using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Win32.Tools;

/// <summary>
/// <c>pc_click</c>：在屏幕物理坐标上点一下（或双击）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ToolRisk.Confirm"/> —— 它会真的动用户的鼠标，所以必须先过权限门。
/// </para>
/// <para>
/// 目标窗口：给了 <c>hwnd</c> / <c>window</c> 就用它，并且如果不是前台窗口会先把它聚焦
/// （会抢用户的前台，这正是要确认的原因）；什么都没给就用当前前台窗口。
/// 焦点门禁由 <see cref="ComputerControl"/> 在真正注入前再核对一次，前台被换掉就拒绝注入。
/// </para>
/// </remarks>
public sealed class PcClickTool : ITool
{
    /// <inheritdoc />
    public string Name => "pc_click";

    /// <inheritdoc />
    public string Description =>
        "Click the mouse at a point on the screen (physical pixels, as reported by pc_state / pc_screenshot). " +
        "This moves the real cursor and presses a real button on the user's machine - it needs the user's confirmation. " +
        "If 'hwnd' or 'window' is given and that window is not in front, it is focused first. " +
        "Nothing is injected unless the target window is the foreground window at the moment of the click. " +
        "Get the coordinates from pc_screenshot (convert image pixels to screen pixels using the mapping in its result) " +
        "or from pc_windows bounds.";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "x": { "type": "integer", "description": "Screen x in physical pixels." },
            "y": { "type": "integer", "description": "Screen y in physical pixels." },
            "button": {
              "type": "string",
              "enum": ["left", "right", "middle"],
              "description": "Mouse button, default left."
            },
            "count": {
              "type": "integer",
              "enum": [1, 2],
              "description": "1 = single click (default), 2 = double click."
            },
            "hwnd": {
              "type": "string",
              "description": "Target window handle from pc_windows. Omit to use the current foreground window."
            },
            "window": {
              "type": "string",
              "description": "Case-insensitive substring of the target window title (alternative to hwnd)."
            }
          },
          "required": ["x", "y"]
        }
        """;

    /// <inheritdoc />
    public ToolRisk Risk => ToolRisk.Confirm;

    /// <inheritdoc />
    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (args.Integer("x") is not { } x || args.Integer("y") is not { } y)
        {
            return Task.FromResult(ToolResult.Error(
                "Both 'x' and 'y' are required (physical screen pixels). Nothing was clicked."));
        }

        if (!ToolWindow.TryResolve(args, out var target, out var targetError))
        {
            return Task.FromResult(ToolResult.Error(targetError!));
        }

        var button = ParseButton(args.Text("button"));
        var count = args.Integer("count") ?? 1;
        if (count is not (1 or 2))
        {
            return Task.FromResult(ToolResult.Error("'count' must be 1 or 2. Nothing was clicked."));
        }

        var control = new ComputerControl(target);

        var focusNote = string.Empty;
        if (Desktop.Foreground() != target)
        {
            var focused = control.FocusWindow(target);
            if (!focused.Ok)
            {
                return Task.FromResult(ToolResult.Error(
                    $"Could not bring the target window to the front, so nothing was clicked. {focused.Message}"));
            }

            focusNote = " The target window was focused first.";
        }

        var result = count == 2
            ? control.DoubleClick(x, y, button)
            : control.Click(x, y, button);

        return Task.FromResult(result.Ok
            ? ToolResult.Ok(result.Message + focusNote)
            : ToolResult.Error(result.Message));
    }

    private static MouseButton ParseButton(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        _ => MouseButton.Left,
    };
}
