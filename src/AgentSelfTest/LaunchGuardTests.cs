// 定位 + 实测：pc_launch 的"不重复开"到底漏在哪。
//
// 三件事，逐条拿真实机器上的证据说话：
//   A) Win11 记事本的"恢复上次会话"会不会自己吐出一个带旧内容的幽灵窗口
//      （用户截图里那个标题是「按不出」的窗口，疑似自测 SendKeys 遗留内容）；
//   B) 模型在同一轮里连调两次 pc_launch，参数【只差一点】时护栏拦不拦得住；
//   C) 竞态：第一次启动后窗口还没出现，第二次启动就发生了 —— 这种情况存不存在。
//
// 计数一律【窗口枚举 + 进程枚举两路都数】：Win11 记事本是打包应用，
// 只信 Get-Process 会得出错的结论。
//
// ⚠ 会真的起记事本、真的关掉它；只动自测自己起的那些，用户自己开着一个都不碰。

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using PotatoAgent.Core.Agent;
using PotatoAgent.Core.Tools;
using PotatoAgent.Win32;
using PotatoAgent.Win32.Tools;

namespace PotatoAgent.Agent.SelfTest;

/// <summary>一个"看起来像记事本"的可见顶层窗口 —— 诊断用的完整证据。</summary>
internal sealed record NotepadWindow(IntPtr Hwnd, int Pid, string Process, string Class, string Title)
{
    /// <summary>一行写完，便于贴进报告。</summary>
    public string Line => $"0x{Hwnd.ToInt64():X8} pid={Pid} process={Process} class={Class} title=\"{Program.Trim(Title, 40)}\"";
}

/// <summary>一组"同一轮两次 pc_launch"的实测结果 —— 断言就看它。</summary>
/// <param name="WindowsBefore">起跑前的记事本窗口数（每组都要求是 0）。</param>
/// <param name="WindowsAfter">跑完之后的记事本窗口数。</param>
/// <param name="NewProcesses">新出现的 notepad 进程数。</param>
/// <param name="DuplicateCalls">被护栏拦下的次数。</param>
/// <param name="Executed">真的执行了的工具调用次数。</param>
/// <param name="ApproverCalls">权限门被问了几次。</param>
/// <param name="Finishes">这一轮所有工具的结果。</param>
internal sealed record LaunchScenarioResult(
    int WindowsBefore,
    int WindowsAfter,
    int NewProcesses,
    int DuplicateCalls,
    int Executed,
    int ApproverCalls,
    IReadOnlyList<AgentToolFinished> Finishes)
{
    /// <summary>窗口净增量。</summary>
    internal int WindowDelta => WindowsAfter - WindowsBefore;
}

/// <summary>定位与实测：pc_launch 重复启动的三种可能成因。</summary>
internal static class LaunchGuardTests
{
    /// <summary>A 阶段写进文档、故意不保存的那段文字（跑完由收尾逻辑清掉）。</summary>
    private const string GhostMarker = "POTATO-GHOST-PROBE-7391";

    /// <summary>本轮自测起过的 notepad pid（收尾只杀这些）。</summary>
    private static readonly List<int> Spawned = new();

    // ==================== A：会话恢复幽灵窗口 ====================

