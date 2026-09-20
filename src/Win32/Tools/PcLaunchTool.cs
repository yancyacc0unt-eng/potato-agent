using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Win32.Tools;

/// <summary>
/// <c>pc_launch</c>：启动一个程序 / 打开一个文件或网址（ShellExecuteEx）。
/// <b>不重复开</b>：启动前先看目标是不是已经在跑，已经在跑就聚焦那个窗口、返回它的 hwnd，
/// 什么都不新起（除非显式传 <c>force_new_instance=true</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ToolRisk.Confirm"/> —— 会在用户的机器上起一个进程，必须先过权限门。
/// 返回 pid 和"新出现的那个窗口"的句柄；窗口没等到不算失败（有些程序本来就没有主窗口）。
/// </para>
/// <para>
/// 去重的由来（真实事故）：模型一时没拿到窗口句柄就反复调本工具，而用户点过 <c>Allow always</c>
/// 之后连确认框都不弹 —— 屏幕上于是静默堆出一堆记事本。判定逻辑见 <see cref="RunningProgram"/>。
/// </para>
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
        "IMPORTANT: if that program is ALREADY running, this tool does NOT start a second copy - " +
        "it focuses the existing window, returns that window's hwnd, and says so. " +
        "So never call it again and again hoping a window will show up: call it once, then use the hwnd it " +
        "returned with pc_type / pc_click / pc_windows to work with the program you just opened. " +
        "Only pass force_new_instance=true when a second copy is really what the user asked for. " +
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
            },
            "force_new_instance": {
              "type": "boolean",
              "description": "Default false. When false (the normal case) and that program is already running, this tool focuses the existing window instead of starting another copy. Set true ONLY to deliberately start a second copy."
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

        var forceNewInstance = args.Flag("force_new_instance", false);
        var waitMs = Math.Clamp(args.Integer("wait_ms") ?? DefaultWaitMs, 0, 60000);

        // ---- 去重：启动之前先问"它是不是已经开着" ----
        // 新起进程是这台机器上最贵的动作，而模型最爱的动作就是"再调一次看看"。
        if (!forceNewInstance)
        {
            var existing = RunningProgram.Find(path);
            if (existing is not null)
            {
                return Task.FromResult(ToolResult.Ok(ReportExisting(path!, existing)));
            }
        }

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

    /// <summary>
    /// 已经在跑：聚焦那个已有窗口，把 hwnd 报回去，并明说<b>没有新起进程</b>。
    /// 聚焦失败也不改口 —— 事实就是"什么都没新起"，把失败原因如实写进去，让模型知道下一步该干嘛。
    /// </summary>
    private static string ReportExisting(string path, RunningInstance existing)
    {
        var notice = "Pass force_new_instance=true if you really want to start another copy.";

        if (!existing.HasWindow)
        {
            return $"{path} is already running ({existing.ProcessName}#{existing.Pid}; {existing.Evidence}) " +
                   "but it has no visible window to focus right now. Nothing new was started. " + notice;
        }

        var focus = new ComputerControl().FocusWindow(existing.Hwnd);
        var where = Desktop.Describe(existing.Hwnd);

        return focus.Ok
            ? $"{path} is already running; focused the existing window (hwnd {where}). Nothing new was started. " +
              $"Use hwnd {where} with pc_type / pc_click. {notice}"
            : $"{path} is already running; could NOT bring the existing window to the front ({focus.Message}). " +
              $"Nothing new was started. The window is hwnd {where}. {notice}";
    }
}
