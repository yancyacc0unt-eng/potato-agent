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
/// <para>
/// 有<b>可观测结果</b>的组合键（Alt+F4 / Ctrl+W 这种"应该把窗口关掉"的）发完必须回读核实：
/// 本机实测 Win11 记事本（WinUI）完全不响应注入的 Alt+F4，而 SendInput 会报"delivered"——
/// 于是模型以为关掉了、窗口还在。现在窗口还在就报失败，并指向 <c>pc_close_window</c>。
/// </para>
/// </remarks>
public sealed class PcKeysTool : ITool
{
    /// <summary>发完"应该关窗口"的按键之后，等窗口消失的时间。</summary>
    private const int CloseVerifyMs = 2500;

    /// <inheritdoc />
    public string Name => "pc_keys";

    /// <inheritdoc />
    public string Description =>
        "Send keystrokes or shortcuts to a window: '^a' = Ctrl+A, '^s' = Ctrl+S, '%{F4}' = Alt+F4, " +
        "'{ENTER}', '{ESC}', '{TAB}', '{DELETE}', '{DOWN}'. " +
        "This sends real key events to the user's machine - it needs the user's confirmation. " +
        "IMPORTANT: these are KEYSTROKES, not text. They pass through the active IME, so letters can be swallowed " +
        "or turned into Chinese. To enter text always use pc_type. " +
        "TO CLOSE A WINDOW use pc_close_window, NOT Alt+F4: many modern apps (Windows 11 Notepad) ignore an injected " +
        "Alt+F4 - the key is delivered, the window stays. When you do send a closing shortcut this tool waits and " +
        "checks whether the window actually went away, and reports FAILURE if it is still there.";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "keys": {
              "type": "string",
              "description": "Key expression, e.g. \"^a\", \"^s\", \"%{F4}\", \"{ENTER}\", \"^c^v\". Prefer pc_close_window over \"%{F4}\"."
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

        // 先解析一遍：解析不了就什么都不发（原来是发到一半才发现表达式不对）。
        List<KeyStroke> strokes;
        try
        {
            strokes = KeyParser.Parse(keys!);
        }
        catch (ArgumentException error)
        {
            return Task.FromResult(ToolResult.Error($"SendKeys(\"{keys}\"): {error.Message}"));
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
            ? VerifyIfObservable(result.Message + focusNote, target, strokes)
            : ToolResult.Error(result.Message));
    }

    /// <summary>
    /// 有可观测结果的组合键（Alt+F4 / Ctrl+W：本意都是"关掉这个窗口"）发完要回读核实。
    /// 窗口还在就<b>报失败</b>，并说清下一步该用 <c>pc_close_window</c> ——
    /// 只报"delivered N events"就是这次事故里骗了模型的那句话。
    /// </summary>
    private static ToolResult VerifyIfObservable(string deliveredMessage, IntPtr target, List<KeyStroke> strokes)
    {
        if (!MeansCloseWindow(strokes))
        {
            return ToolResult.Ok(deliveredMessage);
        }

        long deadline = Environment.TickCount64 + CloseVerifyMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!Native.IsWindow(target))
            {
                return ToolResult.Ok($"{deliveredMessage} — verified: the window closed.");
            }

            Thread.Sleep(100);
        }

        return ToolResult.Error(
            $"{deliveredMessage} — BUT the window is STILL OPEN after {CloseVerifyMs}ms, so the shortcut did NOT close it. " +
            "The key events were delivered; the application simply did not act on them " +
            "(injected Alt+F4 is ignored by, for example, Windows 11 Notepad). This is NOT a success. " +
            "Call pc_close_window with the same hwnd to actually close it. " +
            "If the app has unsaved content, expect a save-confirmation dialog and tell the user about it.");
    }

    /// <summary>这串按键的本意是不是"关掉这个窗口"：Alt+F4，或 Ctrl+W。</summary>
    private static bool MeansCloseWindow(List<KeyStroke> strokes)
    {
        if (strokes.Count != 1)
        {
            return false;
        }

        var stroke = strokes[0];
        if (stroke.IsLiteral || stroke.Win || stroke.Shift)
        {
            return false;
        }

        return (stroke.Alt && stroke.Vk == 0x73) ||                 // Alt+F4
               (stroke.Ctrl && !stroke.Alt && stroke.Vk == 0x57);   // Ctrl+W
    }
}