    /// <summary>
    /// A：杀掉所有记事本 → 开一个 → UIA 写入文字（不保存）→ 强杀 → 再开一次，
    /// 看新窗口是不是自动带着上次没保存的内容（= 会话恢复）。带就说明用户看到的"第二个窗口"可能是幽灵。
    /// </summary>
    public static async Task GhostWindowProbeAsync()
    {
        var preExisting = Program.NotepadPids();
        var preWindows = NotepadWindows();
        Program.Info($"开跑前 notepad 进程 {preExisting.Count} 个 / 记事本窗口 {preWindows.Count} 个");
        foreach (var window in preWindows) Program.Info("        已有窗口: " + window.Line);

        if (preExisting.Count != 0 || preWindows.Count != 0)
        {
            Program.Info("这台机器上本来就有记事本 —— 绝不碰用户的东西，本项【跳过】（不代表通过）");
            return;
        }

        var originalForeground = Desktop.Foreground();

        // ---------- 步骤 1：开一个记事本 ----------
        var first = await LaunchAsync("{\"path\":\"notepad.exe\"}");
        Program.Info($"步骤 1 pc_launch 返回: {Program.Trim(first, 160)}");

        var firstHwnd = FirstHandleIn(first);
        Program.Check(firstHwnd != IntPtr.Zero, "步骤 1：pc_launch 报出了新窗口句柄");
        if (firstHwnd == IntPtr.Zero)
        {
            Cleanup(originalForeground);
            return;
        }

        Program.Info($"步骤 1 窗口: {Desktop.Describe(firstHwnd)}");
        Program.Info($"步骤 1 计数: 进程 {Program.NotepadPids().Count} 个 / 窗口 {NotepadWindows().Count} 个");

        // ---------- 步骤 2：写入一段文字，故意不保存 ----------
        var typed = await TypeAsync(firstHwnd, GhostMarker);
        Program.Info($"步骤 2 pc_type: {Program.Trim(typed, 140)}");
        Program.Check(UiaRead(firstHwnd).Contains(GhostMarker, StringComparison.Ordinal),
            "步骤 2：文字真的写进了文档（独立 UIA 回读，不借被测代码的手）");

        // 给打包版记事本一点时间把"未保存的标签页"写进自己的状态文件。
        await Task.Delay(4000);

        // ---------- 步骤 3：强杀（不给它走保存对话框的机会） ----------
        var killed = KillAllNotepad();
        await Task.Delay(2000);
        Program.Info($"步骤 3 强杀 {killed} 个 notepad 进程；现在进程 {Program.NotepadPids().Count} 个 / 窗口 {NotepadWindows().Count} 个");
        Program.Check(Program.NotepadPids().Count == 0 && NotepadWindows().Count == 0,
            "步骤 3：强杀之后机器上确实一个记事本都不剩");

        // ---------- 步骤 4：再开一次，看内容是不是自己回来了 ----------
        var second = await LaunchAsync("{\"path\":\"notepad.exe\"}");
        Program.Info($"步骤 4 pc_launch 返回: {Program.Trim(second, 160)}");

        var secondHwnd = FirstHandleIn(second);
        var windows = NotepadWindows();
        Program.Info($"步骤 4 计数: 进程 {Program.NotepadPids().Count} 个 / 窗口 {windows.Count} 个");
        foreach (var window in windows) Program.Info("        窗口: " + window.Line);

        if (secondHwnd != IntPtr.Zero)
        {
            await Task.Delay(1500);
            var restored = UiaRead(secondHwnd) ?? string.Empty;
            Program.Info($"步骤 4 新窗口回读内容: \"{Program.Trim(restored, 80)}\"");

            if (restored.Contains(GhostMarker, StringComparison.Ordinal))
            {
                Program.Info("【结论 A】成立：强杀之后重新打开，新窗口自己带回了上次没保存的内容 —— " +
                             "这就是『恢复上次会话』的幽灵窗口，不是模型新开的。");
            }
            else
            {
                Program.Info("【结论 A】不成立：重新打开的是干净的空文档 —— 本次没有观察到会话恢复。");
            }
        }

        // ---------- 步骤 5：规避方案验证 —— 清空文档 + 好好关掉（WM_CLOSE），再看下一次 ----------
        Program.Info("");
        Program.Info("---- 步骤 5 规避方案：收尾时【先清空文档、再用 pc_close_window 好好关掉】----");

        var mitigationMarker = "POTATO-MITIGATION-5150";
        var mitigated = UiaRead(secondHwnd);
        Program.Info($"     现在窗口里是上次恢复出来的内容: \"{Program.Trim(mitigated, 60)}\"");

        if (secondHwnd != IntPtr.Zero)
        {
            using (var typeDocument = JsonDocument.Parse(
                $"{{\"text\":{JsonSerializer.Serialize(mitigationMarker)},\"hwnd\":\"0x{secondHwnd.ToInt64():X8}\"}}"))
            {
                await new PcTypeTool().InvokeAsync(typeDocument.RootElement, CancellationToken.None);
            }

            ClearDocument(secondHwnd);   // 先把文档清空
            using var closeDocument = JsonDocument.Parse($"{{\"hwnd\":\"0x{secondHwnd.ToInt64():X8}\"}}");
            var closed = await new PcCloseWindowTool().InvokeAsync(closeDocument.RootElement, CancellationToken.None);
            Program.Info($"     清空后 pc_close_window: ok={closed.Success} | {Program.Trim(closed.Content, 110)}");
        }

        await Task.Delay(1000);
        Program.Info($"     关完之后的会话状态文件: TabState {SessionStateFiles("TabState")} 个 / " +
                     $"WindowState {SessionStateFiles("WindowState")} 个");

        var third = await LaunchAsync("{\"path\":\"notepad.exe\"}");
        var thirdHwnd = FirstHandleIn(third);
        await Task.Delay(2000);

        var afterMitigation = thirdHwnd == IntPtr.Zero ? "<没开出窗口>" : UiaRead(thirdHwnd);
        var windowCount = NotepadWindows().Count;
        Program.Info($"     清空再关之后，重新启动: 窗口 {windowCount} 个，内容 \"{Program.Trim(afterMitigation, 60)}\"");

        Program.Check(!afterMitigation.Contains(mitigationMarker, StringComparison.Ordinal),
            "【规避方案】清空文档 + WM_CLOSE 收尾之后，下一次启动不再带回上次的内容（幽灵窗口被根治）");
        Program.Check(windowCount == 1,
            $"【规避方案】一次启动只吐出一个干净窗口（实际 {windowCount} 个）");

        Cleanup(originalForeground);
    }

