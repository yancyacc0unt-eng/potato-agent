using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Win32.Tools;

/// <summary>
/// <c>pc_type</c>：往目标窗口输入一段<b>文本</b>（中文也认得）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ToolRisk.Confirm"/> —— 会真的往用户窗口里打字，必须先过权限门。
/// </para>
/// <para>
/// 走 <see cref="ComputerControl.TypeText"/>：优先 UIA 的 <c>ValuePattern.SetValue</c>（整篇覆盖），
/// 拿不到 UIA 就退回 SendInput 的 Unicode 逐字输入（在光标处插入）。
/// <b>不要用 <c>pc_keys</c> 打文本</b>——按键会经过输入法，中文会被吃掉。
/// </para>
/// </remarks>
public sealed class PcTypeTool : ITool
{
    /// <inheritdoc />
    public string Name => "pc_type";

    /// <inheritdoc />
    public string Description =>
        "Type a string of TEXT into a window (Chinese and any other Unicode are supported). " +
        "This sends real input to the user's machine - it needs the user's confirmation. " +
        "With prefer_uia = true (default) it sets the whole text field value at once, which OVERWRITES existing content; " +
        "with prefer_uia = false it inserts like a human typing at the caret. " +
        "Always use this - not pc_keys - to enter text, because keystrokes go through the active IME and get mangled. " +
        "Use \"\\r\\n\" for line breaks (a lone \\n is dropped by some controls). " +
        "The result is verified by reading the text back; a false success is never reported.";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "text": { "type": "string", "description": "The exact text to enter. Use \r\n for new lines." },
            "prefer_uia": {
              "type": "boolean",
              "description": "true (default) = set the whole field value via UI Automation (overwrites). false = insert at the caret like typing."
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
          "required": ["text"]
        }
        """;

    /// <inheritdoc />
    public ToolRisk Risk => ToolRisk.Confirm;

    /// <inheritdoc />
    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var text = args.Text("text");
        if (text is null)
        {
            return Task.FromResult(ToolResult.Error("The 'text' argument is required. Nothing was typed."));
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
                    $"Could not bring the target window to the front, so nothing was typed. {focused.Message}"));
            }

            focusNote = " The target window was focused first.";
        }

        var result = control.TypeText(text, args.Flag("prefer_uia", true));

        return Task.FromResult(result.Ok
            ? ToolResult.Ok(result.Message + focusNote)
            : ToolResult.Error(result.Message));
    }
}
