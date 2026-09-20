// 修完之后的实测（逐条对着验收单来）：
//   1) 同一轮两次 pc_launch，参数【故意写得不一样】→ 真实进程/窗口数只增加 1；
//   2) 反证：force_new_instance=true 时仍然能开第二个；
//   3) 竞态：第一次 wait_ms:0 返回、窗口还没出现时第二次就来 → 仍然只多 1 个；
//   4) pc_close_window 能关掉记事本；pc_keys(Alt+F4) 关不掉时【如实报失败】。
//
// 每组都先把记事本状态清干净（清空文档 + WM_CLOSE 好好关掉），
// 否则 Win11 记事本的"恢复上次会话"会把旧窗口一起还原，把计数搅浑 —— 那正是问题一的根因。

using System.Text.Json;
using PotatoAgent.Core.Agent;
using PotatoAgent.Core.Tools;
using PotatoAgent.Win32;
using PotatoAgent.Win32.Tools;

namespace PotatoAgent.Agent.SelfTest;

/// <summary>修复后的逐条实测。</summary>
internal static class LaunchFixTests
{
    /// <summary>验收 2/3：同一轮两次启动只多一个；force_new_instance 仍能开第二个。</summary>
    public static async Task SameTurnOnlyOneAsync()
    {
        if (!CleanStart()) return;

        var originalForeground = Desktop.Foreground();

        // ---------- 基线：干净状态下开一次，就只应该多一个窗口 ----------
        LaunchGuardTests.ResetNotepads();
        var baseline = await LaunchGuardTests.ScenarioAsync(
            originalForeground,
            "基线 干净状态只开一次",
            "{\"path\":\"notepad.exe\"}",
            "{\"path\":\"notepad.exe\"}");   // 第二次逐字节相同 → 老护栏拦

        Program.Check(baseline.WindowsBefore == 0, $"基线起跑时窗口数为 0（实际 {baseline.WindowsBefore}）");
        Program.Check(baseline.WindowDelta == 1,
            $"基线：一次启动只多 1 个窗口（实际 +{baseline.WindowDelta}）—— 会话状态干净，没有幽灵窗口");

        // ---------- 验收 2：参数故意写得不一样，同一轮连开两次 ----------
        var different = await LaunchGuardTests.ScenarioAsync(
            originalForeground,
            "验收 2 同一轮两次，参数故意不一样",
            "{\"path\":\"notepad.exe\"}",
            "{\"path\":\"notepad.exe\",\"wait_ms\":9000,\"arguments\":\"\",\"working_directory\":\"C:\\\\\"}");

        Program.Check(different.WindowsBefore == 0, $"起跑时窗口数为 0（实际 {different.WindowsBefore}）");
        Program.Check(different.WindowDelta == 1,
            $"【验收 2】参数写得不一样，第二次仍然只多 0 个窗口：合计只增加 1 个（实际 +{different.WindowDelta}）");
        Program.Check(different.NewProcesses == 1,
            $"【验收 2】真实 notepad 进程也只多了 1 个（实际 +{different.NewProcesses}）");
        Program.Check(different.DuplicateCalls == 1,
            $"【验收 2】护栏按「程序身份」拦下了第二次（duplicateCalls={different.DuplicateCalls}）");
        Program.Check(different.ApproverCalls == 1,
            $"【验收 2】第二次没走到权限门，用户只被问了一次（实际 {different.ApproverCalls} 次）");
        Program.Check(
            different.Finishes.Any(f => f.Name == "pc_launch" && !f.Success && f.Summary.Contains("SAME target", StringComparison.Ordinal)),
            "【验收 2】被拦的那次回了明确说明（「参数换了写法，动的是同一个目标」），不是沉默");

        // ---------- 验收 3：反证 —— 显式要求第二个实例 ----------
        var forced = await LaunchGuardTests.ScenarioAsync(
            originalForeground,
            "验收 3 反证 force_new_instance=true",
            "{\"path\":\"notepad.exe\"}",
            "{\"path\":\"notepad.exe\",\"force_new_instance\":true}");

        Program.Check(forced.WindowsBefore == 0, $"起跑时窗口数为 0（实际 {forced.WindowsBefore}）");
        Program.Check(forced.WindowDelta == 2,
            $"【验收 3】force_new_instance=true 时确实开了第二个（合计 +{forced.WindowDelta} 个窗口）");
        Program.Check(
            forced.Finishes.Count(f => f.Name == "pc_launch" && f.Success) == 2,
            "【验收 3】两次 pc_launch 都报成功（显式绕过是允许的）");

        // ---------- 验收 3b：竞态 —— 第一次不等窗口就返回，紧接着第二次 ----------
        await RaceAfterFixAsync(originalForeground);

        LaunchGuardTests.ResetNotepads();
        if (originalForeground != IntPtr.Zero) FocusGuard.Focus(originalForeground, out _);
    }