    // ==================== B + C：同一轮连开两次 ====================

    /// <summary>
    /// B/C：让假模型在同一轮里连调两次 pc_launch（先参数逐字节相同，再"只差一点"），
    /// 每组都从 0 个记事本起跑，数真实窗口/进程增量到底是多少。
    /// </summary>
    public static async Task DuplicateLaunchProbeAsync()
    {
        var preExisting = Program.NotepadPids();
        if (preExisting.Count != 0 || NotepadWindows().Count != 0)
        {
            Program.Info("这台机器上本来就有记事本 —— 绝不碰用户的东西，本项【跳过】（不代表通过）");
            return;
        }

        var originalForeground = Desktop.Foreground();

        // ---- B1：两次参数逐字节相同（老护栏应该拦住） ----
        await ScenarioAsync(
            originalForeground,
            "B1 参数逐字节相同",
            "{\"path\":\"notepad.exe\"}",
            "{\"path\":\"notepad.exe\"}");

        // ---- B2：两次只差一点（多一个 wait_ms）—— 怀疑就是从这儿漏过去的 ----
        await ScenarioAsync(
            originalForeground,
            "B2 参数只差一点（第二次多带 wait_ms）",
            "{\"path\":\"notepad.exe\"}",
            "{\"path\":\"notepad.exe\",\"wait_ms\":8000}");

        // ---- B3：arguments 写空串 vs 省略 ----
        await ScenarioAsync(
            originalForeground,
            "B3 arguments:\"\" vs 省略",
            "{\"path\":\"notepad.exe\",\"arguments\":\"\"}",
            "{\"path\":\"notepad.exe\"}");

        // ---- C：竞态 —— 第一次 wait_ms:0（不等窗口就返回），窗口还没出现第二次就来了 ----
        await RaceTimelineAsync(originalForeground);

        // ---- C2：两次启动真的同时在飞（模拟两条调用挤在一起） ----
        await ConcurrentLaunchAsync(originalForeground);

        // ---- 反证：force_new_instance=true 时到底能不能开第二个 ----
        await ScenarioAsync(
            originalForeground,
            "D 反证 force_new_instance=true 两次",
            "{\"path\":\"notepad.exe\"}",
            "{\"path\":\"notepad.exe\",\"force_new_instance\":true}");

        // ---- E：一次启动能吐出几个窗口（会话恢复是不是会一次性还原好几个） ----
        await MultiWindowRestoreAsync(originalForeground);

        Cleanup(originalForeground);
    }

