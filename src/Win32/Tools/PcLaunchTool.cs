using System.IO;
using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Win32.Tools;

/// <summary>
/// <c>pc_launch</c>：启动一个程序 / 打开一个文件或网址（ShellExecuteEx）。
/// <b>一个程序只启动一次</b>：启动前先看目标是不是已经在跑（已经在跑就聚焦那个窗口、返回它的 hwnd），
/// 再看这一轮的账本里有没有同一程序的启动记录（有就等那次启动的窗口，绝不新起）。
/// 只有显式传 <c>force_new_instance=true</c> 才会真的起第二个。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ToolRisk.Confirm"/> —— 会在用户的机器上起一个进程，必须先过权限门。
/// </para>
/// <para>
/// 去重的由来（真实事故）：模型一时没拿到窗口句柄就反复调本工具，而用户点过 <c>Allow always</c>
/// 之后连确认框都不弹 —— 屏幕上于是静默堆出一堆记事本。三层判定分别在
/// <see cref="AgentSession.DeduplicateToolCalls"/>（同一回合 + 程序身份）、
/// <see cref="LaunchLedger"/>（还没等到窗口的那一段）、<see cref="RunningProgram"/>（本来就在跑）。
/// </para>
/// <para>
/// 还有一件实测出来的事：Win11 记事本会"恢复上次会话"，<b>一次启动可能吐出好几个窗口</b>
/// （本机实测一次吐出过 7 个，LocalState\TabState 里存着每个未保存标签）。
/// 所以返回值会把新窗口<b>全列出来</b>，并提醒模型多出来的那些可以用 <c>pc_close_window</c> 关掉。
/// </para>
/// </remarks>
public sealed class PcLaunchTool : ITool, IToolCallIdentity
{
    /// <summary>等新窗口的默认上限。</summary>
    private const int DefaultWaitMs = 8000;

    /// <summary>
    /// 不管模型把 <c>wait_ms</c> 写多小，都至少等这么久。
    /// 理由：一次启动的耗时里有一大段"窗口还没画出来"的空档，返回太早等于把
    /// "已启动但查不到"的状态漏给下一次调用 —— 那正是重复启动的入口。
    /// </summary>
    private const int MinWaitMs = 2500;

    /// <summary>可执行文件的扩展名：这类目标按"程序"算身份。</summary>
    private static readonly string[] ExecutableExtensions = { ".exe", ".com", ".bat", ".cmd", ".msi", ".lnk", ".ps1" };

    /// <inheritdoc />
    public string Name => "pc_launch";