    /// <summary>
    /// 竞态：第一次 <c>wait_ms:0</c>（会被工具抬到 2500ms 下限），紧接着第二次同一个程序 ——
    /// 修复前这会真的再起一个；现在只应该多 1 个窗口。
    /// </summary>
    private static async Task RaceAfterFixAsync(IntPtr originalForeground)
    {
        LaunchGuardTests.ResetNotepads();

        var before = LaunchGuardTests.NotepadWindows().Count;
        var first = await LaunchToolAsync("{\"path\":\"notepad.exe\",\"wait_ms\":0}");
        var second = await LaunchToolAsync("{\"path\":\"notepad.exe\"}");

        Program.Info($"     第 1 次(wait_ms:0): {Program.Trim(first.Content, 120)}");
        Program.Info($"     第 2 次(默认参数)  : {Program.Trim(second.Content, 120)}");

        await Task.Delay(2500);
        var after = LaunchGuardTests.NotepadWindows();

        Program.Check(first.Success, "第 1 次 pc_launch 成功");
        Program.Check(after.Count - before == 1,
            $"【验收 3b】wait_ms:0 之后紧接着再来一次，仍然只多 1 个窗口（{before} → {after.Count}）");
        Program.Check(
            second.Content.Contains("already started in this turn", StringComparison.Ordinal) ||
            second.Content.Contains("is already running", StringComparison.Ordinal),
            "【验收 3b】第二次如实说明「这一轮已经启动过 / 它已经在跑了」，没有闷声再开一个");

        LaunchGuardTests.ResetNotepads();
    }