    /// <summary>
    /// E：先开 2 个各带标记的记事本 → 强杀 → <b>只调一次</b> pc_launch。
    /// 看一次启动会不会把上次那 2 个窗口连同内容一起还原回来 ——
    /// 这决定了用户看到的"两个记事本"到底是不是"两次启动"。
    /// </summary>
    private static async Task MultiWindowRestoreAsync(IntPtr originalForeground)
    {
        Program.Info("");
        Program.Info("---- E 一次启动吐出几个窗口：开 2 个带标记的 → 强杀 → 只调一次 pc_launch ----");
        ResetNotepads();

        var first = await LaunchAsync("{\"path\":\"notepad.exe\"}");
        var firstHwnd = FirstHandleIn(first);
        await Task.Delay(1500);

        var second = await LaunchAsync("{\"path\":\"notepad.exe\",\"force_new_instance\":true}");
        var secondHwnd = FirstHandleIn(second);
        await Task.Delay(1500);

        var opened = NotepadWindows();
        Program.Info($"     开了 2 次之后: 进程 {Program.NotepadPids().Count} 个 / 窗口 {opened.Count} 个");
        foreach (var window in opened) Program.Info("        窗口: " + window.Line);

        if (firstHwnd != IntPtr.Zero) Program.Info($"     写标记 1: {Program.Trim(await TypeAsync(firstHwnd, "GHOST-ONE-1111"), 100)}");
        if (secondHwnd != IntPtr.Zero) Program.Info($"     写标记 2: {Program.Trim(await TypeAsync(secondHwnd, "GHOST-TWO-2222"), 100)}");
        await Task.Delay(3000);

        KillAllNotepad();
        await Task.Delay(2000);
        Program.Info($"     强杀之后: 进程 {Program.NotepadPids().Count} 个 / 窗口 {NotepadWindows().Count} 个");

        var once = await LaunchAsync("{\"path\":\"notepad.exe\"}");
        Program.Info($"     只调一次 pc_launch: {Program.Trim(once, 140)}");
        await Task.Delay(3000);

        var restored = NotepadWindows();
        Program.Info($"     一次启动之后: 进程 {Program.NotepadPids().Count} 个 / 窗口 {restored.Count} 个");
        foreach (var window in restored)
        {
            Program.Info($"        窗口: {window.Line}  内容=\"{Program.Trim(UiaRead(window.Hwnd), 40)}\"");
        }

        Program.Info(restored.Count > 1
            ? $"【结论 E】一次 pc_launch 就吐出了 {restored.Count} 个窗口 —— " +
              "Win11 记事本的会话恢复是【整场会话】一起还原的，用户看到的两个记事本可能只来自一次启动。"
            : "【结论 E】一次 pc_launch 只吐出一个窗口。");

        ResetNotepads();
        FocusGuard.Focus(originalForeground, out _);
    }

    /// <summary>
    /// C：启动之后"新窗口还没画出来、连启动器进程都退了"的空窗期到底有多长 ——
    /// 空窗期只要长于 0，模型在那会儿再调一次 pc_launch，就会判断成"没在跑"从而再起一个。
    /// </summary>
    private static async Task RaceTimelineAsync(IntPtr originalForeground)
    {
        Program.Info("");
        Program.Info("---- C 竞态：wait_ms:0 返回之后，『进程 0 / 窗口 0』的空窗期有多长 ----");
        ResetNotepads();

        var launched = await LaunchAsync("{\"path\":\"notepad.exe\",\"wait_ms\":0}");
        Program.Info($"     pc_launch(wait_ms:0) 返回: {Program.Trim(launched, 120)}");

        var started = Environment.TickCount64;
        var gapStart = -1L;
        var gapEnd = -1L;

        for (var i = 0; i < 60; i++)
        {
            var pids = Program.NotepadPids().Count;
            var windows = NotepadWindows().Count;
            var elapsed = Environment.TickCount64 - started;

            if (pids == 0 && windows == 0)
            {
                if (gapStart < 0) gapStart = elapsed;
                gapEnd = elapsed;
            }

            if (i % 4 == 0 || (pids > 0 && windows > 0))
            {
                var note = pids == 0 && windows == 0 ? "   ← 空窗期" : string.Empty;
                Program.Info($"     +{elapsed,4}ms  进程 {pids} / 窗口 {windows}{note}");
            }

            if (windows > 0) break;
            await Task.Delay(50);
        }

        if (gapStart >= 0)
        {
            Program.Info($"【结论 C】存在空窗期：pc_launch 返回后，有 {gapEnd - gapStart}ms 以上" +
                         "『既没有记事本进程、也没有记事本窗口』—— 这一刻再调一次 pc_launch，" +
                         "『已在运行就聚焦』必然判断成『没在跑』，于是新起第二个。竞态成立。");
        }
        else
        {
            Program.Info("【结论 C】没有观察到空窗期：启动器进程一直活到窗口出现为止。");
        }

        ResetNotepads();
        FocusGuard.Focus(originalForeground, out _);
    }

