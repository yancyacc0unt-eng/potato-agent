using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Win32.Tools;

/// <summary>
/// <c>pc_keys</c>：发按键 / 快捷键（<c>^a</c> 全选、<c>{ENTER}</c>、<c>{ESC}</c>、<c>^s</c>…）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ToolRisk.Confirm"/> —— 会真的敲用户的键盘，必须先过权限门。
/// </para>
/// <para>
/// 这是<b>按键</b>不是<b>文本</b>：按键会经过当前输入法，字母键可能被当成拼音吞掉。
/// 要输入确定的文本请用 <c>pc_type</c>。
/// </para>
/// </remarks>
public sealed class PcKeysTool : ITool
{
    /// <inheritdoc />
    public string Name => "pc_keys";

    /// <inheritdoc />
    public string Description =>
        "Send keystrokes or shortcuts to a window: '^a' = Ctrl+A, '^s' = Ctrl+S, '%{F4}' = Alt+F4, " +
        "'{ENTER}', '{ESC}', '{TAB}', '{DELETE}', '{DOWN}'. " +
        "This sends real key events to the user's machine - it needs the user's confirmation. " +
        "IMPORTANT: these are KEYSTROKES, not text. They pass through the active IME, so letters can be swallowed " +
        "or turned into Chinese. To enter text always use pc_type.";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "keys": {
              "type": "string",
              "description": "Key expression, e.g. \"^a\", \"^s\", \"%{F4}\", \"{ENTER}\", \"^c^v\"."
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
          "required": ["keys"]
        }
        """;

    /// <inheritdoc />
    public ToolRisk Risk => ToolRisk.Confirm;

    /// <inheritdoc />
    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var keys = args.Text("keys");
        if (string.IsNullOrWhiteSpace(keys))
        {
            return Task.FromResult(ToolResult.Error("The 'keys' argument is required. Nothing was sent."));
        }

        if (!ToolWindow.TryResolve(args, out var target, out var targetError))
        {
            return Task.FromResult(ToolResult.Error(targetError!));
        }

        var control = new ComputerControl(target);

        var focusNote = string.Empty;
        if (Desktop.Foreground() != target)
        {
            var focused = control.FocusWindow(target);
            if (!focused.Ok)
            {
                return Task.FromResult(ToolResult.Error(
                    $"Could not bring the target window to the front, so no keys were sent. {focused.Message}"));
            }

            focusNote = " The target window was focused first.";
        }

        var result = control.SendKeys(keys!);

        return Task.FromResult(result.Ok
            ? ToolResult.Ok(result.Message + focusNote)
            : ToolResult.Error(result.Message));
    }
}
