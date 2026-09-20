// "这个程序是不是已经在跑了？" —— pc_launch 去重用的第一道依据。
//
// 真实事故：模型想开记事本，一时没拿到窗口句柄就反复调 pc_launch，
// 用户点过 Allow always 之后连确认框都不弹 —— 于是屏幕上静默堆出一堆记事本窗口。
// 所以启动之前必须先问一句"它是不是已经开着"。
//
// 判断只用【看得见】的事实，且不再只看进程名字符串（Win11 记事本是打包应用，三样证据见 ProgramMatch）：
//   1) 一个可见顶层窗口属于目标程序（进程名 / 窗口类名 / 映像文件名任一命中，最硬的证据）；
//   2) 一个可见顶层窗口的标题里带目标文件名（打开文档时靠它，例如 "hello.txt - 记事本"）；
//   3) 兜底：目标程序确实在跑，只是这会儿没有可见窗口
//      （打包应用的启动器进程就是这种；那就没有能聚焦的窗口，如实说清楚）。
//
// 刻意不做的两件事：
//   * 不看命令行（拿不到其它用户/提权进程的命令行，读了会给出半真半假的结论）；
//   * URL 一律不去重（"已经开着浏览器"不等于"这个网址已经打开"，去重反而会骗人）。

using System.Diagnostics;

namespace PotatoAgent.Win32.Tools;

/// <summary>已经在跑的目标程序。四样东西都是实测来的：窗口句柄、pid、进程名、判定依据。</summary>
/// <param name="Hwnd">那个已有窗口的句柄；<see cref="IntPtr.Zero"/> 表示目标程序在跑但眼下没有可见窗口。</param>
/// <param name="Pid">进程 id。</param>
/// <param name="ProcessName">进程名（例如 <c>Notepad</c>）。</param>
/// <param name="Title">窗口标题；没有窗口时是空串。</param>
/// <param name="Evidence">凭什么判定它"就是同一个目标"，写进给模型看的话里。</param>
internal sealed record RunningInstance(IntPtr Hwnd, int Pid, string ProcessName, string Title, string Evidence)
{
    /// <summary>有没有可以聚焦的窗口。</summary>
    internal bool HasWindow => Hwnd != IntPtr.Zero;
}

/// <summary>按"路径 / 可执行名 / 文档名"查一个程序是不是已经在跑。</summary>
internal static class RunningProgram
{
    /// <summary>
    /// 找出已经在跑的实例；没有返回 null。
    /// 只在"能明说是同一个目标"时才返回 —— 宁可新起一个，也不要把用户的别的窗口认成目标。
    /// </summary>
    internal static RunningInstance? Find(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path!.Trim();

        // URL 没有"同一个实例"这回事（浏览器开着 ≠ 这个网址开着）。
        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        var stem = ProgramMatch.StemOf(trimmed);
        if (stem.Length == 0)
        {
            return null;
        }

        RunningInstance? byTitle = null;

        foreach (var hwnd in Desktop.VisibleWindows())
        {
            var pid = Desktop.ProcessId(hwnd);
            var processName = ProgramMatch.ProcessNameOf(pid);

            // 硬证据：这个窗口属于目标程序（进程名 / 窗口类名 / 映像文件名任一命中）。
            if (ProgramMatch.MatchesWindow(hwnd, stem))
            {
                return new RunningInstance(
                    hwnd, pid, processName.Length > 0 ? processName : "unknown", Native.WindowText(hwnd),
                    $"a running {stem} process owns this window (process name / window class / image name matched)");
            }

            // 软证据：标题里带目标文件名。排在硬证据之后 —— 标题只能说明"看着像"。
            if (byTitle is null && ProgramMatch.TitleMentions(hwnd, stem))
            {
                byTitle = new RunningInstance(
                    hwnd, pid, processName.Length > 0 ? processName : "unknown", Native.WindowText(hwnd),
                    $"its window title contains \"{stem}\"");
            }
        }

        if (byTitle is not null)
        {
            return byTitle;
        }

        // 兜底：目标程序在跑，但一个可见窗口都没有（后台常驻、还没画出窗口、或者窗口被隐藏了）。
        // 这时候没有可以聚焦的东西，但仍然不该再起一个 —— 如实报告，让模型自己决定要不要 force_new_instance。
        foreach (var process in ProcessesNamed(stem))
        {
            using (process)
            {
                var hwnd = Desktop.FindByPid(process.Id);
                var title = hwnd != IntPtr.Zero ? Native.WindowText(hwnd) : string.Empty;

                return new RunningInstance(
                    hwnd, process.Id, SafeProcessName(process, stem), title,
                    $"a process named {stem} is already running");
            }
        }

        return null;
    }

    /// <summary>按名取进程；名字非法或不存在时返回空列表（绝不抛）。</summary>
    private static IEnumerable<Process> ProcessesNamed(string stem)
    {
        try
        {
            return Process.GetProcessesByName(stem);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Array.Empty<Process>();
        }
    }

    private static string SafeProcessName(Process process, string fallback)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return fallback;
        }
    }
}