    /// <inheritdoc />
    public string Description =>
        "Start a program, open a document or open a URL on the user's machine (like double-clicking it). " +
        "This creates a real process - it needs the user's confirmation. " +
        "STRONG RULE: in one turn, the same program is started AT MOST ONCE. " +
        "If it is already running (or an earlier call in this turn already started it), this tool does NOT start a " +
        "second copy - it focuses the existing window, returns that window's hwnd, and says so. " +
        "Call it once, then use the hwnd it returned with pc_type / pc_click / pc_close_window to work with the program. " +
        "Do not call it again hoping a window shows up; a different wait_ms or a different argument wording does not " +
        "make it a different target. Only pass force_new_instance=true when a second copy is really what the user asked for. " +
        "This call waits until the new window actually exists (or times out), so the hwnd it returns is usable right away. " +
        "NOTE: some apps (Windows 11 Notepad) restore their previous session, so ONE launch can reopen SEVERAL windows - " +
        "every new window is listed in the result; close the ones you do not want with pc_close_window. " +
        "'path' may be a full path (C:\\\\Windows\\\\System32\\\\notepad.exe), a bare executable name resolved from PATH, " +
        "a document path, or an http(s) URL. " +
        "Returns the new pid and the handle of the NEW window it opened (windows that already existed before the call " +
        "are never reported as new).";

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
              "description": "Optional command line arguments. Part of the program's identity: same path + same arguments = same target, which will not be started twice in one turn."
            },
            "working_directory": {
              "type": "string",
              "description": "Optional working directory for the new process."
            },
            "wait_ms": {
              "type": "integer",
              "description": "How long to wait for the new window to appear, in milliseconds (default 8000). Values below 2500 are raised to 2500 on purpose: returning before the window exists is what used to cause duplicate launches."
            },
            "force_new_instance": {
              "type": "boolean",
              "description": "Default false. When false (the normal case) and that program is already running - or was already started earlier in this turn - this tool focuses the existing window instead of starting another copy. Set true ONLY to deliberately start a second copy."
            }
          },
          "required": ["path"]
        }
        """;

    /// <inheritdoc />
    public ToolRisk Risk => ToolRisk.Confirm;

    /// <summary>
    /// 这次调用动的是哪一个程序（+ 哪一份文档）：<c>path</c> 与 <c>arguments</c> 一起决定。
    /// 特意<b>不</b>把 <c>wait_ms</c> / <c>working_directory</c> 算进来 ——
    /// 模型的自由写法不该被当成"另一个目标"。见 <see cref="IToolCallIdentity"/>。
    /// </summary>
    public string? IdentityOf(JsonElement args)
    {
        if (args.Flag("force_new_instance", false))
        {
            return null;   // 显式要求"再来一个"：这次调用不参与身份护栏。
        }

        return IdentityOf(args.Text("path"), args.Text("arguments"));
    }

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
        var requested = args.Integer("wait_ms") ?? DefaultWaitMs;
        var waitMs = Math.Clamp(Math.Max(requested, MinWaitMs), 0, 60000);
        var identity = IdentityOf(path, args.Text("arguments")) ?? path!.Trim().ToLowerInvariant();
        var stem = ProgramMatch.StemOf(path);

        // ---- 防线 1：它是不是本来就开着？ ----
        // 新起进程是这台机器上最贵的动作，而模型最爱的动作就是"再调一次看看"。
        if (!forceNewInstance)
        {
            var existing = RunningProgram.Find(path);
            if (existing is not null)
            {
                return Task.FromResult(ToolResult.Ok(ReportExisting(path!, existing)));
            }

            // ---- 防线 2：这一轮里（或者刚刚）是不是已经有人启动过同一个程序？ ----
            var pending = LaunchLedger.Claim(identity, path!, stem);
            if (pending is not null)
            {
                return Task.FromResult(ToolResult.Ok(ReportAlreadyStarted(path!, pending, stem, waitMs)));
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
            LaunchLedger.Release(identity);
            return Task.FromResult(ToolResult.Error(outcome.Message));
        }

        LaunchLedger.Resolve(identity, outcome.ProcessId, outcome.WindowHandle);

        return Task.FromResult(ToolResult.Ok(ReportOutcome(path!, outcome, waitMs)));
    }

    /// <summary>
    /// 这次调用的"程序身份"：<c>path</c> 决定是哪个程序（可执行目标）或哪份文档（文档目标），
    /// <c>arguments</c> 决定是同一程序上的哪一次启动。规范化掉大小写、首尾空白与空串。
    /// 返回 null = 认不出目标，这次调用不参与身份护栏。
    /// </summary>
    private static string? IdentityOf(string? path, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path!.Trim();
        var stem = ProgramMatch.StemOf(trimmed);
        if (stem.Length == 0)
        {
            return null;
        }

        string target;
        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            // URL：一条网址就是一个身份（浏览器开着 ≠ 这个网址打开着）。
            target = trimmed;
        }
        else if (IsExecutable(trimmed))
        {
            // 程序：按可执行名算 —— "notepad" 与 "C:\Windows\System32\notepad.exe" 是同一个程序。
            target = stem;
        }
        else
        {
            // 文档：整条路径算 —— 两份不同的文档是两个不同的目标。
            target = trimmed;
        }

        return (target + "\u001F" + (arguments?.Trim() ?? string.Empty)).ToLowerInvariant();
    }

    /// <summary>没有扩展名（<c>notepad</c> 这种从 PATH 解析的裸命令名）也按可执行目标算。</summary>
    private static bool IsExecutable(string path)
    {
        string extension;
        try
        {
            extension = Path.GetExtension(path);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (extension.Length == 0)
        {
            return true;
        }

        return ExecutableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 已经在跑：聚焦那个已有窗口，把 hwnd 报回去，并明说<b>没有新起进程</b>。
    /// 聚焦失败也不改口 —— 事实就是"什么都没新起"，把失败原因如实写进去，让模型知道下一步该干嘛。
    /// </summary>
    private static string ReportExisting(string path, RunningInstance existing)
    {
        var notice = "Nothing new was started, and calling this tool again with different arguments will not change that.";

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

    /// <summary>
    /// 这一轮里已经启动过同一个程序：<b>不</b>再启动，而是等那次启动的窗口出现、聚焦它，并如实说明。
    /// 这一步同时把竞态关掉 —— 上一次调用还没等到窗口的那段空档里，来的就是这个分支。
    /// </summary>
    private static string ReportAlreadyStarted(string path, LaunchClaim claim, string stem, int waitMs)
    {
        var hwnd = claim.Window;
        if (hwnd == IntPtr.Zero)
        {
            // 等那次还在飞的启动把窗口画出来 —— "同一个程序只启动一次"意味着这里只能等，不能抢着启动。
            hwnd = Desktop.WaitFor(window => ProgramMatch.MatchesWindow(window, stem), waitMs);
            if (hwnd != IntPtr.Zero)
            {
                LaunchLedger.Resolve(claim.Identity, Desktop.ProcessId(hwnd), hwnd);
            }
        }

        const string notice =
            "A launch of this program was ALREADY started in this turn (an earlier pc_launch call), so this call did " +
            "not start another copy. Do not call pc_launch for it again in this turn - work with the window below. " +
            "Only force_new_instance=true starts a second copy, and only do that if the user explicitly asked for it.";

        if (hwnd == IntPtr.Zero)
        {
            return $"{path} was already started in this turn (pid {claim.Pid}, its window has not appeared yet; " +
                   $"waited {waitMs}ms). {notice}";
        }

        var focus = new ComputerControl().FocusWindow(hwnd);
        var where = Desktop.Describe(hwnd);

        return focus.Ok
            ? $"{path} was already started in this turn; focused the window from that launch (hwnd {where}). {notice}"
            : $"{path} was already started in this turn; the window is hwnd {where} but it could NOT be brought to " +
              $"the front ({focus.Message}). {notice}";
    }

    /// <summary>
    /// 启动成功之后如实汇报：新窗口一个不漏地列出来。
    /// 一个程序一次启动吐出多个窗口是真实存在的（Win11 记事本的会话恢复），
    /// 只报第一个会让模型以为"还有一个是自己冒出来的"。
    /// </summary>
    private static string ReportOutcome(string path, LaunchOutcome outcome, int waitMs)
    {
        var windows = outcome.NewWindows;

        if (windows.Count == 0)
        {
            return $"{outcome.Message}. The process is running but no window showed up within {waitMs}ms; " +
                   "do NOT call pc_launch again for it in this turn - it is already started.";
        }

        var first = Desktop.Describe(windows[0]);

        if (windows.Count == 1)
        {
            return $"{outcome.Message}. Use hwnd {first} with pc_type / pc_click.";
        }

        var list = string.Join(" | ", windows.Select(Desktop.Describe));

        return $"{outcome.Message}. IMPORTANT: this single launch produced {windows.Count} new windows: {list}. " +
               "Some apps (Windows 11 Notepad) restore their previous session, so old windows come back together with " +
               $"the new one. The one to work with is most likely {first}; close the windows you do not want with " +
               "pc_close_window instead of launching again.";
    }
}
