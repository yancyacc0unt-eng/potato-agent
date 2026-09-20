using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Win32.Tools;

/// <summary>
/// <c>pc_close_window</c>：关掉一个窗口（投 <c>WM_CLOSE</c>，必要时补 <c>SC_CLOSE</c>），关完回读核实。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ToolRisk.Confirm"/> —— 会真的关掉用户的一个窗口，必须先过权限门。
/// </para>
/// <para>
/// 为什么专门做一个工具，而不是让模型发 Alt+F4：本机实测 Win11 记事本（WinUI）
/// <b>根本不响应注入的 Alt+F4</b> —— 干净的空文档也一样不关，而 SendInput 会老老实实报
/// "1 keystroke(s) delivered"。WM_CLOSE 走的是窗口消息队列，不经过输入法、也不依赖前台焦点，实测有效。
/// </para>
/// <para>
/// 关不掉就如实说关不掉：窗口有未保存内容时应用会弹保存确认框（<c>WM_CLOSE</c> 也会弹），
/// 这时候窗口<b>还在</b>，返回值就说"还在"，绝不报成功。
/// </para>
/// </remarks>
public sealed class PcCloseWindowTool : ITool
{
    /// <inheritdoc />
    public string Name => "pc_close_window";

    /// <inheritdoc />
    public string Description =>
        "Close a window (sends WM_CLOSE, and SC_CLOSE if that does nothing), then verifies the window is really gone. " +
        "This closes something on the user's machine - it needs the user's confirmation. " +
        "ALWAYS USE THIS INSTEAD OF SENDING Alt+F4 with pc_keys: on Windows 11 Notepad (a WinUI app) an injected " +
        "Alt+F4 is delivered but the window does not close, while this tool does close it. " +
        "Target it with hwnd (from pc_windows / pc_launch), with window (a substring of the title), or with process " +
        "(a process name such as \"notepad\" - that closes every visible window of that process). " +
        "The result is verified by checking that the window no longer exists; if the window is still open (for example " +
        "because the app is showing a save-confirmation dialog) the tool reports FAILURE, never a false success.";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "hwnd": {
              "type": "string",
              "description": "Window handle from pc_windows / pc_launch, e.g. \"0x00123456\"."
            },
            "window": {
              "type": "string",
              "description": "Case-insensitive substring of the window title (alternative to hwnd)."
            },
            "process": {
              "type": "string",
              "description": "Process name, e.g. \"notepad\". Closes every visible top-level window of that process (alternative to hwnd / window)."
            },
            "timeout_ms": {
              "type": "integer",
              "description": "How long to wait for the window to actually disappear, in milliseconds (500-20000, default 4000)."
            }
          }
        }
        """;

    /// <inheritdoc />
    public ToolRisk Risk => ToolRisk.Confirm;

    /// <inheritdoc />
    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var timeoutMs = Math.Clamp(args.Integer("timeout_ms") ?? 4000, 500, 20000);

        // 按进程名关：一次关掉那个进程的所有可见顶层窗口（记事本的会话恢复常一次吐出好几个）。
        var process = args.Text("process");
        if (!string.IsNullOrWhiteSpace(process) && string.IsNullOrWhiteSpace(args.Text("hwnd")) &&
            string.IsNullOrWhiteSpace(args.Text("window")))
        {
            return Task.FromResult(CloseByProcess(process!, timeoutMs));
        }

        if (!ToolWindow.TryResolve(args, out var hwnd, out var error))
        {
            return Task.FromResult(ToolResult.Error(
                $"{error} (or pass \"process\": \"notepad\" to close every window of a program.)"));
        }

        var result = new ComputerControl().CloseWindow(hwnd, timeoutMs);
        return Task.FromResult(result.Ok ? ToolResult.Ok(result.Message) : ToolResult.Error(result.Message));
    }

    /// <summary>关掉某个进程的所有可见顶层窗口，逐个回读核实，最后如实汇报关了几个、还剩几个。</summary>
    private static ToolResult CloseByProcess(string process, int timeoutMs)
    {
        var stem = ProgramMatch.StemOf(process);
        if (stem.Length == 0)
        {
            return ToolResult.Error($"'{process}' is not a usable process name. Nothing was closed.");
        }

        var targets = Desktop.VisibleWindows()
            .Where(hwnd => ProgramMatch.MatchesProcess(Desktop.ProcessId(hwnd), stem))
            .ToList();

        if (targets.Count == 0)
        {
            return ToolResult.Error($"no visible top-level window belongs to a process named \"{stem}\". Nothing was closed.");
        }

        var control = new ComputerControl();
        var closed = new List<string>();
        var refused = new List<string>();

        foreach (var hwnd in targets)
        {
            // 每个窗口给自己一点时间，但整批不超过调用方给的总预算。
            var result = control.CloseWindow(hwnd, Math.Max(500, timeoutMs / targets.Count));
            if (result.Ok)
            {
                closed.Add(Desktop.Describe(hwnd));
            }
            else
            {
                refused.Add(Desktop.Describe(hwnd));
            }
        }

        if (refused.Count == 0)
        {
            return ToolResult.Ok($"closed {closed.Count} window(s) of \"{stem}\": {string.Join(" | ", closed)}");
        }

        return ToolResult.Error(
            $"closed {closed.Count} of {targets.Count} window(s) of \"{stem}\"; {refused.Count} REFUSED to close: " +
            $"{string.Join(" | ", refused)}. Those windows are still open (a save-confirmation dialog is the usual reason).");
    }
}