    /// <summary>验收 4：pc_close_window 真的能关；pc_keys 的 Alt+F4 关不掉时如实报失败。</summary>
    public static async Task CloseHonestlyAsync()
    {
        if (!CleanStart()) return;

        var originalForeground = Desktop.Foreground();

        // ---------- 4a：pc_close_window 关掉一个干净记事本 ----------
        LaunchGuardTests.ResetNotepads();
        var launched = await LaunchToolAsync("{\"path\":\"notepad.exe\"}");
        var hwnd = LaunchGuardTests.FirstHandleIn(launched.Content);
        Program.Check(hwnd != IntPtr.Zero, "开出一个记事本，拿到句柄");

        var closed = await CloseToolAsync($"{{\"hwnd\":\"0x{hwnd.ToInt64():X8}\"}}");
        Program.Info($"     pc_close_window: ok={closed.Success} | {Program.Trim(closed.Content, 130)}");
        Program.Check(closed.Success, "【验收 4a】pc_close_window 报告成功");
        Program.Check(!Native_IsWindow(hwnd), "【验收 4a】独立核实：那个窗口真的没了（不是只报了个成功）");

        // ---------- 4b：pc_keys 的 Alt+F4 对 WinUI 记事本无效 → 必须如实报失败 ----------
        LaunchGuardTests.ResetNotepads();
        var second = await LaunchToolAsync("{\"path\":\"notepad.exe\"}");
        var target = LaunchGuardTests.FirstHandleIn(second.Content);
        Program.Check(target != IntPtr.Zero, "为 Alt+F4 场景开出第二个记事本");

        await Task.Delay(1200);
        var focus = new ComputerControl(target).FocusWindow(target);
        Program.Info($"     聚焦: ok={focus.Ok}，前台 = {Desktop.Describe(Desktop.Foreground())}");

        var keys = await KeysToolAsync($"{{\"keys\":\"%{{F4}}\",\"hwnd\":\"0x{target.ToInt64():X8}\"}}");
        var stillOpen = Native_IsWindow(target);
        Program.Info($"     pc_keys(\"%{{F4}}\"): ok={keys.Success} | {Program.Trim(keys.Content, 160)}");
        Program.Info($"     独立核实：窗口 {(stillOpen ? "还在" : "已经关掉")}");

        Program.Check(
            stillOpen ? !keys.Success : keys.Success,
            "【验收 4b】pc_keys 报的结果与窗口的真实状态一致 —— 没关掉就报失败，绝不说假话");
        Program.Check(
            !stillOpen || keys.Content.Contains("pc_close_window", StringComparison.Ordinal),
            "【验收 4b】失败信息里指明了下一步该用 pc_close_window");

        if (stillOpen)
        {
            var rescue = await CloseToolAsync($"{{\"hwnd\":\"0x{target.ToInt64():X8}\"}}");
            Program.Info($"     补刀 pc_close_window: ok={rescue.Success} | {Program.Trim(rescue.Content, 120)}");
            Program.Check(rescue.Success && !Native_IsWindow(target),
                "【验收 4b】同一件事换 pc_close_window 就真的关掉了");
        }

        // ---------- 4c：有未保存内容时也一样 —— 关没关掉，报告必须与事实一致 ----------
        LaunchGuardTests.ResetNotepads();
        var third = await LaunchToolAsync("{\"path\":\"notepad.exe\"}");
        var dirty = LaunchGuardTests.FirstHandleIn(third.Content);
        await Task.Delay(1200);

        using (var typeDocument = JsonDocument.Parse(
            $"{{\"text\":\"POTATO-UNSAVED-CONTENT\",\"hwnd\":\"0x{dirty.ToInt64():X8}\"}}"))
        {
            await new PcTypeTool().InvokeAsync(typeDocument.RootElement, CancellationToken.None);
        }

        var dirtyClose = await CloseToolAsync($"{{\"hwnd\":\"0x{dirty.ToInt64():X8}\",\"timeout_ms\":3000}}");
        var dirtyStillOpen = Native_IsWindow(dirty);
        Program.Info($"     有未保存内容时 pc_close_window: ok={dirtyClose.Success} | {Program.Trim(dirtyClose.Content, 150)}");
        Program.Check(
            dirtyStillOpen ? !dirtyClose.Success : dirtyClose.Success,
            $"【验收 4c】窗口真实状态（{(dirtyStillOpen ? "还在（保存确认框挡着）" : "已关")}）与工具报告一致");

        // ---------- 4d：收尾必须"先清空、再关" —— 否则内容会被存进 TabState 等下次恢复 ----------
        LaunchGuardTests.ResetNotepads();   // 这一步内含"引出残留会话内容并清掉"

        var fresh = await LaunchToolAsync("{\"path\":\"notepad.exe\"}");
        var freshHwnd = LaunchGuardTests.FirstHandleIn(fresh.Content);
        await Task.Delay(2000);

        var freshContent = freshHwnd == IntPtr.Zero ? "<没开出窗口>" : LaunchGuardTests.UiaRead(freshHwnd);
        var freshWindows = LaunchGuardTests.NotepadWindows().Count;
        Program.Info($"     下一次启动: 窗口 {freshWindows} 个，内容 \"{Program.Trim(freshContent, 50)}\"");
        Program.Check(!freshContent.Contains("POTATO-UNSAVED-CONTENT", StringComparison.Ordinal),
            "【验收 4d】上一次的未保存内容不会被恢复出来（清场确实把它引出并清掉了）");
        Program.Check(freshWindows == 1, $"【验收 4d】一次启动只多 1 个窗口（实际 {freshWindows} 个）");

        // ---------- 4e：正的规避顺序 —— 清空文档 → WM_CLOSE → 再启动，干干净净 ----------
        const string secondMarker = "POTATO-SECOND-DIRTY";
        using (var typeDocument = JsonDocument.Parse(
            $"{{\"text\":{JsonSerializer.Serialize(secondMarker)},\"hwnd\":\"0x{freshHwnd.ToInt64():X8}\"}}"))
        {
            await new PcTypeTool().InvokeAsync(typeDocument.RootElement, CancellationToken.None);
        }

        LaunchGuardTests.ClearDocument(freshHwnd);                     // 先清空
        var cleanClose = await CloseToolAsync($"{{\"hwnd\":\"0x{freshHwnd.ToInt64():X8}\"}}");   // 再关掉
        Program.Check(cleanClose.Success && !Native_IsWindow(freshHwnd), "【验收 4e】清空之后 WM_CLOSE 关得掉");

        // 关掉最后一个窗口之后记事本进程还要收尾一会儿；不等它退干净，"已经在跑"的判断会把下一次启动挡回去，
        // 那样这条断言就成了空过（连窗口都没有，当然也就没有内容）。
        var exited = Environment.TickCount64 + 8000;
        while (Environment.TickCount64 < exited && Program.NotepadPids().Count > 0)
        {
            await Task.Delay(200);
        }

        var clean = await LaunchToolAsync("{\"path\":\"notepad.exe\"}");
        var cleanHwnd = LaunchGuardTests.FirstHandleIn(clean.Content);
        await Task.Delay(2000);
        var cleanContent = cleanHwnd == IntPtr.Zero ? "<没开出窗口>" : LaunchGuardTests.UiaRead(cleanHwnd);

        Program.Check(cleanHwnd != IntPtr.Zero, "【验收 4e】清空 + 好好关掉之后还能正常再开一个（没有卡在『已经在跑』）");
        Program.Check(!cleanContent.Contains(secondMarker, StringComparison.Ordinal),
            $"【验收 4e】清空 + 好好关掉之后，内容不再被恢复（回读 \"{Program.Trim(cleanContent, 30)}\"）");

        LaunchGuardTests.ResetNotepads();
        Program.Check(LaunchGuardTests.NotepadWindows().Count == 0 && Program.NotepadPids().Count == 0,
            "【验收 4 收尾】记事本进程与窗口都清成 0");
        Program.Info($"     会话状态文件: TabState {LaunchGuardTests.SessionStateFiles("TabState")} 个 / " +
                     $"WindowState {LaunchGuardTests.SessionStateFiles("WindowState")} 个（空壳，实测不会再吐出幽灵窗口）");

        if (originalForeground != IntPtr.Zero) FocusGuard.Focus(originalForeground, out _);
    }

