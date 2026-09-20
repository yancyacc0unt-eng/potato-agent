// 定位：pc_keys 发 Alt+F4 报成功、记事本却没关 —— 到底卡在哪一步。
//
// 三种假设，逐条拿真机证据否掉或坐实：
//   H1 按键根本没送到（焦点不在）；
//   H2 送到了但目标窗口不吃注入的 Alt+F4；
//   H3 送到了、窗口也响应了，但【文档有未保存内容】，记事本弹了保存确认框，
//      于是窗口"还在那儿"—— 这不是注入失败，是它本来就不会关。
//
// 顺带验证 PostMessage(WM_CLOSE) 这条更可靠的路（pc_close_window 的实现依据）。

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using PotatoAgent.Win32;
using PotatoAgent.Win32.Tools;

namespace PotatoAgent.Agent.SelfTest;

/// <summary>问题二（pc_keys 的 Alt+F4 报成功却没关掉）的定位实测。</summary>
internal static class CloseWindowTests
{
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_CLOSE = 0xF060;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>Alt+F4 的三种场景，逐个开一个干净记事本跑一遍，数窗口数。</summary>
    public static async Task AltF4ProbeAsync()
    {
        if (Program.NotepadPids().Count != 0 || LaunchGuardTests.NotepadWindows().Count != 0)
        {
            Program.Info("这台机器上本来就有记事本 —— 绝不碰用户的东西，本项【跳过】（不代表通过）");
            return;
        }

        var originalForeground = Desktop.Foreground();

        await AltF4CaseAsync(originalForeground, "H3 空文档 + Alt+F4", typeText: null);
        await AltF4CaseAsync(originalForeground, "H3 有未保存内容 + Alt+F4", typeText: "POTATO-CLOSE-PROBE");
        await AltF4CaseAsync(originalForeground, "对照 空文档 + PostMessage(WM_CLOSE)", typeText: null, usePostMessage: true);

        CloseEverything();
        if (originalForeground != IntPtr.Zero) FocusGuard.Focus(originalForeground, out _);
    }

    private static async Task AltF4CaseAsync(IntPtr originalForeground, string label, string? typeText, bool usePostMessage = false)
    {
        Program.Info("");
        Program.Info($"---- {label} ----");
        LaunchGuardTests.ResetNotepads();

        var launched = await LaunchAsync("{\"path\":\"notepad.exe\"}");
        var hwnd = LaunchGuardTests.FirstHandleIn(launched);
        Program.Info($"     pc_launch: {Program.Trim(launched, 120)}");
        if (hwnd == IntPtr.Zero)
        {
            Program.Fail("拿不到记事本窗口句柄，本场景做不下去");
            return;
        }

        await Task.Delay(1200);

        if (typeText is not null)
        {
            using var typeDocument = JsonDocument.Parse(
                $"{{\"text\":{JsonSerializer.Serialize(typeText)},\"hwnd\":\"0x{hwnd.ToInt64():X8}\"}}");
            var typed = await new PcTypeTool().InvokeAsync(typeDocument.RootElement, CancellationToken.None);
            Program.Info($"     写入未保存内容: {Program.Trim(typed.Content, 110)}");
        }

        var focus = new ComputerControl(hwnd).FocusWindow(hwnd);
        Program.Info($"     聚焦: {focus.Ok} / 现在前台 = {Desktop.Describe(Desktop.Foreground())}");
        await Task.Delay(400);

        var before = LaunchGuardTests.NotepadWindows();

        if (usePostMessage)
        {
            var posted = PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Program.Info($"     PostMessage(hwnd, WM_CLOSE) = {posted}（Win32 错误 {Marshal.GetLastWin32Error()}）");
        }
        else
        {
            using var keys = JsonDocument.Parse($"{{\"keys\":\"%{{F4}}\",\"hwnd\":\"0x{hwnd.ToInt64():X8}\"}}");
            var sent = await new PcKeysTool().InvokeAsync(keys.RootElement, CancellationToken.None);
            Program.Info($"     pc_keys(\"%{{F4}}\") → ok={sent.Success} | {Program.Trim(sent.Content, 130)}");
        }

        await Task.Delay(2500);
        var after = LaunchGuardTests.NotepadWindows();
        var targetGone = !after.Any(w => w.Hwnd == hwnd) && !Native_IsWindow(hwnd);

        Program.Info($"     {before.Count} 个窗口 → {after.Count} 个窗口；目标窗口 {(targetGone ? "已经关掉了" : "还在")}");
        foreach (var window in after) Program.Info("        窗口: " + window.Line);

        // 窗口还在的话，看看它是不是弹了个模态对话框（保存确认）。
        if (!targetGone && hwnd != IntPtr.Zero)
        {
            foreach (var extra in WindowsOfProcess(Desktop.ProcessId(hwnd)))
            {
                if (extra == hwnd) continue;
                Program.Info($"        该进程另一个窗口（疑似对话框）: {Desktop.Describe(extra)}");
            }

            var content = LaunchGuardTests.UiaRead(hwnd);
            Program.Info($"        目标窗口内容回读: \"{Program.Trim(content, 60)}\"");
        }

        Program.Info(targetGone ? "     结论：这一场景下窗口确实关掉了。" : "     结论：这一场景下窗口没有关掉。");
    }

    // ==================== 小助手 ====================

    private static async Task<string> LaunchAsync(string argsJson)
    {
        using var document = JsonDocument.Parse(argsJson);
        var result = await new PcLaunchTool().InvokeAsync(document.RootElement, CancellationToken.None);
        return result.Content;
    }

    /// <summary>某个 pid 名下所有可见顶层窗口（找保存确认框用）。</summary>
    private static List<IntPtr> WindowsOfProcess(int pid)
    {
        var windows = new List<IntPtr>();
        foreach (var hwnd in Desktop.VisibleWindows())
        {
            if (Desktop.ProcessId(hwnd) == pid) windows.Add(hwnd);
        }

        return windows;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    private static bool Native_IsWindow(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    /// <summary>收尾：把记事本全清掉（先清空文档再 WM_CLOSE，最后兜底强杀）。</summary>
    private static void CloseEverything()
    {
        foreach (var window in LaunchGuardTests.NotepadWindows())
        {
            PostMessage(window.Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        Thread.Sleep(1500);
        LaunchGuardTests.ResetNotepads();
        Program.Info($"     收尾: 进程 {Program.NotepadPids().Count} 个 / 窗口 {LaunchGuardTests.NotepadWindows().Count} 个");
    }
}
