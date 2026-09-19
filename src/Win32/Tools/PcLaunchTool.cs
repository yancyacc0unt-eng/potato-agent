using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Win32.Tools;

/// <summary>
/// <c>pc_launch</c>：启动一个程序 / 打开一个文件或网址（ShellExecuteEx）。
/// </summary>
/// <remarks>
/// <see cref="ToolRisk.Confirm"/> —— 会在用户的机器上起一个进程，必须先过权限门。
/// 返回 pid 和"新出现的那个窗口"的句柄；窗口没等到不算失败（有些程序本来就没有主窗口）。
/// </remarks>
public sealed class PcLaunchTool : ITool
{
    /// <summary>等新窗口的默认上限。</summary>
    private const int DefaultWaitMs = 8000;

    /// <inheritdoc />
    public string Name => "pc_launch";

    /// <inheritdoc />
    public string Description =>
        "Start a program, open a document or open a URL on the user's machine (like double-clicking it). " +
        "This creates a real process - it needs the user's confirmation. " +
        "'path' may be a full path (C:\\\\Windows\\\\System32\\\\notepad.exe), a bare executable name resolved from PATH, " +
        "a document path, or an http(s) URL. " +
        "Returns the new pid and, when one appears in time, the handle of the NEW window it opened " +
        "(windows that already existed before the call are never reported).";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Executable, document or URL to open."
            },
            "arguments": {
              "type": "string",
              "description": "Optional command line arguments."
            },
            "working_directory": {
              "type": "string",
              "description": "Optional working directory for the new process."
            },
            "wait_ms": {
              "type": "integer",
              "description": "How long to wait for the new window to appear, in milliseconds (0-60000, default 8000)."
            }
          },
          "required": ["path"]
        }
        """;

    /// <inheritdoc />
    public ToolRisk Risk => ToolRisk.Confirm;

    /// <inheritdoc />
    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var path = args.Text("path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(ToolResult.Error("The 'path' argument is required. Nothing was started."));
        }

        var waitMs = Math.Clamp(args.Integer("wait_ms") ?? DefaultWaitMs, 0, 60000);

        // Launch 只在自己的执行期内阻塞；不传 ct（ShellExecuteEx 不可中断），
        // 启动前先确认没被取消就够了。
        var outcome = new ComputerControl().Launch(
            path!,
            args.Text("arguments"),
            args.Text("working_directory"),
            waitMs);

        if (!outcome.Ok)
        {
            return Task.FromResult(ToolResult.Error(outcome.Message));
        }

        var content = outcome.WindowHandle != IntPtr.Zero
            ? $"{outcome.Message}. Use hwnd {Desktop.Describe(outcome.WindowHandle)} with pc_type / pc_click."
            : outcome.Message;

        return Task.FromResult(ToolResult.Ok(content));
    }
}