    // ==================== 小助手 ====================

    /// <summary>开跑前的安全检查：机器上本来就有记事本就不碰（返回 false = 本项跳过）。</summary>
    private static bool CleanStart()
    {
        var existing = Program.NotepadPids();
        var windows = LaunchGuardTests.NotepadWindows();
        if (existing.Count == 0 && windows.Count == 0)
        {
            return true;
        }

        Program.Info($"这台机器上本来就有记事本（进程 {existing.Count} / 窗口 {windows.Count}）—— " +
                     "绝不碰用户的东西，本项【跳过】（不代表通过）");
        return false;
    }

    private static async Task<ToolResult> LaunchToolAsync(string argsJson)
    {
        using var document = JsonDocument.Parse(argsJson);
        return await new PcLaunchTool().InvokeAsync(document.RootElement, CancellationToken.None);
    }

    private static async Task<ToolResult> CloseToolAsync(string argsJson)
    {
        using var document = JsonDocument.Parse(argsJson);
        return await new PcCloseWindowTool().InvokeAsync(document.RootElement, CancellationToken.None);
    }

    private static async Task<ToolResult> KeysToolAsync(string argsJson)
    {
        using var document = JsonDocument.Parse(argsJson);
        return await new PcKeysTool().InvokeAsync(document.RootElement, CancellationToken.None);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    private static bool Native_IsWindow(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);
}