    /// <summary>C2：两次 pc_launch 真的同时在飞 —— 代码里没有任何"正在启动中"的互斥。</summary>
    private static async Task ConcurrentLaunchAsync(IntPtr originalForeground)
    {
        Program.Info("");
        Program.Info("---- C2 两次 pc_launch 同时发出（没有任何 in-flight 互斥） ----");
        ResetNotepads();

        var first = Task.Run(() => LaunchAsync("{\"path\":\"notepad.exe\",\"wait_ms\":0}"));
        var second = Task.Run(() => LaunchAsync("{\"path\":\"notepad.exe\",\"wait_ms\":8000}"));
        var results = await Task.WhenAll(first, second);

        Program.Info($"     第 1 次返回: {Program.Trim(results[0], 120)}");
        Program.Info($"     第 2 次返回: {Program.Trim(results[1], 120)}");

        await Task.Delay(2500);
        var windows = NotepadWindows();
        foreach (var window in windows) Program.Info("     窗口: " + window.Line);
        Program.Info($"     合计: 进程 {Program.NotepadPids().Count} 个 / 窗口 {windows.Count} 个 —— " +
                     (windows.Count > 1 ? "两次都真的启动了（没有互斥）" : "只启动了一个"));

        ResetNotepads();
        FocusGuard.Focus(originalForeground, out _);
    }

    /// <summary>跑一组"同一轮两次 pc_launch"，数增量并如实打印。每组都从 0 个记事本起跑。</summary>
    internal static async Task<LaunchScenarioResult> ScenarioAsync(
        IntPtr originalForeground, string label, string firstArgs, string secondArgs)
    {
        Program.Info("");
        Program.Info($"---- {label} ----");
        Program.Info($"     第 1 次: {firstArgs}");
        Program.Info($"     第 2 次: {secondArgs}");

        ResetNotepads();

        var beforePids = Program.NotepadPids();
        var beforeWindows = NotepadWindows();
        Program.Info($"     起跑前: 进程 {beforePids.Count} 个 / 窗口 {beforeWindows.Count} 个");

        var phaseStep = 0;
        using var server = MockServer.Start((_, _) => phaseStep++ > 0
            ? Sse.Script(Sse.Text("done"), Sse.Finish("stop"), Sse.Done)
            : Sse.Script(
                Sse.ToolCall(0, "l1", "pc_launch", firstArgs),
                Sse.ToolCall(1, "l2", "pc_launch", secondArgs),
                Sse.Finish("tool_calls"),
                Sse.Done));

        var tools = new ToolRegistry();
        PcTools.RegisterAll(tools);

        using var provider = Provider.For(server);
        var approver = new RecordingApprover(ToolApprovalDecision.AllowOnce);
        var session = new AgentSession(provider, tools, approver);

        var events = await Runner.RunAsync(session, "open notepad twice");
        var result = Runner.Result(events);

        // 窗口要时间才画出来：等一会儿再数，免得把"还没出现"当成"没开"。
        await Task.Delay(2500);

        var afterPids = Program.NotepadPids();
        var afterWindows = NotepadWindows();
        var newPids = afterPids.Where(pid => !beforePids.Contains(pid)).ToList();

        foreach (var finish in events.OfType<AgentToolFinished>())
        {
            Program.Info($"     {finish.Name} → ok={finish.Success} | {Program.Trim(finish.Summary, 150)}");
        }

        foreach (var denied in events.OfType<AgentToolDenied>())
        {
            Program.Info($"     {denied.Name} DENIED: {Program.Trim(denied.Reason, 100)}");
        }

        foreach (var window in afterWindows) Program.Info("     窗口: " + window.Line);

        Program.Info($"     增量: 新进程 {newPids.Count} 个 {string.Join(",", newPids)} / " +
                     $"窗口 {beforeWindows.Count} → {afterWindows.Count}（+{afterWindows.Count - beforeWindows.Count}）");
        Program.Info($"     护栏统计: duplicateCalls={result.DuplicateCallsSkipped}, executed={result.ToolCallsExecuted}, " +
                     $"approver 被问 {approver.Calls} 次");

        // 这一轮起的记事本全部收掉，给下一组一个干净的起点。
        ResetNotepads();
        FocusGuard.Focus(originalForeground, out _);

        return new LaunchScenarioResult(
            beforeWindows.Count,
            afterWindows.Count,
            newPids.Count,
            result.DuplicateCallsSkipped,
            result.ToolCallsExecuted,
            approver.Calls,
            events.OfType<AgentToolFinished>().ToList());
    }

