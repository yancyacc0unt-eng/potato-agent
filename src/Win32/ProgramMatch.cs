// "这个窗口属于不是那个程序" —— 一份判定，三处共用（启动去重 / 等新窗口 / 关窗口）。
//
// 为什么不只看进程名字符串：Win11 的记事本是【打包应用】（MSIX）。
// 本机实测（Win11 + 新版记事本）：
//   * 一次启动会有两个 pid：一个启动器进程 + 一个真正的 UI 进程，启动器随即退场；
//   * UI 进程名 = Notepad，窗口类名 = Notepad，标题 = 「记事本」/「无标题 - Notepad」；
//   * 同一个 UI 进程可以挂好几个窗口（会话恢复一次吐出 7 个窗口，pid 全一样）。
// 只认一样东西（例如进程名）在别的打包应用上就会认不出来，所以判定放宽成
// "进程名 / 窗口类名 / 进程映像文件名 任一命中"。

using System.Diagnostics;
using System.IO;

namespace PotatoAgent.Win32;

/// <summary>把"程序"和"窗口"对上号：三样证据任一命中就算同一个程序。</summary>
internal static class ProgramMatch
{
    /// <summary>标题匹配的最短词长：太短的词（"a"、"1"）乱匹配的概率比命中还高。</summary>
    internal const int MinStemLength = 3;

    /// <summary>目标的可执行名/文件名去掉扩展名后的词（<c>"notepad.exe"</c> → <c>"notepad"</c>）。</summary>
    internal static string StemOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var name = Path.GetFileName(path!.Trim().Trim('"'));
            return name.Length == 0 ? string.Empty : Path.GetFileNameWithoutExtension(name);
        }
        catch (ArgumentException)
        {
            // 路径里有非法字符（模型偶尔会把整条命令行塞进 path）：按无法判定处理。
            return string.Empty;
        }
    }

    /// <summary>
    /// 这个窗口是不是目标程序的窗口。三样证据任一命中即可：
    /// 窗口类名、进程名、进程映像文件名（都按"包含"比，忽略大小写）。
    /// </summary>
    internal static bool MatchesWindow(IntPtr hwnd, string stem)
    {
        if (hwnd == IntPtr.Zero || stem.Length == 0)
        {
            return false;
        }

        if (Native.ClassName(hwnd).Contains(stem, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int pid = Native.ProcessId(hwnd);
        return pid > 0 && MatchesProcess(pid, stem);
    }

    /// <summary>这个进程是不是目标程序（进程名或映像文件名命中）。</summary>
    internal static bool MatchesProcess(int pid, string stem)
    {
        if (pid <= 0 || stem.Length == 0)
        {
            return false;
        }

        if (ProcessNameOf(pid).Contains(stem, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var image = ImageStemOf(pid);
        return image.Length > 0 && image.Contains(stem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>这个窗口的标题里是不是带着目标文件名（打开文档时靠它，例如 "hello.txt - 记事本"）。</summary>
    internal static bool TitleMentions(IntPtr hwnd, string stem) =>
        stem.Length >= MinStemLength &&
        Native.WindowText(hwnd).Contains(stem, StringComparison.OrdinalIgnoreCase);

    /// <summary>pid → 进程名；读不到（提权 / 已退出）返回空串。</summary>
    internal static string ProcessNameOf(int pid)
    {
        if (pid <= 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>pid → 进程映像文件名去掉扩展名（<c>notepad</c>）；读不到返回空串。</summary>
    internal static string ImageStemOf(int pid)
    {
        if (pid <= 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            var file = process.MainModule?.FileName;
            return file is null ? string.Empty : Path.GetFileNameWithoutExtension(file);
        }
        catch
        {
            // 提权 / 打包应用的映像路径可能读不到 —— 这不是错误，其它两样证据照样能命中。
            return string.Empty;
        }
    }

    /// <summary>这个 pid 还在不在。</summary>
    internal static bool ProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