    /// <summary>
    /// 把机器上的记事本清成 0 个，并且<b>先清空文档、再走 WM_CLOSE 好好关掉</b>。
    /// 为什么不能直接强杀：Win11 记事本会把"整场会话"（每个窗口 + 每个未保存标签）写进
    /// LocalState\TabState / WindowState，强杀之后下一次启动会把这堆窗口连同内容一起还原回来 ——
    /// 这正是用户看到"一次开了两个记事本"的真正来源（见 A 项结论）。
    /// 好好关掉之后记事本才会把自己的状态清干净。
    /// 只在"开跑前本来就是 0 个"的前提下调用 —— 用户自己的记事本一个都不碰。
    /// </summary>
    internal static void ResetNotepads()
    {
        CloseAllNotepadWindows();
        DrainPendingSessionContent();
        KillAllNotepad();

        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && NotepadWindows().Count > 0)
        {
            Thread.Sleep(200);
        }
    }

    /// <summary>把所有记事本窗口"先清空文档、再 WM_CLOSE 好好关掉"（最多三轮）。</summary>
    private static void CloseAllNotepadWindows()
    {
        for (var pass = 0; pass < 3 && NotepadWindows().Count > 0; pass++)
        {
            foreach (var window in NotepadWindows())
            {
                ClearDocument(window.Hwnd);
            }

            foreach (var window in NotepadWindows())
            {
                CloseWindow(window.Hwnd);
            }

            Thread.Sleep(1000);
        }
    }

    /// <summary>
    /// 把还等在 TabState 里的会话内容"引出来清掉"：开一次 → 清空它的所有窗口 → 好好关掉。
    /// 为什么需要：关掉（或强杀掉）一个<b>有未保存内容</b>的窗口时，记事本会把内容存进
    /// LocalState\TabState 等着下次恢复 —— 实测直接关掉一个脏窗口，下一次启动内容就回来了。
    /// 所以清场必须"先清空、再关"；对于已经留下内容的，只能再开一次把它引出来清掉。
    /// </summary>
    private static void DrainPendingSessionContent()
    {
        var outcome = new ComputerControl().Launch("notepad.exe", null, null, 6000);
        if (!outcome.Ok)
        {
            return;
        }

        Thread.Sleep(900);
        var windows = NotepadWindows();
        var restoredContent = windows
            .Select(window => UiaRead(window.Hwnd).Trim())
            .FirstOrDefault(text => text.Length > 0);

        if (restoredContent is not null)
        {
            Program.Info($"     （清场）引出了上次没保存的内容 \"{Program.Trim(restoredContent, 40)}\"，已清掉");
        }

        foreach (var window in windows)
        {
            ClearDocument(window.Hwnd);
        }

        foreach (var window in windows)
        {
            CloseWindow(window.Hwnd);
        }

        Thread.Sleep(600);
    }

    /// <summary>投 WM_CLOSE 关掉一个窗口（不经过工具层，清场专用）。</summary>
    private static void CloseWindow(IntPtr hwnd)
    {
        try
        {
            using var document = JsonDocument.Parse(
                $"{{\"hwnd\":\"0x{hwnd.ToInt64():X8}\",\"timeout_ms\":1500}}");
            new PcCloseWindowTool().InvokeAsync(document.RootElement, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // 关不掉不致命：下面还有强杀兜底。
        }
    }

    /// <summary>把某个记事本窗口的文档设成空串（UIA），这样关它的时候不会弹保存确认框。</summary>
    internal static void ClearDocument(IntPtr hwnd)
    {
        try
        {
            using var document = JsonDocument.Parse(
                $"{{\"text\":\"\",\"hwnd\":\"0x{hwnd.ToInt64():X8}\"}}");
            new PcTypeTool().InvokeAsync(document.RootElement, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // 清不掉不致命，后面还有强杀兜底。
        }
    }

    // ==================== 直接调工具 ====================

    /// <summary>直接调 pc_launch（不经过 AgentSession），返回工具结果的正文。</summary>
    private static async Task<string> LaunchAsync(string argsJson)
    {
        using var document = JsonDocument.Parse(argsJson);
        var tool = new PcLaunchTool();
        var result = await tool.InvokeAsync(document.RootElement, CancellationToken.None);
        return result.Content;
    }

    /// <summary>直接调 pc_type，把文字写进指定窗口。</summary>
    private static async Task<string> TypeAsync(IntPtr hwnd, string text)
    {
        using var document = JsonDocument.Parse(
            $"{{\"text\":{JsonSerializer.Serialize(text)},\"hwnd\":\"0x{hwnd.ToInt64():X8}\"}}");
        var tool = new PcTypeTool();
        var result = await tool.InvokeAsync(document.RootElement, CancellationToken.None);
        return result.Content;
    }

    // ==================== 计数与回读 ====================

    /// <summary>
    /// 可见的记事本窗口。判定用三样东西任一命中：进程名、窗口类名、标题 ——
    /// 打包版记事本在这三样上并不总是同一个名字，只认一样会漏。
    /// </summary>
    internal static List<NotepadWindow> NotepadWindows()
    {
        var windows = new List<NotepadWindow>();

        foreach (var hwnd in Desktop.VisibleWindows())
        {
            var info = Desktop.DescribeWindow(hwnd);
            var process = info["process"]?.GetValue<string>() ?? string.Empty;
            var className = info["class"]?.GetValue<string>() ?? string.Empty;
            var title = info["title"]?.GetValue<string>() ?? string.Empty;

            if (process.Contains("notepad", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("notepad", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("记事本", StringComparison.Ordinal) ||
                title.Contains("Notepad", StringComparison.OrdinalIgnoreCase))
            {
                windows.Add(new NotepadWindow(hwnd, info["pid"]!.GetValue<int>(), process, className, title));
            }
        }

        return windows;
    }

    /// <summary>独立于被测代码，用托管 UIA 把窗口里的文本读出来。</summary>
    internal static string UiaRead(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return string.Empty;

            var condition = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

            var element = root.FindFirst(TreeScope.Descendants, condition);
            if (element is null) return string.Empty;

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
            {
                return ((ValuePattern)valuePattern).Current.Value;
            }

            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
            {
                return ((TextPattern)textPattern).DocumentRange.GetText(-1);
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            Program.Info($"独立 UIA 回读失败: {ex.GetType().Name}: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>从工具结果里抠出第一个 "0x1234ABCD" 句柄。</summary>
    internal static IntPtr FirstHandleIn(string text)
    {
        var match = Regex.Match(text ?? string.Empty, @"0x[0-9A-Fa-f]{8}");
        return match.Success ? new IntPtr(Convert.ToInt64(match.Value[2..], 16)) : IntPtr.Zero;
    }

    /// <summary>等指定的记事本窗口数达到 n（打包应用画窗口有延迟）。</summary>
    internal static bool WaitForWindows(int count, int timeoutMs = 6000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (NotepadWindows().Count >= count) return true;
            Thread.Sleep(200);
        }

        return NotepadWindows().Count >= count;
    }

    /// <summary>强杀所有 notepad 进程。只在本项的"起跑前 0 个"前提成立时调用。</summary>
    internal static int KillAllNotepad()
    {
        var killed = 0;
        foreach (var process in Process.GetProcessesByName("notepad"))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(3000);
                    killed++;
                }
            }
            catch (Exception ex)
            {
                Program.Info($"     强杀 pid {process.Id} 失败（忽略）: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        return killed;
    }

    /// <summary>收尾：先清空文档再好好关掉所有记事本，归还前台。</summary>
    internal static void Cleanup(IntPtr originalForeground)
    {
        ResetNotepads();

        Program.Info($"     收尾: 现在进程 {Program.NotepadPids().Count} 个 / 窗口 {NotepadWindows().Count} 个；" +
                     $"会话状态文件 TabState {SessionStateFiles("TabState")} 个 / WindowState {SessionStateFiles("WindowState")} 个");

        if (originalForeground != IntPtr.Zero)
        {
            FocusGuard.Focus(originalForeground, out _);
        }
    }

    /// <summary>记事本留在 LocalState 里的会话状态文件数 —— 幽灵窗口就是从这儿来的。</summary>
    internal static int SessionStateFiles(string folder)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "Microsoft.WindowsNotepad_8wekyb3d8bbwe", "LocalState", folder);

            return Directory.Exists(path) ? Directory.GetFiles(path).Length : 0;
        }
        catch
        {
            return -1;
        }
    }
}
